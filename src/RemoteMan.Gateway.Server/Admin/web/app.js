// RemoteMan gateway admin console. Plain JS, no build step: the relay serves this file as is.
(() => {
  'use strict';

  // ---------- small helpers ----------
  const $ = (s, el = document) => el.querySelector(s);
  const $$ = (s, el = document) => [...el.querySelectorAll(s)];
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const enc = encodeURIComponent;
  const plural = (n, one, many) => `${n} ${n === 1 ? one : (many || one + 's')}`;

  class ApiError extends Error { constructor(msg, status, body) { super(msg); this.status = status; this.body = body; } }

  async function api(method, path, body) {
    let r;
    try {
      r = await fetch('api' + path, {
        method, credentials: 'same-origin', cache: 'no-store',
        headers: { 'Content-Type': 'application/json', 'X-RemoteMan-Admin': '1' },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch { throw new ApiError("Can't reach the relay.", 0); }
    let j = null;
    try { j = await r.json(); } catch { /* empty body */ }
    if (r.status === 401 && path !== '/login' && path !== '/setup') { stop(); showLogin('Your session ended. Sign in again.'); throw new ApiError('Sign in again.', 401, j); }
    if (!r.ok) throw new ApiError(j?.error || `The relay answered ${r.status}.`, r.status, j);
    return j;
  }

  const sec = d => (Date.now() - new Date(d).getTime()) / 1000;
  function ago(d) {
    if (!d) return 'never';
    const s = sec(d);
    if (s < 45) return 'just now';
    if (s < 3600) return `${Math.round(s / 60)} min ago`;
    if (s < 86400) return `${Math.round(s / 3600)} h ago`;
    if (s < 86400 * 30) return plural(Math.round(s / 86400), 'day') + ' ago';
    return fmtDate(d);
  }
  function dur(d) {
    const s = sec(d);
    if (s < 60) return `${Math.max(0, Math.round(s))} s`;
    if (s < 3600) return `${Math.round(s / 60)} min`;
    if (s < 172800) return `${(s / 3600).toFixed(1).replace(/\.0$/, '')} h`;
    return plural(Math.round(s / 86400), 'day');
  }
  const until = d => { const s = -sec(d); return s < 3600 ? `${Math.max(1, Math.round(s / 60))} min` : s < 172800 ? `${Math.round(s / 3600)} h` : plural(Math.round(s / 86400), 'day'); };
  const fmtDate = d => new Date(d).toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  const fmtTime = d => { const x = new Date(d); return sec(d) < 86400 ? x.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }) : x.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) + ' ' + x.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }); };
  function bytes(n) {
    if (n < 1024) return `${n} B`;
    if (n < 1048576) return `${(n / 1024).toFixed(1)} KB`;
    if (n < 1073741824) return `${(n / 1048576).toFixed(1)} MB`;
    return `${(n / 1073741824).toFixed(2)} GB`;
  }
  const host = u => { try { return new URL(u).host; } catch { return u; } };

  const ICON = {
    overview: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><rect x="2" y="2" width="5" height="5" rx="1"/><rect x="9" y="2" width="5" height="5" rx="1"/><rect x="2" y="9" width="5" height="5" rx="1"/><rect x="9" y="9" width="5" height="5" rx="1"/></svg>',
    sites: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><circle cx="8" cy="3.5" r="1.8"/><circle cx="3.5" cy="12.5" r="1.8"/><circle cx="12.5" cy="12.5" r="1.8"/><path d="M7 5 4.5 10.8M9 5l2.5 5.8M5.3 12.5h5.4"/></svg>',
    people: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><circle cx="6" cy="5" r="2.5"/><path d="M1.5 14c.4-2.6 2.2-4 4.5-4s4.1 1.4 4.5 4"/><path d="M10.5 2.8a2.3 2.3 0 0 1 0 4.4M12 10.2c1.4.5 2.3 1.8 2.5 3.8"/></svg>',
    activity: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><path d="M1.5 8.5h3l2-5 3 9 2-4h3"/></svg>',
    settings: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><circle cx="8" cy="8" r="2.2"/><path d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4"/></svg>',
    plus: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M8 3v10M3 8h10"/></svg>',
    more: '<svg viewBox="0 0 16 16" fill="currentColor"><circle cx="3" cy="8" r="1.4"/><circle cx="8" cy="8" r="1.4"/><circle cx="13" cy="8" r="1.4"/></svg>',
    menu: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6"><path d="M2 4h12M2 8h12M2 12h12"/></svg>',
  };

  // ---------- state ----------
  const S = { info: null, snap: null, timer: 0, watchers: new Set(), activity: null, activityFilter: { text: '', kind: '' }, peopleView: 'devices', fast: 0 };
  const root = $('#root');

  function stop() { clearTimeout(S.timer); S.timer = 0; }

  async function refresh() {
    clearTimeout(S.timer);
    try {
      S.snap = await api('GET', '/snapshot');
      if (route().view === 'activity') S.activity = await api('GET', '/activity?limit=1000');
      for (const w of S.watchers) w(S.snap);
      if (!busy()) render();
      setConn(true);
    } catch (e) {
      if (e.status === 401) return;
      setConn(false, e.message);
    }
    const every = S.watchers.size ? 1500 : document.hidden ? 20000 : 4000;
    S.timer = setTimeout(refresh, every);
  }
  document.addEventListener('visibilitychange', () => { if (!document.hidden && S.snap) refresh(); });

  /** Don't redraw under the person's hands: an open menu, focus in a field on the page, or text being selected. */
  function busy() {
    const main = $('.main');
    if (!main) return false;
    if ($('.menu .pop:not([hidden])')) return true;
    const a = document.activeElement;
    if (a && main.contains(a) && /INPUT|SELECT|TEXTAREA/.test(a.tagName)) return true;
    const sel = getSelection();
    return sel && !sel.isCollapsed && main.contains(sel.anchorNode);
  }

  function setConn(ok, msg) {
    const el = $('#conn');
    if (!el) return;
    el.innerHTML = ok ? '<span class="dot ok"></span> Live' : `<span class="dot bad"></span> ${esc(msg || 'Offline')}`;
  }

  // ---------- routing ----------
  function route() {
    const h = location.hash.replace(/^#\/?/, '');
    if (h.startsWith('setup=')) return { view: 'overview' };
    const [view, ...rest] = h.split('/');
    return { view: ['overview', 'sites', 'people', 'activity', 'settings'].includes(view) ? view : 'overview', arg: rest.length ? decodeURIComponent(rest.join('/')) : null };
  }
  addEventListener('hashchange', () => {
    if (!S.snap) return;
    $('.nav')?.classList.remove('open');
    render(); scrollTo(0, 0);
    refresh();
  });

  // ---------- boot, sign-in, setup ----------
  async function boot() {
    try { S.info = await api('GET', '/state'); }
    catch (e) { root.innerHTML = gate(`<h1>Can't open the console</h1><p>${esc(e.message)}</p>`); return; }
    const setup = /setup=([A-Za-z0-9_-]+)/.exec(location.hash)?.[1];
    if (S.info.setup) return setup ? showSetup(setup, true) : showNoAdmin();
    if (S.info.user) { if (setup) history.replaceState(null, '', location.pathname + '#/overview'); return startApp(); }
    if (setup) return showSetup(setup, false);   // a link from `gateway admin setup`: another admin
    showLogin();
  }

  const brand = () => `<div class="brand"><div class="mark">R</div><div><b>RemoteMan Gateway</b><span>${esc(host(S.info?.relay || location.origin))}</span></div></div>`;
  const gate = inner => `<div class="gate"><div class="box">${brand()}${inner}<div class="foot">RemoteMan gateway ${esc(S.info?.version || '')}</div></div></div>`;

  function showNoAdmin() {
    root.innerHTML = gate(`<h1>Create the first admin</h1>
      <p>This relay has no console admin yet. Its log has a one-time setup link (valid 24 hours from the relay's start). Need a new one? On the relay run:</p>
      <div class="copy"><pre>docker exec remoteman-relay gateway admin setup</pre></div>
      <p style="margin-top:12px">Open the link it prints in this browser.</p>`);
  }

  function showSetup(code, first) {
    root.innerHTML = gate(`<h1>${first ? 'Create the first admin' : 'Create an admin'}</h1>
      <p>You opened the relay's setup link. Pick a name and a password for the admin console. The link works once.</p>
      <form id="f" autocomplete="on">
        <div class="field"><label for="u">Name</label><input id="u" autocomplete="username" value="${first ? 'admin' : ''}" required></div>
        <div class="field"><label for="p">Password</label><input id="p" type="password" autocomplete="new-password" minlength="10" required><span class="hint">At least 10 characters. You can add an authenticator code after signing in.</span></div>
        <div class="field"><label for="p2">Password again</label><input id="p2" type="password" autocomplete="new-password" required></div>
        <p class="err" id="e"></p>
        <button class="b pri" type="submit">Create admin and sign in</button>
      </form>${first ? '' : '<p style="margin-top:14px;margin-bottom:0"><a href="#" id="tologin">I have an account; sign in</a></p>'}`);
    $('#tologin')?.addEventListener('click', ev => { ev.preventDefault(); history.replaceState(null, '', location.pathname); showLogin(); });
    $('#f').addEventListener('submit', async ev => {
      ev.preventDefault();
      const e = $('#e'); e.textContent = '';
      if ($('#p').value !== $('#p2').value) { e.textContent = "The passwords don't match."; return; }
      try {
        await api('POST', '/setup', { code, user: $('#u').value, password: $('#p').value });
        history.replaceState(null, '', location.pathname + '#/overview');
        S.info = await api('GET', '/state');
        startApp(true);
      } catch (x) { e.textContent = x.message; }
    });
    $('#p').focus();
  }

  function showLogin(note) {
    stop();
    S.snap = null;
    root.innerHTML = gate(`<h1>Sign in</h1><p>Admin console for this relay.</p>
      ${note ? `<div class="note info">${esc(note)}</div>` : ''}
      <form id="f" autocomplete="on">
        <div class="field"><label for="u">Name</label><input id="u" autocomplete="username" required></div>
        <div class="field"><label for="p">Password</label><input id="p" type="password" autocomplete="current-password" required></div>
        <div class="field" id="cf" hidden><label for="c">Authenticator code</label><input id="c" inputmode="numeric" autocomplete="one-time-code" maxlength="7" placeholder="123456"></div>
        <p class="err" id="e"></p>
        <button class="b pri" type="submit">Sign in</button>
      </form>`);
    $('#f').addEventListener('submit', async ev => {
      ev.preventDefault();
      const e = $('#e'); e.textContent = '';
      const btn = $('#f button'); btn.disabled = true;
      try {
        await api('POST', '/login', { user: $('#u').value, password: $('#p').value, code: $('#c').value || undefined });
        S.info = await api('GET', '/state');
        startApp();
      } catch (x) {
        if (x.body?.needCode) { $('#cf').hidden = false; $('#c').focus(); if ($('#c').value) e.textContent = x.message; }
        else e.textContent = x.message;
      } finally { btn.disabled = false; }
    });
    $('#u').focus();
  }

  async function startApp(fresh) {
    root.innerHTML = `<div class="app">
      <nav class="nav" id="nav">${brand()}
        <ul>
          <li><a href="#/overview" data-v="overview">${ICON.overview}Overview</a></li>
          <li><a href="#/sites" data-v="sites">${ICON.sites}Sites<span class="count" id="n-sites"></span></a></li>
          <li><a href="#/people" data-v="people">${ICON.people}People &amp; devices<span class="count" id="n-dev"></span></a></li>
          <li><a href="#/activity" data-v="activity">${ICON.activity}Activity</a></li>
          <li><a href="#/settings" data-v="settings">${ICON.settings}Settings</a></li>
        </ul>
        <div class="foot"><span class="who" id="who"></span><button class="b sm ghost" data-act="logout">Sign out</button></div>
        <div class="foot" style="border-top:0;padding-top:0" id="conn"></div>
      </nav>
      <div>
        <div class="topbar"><button class="b sm ghost" data-act="menu" aria-label="Menu">${ICON.menu}</button><b>RemoteMan Gateway</b></div>
        <main class="main" id="main"></main>
      </div>
    </div>`;
    root.addEventListener('click', onClick);
    root.addEventListener('change', onChange);
    root.addEventListener('input', onInput);
    await refresh();
    if (fresh) toast('Welcome. Start by adding a site for the first network you want to reach.');
  }

  // ---------- rendering ----------
  function render() {
    const s = S.snap; if (!s) return;
    const r = route();
    $$('.nav a[data-v]').forEach(a => a.classList.toggle('on', a.dataset.v === r.view));
    $('#n-sites').textContent = s.sites.length || '';
    $('#n-dev').textContent = s.devices.length || '';
    $('#who').textContent = s.me.name;
    const view = { overview, sites: r.arg ? site : sites, people, activity, settings }[r.view];
    $('#main').innerHTML = view(r.arg);
    document.title = `${({ overview: 'Overview', sites: r.arg || 'Sites', people: 'People & devices', activity: 'Activity', settings: 'Settings' })[r.view]} · RemoteMan Gateway`;
  }

  const head = (title, text, actions = '', crumb = '') =>
    `<div class="head"><div class="t">${crumb ? `<div class="crumb">${crumb}</div>` : ''}<h1>${title}</h1>${text ? `<p>${text}</p>` : ''}</div><div class="actions">${actions}</div></div>`;

  const siteState = st => {
    const on = st.connectors.filter(c => c.online).length;
    if (on > 0) return `<span class="state"><span class="dot ok"></span>Online</span>`;
    if (!st.connectors.length) return `<span class="state"><span class="dot warn"></span>No connector yet</span>`;
    return `<span class="state"><span class="dot bad"></span>Offline</span>`;
  };

  const publicUrlNote = () => S.snap.relay.publicUrlSet ? '' :
    `<div class="note"><span>⚠</span><span><b>PUBLIC_URL isn't set on the relay.</b> Invites and connector setups use the address this browser used (<code>${esc(S.snap.relay.url)}</code>). If connectors and PCs reach the relay under another name, set <code>PUBLIC_URL</code> in the relay's compose file.</span></div>`;

  function overview() {
    const s = S.snap;
    const online = s.sites.filter(x => x.online).length;
    const conns = s.sites.flatMap(x => x.connectors);
    const connsOn = conns.filter(c => c.online).length;
    const anyConnector = conns.length > 0;
    const firstSite = s.sites.find(x => !x.connectors.length) || s.sites[0];
    const steps = [
      { done: s.sites.length > 0, t: 'Add a site', p: 'A site is a network you want to reach: school, a homelab, an office.', btn: `<button class="b sm pri" data-act="add-site">Add site</button>` },
      { done: anyConnector, t: 'Start a connector in it', p: 'One line on any Linux machine with Docker inside that network. It connects out, nothing to open.', btn: firstSite ? `<button class="b sm ${s.sites.length ? 'pri' : ''}" data-act="add-connector" data-site="${esc(firstSite.name)}">Add connector</button>` : '' },
      { done: s.devices.length > 0, t: 'Invite a PC running RemoteMan', p: 'Send an invite link; it is pasted into RemoteMan\'s Gateway settings once.', btn: `<button class="b sm ${anyConnector ? 'pri' : ''}" data-act="invite">Invite</button>` },
    ];
    const getStarted = steps.every(x => x.done) ? '' : `<div class="card" style="margin-bottom:18px"><div class="ch"><h2>Get started<span class="sub">${steps.filter(x => x.done).length} of 3 done</span></h2></div>
      <ol class="steps">${steps.map(x => `<li class="${x.done ? 'done' : ''}"><div><b>${x.t}</b><p>${x.p}</p></div>${x.done ? '' : x.btn}</li>`).join('')}</ol></div>`;
    const tiles = `<div class="tiles">
      <div class="tile"><span class="k">Sites online</span><span class="v">${online}<small> / ${s.sites.length}</small></span><span class="s">${s.sites.length - online ? plural(s.sites.length - online, 'site') + ' offline' : 'all reachable'}</span></div>
      <div class="tile"><span class="k">Connectors online</span><span class="v">${connsOn}<small> / ${conns.length}</small></span><span class="s">${conns.length ? 'known connectors' : 'none yet'}</span></div>
      <div class="tile"><span class="k">Devices</span><span class="v">${s.devices.filter(d => !d.disabled).length}</span><span class="s">${plural(new Set(s.devices.map(d => d.owner || d.name)).size, 'person', 'people')}${s.invites.length ? ` · ${plural(s.invites.length, 'open invite')}` : ''}</span></div>
      <div class="tile"><span class="k">Tunnels open</span><span class="v">${s.relay.tunnels}</span><span class="s">${s.relay.tunnelsTotal} since the relay started</span></div>
      <div class="tile"><span class="k">Relayed</span><span class="v">${bytes(s.relay.bytes)}</span><span class="s">up ${dur(s.relay.started)}</span></div>
    </div>`;
    const siteRows = s.sites.map(x => `<tr class="click" data-href="#/sites/${enc(x.name)}"><td><span class="name">${esc(x.name)}</span>${x.description ? `<div class="desc">${esc(x.description)}</div>` : ''}</td><td>${siteState(x)}</td>
      <td class="num">${x.connectors.filter(c => c.online).length}/${x.connectors.length}</td><td class="num hide-sm">${x.tunnels}</td></tr>`).join('');
    const live = s.live.length ? `<div class="card"><div class="ch"><h2>Open tunnels<span class="sub">${s.live.length}</span></h2></div><div class="cb flush tablewrap"><table>
      <thead><tr><th>Device</th><th>Site</th><th class="hide-sm">Connector</th><th>Open for</th><th class="r">Traffic</th></tr></thead><tbody>
      ${s.live.map(t => `<tr><td>${esc(t.device)}</td><td><a href="#/sites/${enc(t.site)}">${esc(t.site)}</a></td><td class="hide-sm">${esc(t.connector || '')}</td><td>${dur(t.started)}</td><td class="r num">${bytes(t.bytes)}</td></tr>`).join('')}
      </tbody></table></div></div>` : '';
    return head('Overview', `Everything this relay does, live. ${esc(host(s.relay.url))} · version ${esc(s.relay.version)}`,
      `<button class="b" data-act="invite">Invite someone</button><button class="b pri" data-act="add-site">${ICON.plus}Add site</button>`) +
      publicUrlNote() + getStarted + tiles +
      `<div class="cols"><div class="stack">
        <div class="card"><div class="ch"><h2>Sites</h2><a href="#/sites" class="b sm ghost">All sites</a></div><div class="cb flush tablewrap">
          ${s.sites.length ? `<table><thead><tr><th>Site</th><th>Status</th><th>Connectors</th><th class="hide-sm">Tunnels</th></tr></thead><tbody>${siteRows}</tbody></table>`
            : `<div class="empty"><b>No sites yet</b>Add one for each network you want to reach.</div>`}
        </div></div>${live}</div>
        <div class="card"><div class="ch"><h2>Recent activity</h2><a href="#/activity" class="b sm ghost">All</a></div><div class="cb flush">${feed(s.activity)}</div></div>
      </div>`;
  }

  function sites() {
    const s = S.snap;
    const rows = s.sites.map(x => {
      const on = x.connectors.filter(c => c.online).length;
      return `<tr class="click" data-href="#/sites/${enc(x.name)}">
        <td><span class="name">${esc(x.name)}</span>${x.description ? `<div class="desc">${esc(x.description)}</div>` : ''}</td>
        <td>${siteState(x)}</td>
        <td class="num">${on} of ${x.connectors.length} online</td>
        <td class="hide-sm">${x.resourceRules ? `<div class="chips">${x.resourceRules.slice(0, 3).map(r => `<span class="chip">${esc(r)}</span>`).join('')}${x.resourceRules.length > 3 ? `<span class="faint">+${x.resourceRules.length - 3}</span>` : ''}</div>` : '<span class="faint">Connector ALLOW</span>'}</td>
        <td class="num hide-sm">${plural(x.devices, 'device')}</td>
        <td class="num">${x.tunnels}</td></tr>`;
    }).join('');
    return head('Sites', 'Each site is a network the gateway reaches through one or more connectors running inside it. Two connectors on a site share the load and take over from each other.',
      `<button class="b pri" data-act="add-site">${ICON.plus}Add site</button>`) + publicUrlNote() +
      `<div class="card"><div class="cb flush tablewrap">${s.sites.length ? `<table><thead><tr><th>Site</th><th>Status</th><th>Connectors</th><th class="hide-sm">Resources</th><th class="hide-sm">Access</th><th>Tunnels</th></tr></thead><tbody>${rows}</tbody></table>`
        : `<div class="empty"><b>No sites yet</b>A site stands for one network: the school lab, a friend's homelab, a cabin behind CG-NAT.<br><button class="b pri" data-act="add-site">${ICON.plus}Add site</button></div>`}</div></div>`;
  }

  function site(name) {
    const s = S.snap;
    const x = s.sites.find(y => y.name.toLowerCase() === name.toLowerCase());
    if (!x) return head('Site not found', `There's no site called ${esc(name)} any more.`, '', '<a href="#/sites">Sites</a>');
    const n = esc(x.name);
    const conns = x.connectors.map(c => `<div class="conn ${c.online ? 'on' : ''}">
        <div class="top"><span class="dot ${c.online ? 'ok' : 'bad'}"></span><b title="${esc(c.name)}">${esc(c.name)}</b>
          ${c.online ? `<span class="pill ok">online ${dur(c.since)}</span>` : `<span class="pill">offline</span>`}</div>
        <dl>
          <dt>${c.online ? 'From' : 'Last from'}</dt><dd>${esc(c.from || '—')}</dd>
          ${c.online ? `<dt>Tunnels</dt><dd class="num">${c.active} open</dd>` : `<dt>Last seen</dt><dd>${ago(c.lastSeen)}</dd>`}
          <dt>Version</dt><dd>${c.version ? esc(c.version) : '<span class="faint">older than the console</span>'}</dd>
          <dt>ALLOW</dt><dd title="${esc(c.allow || '')}">${c.allow ? `<code>${esc(c.allow)}</code>` : '<span class="faint">not reported</span>'}</dd>
          <dt>Key</dt><dd><code>${esc(c.fingerprint.slice(0, 16))}</code></dd>
        </dl>
        ${!c.online ? `<div><button class="b sm ghost" data-act="forget-connector" data-site="${n}" data-name="${esc(c.name)}" data-key="${esc(c.key)}">Forget</button></div>` : ''}
        ${c.online && x.resources && !c.version ? `<div class="faint" style="font-size:12px">This connector is older than site resources and only follows its own ALLOW. Update it.</div>` : ''}
      </div>`).join('');
    const tokens = x.tokens.map(t => `<tr><td><span class="name">${esc(t.name)}</span></td><td class="hide-sm">${t.created ? fmtDate(t.created) : ''}</td><td>${t.lastUsed ? ago(t.lastUsed) : t.id === 'site' ? '<span class="faint">—</span>' : '<span class="faint">not used yet</span>'}</td>
      <td class="r"><button class="b sm dan" data-act="revoke-token" data-site="${n}" data-id="${esc(t.id)}" data-name="${esc(t.name)}">Revoke</button></td></tr>`).join('');
    const access = s.devices.filter(d => d.sites.includes('*') || d.sites.some(z => z.toLowerCase() === x.name.toLowerCase()));
    const siteActs = `<button class="b" data-act="edit-site" data-site="${n}">Edit</button>
      <div class="menu"><button class="b" data-act="pop" aria-label="More">${ICON.more}</button><div class="pop" hidden>
        <button data-act="reset-keys" data-site="${n}">Forget connector keys…</button>
        <button class="dan" data-act="remove-site" data-site="${n}">Remove site…</button></div></div>
      <button class="b pri" data-act="add-connector" data-site="${n}">${ICON.plus}Add connector</button>`;
    return head(`${n} <span style="font-size:13px;font-weight:400;margin-left:8px">${siteState(x)}</span>`, esc(x.description || ''), siteActs, '<a href="#/sites">Sites</a> /') +
      `<div class="cols"><div class="stack">
        <div class="card"><div class="ch"><h2>Connectors<span class="sub">${x.connectors.filter(c => c.online).length} of ${x.connectors.length} online</span></h2></div>
          ${x.connectors.length ? `<div class="conns">${conns}</div>` : `<div class="empty"><b>No connector has connected yet</b>Start one on a Linux machine inside this network.<br><button class="b pri" data-act="add-connector" data-site="${n}">${ICON.plus}Add connector</button></div>`}
        </div>
        <div class="card"><div class="ch"><h2>Resources</h2><button class="b sm" data-act="edit-site" data-site="${n}">Edit</button></div><div class="cb">
          ${x.resources ? `<div class="chips" style="margin-bottom:8px">${x.resourceRules.map(r => `<span class="chip">${esc(r)}</span>`).join('')}</div>
            <p class="muted" style="font-size:12.5px">Connectors open only these addresses and ports (and only what their own ALLOW also allows).</p>`
            : `<p class="muted">Not limited here: each connector opens what its own <code>ALLOW</code> allows (all private ranges unless it was set). Add resources to narrow it from here, e.g. <code>10.20.0.0/16</code> or <code>10.20.5.10:3389</code>.</p>`}
        </div></div>
        <div class="card"><div class="ch"><h2>Recent activity</h2><a class="b sm ghost" href="#/activity" data-act="activity-for" data-site="${n}">All</a></div><div class="cb flush">${feed(siteActivity(x.name))}</div></div>
      </div><div class="stack">
        <div class="card"><div class="ch"><h2>Access<span class="sub">${plural(access.length, 'device')}</span></h2><button class="b sm" data-act="invite" data-site="${n}">Invite</button></div><div class="cb flush">
          ${access.length ? `<table><tbody>${access.map(d => `<tr><td><span class="name">${esc(d.name)}</span>${d.owner ? `<div class="desc">${esc(d.owner)}</div>` : ''}</td><td>${d.disabled ? '<span class="pill warn">paused</span>' : d.sites.includes('*') ? '<span class="pill">all sites</span>' : ''}</td><td class="r faint">${ago(d.lastSeen)}</td></tr>`).join('')}</tbody></table>`
            : `<div class="empty">No device may use this site yet.</div>`}
        </div></div>
        <div class="card"><div class="ch"><h2>Connector tokens</h2></div><div class="cb flush">
          ${x.tokens.length ? `<table><thead><tr><th>Token</th><th class="hide-sm">Made</th><th>Last used</th><th></th></tr></thead><tbody>${tokens}</tbody></table>`
            : `<div class="empty">No tokens. Add a connector to make one.</div>`}
        </div></div>
        <div class="card"><div class="ch"><h2>Trusted connector keys</h2></div><div class="cb">
          ${x.keys.length ? `<div class="chips">${x.keys.map(k => `<span class="chip" title="${esc(k)}">${esc(k.slice(0, 16))}</span>`).join('')}</div>` : '<p class="faint">None yet; the first connector to connect is recorded.</p>'}
          <p class="faint" style="font-size:12px;margin-top:8px">RemoteMan only accepts tunnels signed by these keys.</p>
        </div></div>
      </div></div>`;
  }

  function siteActivity(name) {
    const all = (S.activity || S.snap.activity).filter(e => (e.site || '').toLowerCase() === name.toLowerCase());
    return all.slice(0, 10);
  }

  function people() {
    const s = S.snap;
    const view = S.peopleView;
    const siteChips = d => d.sites.includes('*') ? '<span class="pill">All sites</span>' : `<div class="chips">${d.sites.map(z => `<a class="chip" href="#/sites/${enc(z)}">${esc(z)}</a>`).join('')}</div>`;
    const status = d => d.disabled ? '<span class="pill warn">Paused</span>' : d.tunnels ? `<span class="pill ok">${plural(d.tunnels, 'tunnel')} open</span>` : d.lastSeen && sec(d.lastSeen) < 900 ? '<span class="pill acc">Active</span>' : '<span class="pill">Idle</span>';
    const row = d => `<tr><td><span class="name">${esc(d.name)}</span>${view === 'devices' && d.owner ? `<div class="desc">${esc(d.owner)}</div>` : ''}</td>
      <td>${siteChips(d)}</td><td>${status(d)}</td>
      <td class="hide-sm">${ago(d.lastSeen)}${d.lastIp ? `<div class="desc mono">${esc(d.lastIp)}</div>` : ''}</td>
      <td class="r"><div class="menu"><button class="b sm ghost" data-act="pop" aria-label="Actions">${ICON.more}</button><div class="pop" hidden>
        <button data-act="edit-device" data-name="${esc(d.name)}">Edit access…</button>
        <button data-act="toggle-device" data-name="${esc(d.name)}">${d.disabled ? 'Turn back on' : 'Pause'}</button>
        <button class="dan" data-act="remove-device" data-name="${esc(d.name)}">Remove…</button></div></div></td></tr>`;
    const thead = '<thead><tr><th>Device</th><th>Access</th><th>Status</th><th class="hide-sm">Last seen</th><th></th></tr></thead>';
    let body;
    if (!s.devices.length) body = `<div class="empty"><b>No devices yet</b>Each PC running RemoteMan gets its own device, so you can pause or remove one laptop without touching the others.<br><button class="b pri" data-act="invite">Invite someone</button></div>`;
    else if (view === 'people') {
      const groups = {};
      for (const d of s.devices) (groups[d.owner || '—'] ||= []).push(d);
      body = Object.keys(groups).sort((a, b) => a === '—' ? 1 : b === '—' ? -1 : a.localeCompare(b)).map(o =>
        `<div class="ch" style="border-top:1px solid var(--border)"><h2>${o === '—' ? '<span class="faint">No owner set</span>' : esc(o)}<span class="sub">${plural(groups[o].length, 'device')}</span></h2>${o !== '—' ? `<button class="b sm" data-act="invite" data-owner="${esc(o)}">Invite another PC</button>` : ''}</div>
         <table>${thead}<tbody>${groups[o].map(row).join('')}</tbody></table>`).join('');
    } else body = `<table>${thead}<tbody>${s.devices.map(row).join('')}</tbody></table>`;

    const invites = s.invites.length ? `<div class="card" style="margin-top:14px"><div class="ch"><h2>Open invite links<span class="sub">${s.invites.length}</span></h2></div><div class="cb flush tablewrap"><table>
      <thead><tr><th>For</th><th>Access</th><th>Uses left</th><th class="hide-sm">Expires</th><th></th></tr></thead><tbody>
      ${s.invites.map(i => `<tr><td><span class="name">${esc(i.owner || i.device || 'Anyone with the link')}</span>${i.device && i.owner ? `<div class="desc">${esc(i.device)}</div>` : ''}${i.createdBy ? `<div class="desc">by ${esc(i.createdBy)}</div>` : ''}</td>
        <td>${siteChips(i)}</td><td class="num">${i.usesLeft}</td><td class="hide-sm">in ${until(i.expires)}</td>
        <td class="r"><button class="b sm dan" data-act="revoke-invite" data-id="${esc(i.id)}">Revoke</button></td></tr>`).join('')}
      </tbody></table></div></div>` : '';

    return head('People &amp; devices', 'Every PC running RemoteMan has its own device and token. Give each one access to all sites or only some, pause it, or remove it; a removed device is refused at once and its open tunnels close.',
      `<button class="b" data-act="add-device">Add device manually</button><button class="b pri" data-act="invite">${ICON.plus}Invite someone</button>`) +
      `<div class="card"><div class="ch"><h2>${plural(s.devices.length, 'device')}</h2>
        <div class="seg" role="group" aria-label="Group by"><button data-act="people-view" data-v="devices" aria-pressed="${view === 'devices'}">Devices</button><button data-act="people-view" data-v="people" aria-pressed="${view === 'people'}">By person</button></div></div>
        <div class="cb flush tablewrap">${body}</div></div>` + invites;
  }

  const KINDS = { '': 'Everything', connector: 'Connectors', tunnel: 'Tunnels', device: 'Devices and invites', invite: 'Invites', site: 'Sites', admin: 'Console and admins' };
  function activity() {
    const f = S.activityFilter;
    const all = S.activity || S.snap.activity;
    const t = f.text.toLowerCase();
    const list = all.filter(e => (!f.kind || e.kind.startsWith(f.kind)) && (!t || `${e.text} ${e.actor || ''} ${e.site || ''}`.toLowerCase().includes(t)));
    return head('Activity', 'Connectors coming and going, tunnels, devices, invites and every change made in this console. Kept on the relay (the newest 2,000).') +
      `<div class="row" style="margin-bottom:12px"><input class="search" id="af-text" placeholder="Filter…" value="${esc(f.text)}" aria-label="Filter">
        <select id="af-kind" style="width:auto" aria-label="Kind">${Object.entries(KINDS).map(([k, v]) => `<option value="${k}" ${k === f.kind ? 'selected' : ''}>${v}</option>`).join('')}</select>
        <span class="faint">${plural(list.length, 'event')}</span></div>
      <div class="card"><div class="cb flush">${feed(list.slice(0, 500), true)}</div></div>`;
  }

  function feed(list, full) {
    if (!list || !list.length) return '<div class="empty">Nothing yet.</div>';
    const cls = k => k === 'connector.online' ? 'ok' : k === 'connector.offline' ? 'bad' : k === 'admin.login-failed' ? 'warn' : k.startsWith('admin') ? 'admin' : k.startsWith('tunnel') || k.startsWith('device') || k.startsWith('invite') ? 'acc' : '';
    return `<ul class="feed">${list.map(e => `<li><span class="ic ${cls(e.kind)}"></span><span class="t">${esc(e.text)}${e.actor && full ? ` <span class="by">· ${esc(e.actor)}</span>` : ''}</span><time datetime="${esc(e.at)}" title="${esc(fmtDate(e.at))}">${fmtTime(e.at)}</time></li>`).join('')}</ul>`;
  }

  function settings() {
    const s = S.snap;
    return head('Settings', '') +
      `<div class="cols"><div class="stack">
        <div class="card"><div class="ch"><h2>Your account</h2></div><div class="cb">
          <div class="row" style="justify-content:space-between;margin-bottom:12px"><div><b>${esc(s.me.name)}</b><div class="muted">Two-step sign-in: ${s.me.totp ? '<span class="pill ok">on</span>' : '<span class="pill warn">off</span>'}</div></div>
          <div class="actions"><button class="b" data-act="password">Change password</button>${s.me.totp ? '<button class="b" data-act="totp-off">Turn off code</button>' : '<button class="b pri" data-act="totp-on">Turn on authenticator code</button>'}</div></div>
          ${s.me.totp ? '' : '<p class="muted" style="font-size:12.5px">This console is reachable from the internet. An authenticator code (any app: Microsoft Authenticator, 1Password, Aegis…) means a leaked password alone isn\'t enough.</p>'}
        </div></div>
        <div class="card"><div class="ch"><h2>Console admins</h2><button class="b sm" data-act="add-admin">${ICON.plus}Add admin</button></div><div class="cb flush tablewrap"><table>
          <thead><tr><th>Name</th><th>Sign-in</th><th class="hide-sm">Last sign-in</th><th></th></tr></thead><tbody>
          ${s.admins.map(a => `<tr><td><span class="name">${esc(a.name)}</span>${a.name === s.me.name ? ' <span class="pill acc">you</span>' : ''}</td><td>${a.totp ? 'Password + code' : 'Password'}</td><td class="hide-sm">${ago(a.lastLogin)}</td>
            <td class="r">${a.name === s.me.name ? '' : `<button class="b sm dan" data-act="remove-admin" data-name="${esc(a.name)}">Remove</button>`}</td></tr>`).join('')}
          </tbody></table></div></div>
        <div class="card"><div class="ch"><h2>Your sessions</h2>${s.sessions.length > 1 ? '<button class="b sm" data-act="signout-others">Sign out the others</button>' : ''}</div><div class="cb flush tablewrap"><table><tbody>
          ${s.sessions.map(x => `<tr><td>${x.current ? '<span class="pill acc">this browser</span> ' : ''}<span class="desc">${esc((x.agent || '').slice(0, 90))}</span></td><td class="mono">${esc(x.ip || '')}</td><td class="r faint">${ago(x.lastSeen)}</td></tr>`).join('')}
          </tbody></table></div></div>
      </div><div class="stack">
        <div class="card"><div class="ch"><h2>Relay</h2></div><div class="cb">
          <dl class="kv" style="display:grid;grid-template-columns:auto 1fr;gap:6px 14px;margin:0">
            <dt class="faint">Address</dt><dd style="margin:0"><code>${esc(s.relay.url)}</code>${s.relay.publicUrlSet ? '' : ' <span class="pill warn">PUBLIC_URL not set</span>'}</dd>
            <dt class="faint">Version</dt><dd style="margin:0">${esc(s.relay.version)}</dd>
            <dt class="faint">Running for</dt><dd style="margin:0">${dur(s.relay.started)}</dd>
            <dt class="faint">Connector image</dt><dd style="margin:0"><code>${esc(s.relay.image)}</code></dd>
            <dt class="faint">Console open to</dt><dd style="margin:0">${s.relay.consoleLimited ? 'the addresses in ADMIN_ALLOW' : 'anyone who can reach the relay (sign-in required)'}</dd>
          </dl></div></div>
        <div class="card"><div class="ch"><h2>Locked out?</h2></div><div class="cb">
          <p class="muted" style="margin-bottom:8px">On the relay's host, set a new password (turns the code off too):</p>
          <div class="copy"><pre>docker exec -it remoteman-relay gateway admin reset ${esc(s.me.name)}</pre></div>
          <p class="muted" style="margin:10px 0 8px">Limit who can open the console at all with <code>ADMIN_ALLOW</code> (e.g. <code>192.168.0.0/16</code>), or turn it off with <code>ADMIN=off</code>. RemoteMan and connectors keep working either way.</p>
        </div></div>
      </div></div>`;
  }

  // ---------- dialogs ----------
  function modal({ title, body, wide, buttons = [], onClose }) {
    const el = document.createElement('div');
    el.className = 'shade';
    el.innerHTML = `<div class="dlg ${wide ? 'wide' : ''}" role="dialog" aria-modal="true" aria-label="${esc(title)}"><div class="dh"><h2>${esc(title)}</h2><button class="b sm ghost" data-x aria-label="Close">✕</button></div>
      <div class="db"></div><div class="df"></div></div>`;
    const m = {
      el, body: $('.db', el),
      close() { el.remove(); document.removeEventListener('keydown', key); onClose?.(); },
      err(msg) { let e = $('.err.m', el); if (!e) { e = document.createElement('p'); e.className = 'err m'; $('.db', el).append(e); } e.textContent = msg || ''; },
      setBody(html) { $('.db', el).innerHTML = html; },
      setButtons(list) {
        const f = $('.df', el); f.innerHTML = '';
        f.hidden = !list.length;
        for (const b of list) {
          const btn = document.createElement('button');
          btn.className = 'b ' + (b.cls || ''); btn.textContent = b.label; btn.type = 'button';
          btn.addEventListener('click', async () => {
            if (!b.act) return m.close();
            btn.disabled = true; m.err('');
            try { if (await b.act(m) !== false) { if (b.close !== false) m.close(); } }
            catch (x) { m.err(x.message); }
            finally { btn.disabled = false; }
          });
          f.append(btn);
        }
      },
    };
    const key = e => {
      if (e.key === 'Escape') m.close();
      if (e.key === 'Enter' && e.target.tagName === 'INPUT' && el.contains(e.target)) { e.preventDefault(); $('.df .pri', el)?.click(); }
    };
    m.setBody(body);
    m.setButtons(buttons);
    $('[data-x]', el).addEventListener('click', () => m.close());
    el.addEventListener('mousedown', e => { if (e.target === el) m.close(); });
    el.addEventListener('click', onClick);
    document.addEventListener('keydown', key);
    document.body.append(el);
    setTimeout(() => $('input:not([type=checkbox]):not([type=radio]):not([disabled]),textarea', el)?.focus(), 0);
    return m;
  }

  const confirmDlg = (title, text, label, danger = true) => new Promise(res => {
    const m = modal({ title, body: `<p>${text}</p>`, onClose: () => res(false), buttons: [{ label: 'Cancel' }, { label, cls: danger ? 'dan solid' : 'pri', act: () => { res(true); } }] });
    return m;
  });

  function toast(text, bad) {
    $('.toast')?.remove();
    const t = document.createElement('div'); t.className = 'toast' + (bad ? ' bad' : ''); t.textContent = text; t.setAttribute('role', 'status');
    document.body.append(t);
    setTimeout(() => t.remove(), 4500);
  }

  const copyBox = (text, label = 'Copy') => `<div class="copy"><pre>${esc(text)}</pre><button class="b sm" data-act="copy" data-text="${esc(text)}">${label}</button></div>`;

  const sitePicker = (selected, id = 'sp') => {
    const all = !selected || selected.includes('*');
    return `<div class="field"><span class="lbl">Access</span>
      <label class="check"><input type="radio" name="${id}-mode" value="all" ${all ? 'checked' : ''}> All sites, including ones added later</label>
      <label class="check"><input type="radio" name="${id}-mode" value="some" ${all ? '' : 'checked'} ${S.snap.sites.length ? '' : 'disabled'}> Only these sites</label>
      <div class="checks" id="${id}" ${all ? 'hidden' : ''}>${S.snap.sites.map(x => `<label class="check"><input type="checkbox" value="${esc(x.name)}" ${!all && selected.some(z => z.toLowerCase() === x.name.toLowerCase()) ? 'checked' : ''}> ${esc(x.name)}</label>`).join('') || '<span class="faint">No sites yet</span>'}</div></div>`;
  };
  function readSites(m, id = 'sp') {
    if ($(`input[name=${id}-mode]:checked`, m.el).value === 'all') return ['*'];
    const list = $$(`#${id} input:checked`, m.el).map(i => i.value);
    if (!list.length) throw new Error('Pick at least one site, or All sites.');
    return list;
  }
  function wirePicker(m, id = 'sp') {
    $$(`input[name=${id}-mode]`, m.el).forEach(r => r.addEventListener('change', () => { $('#' + id, m.el).hidden = $(`input[name=${id}-mode]:checked`, m.el).value === 'all'; }));
  }

  function addSite() {
    modal({
      title: 'Add a site', body: `<p>A site is one network RemoteMan should reach (a school lab, a homelab, an office). Next you start a connector inside it.</p>
        <div class="field"><label for="sn">Name</label><input id="sn" placeholder="school" maxlength="40" pattern="[A-Za-z0-9._-]+"><span class="hint">Letters, digits, - _ and . RemoteMan entries and folders pick the site by this name.</span></div>
        <div class="field"><label for="sd">Description <span class="faint">(optional)</span></label><input id="sd" placeholder="AD lab at school, 10.20.0.0/16" maxlength="200"></div>`,
      buttons: [{ label: 'Cancel' }, {
        label: 'Add site and set up a connector', cls: 'pri', act: async () => {
          const name = $('#sn').value.trim();
          await api('POST', '/sites', { name, description: $('#sd').value });
          await refresh();
          location.hash = '#/sites/' + enc(name);
          setTimeout(() => addConnector(name), 50);
        },
      }],
    });
  }

  function suggestConnector(st) {
    const taken = new Set([...st.tokens.map(t => t.name.toLowerCase()), ...st.connectors.map(c => c.name.toLowerCase())]);
    for (let i = 1; ; i++) if (!taken.has(`${st.name}-${i}`.toLowerCase())) return `${st.name}-${i}`;
  }

  function addConnector(siteName) {
    const st = S.snap.sites.find(x => x.name === siteName);
    if (!st) return;
    const m = modal({
      title: `Add a connector to ${siteName}`, wide: true,
      body: `<p>The connector runs in Docker on any Linux machine inside ${esc(siteName)}'s network (a VM, a Raspberry Pi, a NAS). It only connects out to this relay on 443, so nothing needs opening there. A second connector on another machine gives failover.</p>
        <div class="field"><label for="cn">Connector name</label><input id="cn" value="${esc(suggestConnector(st))}" maxlength="40"><span class="hint">Shown here and in the activity list. Each connector gets its own token, so you can revoke one without touching the others.</span></div>`,
      buttons: [{ label: 'Cancel' }, {
        label: 'Make setup', cls: 'pri', close: false, act: async () => {
          const res = await api('POST', `/sites/${enc(siteName)}/connectors`, { name: $('#cn', m.el).value.trim() });
          showConnectorSetup(m, siteName, res);
        },
      }],
      onClose: () => { S.watchers.delete(m.watch); },
    });
  }

  function showConnectorSetup(m, siteName, res) {
    const tabs = [
      ['install', 'One-line install', `<p class="muted" style="margin-bottom:8px">Paste on the Linux machine. Installs Docker if it's missing, then starts the connector. The link works once, within an hour.</p>${copyBox(res.install)}`],
      ['run', 'docker run', `<p class="muted" style="margin-bottom:8px">On a machine that already has Docker:</p>${copyBox(res.dockerRun)}`],
      ['compose', 'compose.yaml', `<p class="muted" style="margin-bottom:8px">Save as <code>compose.yaml</code> and run <code>docker compose up -d</code>:</p>${copyBox(res.compose)}`],
    ];
    m.setBody(`${res.publicUrlSet ? '' : `<div class="note"><span>⚠</span><span>PUBLIC_URL isn't set, so this uses <code>${esc(res.relayUrl)}</code>. Make sure the connector can reach the relay at that address.</span></div>`}
      <div class="tabs" role="tablist">${tabs.map(([k, l], i) => `<button role="tab" data-act="tab" data-tab="${k}" aria-selected="${i === 0}">${l}</button>`).join('')}</div>
      ${tabs.map(([k, , h], i) => `<div data-pane="${k}" ${i ? 'hidden' : ''}>${h}</div>`).join('')}
      <p class="faint" style="margin-top:10px;font-size:12px">The token inside is shown only now. Lost it? Revoke it under Connector tokens and add the connector again.</p>
      <div class="wait" id="wait"><span class="spin"></span><span>Waiting for <b>${esc(res.name)}</b> to connect…</span></div>`);
    m.setButtons([{ label: 'Done', cls: 'pri' }]);
    m.watch = snap => {
      const st = snap.sites.find(x => x.name === siteName);
      const c = st?.connectors.find(x => x.name.toLowerCase() === res.name.toLowerCase() && x.online);
      if (!c) return;
      const w = $('#wait', m.el);
      if (w) { w.className = 'wait ok'; w.innerHTML = `<span>✓</span><span><b>${esc(c.name)}</b> is online${c.from ? ` from ${esc(c.from)}` : ''}. ${esc(siteName)} is reachable.${snap.devices.length ? '' : ' Next: invite a PC.'}</span>`; }
      S.watchers.delete(m.watch);
      if (!snap.devices.length) m.setButtons([{ label: 'Close' }, { label: 'Invite a PC', cls: 'pri', act: () => { setTimeout(() => invite({ site: siteName }), 30); } }]);
    };
    S.watchers.add(m.watch);
    refresh();
  }

  function editSite(name) {
    const st = S.snap.sites.find(x => x.name === name);
    modal({
      title: `Edit ${name}`, body: `
        <div class="field"><label for="sd">Description</label><input id="sd" value="${esc(st.description || '')}" maxlength="200"></div>
        <div class="field"><label for="sr">Resources</label><textarea id="sr" rows="5" placeholder="10.20.0.0/16&#10;192.168.1.10&#10;10.20.5.0/24:22|3389">${esc((st.resourceRules || []).join('\n'))}</textarea>
          <span class="hint">What this site's connectors may open: addresses, ranges, and <code>:port|port</code> to limit ports. One per line. Connectors open only what both this list and their own ALLOW allow. Leave empty to let each connector's ALLOW decide.</span>
          <div class="row" style="margin-top:4px"><button class="b sm" type="button" data-act="preset" data-v="private">All private ranges</button><button class="b sm" type="button" data-act="preset" data-v="">Clear</button></div></div>`,
      buttons: [{ label: 'Cancel' }, {
        label: 'Save', cls: 'pri', act: async () => {
          const res = $('#sr').value.split(/[\n,]+/).map(x => x.trim()).filter(Boolean).join(', ');
          await api('PUT', `/sites/${enc(name)}`, { description: $('#sd').value, resources: res || null });
          toast(res ? 'Saved. Online connectors use the new resources now.' : 'Saved.');
          refresh();
        },
      }],
    });
  }

  function invite(opts = {}) {
    const m = modal({
      title: 'Invite someone', body: `<p>Makes a link to send to the person. They paste it into RemoteMan (More › Gateway settings) and their PC joins by itself, with its own device. Or they open it in a browser to get an invite code.</p>
        <div class="row"><div class="field"><label for="io">Person</label><input id="io" placeholder="Anna" maxlength="80" value="${esc(opts.owner || '')}"><span class="hint">Groups their devices.</span></div>
        <div class="field"><label for="id">Device name <span class="faint">(optional)</span></label><input id="id" placeholder="Their PC's own name" maxlength="40"><span class="hint">Empty: the PC names itself.</span></div></div>
        ${sitePicker(opts.site ? [opts.site] : null)}
        <div class="row"><div class="field"><label for="iv">Valid for</label><select id="iv"><option value="1">1 day</option><option value="7" selected>7 days</option><option value="30">30 days</option></select></div>
        <div class="field"><label for="iu">PCs it can add</label><select id="iu"><option>1</option><option>2</option><option>3</option><option>5</option><option>10</option></select></div></div>`,
      buttons: [{ label: 'Cancel' }, {
        label: 'Make invite link', cls: 'pri', close: false, act: async () => {
          const owner = $('#io', m.el).value.trim();
          const res = await api('POST', '/invites', { owner: owner || null, device: $('#id', m.el).value.trim() || null, sites: readSites(m), days: +$('#iv', m.el).value, uses: +$('#iu', m.el).value });
          m.setBody(`<p>Send this to ${esc(owner || 'the person')}. It works ${$('#iu', m.el).value === '1' ? 'once' : `for ${$('#iu', m.el).value} PCs`} and runs out in ${$('#iv', m.el).selectedOptions[0].text}.</p>
            ${copyBox(res.link, 'Copy link')}
            <ol class="muted" style="margin:12px 0 0;padding-left:18px"><li>In RemoteMan: <b>More › Gateway settings</b></li><li>Paste the link, <b>Save</b>.</li></ol>
            <p class="faint" style="margin-top:8px;font-size:12px">The link is shown only now. You can revoke it under People &amp; devices until it's used.</p>`);
          m.setButtons([{ label: 'Done', cls: 'pri' }]);
          refresh();
        },
      }],
    });
    wirePicker(m);
    if (opts.site) { $('input[value=some]', m.el).checked = true; $('#sp', m.el).hidden = false; }
  }

  function addDevice() {
    const m = modal({
      title: 'Add a device manually', body: `<p>Makes the device now and shows its <code>rmgw1.</code> invite code to paste into RemoteMan on that PC. An invite link is usually easier.</p>
        <div class="row"><div class="field"><label for="dn">Device name</label><input id="dn" placeholder="lange-laptop" maxlength="40"></div>
        <div class="field"><label for="do">Person <span class="faint">(optional)</span></label><input id="do" maxlength="80"></div></div>${sitePicker(null)}`,
      buttons: [{ label: 'Cancel' }, {
        label: 'Add device', cls: 'pri', close: false, act: async () => {
          const res = await api('POST', '/devices', { name: $('#dn', m.el).value.trim(), owner: $('#do', m.el).value.trim() || null, sites: readSites(m) });
          m.setBody(`<p>In RemoteMan on that PC: <b>More › Gateway settings</b>, paste this and Save. Use it on that one PC only; it's shown only now.</p>${copyBox(res.invite)}`);
          m.setButtons([{ label: 'Done', cls: 'pri' }]);
          refresh();
        },
      }],
    });
    wirePicker(m);
  }

  function editDevice(name) {
    const d = S.snap.devices.find(x => x.name === name);
    const m = modal({
      title: `Edit ${name}`, body: `<div class="field"><label for="do">Person</label><input id="do" value="${esc(d.owner || '')}" maxlength="80"></div>${sitePicker(d.sites)}
        <label class="check"><input type="checkbox" id="dd" ${d.disabled ? 'checked' : ''}> Paused (refused like a removed device, until turned back on)</label>`,
      buttons: [{ label: 'Cancel' }, {
        label: 'Save', cls: 'pri', act: async () => {
          await api('PUT', `/devices/${enc(name)}`, { owner: $('#do', m.el).value.trim() || null, sites: readSites(m), disabled: $('#dd', m.el).checked });
          refresh();
        },
      }],
    });
    wirePicker(m);
  }

  function addAdmin() {
    modal({
      title: 'Add a console admin', body: `<p>They sign in with this name and password, and can turn on their own authenticator code. Every admin can change everything here.</p>
        <div class="field"><label for="an">Name</label><input id="an" maxlength="40" autocomplete="off"></div>
        <div class="field"><label for="ap">Password</label><input id="ap" type="password" autocomplete="new-password"><span class="hint">At least 10 characters. Give it to them some safe way.</span></div>`,
      buttons: [{ label: 'Cancel' }, { label: 'Add admin', cls: 'pri', act: async () => { await api('POST', '/admins', { name: $('#an').value.trim(), password: $('#ap').value }); refresh(); } }],
    });
  }

  function changePassword() {
    modal({
      title: 'Change your password', body: `<div class="field"><label for="p0">Current password</label><input id="p0" type="password" autocomplete="current-password"></div>
        <div class="field"><label for="p1">New password</label><input id="p1" type="password" autocomplete="new-password"><span class="hint">At least 10 characters. Your other sessions are signed out.</span></div>
        <div class="field"><label for="p2">New password again</label><input id="p2" type="password" autocomplete="new-password"></div>`,
      buttons: [{ label: 'Cancel' }, {
        label: 'Change password', cls: 'pri', act: async () => {
          if ($('#p1').value !== $('#p2').value) throw new Error("The new passwords don't match.");
          await api('POST', '/me/password', { current: $('#p0').value, next: $('#p1').value });
          toast('Password changed.');
        },
      }],
    });
  }

  async function totpOn() {
    const r = await api('POST', '/me/totp/start');
    let qr = '';
    try { const q = qrcode(0, 'M'); q.addData(r.uri); q.make(); qr = q.createSvgTag({ cellSize: 4, margin: 0, scalable: true }); } catch { /* the secret below still works */ }
    modal({
      title: 'Turn on authenticator code', body: `<p>Scan this with your authenticator app, then type the 6-digit code it shows.</p>
        <div class="row" style="align-items:flex-start;gap:18px;margin-bottom:12px">${qr ? `<div class="qr">${qr}</div>` : ''}
          <div style="flex:1;min-width:200px"><span class="lbl">Or type this key</span><div class="copy" style="margin-top:5px"><pre>${esc(r.secret.replace(/(.{4})/g, '$1 ').trim())}</pre><button class="b sm" data-act="copy" data-text="${esc(r.secret)}">Copy</button></div></div></div>
        <div class="field"><label for="tc">Code</label><input id="tc" inputmode="numeric" autocomplete="one-time-code" maxlength="7" placeholder="123456" style="max-width:160px"></div>`,
      buttons: [{ label: 'Cancel' }, { label: 'Turn on', cls: 'pri', act: async () => { await api('POST', '/me/totp/confirm', { code: $('#tc').value }); toast('Sign-in now asks for a code too.'); refresh(); } }],
    });
  }

  function totpOff() {
    modal({
      title: 'Turn off authenticator code', body: `<p>Sign-in will only ask for your password.</p><div class="field"><label for="tp">Password</label><input id="tp" type="password" autocomplete="current-password"></div>`,
      buttons: [{ label: 'Cancel' }, { label: 'Turn off', cls: 'dan solid', act: async () => { await api('POST', '/me/totp/off', { password: $('#tp').value }); refresh(); } }],
    });
  }

  // ---------- events ----------
  const acts = {
    menu: () => $('#nav').classList.toggle('open'),
    logout: async () => { await api('POST', '/logout').catch(() => {}); showLogin(); },
    pop: (d, el) => {
      const pop = el.parentElement.querySelector('.pop');
      const wasHidden = pop.hidden;
      $$('.menu .pop').forEach(p => p.hidden = true);
      pop.hidden = !wasHidden;
    },
    'add-site': addSite,
    'add-connector': d => addConnector(d.site),
    'edit-site': d => editSite(d.site),
    'remove-site': async d => {
      if (!await confirmDlg(`Remove ${d.site}?`, `Its connectors are disconnected and its tokens stop working. Devices lose access to it. RemoteMan entries that use this site won't connect from other networks.`, 'Remove site')) return;
      await api('DELETE', `/sites/${enc(d.site)}`); location.hash = '#/sites'; toast(`${d.site} removed.`); refresh();
    },
    'reset-keys': async d => {
      if (!await confirmDlg(`Forget ${d.site}'s connector keys?`, 'Do this after replacing a connector on purpose. Its connectors reconnect at once and their keys are trusted again. RemoteMan picks up the new keys on its next check.', 'Forget keys')) return;
      await api('POST', `/sites/${enc(d.site)}/reset-keys`); toast('Keys forgotten.'); refresh();
    },
    'revoke-token': async d => {
      if (!await confirmDlg(`Revoke ${d.name}?`, 'Connectors using this token are disconnected and can\'t connect again until they get a new token.', 'Revoke')) return;
      await api('DELETE', `/sites/${enc(d.site)}/tokens/${enc(d.id)}`); refresh();
    },
    'forget-connector': async d => { await api('POST', `/sites/${enc(d.site)}/forget`, { name: d.name, key: d.key }); refresh(); },
    invite: d => invite({ site: d.site, owner: d.owner }),
    'add-device': addDevice,
    'edit-device': d => editDevice(d.name),
    'toggle-device': async d => {
      const dev = S.snap.devices.find(x => x.name === d.name);
      await api('PUT', `/devices/${enc(d.name)}`, { owner: dev.owner, sites: dev.sites, disabled: !dev.disabled });
      toast(dev.disabled ? `${d.name} is back on.` : `${d.name} is paused; its tunnels were closed.`); refresh();
    },
    'remove-device': async d => {
      if (!await confirmDlg(`Remove ${d.name}?`, 'It is refused at once and its open tunnels close. To use the gateway again that PC needs a new invite.', 'Remove device')) return;
      await api('DELETE', `/devices/${enc(d.name)}`); refresh();
    },
    'revoke-invite': async d => { await api('DELETE', `/invites/${enc(d.id)}`); refresh(); },
    'people-view': d => { S.peopleView = d.v; render(); },
    'activity-for': d => { S.activityFilter = { text: d.site, kind: '' }; },
    'add-admin': addAdmin,
    'remove-admin': async d => {
      if (!await confirmDlg(`Remove ${d.name}?`, 'They are signed out and can\'t sign in again.', 'Remove admin')) return;
      await api('DELETE', `/admins/${enc(d.name)}`); refresh();
    },
    password: changePassword,
    'totp-on': totpOn,
    'totp-off': totpOff,
    'signout-others': async () => { await api('POST', '/me/signout-others'); toast('Your other sessions are signed out.'); refresh(); },
    copy: async (d, el) => {
      try { await navigator.clipboard.writeText(d.text); }
      catch {
        const r = document.createRange(); r.selectNodeContents(el.parentElement.querySelector('pre'));
        const sel = getSelection(); sel.removeAllRanges(); sel.addRange(r); document.execCommand('copy');
      }
      const was = el.textContent; el.textContent = 'Copied'; setTimeout(() => el.textContent = was, 1500);
    },
    tab: (d, el) => {
      const dlg = el.closest('.dlg');
      $$('[role=tab]', dlg).forEach(t => t.setAttribute('aria-selected', t === el));
      $$('[data-pane]', dlg).forEach(p => p.hidden = p.dataset.pane !== d.tab);
    },
    preset: (d, el) => { el.closest('.dlg').querySelector('#sr').value = d.v ? '10.0.0.0/8\n172.16.0.0/12\n192.168.0.0/16\n100.64.0.0/10' : ''; },
  };

  async function onClick(e) {
    const a = e.target.closest('[data-act]');
    if (!a) {
      if (!e.target.closest('.menu')) $$('.menu .pop').forEach(p => p.hidden = true);
      const row = e.target.closest('[data-href]');
      if (row && !e.target.closest('a,button')) location.hash = row.dataset.href;
      return;
    }
    if (a.dataset.act !== 'pop') $$('.menu .pop').forEach(p => p.hidden = true);
    const fn = acts[a.dataset.act];
    if (!fn) return;
    if (a.tagName === 'A' && a.dataset.act !== 'activity-for') e.preventDefault();
    e.stopPropagation();
    try { await fn(a.dataset, a); } catch (x) { if (x.status !== 401) toast(x.message, true); }
  }
  function onChange(e) {
    if (e.target.id === 'af-kind') { S.activityFilter.kind = e.target.value; render(); }
  }
  let filterTimer = 0;
  function onInput(e) {
    if (e.target.id !== 'af-text') return;
    clearTimeout(filterTimer);
    filterTimer = setTimeout(() => {
      S.activityFilter.text = e.target.value;
      const pos = e.target.selectionStart;
      render();
      const i = $('#af-text'); i.focus(); i.setSelectionRange(pos, pos);
    }, 200);
  }

  boot();
})();
