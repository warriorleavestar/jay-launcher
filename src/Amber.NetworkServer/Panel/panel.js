// Jay node panel. Talks to the node's local admin API with the admin token; never builds HTML from data
// (room names and host names come from players), everything goes through textContent.
"use strict";

const STRINGS = {
  en: {
    title: "Jay node", refresh: "Refresh", logout: "Sign out", login_title: "Sign in",
    login_hint: "Paste the admin token from admin.token in the node's data folder, or run \"jay-server node panel\" on the node.",
    signin: "Sign in", bad_token: "That token is not right.", unreachable: "Can't reach the node. Is it running?",
    rooms: "Public rooms", volunteers: "Volunteer relays", bans: "Bans", add_ban: "Add a ban",
    col_name: "Name", col_host: "Host", col_mode: "Mode", col_players: "Players", col_game: "Game",
    col_kind: "Kind", col_value: "Value", col_reason: "Reason", col_until: "Until",
    kind_host: "Host", kind_room: "Room", kind_account: "Account", kind_node: "Volunteer relay",
    ban: "Ban", unban: "Unban", remove: "Remove", ban_host: "Ban host",
    forever: "Permanently", h1: "1 hour", d1: "1 day", d7: "7 days", d30: "30 days",
    reason_placeholder: "Reason (shown to the host)", value_placeholder: "Fingerprint, room id, account or tcp://address:port",
    rooms_summary: "{0} rooms in the directory, {1} not public (contents not shown).",
    nodes_off: "This network doesn't take volunteer relays.", nodes_none: "No volunteer relays right now.",
    no_rooms: "No public rooms.", no_bans: "No bans.", permanent: "permanent",
    healthy: "Running", unhealthy: "Not healthy", seed: "seed only",
    s_rooms: "Rooms", s_listed: "Public", s_published: "Published", s_renewed: "Renewals", s_withdrawn: "Closed",
    s_expired: "Expired", s_removed: "Removed", s_rejected: "Rejected", s_login_ok: "Logins", s_login_fail: "Failed logins",
    s_volunteers: "Volunteer relays", s_uptime: "Uptime", s_min_version: "Min. Jay version",
    confirm_remove: "Remove room \"{0}\"? The host's next renewal is checked again.",
    confirm_ban_host: "Ban the host of \"{0}\" (their identity and account) for: {1}?",
    confirm_unban: "Lift this ban?", done_ban: "Banned. {0} room(s) taken down.", done: "Done.",
    mode_public: "public", mode_password: "password", mode_hidden: "hidden", mode_direct: "direct",
  },
  "zh-Hans": {
    title: "Jay 节点", refresh: "刷新", logout: "退出", login_title: "登录",
    login_hint: "粘贴节点数据目录里 admin.token 的内容，或者在节点上运行“jay-server node panel”。",
    signin: "登录", bad_token: "口令不对。", unreachable: "连不上节点，它在运行吗？",
    rooms: "公开房间", volunteers: "志愿中转", bans: "封禁", add_ban: "添加封禁",
    col_name: "名字", col_host: "房主", col_mode: "方式", col_players: "人数", col_game: "游戏",
    col_kind: "类型", col_value: "对象", col_reason: "原因", col_until: "到期",
    kind_host: "房主", kind_room: "房间", kind_account: "账号", kind_node: "志愿中转",
    ban: "封禁", unban: "解封", remove: "删除", ban_host: "封房主",
    forever: "永久", h1: "1 小时", d1: "1 天", d7: "7 天", d30: "30 天",
    reason_placeholder: "原因（房主能看到）", value_placeholder: "房主指纹、房间编号、账号或 tcp://地址:端口",
    rooms_summary: "目录里 {0} 个房间，其中 {1} 个不公开（不显示内容）。",
    nodes_off: "这个网络不收志愿中转。", nodes_none: "现在没有志愿中转。",
    no_rooms: "没有公开房间。", no_bans: "没有封禁。", permanent: "永久",
    healthy: "运行中", unhealthy: "状态异常", seed: "只当种子",
    s_rooms: "房间", s_listed: "公开", s_published: "发布", s_renewed: "续期", s_withdrawn: "关闭",
    s_expired: "过期", s_removed: "删除", s_rejected: "被拒", s_login_ok: "登录", s_login_fail: "登录失败",
    s_volunteers: "志愿中转", s_uptime: "运行时间", s_min_version: "最低 Jay 版本",
    confirm_remove: "删除房间“{0}”？房主下次续期时会重新审核。",
    confirm_ban_host: "封禁“{0}”的房主（联机身份和账号），时长：{1}？",
    confirm_unban: "解除这条封禁？", done_ban: "已封禁，拿掉了 {0} 个房间。", done: "好了。",
    mode_public: "公开", mode_password: "密码", mode_hidden: "隐藏", mode_direct: "直连",
  },
  "zh-Hant": {
    title: "Jay 節點", refresh: "重新整理", logout: "登出", login_title: "登入",
    login_hint: "貼上節點資料夾裡 admin.token 的內容，或在節點上執行「jay-server node panel」。",
    signin: "登入", bad_token: "口令不對。", unreachable: "連不上節點，它在執行嗎？",
    rooms: "公開房間", volunteers: "志願中轉", bans: "封鎖", add_ban: "新增封鎖",
    col_name: "名字", col_host: "房主", col_mode: "方式", col_players: "人數", col_game: "遊戲",
    col_kind: "類型", col_value: "對象", col_reason: "原因", col_until: "到期",
    kind_host: "房主", kind_room: "房間", kind_account: "帳號", kind_node: "志願中轉",
    ban: "封鎖", unban: "解除封鎖", remove: "刪除", ban_host: "封鎖房主",
    forever: "永久", h1: "1 小時", d1: "1 天", d7: "7 天", d30: "30 天",
    reason_placeholder: "原因（房主看得到）", value_placeholder: "房主指紋、房間編號、帳號或 tcp://位址:連接埠",
    rooms_summary: "目錄裡 {0} 個房間，其中 {1} 個不公開（不顯示內容）。",
    nodes_off: "這個網路不收志願中轉。", nodes_none: "現在沒有志願中轉。",
    no_rooms: "沒有公開房間。", no_bans: "沒有封鎖。", permanent: "永久",
    healthy: "執行中", unhealthy: "狀態異常", seed: "只當種子",
    s_rooms: "房間", s_listed: "公開", s_published: "發布", s_renewed: "續期", s_withdrawn: "關閉",
    s_expired: "過期", s_removed: "刪除", s_rejected: "被拒", s_login_ok: "登入", s_login_fail: "登入失敗",
    s_volunteers: "志願中轉", s_uptime: "執行時間", s_min_version: "最低 Jay 版本",
    confirm_remove: "刪除房間「{0}」？房主下次續期時會重新審核。",
    confirm_ban_host: "封鎖「{0}」的房主（連線身分和帳號），時長：{1}？",
    confirm_unban: "解除這條封鎖？", done_ban: "已封鎖，拿掉了 {0} 個房間。", done: "好了。",
    mode_public: "公開", mode_password: "密碼", mode_hidden: "隱藏", mode_direct: "直連",
  },
  ja: {
    title: "Jay ノード", refresh: "更新", logout: "サインアウト", login_title: "サインイン",
    login_hint: "ノードのデータフォルダーにある admin.token の内容を貼り付けるか、ノードで「jay-server node panel」を実行してください。",
    signin: "サインイン", bad_token: "トークンが違います。", unreachable: "ノードに接続できません。起動していますか？",
    rooms: "公開ルーム", volunteers: "ボランティア中継", bans: "BAN", add_ban: "BAN を追加",
    col_name: "名前", col_host: "ホスト", col_mode: "方式", col_players: "人数", col_game: "ゲーム",
    col_kind: "種類", col_value: "対象", col_reason: "理由", col_until: "期限",
    kind_host: "ホスト", kind_room: "ルーム", kind_account: "アカウント", kind_node: "ボランティア中継",
    ban: "BAN", unban: "解除", remove: "削除", ban_host: "ホストを BAN",
    forever: "無期限", h1: "1 時間", d1: "1 日", d7: "7 日", d30: "30 日",
    reason_placeholder: "理由（ホストに表示）", value_placeholder: "ホスト ID、ルーム ID、アカウント、または tcp://アドレス:ポート",
    rooms_summary: "ディレクトリに {0} ルーム、うち {1} は非公開（内容は表示しません）。",
    nodes_off: "このネットワークはボランティア中継を受け付けていません。", nodes_none: "いまボランティア中継はありません。",
    no_rooms: "公開ルームはありません。", no_bans: "BAN はありません。", permanent: "無期限",
    healthy: "稼働中", unhealthy: "異常", seed: "シードのみ",
    s_rooms: "ルーム", s_listed: "公開", s_published: "公開数", s_renewed: "更新", s_withdrawn: "終了",
    s_expired: "期限切れ", s_removed: "削除", s_rejected: "拒否", s_login_ok: "ログイン", s_login_fail: "ログイン失敗",
    s_volunteers: "ボランティア中継", s_uptime: "稼働時間", s_min_version: "最低 Jay バージョン",
    confirm_remove: "ルーム「{0}」を削除しますか？ ホストの次回更新時に再審査されます。",
    confirm_ban_host: "「{0}」のホスト（ID とアカウント）を BAN しますか？ 期間：{1}",
    confirm_unban: "この BAN を解除しますか？", done_ban: "BAN しました。{0} ルームを外しました。", done: "完了しました。",
    mode_public: "公開", mode_password: "パスワード", mode_hidden: "非公開", mode_direct: "直結",
  },
};

const lang = (() => {
  for (const l of navigator.languages || [navigator.language || "en"]) {
    const s = String(l).toLowerCase();
    if (s.startsWith("zh")) return /hant|tw|hk|mo/.test(s) ? "zh-Hant" : "zh-Hans";
    if (s.startsWith("ja")) return "ja";
    if (s.startsWith("en")) return "en";
  }
  return "en";
})();
const T = (key, ...args) => (STRINGS[lang][key] ?? STRINGS.en[key] ?? key).replace(/\{(\d)\}/g, (_, i) => String(args[Number(i)] ?? ""));
const $ = (id) => document.getElementById(id);
const DURATIONS = [["forever", null], ["h1", 3600], ["d1", 86400], ["d7", 604800], ["d30", 2592000]];
const TOKEN_KEY = "jay-node-token";

let token = null;

function el(tag, text, className) {
  const e = document.createElement(tag);
  if (text !== undefined && text !== null) e.textContent = String(text);
  if (className) e.className = className;
  return e;
}

function button(text, onClick, className = "small") {
  const b = el("button", text, className);
  b.type = "button";
  b.addEventListener("click", onClick);
  return b;
}

function toast(text, isError = false) {
  const t = $("toast");
  t.textContent = text;
  t.className = isError ? "error" : "";
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { t.hidden = true; }, 4000);
}

function readStoredToken() {
  try { return sessionStorage.getItem(TOKEN_KEY); } catch { return null; }
}

function storeToken(value) {
  try { value ? sessionStorage.setItem(TOKEN_KEY, value) : sessionStorage.removeItem(TOKEN_KEY); } catch { /* private mode */ }
}

async function api(method, path, body) {
  const response = await fetch(path, {
    method,
    headers: { Authorization: "Bearer " + token, ...(body ? { "Content-Type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
    cache: "no-store",
    credentials: "omit",
  });
  const data = await response.json().catch(() => ({ ok: false, error: "bad" }));
  if (response.status === 401) { signOut(T("bad_token")); throw new Error("token"); }
  return data;
}

function showLogin(error) {
  $("login").hidden = false;
  $("app").hidden = true;
  $("logout").hidden = true;
  $("refresh").hidden = true;
  const e = $("login-error");
  e.hidden = !error;
  e.textContent = error || "";
  $("token").focus();
}

function signOut(error) {
  token = null;
  storeToken(null);
  showLogin(error);
}

function formatUptime(seconds) {
  const d = Math.floor(seconds / 86400), h = Math.floor((seconds % 86400) / 3600), m = Math.floor((seconds % 3600) / 60);
  return d > 0 ? `${d}d ${h}h` : h > 0 ? `${h}h ${m}m` : `${m}m`;
}

function formatTime(unix) {
  return unix == null ? T("permanent") : new Date(unix * 1000).toLocaleString(lang);
}

function durationSelect(select) {
  select.replaceChildren(...DURATIONS.map(([key, seconds]) => {
    const o = el("option", T(key));
    o.value = seconds === null ? "" : String(seconds);
    return o;
  }));
}

function durationLabel(seconds) {
  const found = DURATIONS.find(([, s]) => String(s ?? "") === seconds);
  return T(found ? found[0] : "forever");
}

function renderNumbers(health, stats, nodes) {
  const rejected = Object.values(stats.rejected || {}).reduce((a, b) => a + b, 0);
  const items = [
    ["s_rooms", health.rooms], ["s_listed", health.listed], ["s_published", stats.published], ["s_renewed", stats.renewed],
    ["s_withdrawn", stats.withdrawn], ["s_expired", stats.expired], ["s_removed", stats.removed], ["s_rejected", rejected],
  ];
  if (health.login) items.push(["s_login_ok", stats.loginOk], ["s_login_fail", stats.loginFail]);
  if (nodes && nodes.enabled) items.push(["s_volunteers", nodes.nodes.length]);
  items.push(["s_uptime", formatUptime(health.uptime || 0)]);
  if (health.minVersion) items.push(["s_min_version", health.minVersion]);
  $("numbers").replaceChildren(...items.map(([key, value]) => {
    const card = el("div", null, "stat");
    card.append(el("b", value ?? 0), el("span", T(key)));
    return card;
  }));
  $("network").textContent = health.network + (health.directory ? " · " + health.directory : "") + (health.role === "seed" ? " · " + T("seed") : "");
  const state = $("state");
  state.textContent = health.ok ? T("healthy") : T("unhealthy");
  state.className = "pill " + (health.ok ? "ok" : "bad");
}

function renderRooms(data) {
  $("rooms-summary").textContent = T("rooms_summary", data.total, data.hidden);
  const body = $("rooms").tBodies[0];
  if (!data.rooms.length) {
    const row = body.insertRow();
    const cell = row.insertCell();
    cell.colSpan = 6;
    cell.textContent = T("no_rooms");
    cell.className = "muted";
    body.replaceChildren(row);
    return;
  }
  body.replaceChildren(...data.rooms.map((r) => {
    const row = document.createElement("tr");
    const name = el("td");
    name.append(el("div", r.name), el("div", r.id, "mono muted"));
    const host = el("td");
    host.append(el("div", r.host + (r.account ? " (" + r.account + ")" : "")), el("div", r.hostFingerprint, "mono muted"));
    const actions = el("td", null, "actions");
    actions.append(
      button(T("remove"), () => removeRoom(r)),
      " ",
      button(T("ban_host"), () => banHost(r), "small danger"));
    row.append(name, host, el("td", T("mode_" + r.mode)), el("td", r.players), el("td", r.game), actions);
    return row;
  }));
}

function renderNodes(data) {
  const list = $("nodes");
  $("nodes-summary").textContent = !data.enabled ? T("nodes_off") : data.nodes.length ? "" : T("nodes_none");
  list.replaceChildren(...data.nodes.map((url) => {
    const li = el("li");
    li.append(el("span", url, "mono"), button(T("ban"), () => ban("node", url, "", null), "small danger"));
    return li;
  }));
}

function renderBans(data) {
  const body = $("bans").tBodies[0];
  if (!data.bans.length) {
    const row = body.insertRow();
    const cell = row.insertCell();
    cell.colSpan = 5;
    cell.textContent = T("no_bans");
    cell.className = "muted";
    body.replaceChildren(row);
    return;
  }
  body.replaceChildren(...data.bans.map((b) => {
    const row = document.createElement("tr");
    const actions = el("td", null, "actions");
    actions.append(button(T("unban"), () => unban(b)));
    row.append(el("td", T("kind_" + b.kind)), el("td", b.value, "mono"), el("td", b.reason || ""), el("td", formatTime(b.until)), actions);
    return row;
  }));
}

async function refresh() {
  try {
    const [stats, rooms, nodes, bans] = await Promise.all([api("GET", "/stats"), api("GET", "/rooms"), api("GET", "/nodes"), api("GET", "/bans")]);
    $("login").hidden = true;
    $("app").hidden = false;
    $("logout").hidden = false;
    $("refresh").hidden = false;
    renderNumbers(stats.health, stats.stats, nodes.ok ? nodes : null);
    renderRooms(rooms);
    renderNodes(nodes.ok ? nodes : { enabled: false, nodes: [] });
    renderBans(bans.ok ? bans : { bans: [] });
  } catch (e) {
    if (e.message !== "token") (token ? toast : showLogin)(T("unreachable"), true);
  }
}

async function act(promise, message) {
  const reply = await promise;
  if (reply.ok) toast(message ? message(reply) : T("done"));
  else toast(reply.error || "error", true);
  await refresh();
}

function removeRoom(r) {
  if (confirm(T("confirm_remove", r.name))) act(api("POST", "/remove", { id: r.id }));
}

function banHost(r) {
  const seconds = $("ban-for").value;
  if (!confirm(T("confirm_ban_host", r.name, durationLabel(seconds)))) return;
  ban("room-host", r.id, $("ban-reason").value, seconds ? Number(seconds) : null);
}

function ban(kind, value, reason, seconds) {
  const body = { kind, value };
  if (reason) body.reason = reason;
  if (seconds) body.for = seconds;
  act(api("POST", "/ban", body), (reply) => T("done_ban", reply.removed ?? 0));
}

function unban(b) {
  // Each row is one entry: lift only that one, even when an exact and an older any-case account ban share the name.
  const match = b.kind === "account" ? (b.anyCase ? "anyCase" : "exact") : undefined;
  if (confirm(T("confirm_unban"))) act(api("POST", "/unban", { kind: b.kind, value: b.value, match }));
}

function signIn(value) {
  const v = (value || "").trim();
  if (!/^[0-9a-f]{64}$/i.test(v)) { showLogin(T("bad_token")); return; }
  token = v;
  storeToken(v);
  refresh();
}

document.addEventListener("DOMContentLoaded", () => {
  document.documentElement.lang = lang;
  document.title = T("title");
  for (const e of document.querySelectorAll("[data-t]")) e.textContent = T(e.dataset.t);
  $("ban-reason").placeholder = T("reason_placeholder");
  $("ban-value").placeholder = T("value_placeholder");
  durationSelect($("ban-for"));
  $("signin").addEventListener("click", () => signIn($("token").value));
  $("token").addEventListener("keydown", (e) => { if (e.key === "Enter") signIn($("token").value); });
  $("logout").addEventListener("click", () => signOut());
  $("refresh").addEventListener("click", refresh);
  $("ban-add").addEventListener("click", () => {
    const value = $("ban-value").value.trim();
    if (!value) return;
    const seconds = $("ban-for").value;
    ban($("ban-kind").value, value, $("ban-reason").value, seconds ? Number(seconds) : null);
    $("ban-value").value = "";
  });

  // "jay-server node panel" opens #token=...: take it, then drop it from the address bar and history.
  const match = /(?:^#|&)token=([0-9a-f]{64})/i.exec(location.hash);
  if (match) history.replaceState(null, "", location.pathname);
  const initial = match ? match[1] : readStoredToken();
  if (initial) signIn(initial); else showLogin();
  setInterval(() => { if (token && !document.hidden) refresh(); }, 15000);
});
