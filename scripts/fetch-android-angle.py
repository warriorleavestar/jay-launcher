"""Stage the two standalone ANGLE libraries per emulator ABI using build/pins.json."""

import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[1]


def matches(path, pin):
    if not path.is_file() or path.stat().st_size != pin["size"]:
        return False
    return hashlib.sha256(path.read_bytes()).hexdigest() == pin["sha256"]


def main(output):
    pins = {pin["id"]: pin for pin in json.loads((root / "build/pins.json").read_text())["pins"]}
    for abi in ("arm64-v8a", "x86_64"):
        directory = output / abi
        directory.mkdir(parents=True, exist_ok=True)
        for lib in ("egl", "glesv2"):
            pin = pins[f"android-angle-{lib}-{abi}"]
            destination = directory / pin["file"]
            if matches(destination, pin):
                continue
            with tempfile.TemporaryDirectory(dir=directory) as temporary:
                staged = Path(temporary) / destination.name
                subprocess.run(["curl", "--fail", "--location", "--silent", "--show-error",
                                "--retry", "3", "--proto", "=https", "--output", str(staged),
                                pin["urls"][0]], check=True)
                if not matches(staged, pin):
                    raise ValueError(f"Checksum or length mismatch for {pin['id']}")
                staged.replace(destination)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: fetch-android-angle.py <output-directory>")
    main(Path(sys.argv[1]))
