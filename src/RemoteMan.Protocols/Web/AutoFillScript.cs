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
      const visible = (el) => !!el && !el.disabled && !el.readOnly && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden';
      const first = (root, sel) => Array.from((root || document).querySelectorAll(sel)).find(visible) || null;
      const set = (el, v) => {
        if (!el || !v) return;
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
            if (cfg.submit && !recent('__rm_user')) { mark('__rm_user'); press(u.form, u); }
          }
        }
        return false;
      };
      const start = () => {
        if (tick()) return;
        const obs = new MutationObserver(() => { if (tick()) obs.disconnect(); });
        obs.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['style', 'class', 'hidden', 'type'] });
        setTimeout(() => obs.disconnect(), 20000);
      };
      document.readyState === 'loading' ? document.addEventListener('DOMContentLoaded', start) : start();
    """;
}
