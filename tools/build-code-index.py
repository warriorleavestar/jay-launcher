#!/usr/bin/env python3
"""Build a symbol-level code index for the Jay launcher, or check docs/CODEMAP.md.

Usage (from anywhere inside the repository):
  python3 tools/build-code-index.py [output dir]   # default output: .code-index/ (gitignored)
  python3 tools/build-code-index.py --check        # exit 1 if a source folder is missing from docs/CODEMAP.md

Writes:
  index.md     project -> folder -> file -> types (with /// summaries) and public members, with line numbers
  symbols.tsv  one row per symbol: kind, name, container, file, line, signature
  files.tsv    one row per tracked file: path, lines, kind
It reads the files git tracks (git ls-files), so build output and ignored files are skipped.
Parsing is regex-based (no compiler needed); it is good enough for navigation, not for refactoring.
The index is a local artifact: do not commit it.
"""
import os, re, subprocess, sys, collections

repo = subprocess.run(["git", "-C", os.path.dirname(os.path.abspath(__file__)), "rev-parse", "--show-toplevel"],
                      capture_output=True, text=True, encoding="utf-8", check=True).stdout.strip()
files = subprocess.run(["git", "-C", repo, "ls-files"], capture_output=True, text=True, encoding="utf-8", check=True).stdout.splitlines()

def check_codemap():
    """Every folder that holds C# or XAML sources (tests: project root only) must appear in docs/CODEMAP.md as `folder/`."""
    with open(os.path.join(repo, "docs", "CODEMAP.md"), encoding="utf-8") as f:
        text = f.read()
    folders = set()
    for path in files:
        if not path.endswith((".cs", ".axaml")):
            continue
        folder = os.path.dirname(path)
        if folder.startswith("src/Amber.Core.Tests/"):
            folder = "src/Amber.Core.Tests"
        folders.add(folder)
    missing = sorted(d for d in folders if f"`{d}/`" not in text)
    for d in missing:
        print(f"docs/CODEMAP.md does not mention `{d}/`")
    if missing:
        print(f"{len(missing)} folder(s) missing. Add a line for each to docs/CODEMAP.md.")
        return 1
    print(f"docs/CODEMAP.md covers all {len(folders)} source folders.")
    return 0

if "--check" in sys.argv[1:]:
    sys.exit(check_codemap())

args = [a for a in sys.argv[1:] if not a.startswith("-")]
out = os.path.abspath(args[0]) if args else os.path.join(repo, ".code-index")
os.makedirs(out, exist_ok=True)
head = subprocess.run(["git", "-C", repo, "log", "-1", "--format=%h %cI %s"], capture_output=True, text=True, encoding="utf-8").stdout.strip()

TYPE_RE = re.compile(r'^\s*(?:\[[^\]]*\]\s*)*((?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|ref|unsafe|new)\s+)*)(class|record\s+struct|record\s+class|record|struct|interface|enum)\s+([A-Za-z_][\w]*(?:<[^>{(]*>)?)')
MEMBER_RE = re.compile(r'^\s*((?:(?:public|internal|protected|static|override|virtual|abstract|async|sealed|readonly|required|partial|extern|unsafe|new|const|event)\s+)+)([\w<>\[\],.?() ]+?)\s+([A-Za-z_]\w*)\s*(\(|\{|=>|=|;)')
NS_RE = re.compile(r'^\s*namespace\s+([\w.]+)')
SUMMARY_LINE = re.compile(r'^\s*///\s?(.*)$')
SKIP_KW = {"return", "new", "else", "if", "await", "throw", "var", "using", "case", "goto", "yield"}

def summary_from(lines, i):
    """Collect the /// summary right above line i (first sentence-ish, max 160 chars)."""
    buf = []
    j = i - 1
    while j >= 0 and (lines[j].strip().startswith("///") or lines[j].strip().startswith("[")):
        m = SUMMARY_LINE.match(lines[j])
        if m: buf.insert(0, m.group(1))
        j -= 1
    text = " ".join(buf)
    text = re.sub(r'</?(summary|para|c|code)>', ' ', text)
    text = re.sub(r'<see cref="([^"]+)"\s*/>', r'\1', text)
    text = re.sub(r'<param name="[^"]*">.*', '', text)
    text = re.sub(r'<[^>]+>', '', text)
    text = re.sub(r'\s+', ' ', text).strip()
    return text[:160] + ("…" if len(text) > 160 else "")

symbols = []           # (kind, name, container, file, line, signature)
by_file = collections.OrderedDict()
file_rows = []

for path in files:
    full = os.path.join(repo, path)
    ext = os.path.splitext(path)[1].lower()
    kind = {".cs": "code", ".axaml": "xaml", ".json": "data", ".md": "doc", ".csproj": "project", ".ps1": "script",
            ".sh": "script", ".yml": "ci", ".yaml": "ci", ".py": "script"}.get(ext, "other")
    try:
        with open(full, encoding="utf-8", errors="replace") as f:
            lines = f.read().splitlines()
    except (IsADirectoryError, FileNotFoundError):
        continue
    file_rows.append((path, len(lines), kind))
    if ext != ".cs":
        continue
    ns = ""
    stack = []         # (type name, brace depth when opened)
    depth = 0
    entries = []
    for i, raw in enumerate(lines):
        line = re.sub(r'//.*$', '', raw) if not raw.strip().startswith("///") else ""
        m = NS_RE.match(line)
        if m: ns = m.group(1)
        t = TYPE_RE.match(line)
        if t and "(" not in line.split(t.group(3))[0]:
            name = t.group(3).strip()
            tkind = t.group(2).replace("  ", " ")
            container = ".".join(s[0] for s in stack) or ns
            sig = raw.strip()[:200]
            summ = summary_from(lines, i)
            symbols.append((tkind, name, container, path, i + 1, sig))
            entries.append(("type", len(stack), tkind, name, i + 1, summ))
            stack.append((re.sub(r'<.*', '', name), depth))
        else:
            mm = MEMBER_RE.match(line)
            if mm and stack and mm.group(3) not in SKIP_KW and not raw.strip().startswith("///"):
                mods = mm.group(1)
                if ("public" in mods or "internal" in mods or "protected" in mods) and len(stack) <= 3:
                    mkind = "const" if "const" in mods else "event" if "event" in mods else "method" if mm.group(4) == "(" else "property"
                    container = ".".join(s[0] for s in stack)
                    sig = raw.strip()[:200]
                    symbols.append((mkind, mm.group(3), container, path, i + 1, sig))
                    entries.append(("member", len(stack), mkind, mm.group(3), i + 1, ""))
        # crude brace tracking (ignores braces inside strings, good enough for navigation)
        depth += line.count("{") - line.count("}")
        while stack and depth <= stack[-1][1] and ("}" in line):
            stack.pop()
    by_file[path] = entries

with open(os.path.join(out, "symbols.tsv"), "w", encoding="utf-8", newline="\n") as f:
    f.write("kind\tname\tcontainer\tfile\tline\tsignature\n")
    for s in symbols:
        f.write("\t".join(str(x).replace("\t", " ") for x in s) + "\n")

with open(os.path.join(out, "files.tsv"), "w", encoding="utf-8", newline="\n") as f:
    f.write("path\tlines\tkind\n")
    for r in file_rows:
        f.write("\t".join(map(str, r)) + "\n")

# index.md
projects = collections.OrderedDict()
for path, entries in by_file.items():
    parts = path.split("/")
    project = "/".join(parts[:2]) if parts[0] == "src" and len(parts) > 2 else parts[0]
    folder = "/".join(parts[:-1])
    projects.setdefault(project, collections.OrderedDict()).setdefault(folder, []).append((path, entries))

total_lines = sum(r[1] for r in file_rows if r[2] == "code")
md = [f"# Jay code index\n", f"Generated from `{head}`. {len(file_rows)} tracked files, {len(by_file)} C# files, {total_lines} lines of C#, "
      f"{sum(1 for s in symbols if s[0] not in ('method','property','const','event'))} types, {len(symbols)} symbols. "
      "Regenerate with `python3 tools/build-code-index.py`. Paths are relative to the repo root; `file:line` links open in most editors.\n"]
md.append("## Projects\n")
md.append("| Project | C# files | Lines |\n| --- | --- | --- |")
for project, folders in projects.items():
    n = sum(len(v) for v in folders.values())
    lines_ = sum(r[1] for r in file_rows if r[0].startswith(project + "/") and r[2] == "code")
    md.append(f"| `{project}` | {n} | {lines_} |")
md.append("")
for project, folders in projects.items():
    md.append(f"## {project}\n")
    for folder, flist in folders.items():
        md.append(f"### {folder}/\n")
        for path, entries in flist:
            fname = path.split("/")[-1]
            types = [e for e in entries if e[0] == "type"]
            if not types:
                md.append(f"- `{fname}`")
                continue
            md.append(f"- `{fname}`")
            for e in entries:
                _, lvl, k, name, line, summ = e
                if e[0] == "type":
                    s = f" — {summ}" if summ else ""
                    md.append(f"{'  ' * (lvl + 1)}- {k} **{name}** `:{line}`{s}")
            members = [e for e in entries if e[0] == "member" and e[1] == 1]
            if members:
                shown = ", ".join(f"{e[3]}`:{e[4]}`" for e in members[:40])
                more = f" … (+{len(members) - 40})" if len(members) > 40 else ""
                md.append(f"    - members: {shown}{more}")
        md.append("")

with open(os.path.join(out, "index.md"), "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(md) + "\n")
print(f"{len(by_file)} C# files, {len(symbols)} symbols -> {out}")
