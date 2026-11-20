using System.Text.Json;

namespace RemoteMan.Protocols.Web;

public sealed record AutoLoginOptions(
    IReadOnlyCollection<string> Hosts,
    bool Submit = true,
    string? UserSelector = null,
    string? PasswordSelector = null,
    string? SubmitSelector = null);

/// <summary>
/// Script injected into every page of a web tab: finds the user name and password boxes,
/// fills them, and presses the login button. Handles React/Angular inputs, forms that appear
/// late (SPAs), and two-step logins (user name page, then password page). It only runs on the
/// entry's own hosts, and presses login at most once per 20 s so a failed login doesn't loop.
/// </summary>
public static class AutoFillScript
{
    /// <summary>Known login pages: name, user box, password box, login button (CSS selectors).</summary>
    public static readonly IReadOnlyList<(string Name, string User, string Password, string Submit)> Presets =
    [
        ("FortiGate", "#username", "#secretkey", "#login_button"),
        ("pfSense / OPNsense", "#usernamefld", "#passwordfld", "button[name=login]"),
        ("Zabbix", "#name", "#password", "#enter"),
        ("Grafana", "input[name=user]", "input[name=password]", "button[type=submit]"),
    ];

    /// <summary>
    /// Script for the field picker: highlights the box under the mouse and, on click, posts
    /// {"pick": selector} to the host instead of clicking. Also posts {"guess": {...}} with what
    /// the automatic detection would use, once the page has a password box.
    /// </summary>
    public const string PickerScript = """
      (() => {
        if (window.__rmPicker) return; window.__rmPicker = true;
        const post = (m) => { try { window.chrome.webview.postMessage(JSON.stringify(m)); } catch {} };
        const esc = (v) => (window.CSS && CSS.escape) ? CSS.escape(v) : v.replace(/[^a-zA-Z0-9_-]/g, '\\$&');
        const unique = (sel) => { try { return document.querySelectorAll(sel).length === 1; } catch { return false; } };
        const selectorFor = (el) => {
          if (el.id && unique('#' + esc(el.id))) return '#' + esc(el.id);
          const tag = el.tagName.toLowerCase();
          const name = el.getAttribute('name');
          if (name && unique(`${tag}[name="${name}"]`)) return `${tag}[name="${name}"]`;
          const type = el.getAttribute('type');
          if (type && unique(`${tag}[type="${type}"]`)) return `${tag}[type="${type}"]`;
          const parts = [];
          for (let e = el; e && e.nodeType === 1 && e !== document.body; e = e.parentElement) {
            if (e.id) { parts.unshift('#' + esc(e.id)); break; }
            const same = Array.from(e.parentElement?.children || []).filter(c => c.tagName === e.tagName);
            parts.unshift(e.tagName.toLowerCase() + (same.length > 1 ? `:nth-of-type(${same.indexOf(e) + 1})` : ''));
          }
          return parts.join(' > ');
        };
        const target = (el) => el?.closest?.('input, button, a, [role=button], select, textarea') || el;
        let last = null;
        const mark = (el) => {
          if (last) { last.style.outline = last.__rmOutline || ''; }
          last = el;
          if (el) { el.__rmOutline = el.style.outline; el.style.outline = '2px solid #3FB8A5'; }
        };
        document.addEventListener('mouseover', (e) => mark(target(e.target)), true);
        for (const t of ['mousedown', 'mouseup', 'pointerdown', 'pointerup'])
          document.addEventListener(t, (e) => { e.preventDefault(); e.stopPropagation(); }, true);
        document.addEventListener('click', (e) => {
          e.preventDefault(); e.stopPropagation();
          const el = target(e.target);
          if (el) post({ pick: selectorFor(el), tag: el.tagName.toLowerCase(), type: el.getAttribute('type') || '' });
        }, true);
        const guess = () => {
          const pw = document.querySelector('input[type=password]');
          if (!pw) return false;
          const scope = pw.form || document;
          const user = Array.from(scope.querySelectorAll('input:not([type=password]):not([type=hidden]):not([type=checkbox]):not([type=submit])')).find(i => i.getClientRects().length > 0);
          const btn = scope.querySelector('button[type=submit], input[type=submit]') ||
                      Array.from(scope.querySelectorAll('button, input[type=button], [role=button]')).find(b => /log ?in|sign ?in|log ?on|submit|continue/i.test(b.innerText || b.value || ''));
          post({ guess: { user: user ? selectorFor(user) : '', password: selectorFor(pw), submit: btn ? selectorFor(btn) : '' } });
          return true;
        };
        const start = () => { if (guess()) return; const o = new MutationObserver(() => { if (guess()) o.disconnect(); }); o.observe(document.documentElement, { childList: true, subtree: true }); };
        document.readyState === 'loading' ? document.addEventListener('DOMContentLoaded', start) : start();
      })();
    """;

    public static string Build(string? user, string? password, AutoLoginOptions o)
    {
        var cfg = JsonSerializer.Serialize(new
        {
            user = user ?? "",
            pass = password ?? "",
            hosts = o.Hosts.Select(h => h.ToLowerInvariant()).Distinct().ToArray(),
            submit = o.Submit,
            userSel = o.UserSelector,
            passSel = o.PasswordSelector,
            submitSel = o.SubmitSelector,
        });
        return "(() => { const cfg = " + cfg + ";\n" + Body + "\n})();";
    }

    private const string Body = """
      if (!cfg.hosts.includes(location.hostname.toLowerCase())) return;
      const LOGIN_WORDS = /^\s*(log ?in|log ?on|sign ?in|logon|submit|continue|next|anmeld|log på|login)\s*$/i;
      const recent = (k) => { try { return Date.now() - (+sessionStorage.getItem(k) || 0) < 20000; } catch { return false; } };
      const mark = (k) => { try { sessionStorage.setItem(k, String(Date.now())); } catch {} };
      const visible = (el) => !!el && !el.disabled && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden';
      const first = (root, sel) => Array.from((root || document).querySelectorAll(sel)).find(visible) || null;
      const set = (el, v) => {
        if (!el || !v) return;
        // Pages that block browser autofill keep fields readonly until they get focus.
        if (el.readOnly) el.removeAttribute('readonly');
        el.focus();
        const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value')?.set;
        setter ? setter.call(el, v) : (el.value = v);
        for (const t of ['input', 'change']) el.dispatchEvent(new Event(t, { bubbles: true }));
        el.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true }));
        el.blur();
      };
      const USER_SEL = 'input[autocomplete=username], input[type=email], input[name*=user i], input[id*=user i], input[name*=login i], input[id*=login i], input[name*=email i], input[type=text]';
      const findPw = () => cfg.passSel ? first(document, cfg.passSel) : first(document, 'input[type=password]');
      const findUser = (scope) => cfg.userSel ? first(document, cfg.userSel) : first(scope, USER_SEL) || first(document, USER_SEL);
      const label = (b) => (b.innerText || b.value || b.getAttribute('aria-label') || '').trim();
      const findSubmit = (form) => {
        if (cfg.submitSel) return first(document, cfg.submitSel);
        const scopes = form ? [form, document] : [document];
        for (const s of scopes) {
          const typed = first(s, 'button[type=submit], input[type=submit], input[type=image]');
          if (typed) return typed;
          const worded = Array.from(s.querySelectorAll('button, input[type=button], a, [role=button]')).filter(visible).find(b => LOGIN_WORDS.test(label(b)));
          if (worded) return worded;
        }
        return null;
      };
      const press = (form, field) => setTimeout(() => {
        const b = findSubmit(form);
        if (b) b.click();
        else if (form && form.requestSubmit) form.requestSubmit();
        else field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
      }, 200);

      let donePw = false, doneUser = false;
      const tick = () => {
        const pw = findPw();
        if (pw && !donePw) {
          donePw = true;
          const u = findUser(pw.form);
          if (u && u !== pw && !u.value) set(u, cfg.user);
          set(pw, cfg.pass);
          if (cfg.submit && !recent('__rm_login')) { mark('__rm_login'); press(pw.form, pw); }
          return true;
        }
        if (!pw && !doneUser && cfg.user) {
          // Two-step login: a page that only asks for the user name first.
          const u = cfg.userSel ? first(document, cfg.userSel) : first(document, 'input[autocomplete=username], input[type=email], input[name*=user i], input[name*=login i], input[name*=email i]');
          if (u) {
            doneUser = true;
            set(u, cfg.user);
            // Only press "Next" on a real two-step page: if a password box exists at all (hidden, disabled
            // or not drawn yet), it belongs to this page and gets filled when it turns up.
            const pwSomewhere = cfg.passSel ? document.querySelector(cfg.passSel) : document.querySelector('input[type=password]');
            if (cfg.submit && !pwSomewhere && !recent('__rm_user')) { mark('__rm_user'); press(u.form, u); }
          }
        }
        return false;
      };
      const start = () => {
        if (tick()) return;
        // Watch the page change, and also poll: some frameworks enable or show the password box
        // through properties that don't show up as mutations.
        const stop = () => { obs.disconnect(); clearInterval(poll); };
        const obs = new MutationObserver(() => { if (tick()) stop(); });
        obs.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['style', 'class', 'hidden', 'type', 'disabled', 'readonly'] });
        const poll = setInterval(() => { if (tick()) stop(); }, 400);
        setTimeout(stop, 30000);
      };
      document.readyState === 'loading' ? document.addEventListener('DOMContentLoaded', start) : start();
    """;
}
