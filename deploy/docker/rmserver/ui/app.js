// RemoteMan server admin page. Plain JS, no libraries. Polls /api/live once a second.
"use strict";

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const COL = { accent: "#3FB8A4", blue: "#6FA8E8", warn: "#E0A84A", bad: "#E5534B", admin: "#B49CF0", ok: "#54C08A", grid: "#22272C", faint: "#7D8690", muted: "#9AA3AD" };
const SERVER_COLORS = ["#3FB8A4", "#6FA8E8", "#B49CF0", "#E0A84A", "#E58FB4", "#9AD16E"];
const AV_COLORS = ["#B49CF0", "#54C08A", "#6FA8E8", "#E0A84A", "#3FB8A4", "#E58FB4", "#9AD16E", "#F0A070"];

let live = null, page = "overview", range = "2m", hist = null, histAt = 0, lastOk = 0, users = null, usersAt = 0;
const relayPrev = {}; const relaySeries = []; const seenFeed = new Set(); let feedPrimed = false;

// ------------------------------------------------------------------ helpers
async function api(path, opts = {}) {
  const r = await fetch(path, {
    method: opts.method || "GET", credentials: "same-origin",
    headers: { "Content-Type": "application/json", "X-Requested-With": "remoteman" },
    body: opts.body ? JSON.stringify(opts.body) : undefined,
  });
  let data = {};
  try { data = await r.json(); } catch { /* empty */ }
  if (r.status === 401 && !opts.noAuthRedirect) { showLogin(); throw new Error(data.error || "Sign in first."); }
  if (!r.ok) throw new Error(data.error || `Request failed (${r.status})`);
  return data;
}
const post = (path, body) => api(path, { method: "POST", body: body || {} });

function fmtBytes(b, d = 1) {
  if (b == null || isNaN(b)) return "–";
  const u = ["B", "KB", "MB", "GB", "TB"]; let i = 0; b = Number(b);
  while (Math.abs(b) >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  return (i === 0 ? Math.round(b) : b.toFixed(b >= 100 ? 0 : d)) + " " + u[i];
}
const fmtRate = b => fmtBytes(b) + "/s";
function fmtNum(v, d = 0) { if (v == null || isNaN(v)) return "–"; return Number(v).toLocaleString(undefined, { maximumFractionDigits: d, minimumFractionDigits: d }); }
function fmtDur(s, long) {
  if (s == null || isNaN(s)) return "–"; s = Math.max(0, Math.floor(s));
  const d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60), sec = s % 60;
  if (long) return d ? `${d} d ${h} h ${String(m).padStart(2, "0")} min ${String(sec).padStart(2, "0")} s` : h ? `${h} h ${String(m).padStart(2, "0")} min ${String(sec).padStart(2, "0")} s` : `${m} min ${String(sec).padStart(2, "0")} s`;
  if (d) return `${d} d ${h} h`; if (h) return `${h} h ${String(m).padStart(2, "0")} min`; if (m) return `${m} min ${String(sec).padStart(2, "0")} s`; return `${sec} s`;
}
function fmtAgo(ts) { if (!ts) return "never"; const s = Date.now() / 1000 - ts; if (s < 60) return "just now"; if (s < 3600) return Math.floor(s / 60) + " min ago"; if (s < 86400) return Math.floor(s / 3600) + " h ago"; return Math.floor(s / 86400) + " days ago"; }
function fmtWhen(ts) {
  if (!ts) return "–"; const d = new Date(ts * 1000), now = new Date();
  const t = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  const y = new Date(now); y.setDate(now.getDate() - 1);
  if (d.toDateString() === now.toDateString()) return "Today " + t;
  if (d.toDateString() === y.toDateString()) return "Yesterday " + t;
  return d.toLocaleDateString([], { day: "numeric", month: "short" }) + " " + t;
}
const clock = ts => new Date(ts * 1000).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
function hashColor(s, list = AV_COLORS) { let h = 0; for (const c of String(s)) h = (h * 31 + c.charCodeAt(0)) >>> 0; return list[h % list.length]; }
const initials = s => String(s || "?").replace(/[^a-zA-Z0-9]/g, "").slice(0, 2) || "?";
const av = n => `<div class="av" style="background:${hashColor(n)}">${esc(initials(n))}</div>`;
const accessPill = a => a ? `<span class="pill ${esc(a)}">${esc(a)}</span>` : "";
const lastOf = arr => arr && arr.length ? arr[arr.length - 1] : null;

function toast(text, bad) {
  const t = document.createElement("div"); t.className = "toast" + (bad ? " bad" : ""); t.textContent = text;
  $("#toasts").appendChild(t); setTimeout(() => t.remove(), bad ? 9000 : 5000);
}
async function copyText(text, btn) {
  try { await navigator.clipboard.writeText(text); if (btn) { const o = btn.textContent; btn.textContent = "Copied"; setTimeout(() => btn.textContent = o, 1500); } }
  catch { toast("Couldn't copy. Select the text and copy it yourself.", true); }
}

// ------------------------------------------------------------------ charts
function linePath(pts, x, y) { let d = "", pen = false; pts.forEach((v, i) => { if (v == null || isNaN(v)) { pen = false; return; } d += (pen ? "L" : "M") + x(i).toFixed(1) + " " + y(v).toFixed(1) + " "; pen = true; }); return d; }
function niceMax(v) { if (!(v > 0)) return 1; const p = Math.pow(10, Math.floor(Math.log10(v))); for (const m of [1, 2, 2.5, 5, 10]) if (v <= m * p) return m * p; return 10 * p; }
const tickFmt = v => v >= 1000 ? (v / 1000).toFixed(v % 1000 ? 1 : 0) + "k" : (v < 1 && v > 0 ? v.toFixed(2) : String(+v.toFixed(1)));

function chart(svg, opt) {
  // opt: {series:[{d,c,area,right,dash}], bars:[...], n, left:{max,fmt}, right:{max,fmt,color}, labels:[l,m,r]}
  const W = 560, H = 170, L = 40, R = opt.right ? 44 : 10, T = 10, B = 22, n = opt.n;
  const x = i => L + (W - L - R) * (n <= 1 ? 1 : i / (n - 1));
  const lmax = niceMax(opt.left.max), rmax = opt.right ? niceMax(opt.right.max) : 1;
  const yL = v => T + (H - T - B) * (1 - Math.min(v, lmax) / lmax), yR = v => T + (H - T - B) * (1 - Math.min(v, rmax) / rmax);
  let g = "";
  for (let k = 0; k <= 4; k++) {
    const v = lmax * k / 4;
    g += `<line x1="${L}" x2="${W - R}" y1="${yL(v)}" y2="${yL(v)}" stroke="${COL.grid}"/><text x="${L - 6}" y="${yL(v) + 4}" fill="${COL.faint}" font-size="10" text-anchor="end">${esc((opt.left.fmt || tickFmt)(v))}</text>`;
    if (opt.right) g += `<text x="${W - R + 6}" y="${yR(rmax * k / 4) + 4}" fill="${opt.right.color || COL.warn}" font-size="10">${esc((opt.right.fmt || tickFmt)(rmax * k / 4))}</text>`;
  }
  (opt.labels || []).forEach((t, k) => { g += `<text x="${[L, (L + W - R) / 2, W - R][k]}" y="${H - 6}" fill="${COL.faint}" font-size="10" text-anchor="${["start", "middle", "end"][k]}">${esc(t)}</text>`; });
  (opt.bars || []).forEach(s => s.d.forEach((v, i) => { if (v > 0) g += `<rect x="${x(i) - 1.5}" y="${T}" width="3" height="${H - T - B}" fill="${s.c}" fill-opacity="${Math.min(0.15 + v * 0.1, 0.5)}"/>`; }));
  for (const s of opt.series) {
    const y = s.right ? yR : yL, p = linePath(s.d, x, y);
    if (!p) continue;
    if (s.area) { const first = s.d.findIndex(v => v != null), last = s.d.length - 1 - [...s.d].reverse().findIndex(v => v != null); g += `<path d="${p} L${x(last)} ${y(0)} L${x(first)} ${y(0)} Z" fill="${s.c}" fill-opacity=".13"/>`; }
    g += `<path d="${p}" fill="none" stroke="${s.c}" stroke-width="1.6" ${s.dash ? 'stroke-dasharray="4 3"' : ""}/>`;
    const li = s.d.length - 1; if (s.d[li] != null) g += `<circle cx="${x(li)}" cy="${y(s.d[li]).toFixed(1)}" r="3" fill="${s.c}"/>`;
  }
  svg.innerHTML = g;
}

function spark(svg, arr, color) {
  const h = arr.map(v => v == null ? null : +v), vals = h.filter(v => v != null);
  if (vals.length < 2) { svg.innerHTML = ""; return; }
  const lo = Math.min(...vals), hi = Math.max(...vals), r = (hi - lo) || Math.max(hi, 1);
  const x = i => i * 120 / (h.length - 1), y = v => 28 - (v - lo) / r * 24;
  const p = linePath(h, x, y);
  svg.innerHTML = `<path d="${p} L120 30 L0 30 Z" fill="${color}" fill-opacity=".14"/><path d="${p}" fill="none" stroke="${color}" stroke-width="1.4" vector-effect="non-scaling-stroke"/>` +
    (h[h.length - 1] != null ? `<circle cx="118.5" cy="${y(h[h.length - 1]).toFixed(1)}" r="2" fill="${color}"/>` : "");
}

// ------------------------------------------------------------------ sign in
function showLogin() {
  $("#app").hidden = true; $("#login").hidden = false;
  fetch("/api/hello").then(r => r.json()).then(h => { $("#loginServer").textContent = `Server admin · ${h.server}`; }).catch(() => {});
  setTimeout(() => $("#lgUser").focus(), 50);
}
$("#loginForm").addEventListener("submit", async e => {
  e.preventDefault(); $("#lgErr").hidden = true; $("#lgBtn").disabled = true;
  try {
    await api("/api/login", { method: "POST", body: { user: $("#lgUser").value, password: $("#lgPass").value }, noAuthRedirect: true });
    $("#lgPass").value = ""; start();
  } catch (err) { $("#lgErr").textContent = err.message; $("#lgErr").hidden = false; }
  finally { $("#lgBtn").disabled = false; }
});
$("#logout").addEventListener("click", async () => { try { await post("/api/logout"); } catch { /* */ } location.href = "/"; });

// ------------------------------------------------------------------ navigation
function go(p, push = true) {
  page = p;
  $$("section[data-page]").forEach(s => s.hidden = s.dataset.page !== p);
  $$("nav a[data-page]").forEach(a => a.dataset.page === p ? a.setAttribute("aria-current", "page") : a.removeAttribute("aria-current"));
  if (push) history.pushState({}, "", "/" + p);
  $("#main").scrollTop = 0;
  if (p === "users") loadUsers(true);
  if (p === "logins") loadHistory();
  render();
}
$$("nav a[data-page]").forEach(a => a.addEventListener("click", e => { e.preventDefault(); go(a.dataset.page); }));
window.addEventListener("popstate", () => go(location.pathname.slice(1) || "overview", false));
$("#range").addEventListener("change", e => { range = e.target.value; hist = null; histAt = 0; loadHist().then(render); });

document.addEventListener("click", e => {
  const b = e.target.closest("[data-act]"); if (!b) return;
  const a = b.dataset.act, n = b.dataset.name;
  if (a === "add-server") dlgAddServer();
  else if (a === "promote") dlgPromote(n);
  else if (a === "rejoin") dlgRejoin(n);
  else if (a === "remove-server") dlgRemoveServer(n);
  else if (a === "logs") dlgLogs(n);
  else if (a === "new-key") dlgAddServer(n, b.dataset.addr);
  else if (a === "add-user") dlgAddUser();
  else if (a === "u-access") dlgAccess(n, b.dataset.access);
  else if (a === "u-pass") dlgPassword(n);
  else if (a === "u-enable") userEnable(n, b.dataset.on === "1");
  else if (a === "u-remove") dlgRemoveUser(n);
  else if (a === "backup") runBackup(b);
  else if (a === "oxidized") dlgOxidized();
});
document.addEventListener("click", e => { const b = e.target.closest("[data-copy]"); if (b) copyText($("#" + b.dataset.copy).textContent, b); });

// ------------------------------------------------------------------ polling
async function tick() {
  try {
    live = await api("/api/live"); lastOk = Date.now();
    if (range !== "2m" && Date.now() - histAt > 30000) await loadHist();
    render();
  } catch (e) { if (!$("#app").hidden) staleBadge(); }
}
async function loadHist() { if (range === "2m") return; try { hist = await api("/api/history?range=" + range); histAt = Date.now(); } catch { /* keep */ } }
function staleBadge() {
  const s = (Date.now() - lastOk) / 1000, b = $("#liveBadge");
  b.classList.toggle("stale", s > 5); b.lastElementChild.textContent = s > 5 ? "Reconnecting" : "Live";
}
async function start() {
  $("#login").hidden = true; $("#app").hidden = false;
  go(location.pathname.slice(1) || "overview", false);
  await tick();
  if (!window._timer) window._timer = setInterval(() => { tick(); staleBadge(); }, 1000);
}

// ------------------------------------------------------------------ derived data
const byName = () => Object.fromEntries((live?.servers || []).map(s => [s.name, s]));
const primary = () => (live?.servers || []).find(s => s.role === "primary");
function seriesOf(name, key) {
  if (range === "2m") return (live.series[name] || []).map(p => p[key]);
  const h = hist?.[name] || []; const map = { lat: null };
  return h.map(p => key in map ? map[key] : p[key]);
}
function labels() { return range === "2m" ? ["2 min ago", "1 min", "now"] : range === "1h" ? ["1 hour ago", "30 min", "now"] : ["24 hours ago", "12 h", "now"]; }

// ------------------------------------------------------------------ render
function render() {
  if (!live) return;
  $("#navServer").textContent = "Server admin · " + live.me.server;
  $("#meName").textContent = live.me.login; $("#meAv").textContent = initials(live.me.login);
  $("#cntServers").textContent = live.servers.length;
  $("#updated").textContent = "Updated " + clock(live.now);
  if (page === "overview") renderOverview();
  if (page === "servers") renderServers();
  if (page === "users") { if (Date.now() - usersAt > 15000) loadUsers(); }
  updateDialogs();
}

function renderOverview() {
  const P = primary(), S = live.servers, settings = live.settings;
  // banner
  const bad = live.health.filter(h => h.level === "bad"), warn = live.health.filter(h => h.level === "warn");
  const standbys = S.filter(s => s.role === "standby");
  let level = bad.length ? "bad" : (warn.length && !P ? "warn" : ""), big = bad.length ? "Needs attention" : "Healthy";
  let meta = P ? `<b>${esc(P.name)}</b> is primary` : "No server is primary right now";
  if (standbys.length) meta += ", " + standbys.map(s => `<b>${esc(s.name)}</b> is ${s.repl ? fmtNum(s.repl.lag, 1) + " s" : "?"} behind`).join(", ");
  meta += settings.auto_failover === "on" ? ". Automatic failover is on." : ". Automatic failover is off.";
  if (bad.length) meta = esc(bad.map(b => b.title + ": " + b.detail).join(" · "));
  const okCount = live.health.filter(h => h.level === "ok").length;
  const relayUp = S.some(s => s.relay?.up);
  $("#banner").className = "banner " + level;
  $("#banner").innerHTML = `<span class="dot" style="color:${bad.length ? COL.bad : COL.ok}"></span><span class="big">${big}</span><span class="meta">${meta}</span><span class="right">${live.people.length} ${live.people.length === 1 ? "person" : "people"} connected · relay ${relayUp ? "running" : "off"} · ${okCount} of ${live.health.length} checks OK</span>`;

  // relay rate (client side, from byte counters)
  let relayBw = 0, relayHosts = 0, relayGuests = 0;
  for (const s of S) {
    const r = s.relay; if (!r?.up) continue;
    relayHosts += r.hosts || 0; relayGuests += r.guests || 0;
    const p = relayPrev[s.name]; if (p && r.bytes >= p.bytes && live.now > p.t) relayBw += (r.bytes - p.bytes) / (live.now - p.t);
    if (!p || p.bytes !== r.bytes || live.now - p.t > 20) relayPrev[s.name] = { bytes: r.bytes, t: live.now };
  }
  relaySeries.push(relayBw); while (relaySeries.length > 120) relaySeries.shift();

  // tiles
  const pn = P?.name;
  const pm = P?.m || {};
  const lagSeries = (live.series[pn] || []).map((_, i) => { let m = null; for (const s of standbys) { const v = (live.series[s.name] || [])[i]?.lag; if (v != null) m = Math.max(m ?? 0, v); } return m; });
  const maxLag = standbys.length ? Math.max(...standbys.map(s => s.repl?.lag ?? 0)) : null;
  const tiles = [
    ["Transactions/s", pm.tps, v => fmtNum(v, v < 10 ? 1 : 0), seriesOf(pn, "tps"), COL.accent],
    ["Rows read/s", pm.reads, v => fmtNum(v), seriesOf(pn, "reads"), COL.accent],
    ["Connections", pm.conns, v => fmtNum(v), seriesOf(pn, "conns"), COL.blue],
    ["Standby behind", maxLag, v => v == null ? "no standby" : fmtNum(v, 2) + " s", range === "2m" ? lagSeries : [], COL.warn],
    ["Changes sent", pm.wal, v => fmtRate(v), seriesOf(pn, "wal"), COL.admin],
    ["Cache hit rate", pm.cache, v => fmtNum(v, 1) + " %", seriesOf(pn, "cache"), COL.ok],
    ["Relay traffic", relayBw, v => fmtRate(v), relaySeries, COL.blue],
    ["Sign-ins today", live.signins.today_ok, v => fmtNum(v), live.signins.hours.map(h => h.ok), COL.ok],
  ];
  const tEl = $("#tiles");
  if (tEl.children.length !== tiles.length) tEl.innerHTML = tiles.map(() => `<div class="tile"><span class="tl"></span><b class="tv num"></b><span class="td num"></span><svg viewBox="0 0 120 30" preserveAspectRatio="none" aria-hidden="true"></svg></div>`).join("");
  tiles.forEach(([label, v, fmt, arr, c], i) => {
    const el = tEl.children[i];
    el.querySelector(".tl").textContent = label;
    el.querySelector(".tv").textContent = v == null && label !== "Standby behind" ? "–" : fmt(v);
    const td = el.querySelector(".td"), vals = (arr || []).filter(x => x != null);
    if (label === "Sign-ins today") { td.textContent = `${live.signins.today_failed} failed`; td.className = "td num" + (live.signins.today_failed ? " down" : ""); }
    else if (vals.length > 11 && range === "2m") {
      const prev = vals[vals.length - 11], cur = vals[vals.length - 1], pct = prev ? (cur - prev) / Math.abs(prev) * 100 : 0;
      td.textContent = (pct >= 0 ? "▲ " : "▼ ") + Math.abs(pct).toFixed(1) + "% vs 10 s ago"; td.className = "td num " + (Math.abs(pct) < 1 ? "" : pct > 0 ? "up" : "down");
    } else td.textContent = "";
    spark(el.querySelector("svg"), arr || [], c);
  });

  // server cards
  $("#srvCards").innerHTML = S.map(s => serverCard(s, P)).join("");

  // latency chart
  const lat = S.map((s, i) => ({ d: range === "2m" ? seriesOf(s.name, "lat") : [], c: SERVER_COLORS[i % SERVER_COLORS.length], name: s.name }));
  const lags = standbys.map(s => ({ d: seriesOf(s.name, "lag"), c: COL.warn, right: true, area: true, name: s.name }));
  const n = Math.max(...[...lat, ...lags].map(s => s.d.length), 2);
  const pad = d => Array(Math.max(0, n - d.length)).fill(null).concat(d);
  const allLat = lat.flatMap(s => s.d).filter(v => v != null), allLag = lags.flatMap(s => s.d).filter(v => v != null);
  chart($("#chLat"), { n, series: [...lags.map(s => ({ ...s, d: pad(s.d) })), ...lat.map(s => ({ ...s, d: pad(s.d) }))], left: { max: Math.max(1, ...allLat) * 1.15, fmt: v => tickFmt(v) + " ms" }, right: { max: Math.max(0.5, ...allLag) * 1.15, fmt: v => tickFmt(v) + " s", color: COL.warn }, labels: labels() });
  $("#lgLat").innerHTML = lat.map(s => `<span><i style="background:${s.c}"></i>${esc(s.name)} latency</span>`).join("") + (lags.length ? `<span><i style="background:${COL.warn}"></i>standby behind (s, right axis)</span>` : "");
  $("#latNote").textContent = range === "2m" ? "Every second, measured from " + live.me.server : "Replication delay, per minute";

  // activity chart
  const rd = seriesOf(pn, "reads"), wr = seriesOf(pn, "writes"), lk = seriesOf(pn, "locks");
  chart($("#chAct"), { n: Math.max(rd.length, 2), bars: [{ d: lk, c: COL.bad }], series: [{ d: rd, c: COL.accent, area: true }, { d: wr, c: COL.admin, area: true, right: true }], left: { max: Math.max(10, ...rd.filter(v => v != null)) * 1.1 }, right: { max: Math.max(5, ...wr.filter(v => v != null)) * 1.1, color: COL.admin }, labels: labels() });
  $("#actNote").textContent = P ? "on the primary, " + P.name : "no primary";

  // people
  $("#peopleNote").textContent = `${live.people.length} ${live.people.length === 1 ? "person" : "people"} · ${live.people.reduce((a, p) => a + p.conns, 0)} connections`;
  $("#people").innerHTML = live.people.length ? live.people.map(p => `<div class="person">${av(p.user)}<div class="pinfo"><div><b>${esc(p.user)}</b> ${accessPill(p.access)}</div><small class="act">${esc(p.activity?.[0] || (p.machines?.length ? "On " + p.machines.join(", ") : "Connected"))}</small></div><div class="pmeta"><span class="num">${fmtDur(live.now - p.since)}</span><small class="mono">${esc([...new Set(p.addrs)].join(", "))}</small></div></div>`).join("") : `<p class="empty">Nobody is connected to the database right now.</p>`;

  // feed
  const feedHtml = live.feed.map(f => {
    const key = f.ts + f.who + f.text; const isNew = feedPrimed && !seenFeed.has(key); seenFeed.add(key);
    const color = { login: COL.ok, failed: COL.bad, entry: COL.accent, failover: COL.warn, warning: COL.warn, server: COL.blue, backup: COL.faint, user: COL.admin }[f.kind] || COL.faint;
    return `<div class="fe${isNew ? " new" : ""}"><span class="dot" style="color:${color}"></span><span>${f.who ? `<b>${esc(f.who)}</b> ` : ""}${esc(f.text)}${f.subject ? ` <b>${esc(f.subject)}</b>` : ""}${f.detail ? ` <span class="faint">from ${esc(f.detail)}</span>` : ""}</span><span class="t num">${fmtWhen(f.ts).replace("Today ", "")}</span></div>`;
  }).join("");
  feedPrimed = true;
  $("#feed").innerHTML = feedHtml || `<p class="empty">Nothing yet. Sign-ins, changes to entries and server events show up here.</p>`;

  // sign-ins
  const sg = live.signins;
  $("#signNote").innerHTML = `<b class="num" style="color:var(--strong)">${sg.day_ok}</b> OK · <b class="num badc">${sg.day_failed}</b> failed`;
  { const W = 360, H = 120, L = 24, B = 18, T = 6, bw = (W - L) / 24, mx = niceMax(Math.max(3, ...sg.hours.map(h => h.ok + h.failed))), y = v => (H - B - T) * v / mx;
    let g = ""; for (let k = 0; k <= 3; k++) { const v = mx * k / 3; g += `<line x1="${L}" x2="${W}" y1="${H - B - y(v)}" y2="${H - B - y(v)}" stroke="${COL.grid}"/><text x="${L - 5}" y="${H - B - y(v) + 3}" fill="${COL.faint}" font-size="9" text-anchor="end">${tickFmt(v)}</text>`; }
    sg.hours.forEach((h, i) => { const x = L + i * bw + 1.5; if (h.ok) g += `<rect x="${x}" y="${H - B - y(h.ok)}" width="${bw - 3}" height="${y(h.ok)}" fill="${COL.ok}" fill-opacity=".75" rx="1"><title>${h.ok} sign-ins</title></rect>`; if (h.failed) g += `<rect x="${x}" y="${H - B - y(h.ok) - y(h.failed)}" width="${bw - 3}" height="${y(h.failed)}" fill="${COL.bad}" rx="1"><title>${h.failed} failed</title></rect>`; });
    const nowH = new Date().getHours(); [0, 6, 12, 18].forEach(k => { const hr = (nowH - 23 + k + 24) % 24; g += `<text x="${L + k * bw + bw / 2}" y="${H - 5}" fill="${COL.faint}" font-size="9" text-anchor="middle">${String(hr).padStart(2, "0")}</text>`; });
    g += `<text x="${W}" y="${H - 5}" fill="${COL.faint}" font-size="9" text-anchor="end">now</text>`;
    $("#chSign").innerHTML = g; }

  // size
  const sh = live.size_hist || [];
  $("#sizeNote").innerHTML = live.dbsize ? `<b class="num" style="color:var(--strong)">${fmtBytes(live.dbsize)}</b>` + (sh.length > 1 ? ` · ${(live.dbsize - sh[0][1]) >= 0 ? "+" : ""}${fmtBytes(live.dbsize - sh[0][1])} since ${esc(sh[0][0].slice(5))}` : "") : "";
  if (sh.length) {
    const W = 360, H = 120, L = 46, B = 18, T = 6, vals = sh.map(r => r[1]), lo = Math.min(...vals) * 0.98, hi = Math.max(...vals) * 1.02 || 1;
    const x = i => L + (W - L) * (sh.length === 1 ? 1 : i / (sh.length - 1)), y = v => T + (H - B - T) * (1 - (v - lo) / (hi - lo || 1));
    let g = ""; [lo, (lo + hi) / 2, hi].forEach(v => { g += `<line x1="${L}" x2="${W}" y1="${y(v)}" y2="${y(v)}" stroke="${COL.grid}"/><text x="${L - 5}" y="${y(v) + 3}" fill="${COL.faint}" font-size="9" text-anchor="end">${fmtBytes(v, 1)}</text>`; });
    if (sh.length > 1) { const p = linePath(vals, x, y); g += `<path d="${p} L${x(sh.length - 1)} ${H - B} L${x(0)} ${H - B} Z" fill="${COL.accent}" fill-opacity=".14"/><path d="${p}" fill="none" stroke="${COL.accent}" stroke-width="1.6"/>`; }
    g += `<circle cx="${x(sh.length - 1)}" cy="${y(vals[vals.length - 1])}" r="3" fill="${COL.accent}"/><text x="${L}" y="${H - 5}" fill="${COL.faint}" font-size="9">${esc(sh[0][0])}</text><text x="${W}" y="${H - 5}" fill="${COL.faint}" font-size="9" text-anchor="end">today</text>`;
    $("#chSize").innerHTML = g;
  }
  const tbl = (live.tables || []).slice(0, 6), tmax = Math.max(1, ...tbl.map(t => t.bytes));
  const pretty = n => ({ remoteman_entries: "Entries", remoteman_presence: "Who's where", remoteman_chat_messages: "Team chat", remoteman_chat_rooms: "Chat rooms", remoteman_shares: "Shared sessions", remoteman_meta: "Counters", remoteman_chat_reads: "Chat read marks" }[n] || n);
  $("#tbls").innerHTML = tbl.map(t => `<div class="tb"><span title="${esc(t.name)}">${esc(pretty(t.name))}<small>${fmtNum(t.rows)} rows</small></span><div class="bar"><i style="width:${(t.bytes / tmax * 100).toFixed(1)}%"></i></div><b class="num">${fmtBytes(t.bytes)}</b></div>`).join("") || `<p class="empty">RemoteMan hasn't created its tables yet.</p>`;

  // relay
  const relayOn = S.filter(s => s.relay?.up).map(s => s.name);
  $("#relayNote").textContent = relayOn.length ? relayOn.join(", ") + " · port 47120" : "not running";
  $("#rlHosts").textContent = relayHosts; $("#rlGuests").textContent = relayGuests; $("#rlBw").textContent = fmtRate(relayBw);
  { const h = relaySeries, mx = niceMax(Math.max(1024, ...h)), x = i => i * 360 / Math.max(h.length - 1, 1), y = v => 58 - v / mx * 54, p = linePath(h, x, y);
    $("#chRelay").innerHTML = h.length > 1 ? `<path d="${p} L${x(h.length - 1)} 60 L0 60 Z" fill="${COL.blue}" fill-opacity=".16"/><path d="${p}" fill="none" stroke="${COL.blue}" stroke-width="1.5"/>` : ""; }
  $("#shares").innerHTML = live.shares.length ? live.shares.map(s => `<div class="minirow"><span class="dot on"></span>${esc(s.host)} shares <b>${esc(s.entry || "a session")}</b><span class="t">${esc(s.guests.join(", ") || "waiting")}${s.pending.length ? ` · invited: ${esc(s.pending.join(", "))}` : ""}</span></div>`).join("") : `<p class="empty">No shared sessions right now.</p>`;

  // health
  $("#checks").innerHTML = live.health.map(h => `<div><span class="dot" style="color:${{ ok: COL.ok, warn: COL.warn, bad: COL.bad }[h.level]}"></span>${esc(h.title)}<small>${esc(h.detail)}</small></div>`).join("");
}

function serverCard(s, P) {
  const m = s.m || {}, isP = s.role === "primary", down = !s.reachable || s.status === "stopped";
  const pill = down ? `<span class="pill down"><span class="dot"></span>${s.state === "pending" ? "Waiting to join" : "Unreachable"}</span>` : isP ? `<span class="pill primary"><span class="dot"></span>Primary</span>` : s.role === "standby" ? `<span class="pill standby"><span class="dot"></span>Standby</span>` : `<span class="pill pending">${esc(s.status || s.state)}</span>`;
  if (s.state === "pending" || s.state === "expired") {
    return `<div class="card"><div class="srvhead"><span class="name">${esc(s.name)}</span><span class="addr">${esc(s.address || "address not known yet")}</span><span class="pill pending">${s.state === "expired" ? "Join key expired" : "Waiting to join"}</span>
      <span class="right"><button class="btn sm" data-act="new-key" data-name="${esc(s.name)}" data-addr="${esc(s.address)}">New join key</button><button class="btn sm danger" data-act="remove-server" data-name="${esc(s.name)}">Remove</button></span></div>
      <p class="muted">${s.state === "expired" ? "The join key ran out before the server connected. Make a new one." : `Start the container on ${esc(s.name)} with the join key. It's valid until ${new Date(s.key_expires * 1000).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}.`}</p></div>`;
  }
  const up = s.pg_started ? `<span class="uptime num">Up ${fmtDur(live.now - s.pg_started, true)}</span>` : "";
  const btns = `${!isP && !down ? `<button class="btn sm" data-act="promote" data-name="${esc(s.name)}">Make primary</button>` : ""}<button class="btn sm" data-act="logs" data-name="${esc(s.name)}" ${down ? "disabled" : ""}>Logs</button>`;
  if (down) {
    return `<div class="card downcard"><div class="srvhead"><span class="name">${esc(s.name)}</span><span class="addr">${esc(s.address)}:${s.pg_port}</span>${pill}<span class="right">${s.seen ? `<span class="uptime">Last seen ${fmtAgo(s.seen)}</span>` : ""}</span></div>
      <p class="downnote">${esc(s.name)} doesn't answer on ${esc(s.address)}:${s.admin_port}. ${s.last?.role === "primary" ? (live.settings.auto_failover === "on" ? `A standby takes over after ${esc(live.settings.failover_seconds)} s.` : "Automatic failover is off: make a standby the primary.") : "The others carry on without it, and it catches up when it's back."}</p></div>`;
  }
  const pct = (a, b) => b ? Math.min(100, a / b * 100) : 0, cls = p => p > 85 ? "hot" : p > 65 ? "warm" : "";
  const meter = (label, p, text) => `<div class="meter"><span>${label}</span><div class="bar"><i class="${cls(p)}" style="width:${p.toFixed(1)}%"></i></div><b class="num">${text}</b></div>`;
  const kv = isP ? [["Latency", fmtNum(s.lat, 1), "ms"], ["Connections", fmtNum(m.conns), "/ " + (m.max_conns || "?")], ["Transactions/s", fmtNum(m.tps, m.tps < 10 ? 1 : 0), ""], ["Cache hits", fmtNum(m.cache, 1), "%"]]
    : [["Latency", fmtNum(s.lat, 1), "ms"], ["Behind by", s.repl ? fmtNum(s.repl.lag, 1) : "?", "s"], ["Unapplied", s.repl ? fmtBytes(s.repl.lagb).split(" ")[0] : "?", s.repl ? fmtBytes(s.repl.lagb).split(" ")[1] : ""], ["Reads/s", fmtNum(m.reads), ""]];
  const relay = s.relay?.up ? `<span class="ctr ok">relay</span>` : "";
  const standbys = live.servers.filter(x => x.role === "standby").map(x => x.name);
  const flow = isP
    ? `<div class="flow"><span>Sending changes</span><span class="line${standbys.length ? "" : " idle"}"></span><span class="num">${standbys.length ? fmtRate(m.wal || 0) + " to " + esc(standbys.join(", ")) : "no standby yet"}</span></div>`
    : `<div class="flow"><span class="${s.receiver === "streaming" ? "on" : "warnc"}">● ${s.receiver === "streaming" ? "Streaming" : esc(s.receiver || "not streaming")} from ${esc(P?.name || "?")}</span><span style="margin-left:auto">${live.settings.auto_failover === "on" ? `Takes over ${esc(live.settings.failover_seconds)} s after the primary stops answering` : "Automatic failover is off"}</span></div>`;
  return `<div class="card ${isP ? "primarycard" : ""}">
    <div class="srvhead"><span class="name">${esc(s.name)}</span><span class="addr">${esc(s.address)}:${s.pg_port}</span>${pill}${s.here ? `<span class="tag">this page</span>` : ""}<span class="right">${up}${btns}</span></div>
    <div class="kv">${kv.map(([k, v, u]) => `<div><span>${k}</span><b class="num">${v} <small>${u}</small></b></div>`).join("")}</div>
    <div class="meters">
      ${meter("CPU", m.cpu || 0, fmtNum(m.cpu, 0) + "% of " + (m.cores || "?") + " cores")}
      ${meter("Memory", pct(m.mem_used, m.mem_total), fmtBytes(m.mem_used) + " / " + fmtBytes(m.mem_total, 0))}
      ${meter("Disk", pct(m.disk_used, m.disk_total), fmtBytes(m.disk_used) + " / " + fmtBytes(m.disk_total, 0))}
      <div class="meter"><span>Network</span><div class="net num"><span>↓ ${fmtRate(m.rx)}</span><span>↑ ${fmtRate(m.tx)}</span></div><b class="num">${fmtNum(m.iops)} IOPS</b></div>
    </div>
    <div class="chips"><span class="ctr ok">postgres</span><span class="ctr ok">admin</span>${relay}<span class="tag">PostgreSQL ${esc((s.pg_version || "?").split(" ")[0])}</span><span class="tag">image ${esc(s.version || "?")}</span><span class="tag">timeline ${esc(s.timeline ?? "?")}</span>${!s.here ? `<span class="tag">clock ${fmtNum(Math.abs(s.offset_ms || 0))} ms off</span>` : ""}${s.dbsize ? `<span class="tag">${fmtBytes(s.dbsize)}</span>` : ""}</div>
    ${flow}</div>`;
}

function renderServers() {
  const P = primary();
  $("#srvRows").innerHTML = live.servers.map(s => {
    const m = s.m || {}, pend = s.state === "pending" || s.state === "expired", down = !pend && (!s.reachable || s.status === "stopped");
    const role = pend ? `<span class="pill pending">${s.state === "expired" ? "Key expired" : "Waiting to join"}</span>` : down ? `<span class="pill down">Unreachable</span>` : s.role === "primary" ? `<span class="pill primary"><span class="dot"></span>Primary</span>` : `<span class="pill standby"><span class="dot"></span>Standby</span>`;
    const state = pend ? `<span class="faint">key ${s.state === "expired" ? "expired" : "valid until " + new Date(s.key_expires * 1000).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span>` : down ? `<span class="badc">● Not answering</span>` : s.role === "standby" ? (s.receiver === "streaming" ? `<span class="on">● Streaming</span>` : `<span class="warnc">● ${esc(s.receiver || s.status)}</span>`) : `<span class="on">● ${esc(s.status === "running" ? "Running" : s.status)}</span>`;
    const bar = (p, t) => `<span class="bar"><i class="${p > 85 ? "hot" : p > 65 ? "warm" : ""}" style="width:${(p || 0).toFixed(1)}%"></i></span><b class="num" style="font-weight:500">${t}</b>`;
    const cells = pend || down ? `<td></td><td></td><td></td><td></td><td></td>` :
      `<td>${bar(m.cpu, fmtNum(m.cpu) + "%")}</td><td>${bar(m.mem_total ? m.mem_used / m.mem_total * 100 : 0, fmtBytes(m.mem_used))}</td><td>${bar(m.disk_total ? m.disk_used / m.disk_total * 100 : 0, fmtBytes(m.disk_used))}</td><td class="num">${fmtNum(s.lat, 1)} ms</td><td class="num">${s.role === "standby" ? (s.repl ? fmtNum(s.repl.lag, 1) + " s · " + fmtBytes(s.repl.lagb) : "?") : '<span class="faint">n/a</span>'}</td>`;
    const acts = pend ? `<button class="btn sm" data-act="new-key" data-name="${esc(s.name)}" data-addr="${esc(s.address)}">New join key</button><button class="btn sm danger" data-act="remove-server" data-name="${esc(s.name)}">Remove</button>`
      : `<button class="btn sm" data-act="logs" data-name="${esc(s.name)}" ${down ? "disabled" : ""}>Logs</button>${s.role === "standby" && !down ? `<button class="btn sm" data-act="promote" data-name="${esc(s.name)}">Make primary</button><button class="btn sm" data-act="rejoin" data-name="${esc(s.name)}">Rejoin</button>` : ""}${s.role === "primary" && P && primary() && live.servers.filter(x => x.role === "primary").length > 1 ? `<button class="btn sm" data-act="rejoin" data-name="${esc(s.name)}">Make standby</button>` : ""}${s.role !== "primary" ? `<button class="btn sm danger" data-act="remove-server" data-name="${esc(s.name)}">Remove</button>` : ""}`;
    return `<tr><td><b>${esc(s.name)}</b> <span class="mono faint">${esc(s.address)}:${s.pg_port}</span>${s.here ? ' <span class="tag">this page</span>' : ""}</td><td>${role}</td><td>${state}</td>${cells}<td class="num">${esc((s.pg_version || s.last?.pg_version || "").split(" ")[0])}</td><td class="mono">${esc(s.version || "")}</td><td class="acts">${acts}</td></tr>`;
  }).join("");
  // failover settings (only re-render when not being edited)
  const f = $("#failover");
  if (!f.contains(document.activeElement)) {
    const st = live.settings;
    f.innerHTML = `
      <div class="setting"><div><b>Automatic failover</b><p>A standby takes over when the primary has been gone this long.</p></div><div class="ctl"><select class="f" id="foDelay" aria-label="Delay">${[15, 30, 60, 120, 300].map(v => `<option value="${v}" ${String(v) === st.failover_seconds ? "selected" : ""}>${v < 60 ? v + " s" : v / 60 + " min"}</option>`).join("")}${[15, 30, 60, 120, 300].includes(+st.failover_seconds) ? "" : `<option selected value="${esc(st.failover_seconds)}">${esc(st.failover_seconds)} s</option>`}</select><button class="toggle" id="foOn" role="switch" aria-checked="${st.auto_failover === "on"}" aria-label="Automatic failover"></button></div></div>
      <div class="setting"><div><b>Witness</b><p>A machine a standby must still reach before it takes over, so a standby that lost its own network stays put. "auto" uses the default gateway; "off" turns the check off. Use host or host:port.</p></div><div class="ctl"><input class="f" id="foWitness" value="${esc(st.witness)}" style="width:130px" aria-label="Witness address"></div></div>
      <div class="setting"><div><p>With three or more servers, a standby only takes over when it can see most of them.</p></div><div class="ctl"><button class="btn sm primary" id="foSave">Save</button></div></div>`;
    $("#foOn").onclick = e => e.currentTarget.setAttribute("aria-checked", e.currentTarget.getAttribute("aria-checked") !== "true");
    $("#foSave").onclick = async () => {
      try { await post("/api/settings", { auto_failover: $("#foOn").getAttribute("aria-checked") === "true" ? "on" : "off", failover_seconds: $("#foDelay").value, witness: $("#foWitness").value.trim() || "off" }); toast("Failover settings saved."); }
      catch (e) { toast(e.message, true); }
    };
  }
  const hosts = live.servers.filter(s => s.state === "joined").map(s => `${s.address}:${s.pg_port}`).join(",");
  $("#connStr").textContent = `Host=${hosts};Database=remoteman;Username=<login>;Password=<password>;Target Session Attributes=primary`;
  const b = P?.backup || {};
  $("#backups").innerHTML = `<div class="setting"><div><b>${b.ts ? "Last backup " + fmtWhen(b.ts) : "No backup yet"}</b><p>${b.ts ? `${esc(b.file)}, ${fmtBytes(b.size)}. ${b.count} kept on ${esc(P.name)} in /var/lib/remoteman/backups.` : `Runs every night at ${esc(b.at || "03:00")} on the primary.`}${b.error ? ` <span class="badc">Last attempt failed.</span>` : ""}</p></div><div class="ctl"><button class="btn sm" data-act="backup" ${P ? "" : "disabled"}>Back up now</button></div></div>`;
  $("#relays").innerHTML = live.servers.filter(s => s.reachable).map(s => `<div class="setting"><div><b class="${s.relay?.up ? "on" : "off"}">● ${s.relay?.up ? "Running" : "Not running"} on ${esc(s.name)}</b><p>${s.relay?.up ? `Port 47120. ${s.relay.hosts} share${s.relay.hosts === 1 ? "" : "s"}, ${s.relay.guests} guest${s.relay.guests === 1 ? "" : "s"} now, ${fmtBytes(s.relay.bytes)} passed since ${fmtWhen(s.relay.up_since)}.` : "Start the relay service in this server's compose file to use it."}</p></div></div>`).join("");
}

// ------------------------------------------------------------------ users
async function loadUsers(force) {
  if (!force && Date.now() - usersAt < 15000) return;
  usersAt = Date.now();
  try { users = await api("/api/users"); renderUsers(); }
  catch (e) { $("#userRows").innerHTML = `<tr><td colspan="6" class="err">${esc(e.message)}</td></tr>`; }
}
$("#uSearch").addEventListener("input", () => renderUsers()); $("#uAccess").addEventListener("change", () => renderUsers());
function renderUsers() {
  if (!users) return;
  $("#cntUsers").textContent = users.length;
  const q = $("#uSearch").value.toLowerCase().trim(), a = $("#uAccess").value;
  const rows = users.filter(u => (!q || u.login.includes(q)) && (!a || u.access === a));
  $("#userRows").innerHTML = rows.map(u => {
    const me = u.login === live?.me.login, shared = u.access === "shared";
    const now = !u.enabled ? `<span class="off">Turned off</span>` : u.connections ? `<span class="on">● Connected</span> <span class="faint">${u.connections} connection${u.connections > 1 ? "s" : ""}</span>` : `<span class="faint">Not connected</span>`;
    const ch = u.last_change ? `${fmtWhen(u.last_change.ts)} · ${u.last_change.deleted ? "deleted" : "edited"} "${esc(u.last_change.entry)}"` : `<span class="faint">Never</span>`;
    const acts = [
      !shared && !me ? `<button class="btn sm" data-act="u-access" data-name="${esc(u.login)}" data-access="${esc(u.access)}">Access</button>` : "",
      `<button class="btn sm" data-act="u-pass" data-name="${esc(u.login)}">New password</button>`,
      !me ? `<button class="btn sm" data-act="u-enable" data-name="${esc(u.login)}" data-on="${u.enabled ? 0 : 1}">${u.enabled ? "Turn off" : "Turn on"}</button>` : "",
      !shared && !me ? `<button class="btn sm danger" data-act="u-remove" data-name="${esc(u.login)}">Remove</button>` : "",
    ].join("");
    return `<tr><td><div class="who">${av(u.login)}<span>${esc(u.login)}${shared ? "<small>shared login</small>" : me ? "<small>you</small>" : u.activity ? `<small>${esc(u.activity)}</small>` : ""}</span></div></td><td>${accessPill(u.access)}</td><td>${now}</td><td class="num">${u.last_login ? fmtWhen(u.last_login) + ` <span class="faint">${esc(u.last_from || "")}</span>` : '<span class="faint">Not seen</span>'}</td><td class="num">${ch}</td><td class="acts">${acts}</td></tr>`;
  }).join("") || `<tr><td colspan="6" class="empty">No logins match.</td></tr>`;
}

// ------------------------------------------------------------------ login history
let histTimer = null;
function histQuery() {
  const p = new URLSearchParams({ q: $("#hSearch").value, result: $("#hResult [aria-pressed=true]").dataset.v, server: $("#hServer").value, days: $("#hDays").value });
  $("#csv").href = "/api/logins?format=csv&" + p.toString();
  return p.toString();
}
async function loadHistory() {
  const sel = $("#hServer"), cur = sel.value;
  sel.innerHTML = `<option value="">All servers</option>` + (live?.servers || []).map(s => `<option ${s.name === cur ? "selected" : ""}>${esc(s.name)}</option>`).join("");
  try {
    const rows = await api("/api/logins?" + histQuery());
    $("#hCount").textContent = `${rows.length} ${rows.length === 1 ? "row" : "rows"}`;
    $("#histRows").innerHTML = rows.map(r => {
      const res = r.result === "ok" ? `<span class="on">● Signed in</span>` : `<span class="${r.reason === "Login turned off" ? "warnc" : "badc"}">● ${esc(r.reason || "Failed")}</span>`;
      const len = r.result !== "ok" ? '<span class="faint">n/a</span>' : r.ended ? fmtDur(r.duration ?? 0) : `<span class="on">Connected</span>`;
      return `<tr><td class="num">${fmtWhen(r.ts)}</td><td>${esc(r.user || "unknown")}</td><td>${res}</td><td>${esc(r.server)}</td><td class="mono">${esc(r.host || "local")}</td><td>${esc(r.app || "–")}</td><td class="num">${len}</td></tr>`;
    }).join("") || `<tr><td colspan="7" class="empty">No sign-ins in this period.</td></tr>`;
  } catch (e) { $("#histRows").innerHTML = `<tr><td colspan="7" class="err">${esc(e.message)}</td></tr>`; }
}
$("#hSearch").addEventListener("input", () => { clearTimeout(histTimer); histTimer = setTimeout(loadHistory, 250); });
["#hServer", "#hDays"].forEach(s => $(s).addEventListener("change", loadHistory));
$$("#hResult button").forEach(b => b.addEventListener("click", () => { $$("#hResult button").forEach(x => x.setAttribute("aria-pressed", x === b)); loadHistory(); }));
setInterval(() => { if (page === "logins" && !$("#app").hidden && document.activeElement !== $("#hSearch")) loadHistory(); }, 15000);

// ------------------------------------------------------------------ dialogs
const openDialogs = [];
function dialog({ title, body, actions = [], wide, onUpdate }) {
  const scrim = document.createElement("div"); scrim.className = "scrim";
  scrim.innerHTML = `<div class="dialog ${wide ? "wide" : ""}" role="dialog" aria-modal="true" aria-label="${esc(title)}"><header><h3>${esc(title)}</h3><button class="x" aria-label="Close">✕</button></header><div class="db">${body}</div><footer></footer></div>`;
  const d = { el: scrim, close() { scrim.remove(); const i = openDialogs.indexOf(d); if (i >= 0) openDialogs.splice(i, 1); }, onUpdate };
  const foot = scrim.querySelector("footer");
  d.setActions = acts => {
    foot.innerHTML = "";
    for (const a of acts) {
      if (a.hint) { const s = document.createElement("span"); s.className = "hint"; s.innerHTML = a.hint; foot.appendChild(s); continue; }
      const b = document.createElement("button"); b.className = "btn" + (a.primary ? " primary" : "") + (a.danger ? " danger" : ""); b.textContent = a.label;
      b.onclick = async () => { if (!a.run) return d.close(); b.disabled = true; try { await a.run(d, b); } finally { b.disabled = false; } };
      foot.appendChild(b);
    }
  };
  d.setActions(actions);
  scrim.querySelector(".x").onclick = () => d.close();
  scrim.addEventListener("mousedown", e => { if (e.target === scrim) d.close(); });
  scrim.addEventListener("keydown", e => { if (e.key === "Escape") d.close(); });
  $("#dialogs").appendChild(scrim); openDialogs.push(d);
  setTimeout(() => (scrim.querySelector("input,select") || scrim.querySelector("footer .btn.primary") || scrim.querySelector(".x")).focus(), 30);
  return d;
}
function updateDialogs() { for (const d of openDialogs) if (d.onUpdate) d.onUpdate(d); }
const errBox = (d, msg) => { let e = d.el.querySelector(".err.dlg"); if (!e) { e = document.createElement("p"); e.className = "err dlg"; d.el.querySelector(".db").appendChild(e); } e.textContent = msg; };

function dlgPromote(name) {
  const P = primary(), pr = P && P.reachable;
  const people = live.people.length;
  let mode = pr ? "switchover" : "force";
  const d = dialog({
    title: `Make ${name} the primary`,
    body: `<div class="choice ${mode === "switchover" ? "sel" : ""}" data-m="switchover" ${pr ? "" : 'style="opacity:.5;pointer-events:none"'}><span class="radio"></span><div><b>Switch over</b><p>${pr ? `${esc(P.name)} is healthy, so it finishes what it's writing, ${esc(name)} replays everything and takes over with nothing lost, and ${esc(P.name)} becomes a standby. RemoteMan shows offline for a few seconds.` : "Needs a running primary to hand over from."}</p></div></div>
      <div class="choice ${mode === "force" ? "sel" : ""}" data-m="force"><span class="radio"></span><div><b>Force failover</b><p>Use only when the primary is down or unreachable. ${esc(name)} takes over right away. Changes the old primary hadn't sent yet are lost. When it comes back it rewinds itself and joins as a standby.</p></div></div>
      ${people ? `<div class="callout">${people} ${people === 1 ? "person is" : "people are"} connected. RemoteMan reconnects to ${esc(name)} by itself.</div>` : ""}`,
    actions: [{ hint: "Takes about 10 seconds" }, { label: "Cancel" }, {
      label: `Make ${name} primary`, primary: true, run: async (d) => {
        d.setActions([{ hint: `<span class="waiting"><span class="spin"></span>${mode === "switchover" ? "Handing over" : "Taking over"}…</span>` }]);
        try { await post("/api/servers/promote", { name, mode }); d.close(); toast(`${name} is now the primary.`); }
        catch (e) { d.setActions([{ label: "Close" }]); errBox(d, e.message); }
      }
    }],
  });
  $$(".choice", d.el).forEach(c => c.onclick = () => { mode = c.dataset.m; $$(".choice", d.el).forEach(x => x.classList.toggle("sel", x === c)); });
}

function dlgRejoin(name) {
  dialog({
    title: `Rejoin ${name} as a standby`,
    body: `<p>${esc(name)} stops its database, rewinds to match the current primary (or copies it again if a rewind isn't possible) and starts as a standby. Use it when a server is stuck or was a primary that should now follow.</p>`,
    actions: [{ label: "Cancel" }, { label: "Rejoin", primary: true, run: async d => {
      d.setActions([{ hint: `<span class="waiting"><span class="spin"></span>Rejoining…</span>` }]);
      try { const r = await post("/api/servers/rejoin", { name }); d.close(); toast(`${name} rejoined (${r.result}).`); } catch (e) { d.setActions([{ label: "Close" }]); errBox(d, e.message); }
    } }],
  });
}

function dlgRemoveServer(name) {
  dialog({
    title: `Remove ${name}`,
    body: `<p>${esc(name)} leaves the cluster: the primary stops sending it changes and forgets its join key. If it's running, it stops its database. Its data stays in its Docker volume until you delete the container and volume.</p>`,
    actions: [{ label: "Cancel" }, { label: `Remove ${name}`, danger: true, run: async d => {
      try { await post("/api/servers/remove", { name }); d.close(); toast(`${name} was removed.`); } catch (e) { errBox(d, e.message); }
    } }],
  });
}

function dlgAddServer(name = "", addr = "") {
  const names = (live?.servers || []).map(s => s.name);
  let guess = name; if (!guess) { for (let i = 2; i < 20; i++) if (!names.includes("db" + i)) { guess = "db" + i; break; } }
  let joined = false, target = null;
  const d = dialog({
    title: "Add a server", wide: true,
    body: `<div class="row2"><div class="field"><label for="nsName">Name</label><input id="nsName" value="${esc(guess)}" maxlength="30"></div>
      <div class="field"><label for="nsAddr">Address RemoteMan and the other servers reach it on</label><input id="nsAddr" value="${esc(addr)}" placeholder="10.0.0.12 (found by itself if left empty)"></div></div>
      <div id="nsOut"></div>`,
    actions: [{ label: "Cancel" }, { label: "Make join key", primary: true, run: makeKey }],
    onUpdate(d) {
      if (!target || joined) return;
      const s = (live.servers || []).find(x => x.name === target);
      if (s && s.state === "joined" && s.reachable) {
        joined = true; const w = $(".waiting", d.el); if (w) { w.className = "waiting done"; w.innerHTML = `● ${esc(target)} joined and is streaming as a standby.`; }
        d.setActions([{ label: "Done", primary: true }]);
      }
    },
  });
  async function makeKey(d) {
    try {
      const r = await post("/api/servers/add", { name: $("#nsName", d.el).value, address: $("#nsAddr", d.el).value });
      target = r.name;
      $("#nsOut", d.el).innerHTML = `<div class="field"><label>On the new machine, save this as <span class="mono">compose.yaml</span> and run <span class="mono">docker compose up -d</span></label><pre class="code" id="nsCompose">${esc(r.compose)}</pre></div>
        <div class="waiting"><span class="spin"></span>Waiting for ${esc(r.name)} to connect. It copies the database and then shows up as a standby.</div>`;
      $("#nsName", d.el).disabled = true; $("#nsAddr", d.el).disabled = true;
      d.setActions([{ hint: "The join key works once, for 30 minutes" }, { label: "Copy", run: async (dd, b) => copyText(r.compose, b) }, { label: "Close" }]);
    } catch (e) { errBox(d, e.message); }
  }
}

function dlgLogs(name) {
  const d = dialog({ title: `${name}: recent log`, wide: true, body: `<p class="faint">Loading…</p>`, actions: [{ label: "Refresh", run: load }, { label: "Close", primary: true }] });
  async function load() {
    try {
      const rows = await api("/api/logs?server=" + encodeURIComponent(name));
      d.el.querySelector(".db").innerHTML = rows.length ? rows.map(r => `<div class="logrow"><span class="faint">${esc((r.ts || "").replace(" UTC", ""))}</span> <span class="lv ${esc(r.level)}">${esc(r.level || "")}</span> ${r.user ? `<span class="faint">${esc(r.user)}@${esc(r.host || "local")}</span> ` : ""}${esc(r.message)}${r.detail ? `\n    ${esc(r.detail)}` : ""}</div>`).join("") : `<p class="empty">Nothing logged yet.</p>`;
    } catch (e) { d.el.querySelector(".db").innerHTML = `<p class="err">${esc(e.message)}</p>`; }
  }
  load();
}

function credsBody(r, who) {
  return `<div class="field"><label>Password for ${esc(r.name)} (shown only now)</label><pre class="code" id="crPw">${esc(r.password)}</pre></div>
    <div class="field"><label>Give ${esc(who)} this connection string for RemoteMan's data source</label><pre class="code wrap" id="crConn">${esc(r.conn)}</pre></div>
    <p class="faint" style="font-size:12px">They also need the team vault passphrase to open saved passwords.</p>`;
}

function dlgAddUser() {
  const d = dialog({
    title: "Add a user",
    body: `<div class="row2"><div class="field"><label for="nuName">Login name</label><input id="nuName" placeholder="anna" maxlength="63" autocomplete="off"></div>
      <div class="field"><label for="nuAccess">Access</label><select id="nuAccess"><option value="edit">Edit</option><option value="read-only">Read-only</option><option value="admin">Admin</option></select></div></div>
      <div class="field"><label for="nuPw">Password (leave empty for a random one)</label><input id="nuPw" class="mono" autocomplete="new-password"></div>
      <p class="faint" style="font-size:12px">Lowercase letters, digits and _ only. Admins can also manage logins and open this page.</p>`,
    actions: [{ label: "Cancel" }, { label: "Add user", primary: true, run: async d => {
      try {
        const r = await post("/api/users/add", { name: $("#nuName", d.el).value.trim().toLowerCase(), access: $("#nuAccess", d.el).value, password: $("#nuPw", d.el).value });
        d.el.querySelector(".db").innerHTML = credsBody(r, r.name);
        d.setActions([{ label: "Copy connection string", run: async (dd, b) => copyText(r.conn, b) }, { label: "Done", primary: true }]);
        loadUsers(true);
      } catch (e) { errBox(d, e.message); }
    } }],
  });
}

function dlgAccess(name, current) {
  dialog({
    title: `Access for ${name}`,
    body: `<div class="field"><label for="acSel">Access</label><select id="acSel">${["read-only", "edit", "admin"].map(a => `<option value="${a}" ${a === current ? "selected" : ""}>${a[0].toUpperCase() + a.slice(1)}</option>`).join("")}</select></div>
      <p class="faint" style="font-size:12px">Read-only sees everything and changes nothing. Edit can change entries. Admin can also manage logins and open this page. It applies the next time ${esc(name)} connects.</p>`,
    actions: [{ label: "Cancel" }, { label: "Save", primary: true, run: async d => {
      try { await post("/api/users/access", { name, access: $("#acSel", d.el).value }); d.close(); toast(`${name} now has ${$("#acSel", d.el).value} access.`); loadUsers(true); } catch (e) { errBox(d, e.message); }
    } }],
  });
}

function dlgPassword(name) {
  dialog({
    title: `New password for ${name}`,
    body: `<div class="field"><label for="pwNew">New password (leave empty for a random one)</label><input id="pwNew" class="mono" autocomplete="new-password"></div><p class="faint" style="font-size:12px">This also turns the login back on if it was turned off.</p>`,
    actions: [{ label: "Cancel" }, { label: "Set password", primary: true, run: async d => {
      try {
        const r = await post("/api/users/password", { name, password: $("#pwNew", d.el).value });
        d.el.querySelector(".db").innerHTML = credsBody(r, name);
        d.setActions([{ label: "Copy connection string", run: async (dd, b) => copyText(r.conn, b) }, { label: "Done", primary: true }]);
        loadUsers(true);
      } catch (e) { errBox(d, e.message); }
    } }],
  });
}

async function userEnable(name, on) {
  try { await post("/api/users/enable", { name, enabled: on }); toast(`${name}'s login is ${on ? "on" : "off"}.`); loadUsers(true); } catch (e) { toast(e.message, true); }
}

function dlgRemoveUser(name) {
  dialog({
    title: `Remove ${name}`,
    body: `<p>${esc(name)} can't sign in any more and their open connections close. Their name stays on the changes they made.</p>`,
    actions: [{ label: "Cancel" }, { label: `Remove ${name}`, danger: true, run: async d => {
      try { const r = await post("/api/users/remove", { name }); d.close(); toast(r.removed ? `${name} was removed.` : `${name} owns something in the database, so the login was turned off instead.`); loadUsers(true); } catch (e) { errBox(d, e.message); }
    } }],
  });
}

function dlgOxidized() {
  const d = dialog({ title: "Oxidized device list", wide: true, body: `<p class="faint">Loading…</p>`, actions: [{ label: "New token", run: renew }, { label: "Close", primary: true }] });
  function show(o) {
    d.el.querySelector(".db").innerHTML = `
      <p style="font-size:13px">Put this in Oxidized's config (usually <span class="mono">~/.config/oxidized/config</span>) in place of its <span class="mono">source:</span> section, then restart Oxidized. It then backs up every SSH entry in RemoteMan${o.devices == null ? "" : ` (${o.devices} now)`}, and picks up new ones each time it reloads its list.</p>
      <div class="field"><label>Config</label><pre class="code" id="oxCfg">${esc(o.config)}</pre><div><button class="btn sm" data-copy="oxCfg">Copy</button></div></div>
      <div class="field"><label>Address</label><pre class="code wrap">${esc(o.url)}</pre></div>
      <p class="faint" style="font-size:12px">Any server in the cluster answers with the same list and token, so Oxidized can use a standby's address too. Logins only come through after an admin has opened RemoteMan once with the vault passphrase; devices without one use the <span class="mono">username</span> and <span class="mono">password</span> at the top of Oxidized's config. The token is like a password for every device login: keep it in Oxidized only. "New token" stops the old one at once.</p>`;
  }
  async function renew() {
    try { show(await post("/api/oxidized/token")); toast("New token made. Put it in Oxidized's config."); }
    catch (e) { errBox(d, e.message); }
  }
  api("/api/oxidized").then(show).catch(e => { d.el.querySelector(".db").innerHTML = `<p class="err">${esc(e.message)}</p>`; });
}

async function runBackup(btn) {
  btn.disabled = true; btn.textContent = "Backing up…";
  try { const r = await post("/api/backup"); toast("Backup saved: " + r.file); } catch (e) { toast(e.message, true); }
  finally { btn.disabled = false; btn.textContent = "Back up now"; }
}

// ------------------------------------------------------------------ boot
(async () => {
  try { await api("/api/live", { noAuthRedirect: true }); start(); }
  catch { showLogin(); }
})();
