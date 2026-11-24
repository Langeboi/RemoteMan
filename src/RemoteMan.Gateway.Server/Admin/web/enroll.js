// The page behind an invite link: shows who it's for, and turns it into an rmgw1 invite for RemoteMan.
(() => {
  'use strict';
  const $ = s => document.querySelector(s);
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const code = location.hash.replace(/^#/, '').trim();
  const link = location.href;
  const body = $('#body');
  $('#host').textContent = location.host;

  async function post(path, data) {
    const r = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data), cache: 'no-store' });
    let j = null; try { j = await r.json(); } catch { }
    if (!r.ok) throw new Error(j?.error || `The relay answered ${r.status}.`);
    return j;
  }

  const copyBox = text => `<div class="copy"><pre>${esc(text)}</pre><button class="b sm" data-copy="${esc(text)}">Copy</button></div>`;
  document.addEventListener('click', async e => {
    const b = e.target.closest('[data-copy]');
    if (!b) return;
    try { await navigator.clipboard.writeText(b.dataset.copy); } catch { }
    const was = b.textContent; b.textContent = 'Copied'; setTimeout(() => b.textContent = was, 1500);
  });

  async function start() {
    if (!code) { body.innerHTML = '<h1>No invite here</h1><p>Open the whole link you were sent, including the part after #.</p>'; return; }
    let info;
    try { info = await post('v1/enroll/peek', { code }); }
    catch (e) { body.innerHTML = `<h1>This invite doesn't work</h1><p>${esc(e.message)}</p>`; return; }
    const sites = info.sites.includes('*') ? 'all sites' : info.sites.join(', ');
    body.innerHTML = `<h1>${info.owner ? `Hi ${esc(info.owner)}` : 'You\'re invited'}</h1>
      <p>This invite lets a PC running RemoteMan reach ${esc(sites)} through <b>${esc(new URL(info.relay).host)}</b>.</p>
      <h3 style="margin:4px 0 6px">Easiest: paste this link into RemoteMan</h3>
      <ol class="muted" style="margin:0 0 10px;padding-left:18px"><li>RemoteMan: <b>More › Gateway settings</b></li><li>Paste the link and <b>Save</b>. The PC joins by itself.</li></ol>
      ${copyBox(link)}
      <h3 style="margin:18px 0 6px">Older RemoteMan? Get an invite code</h3>
      <p class="muted" style="margin-bottom:10px">Uses up this link for one PC and shows an <code>rmgw1.</code> code to paste instead.</p>
      ${info.device ? `<p style="margin-bottom:10px">This PC will be called <b>${esc(info.device)}</b>.</p>` : `<div class="field"><label for="pc">Name for this PC</label><input id="pc" maxlength="40" placeholder="laptop"></div>`}
      <p class="err" id="e"></p>
      <button class="b" id="go" style="width:100%">Get invite code</button>
      <p class="faint" style="margin-top:12px;font-size:12px">Valid until ${esc(new Date(info.expires).toLocaleString())}.</p>`;
    $('#go').addEventListener('click', redeem);
  }

  async function redeem() {
    $('#e').textContent = '';
    $('#go').disabled = true;
    try {
      const r = await post('v1/enroll', { code, name: $('#pc')?.value.trim() || null });
      history.replaceState(null, '', location.pathname);
      body.innerHTML = `<h1>${esc(r.device)} is ready</h1>
        <p>In RemoteMan on this PC: <b>More › Gateway settings</b>, paste this and <b>Save</b>. Use it on this one PC only; it's shown only now.</p>
        ${copyBox(r.invite)}`;
    } catch (e) { $('#e').textContent = e.message; $('#go').disabled = false; }
  }

  start();
})();
