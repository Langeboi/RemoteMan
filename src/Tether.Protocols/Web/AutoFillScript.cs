using System.Text.Json;

namespace Tether.Protocols.Web;

/// <summary>JavaScript injected into the embedded browser to fill the first login form.</summary>
public static class AutoFillScript
{
    public static string Build(string? user, string? password) => $$"""
        (() => {
          const user = {{JsonSerializer.Serialize(user ?? "")}};
          const pass = {{JsonSerializer.Serialize(password ?? "")}};
          const set = (el, v) => {
            if (!el || !v) return;
            const proto = Object.getPrototypeOf(el);
            const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
            setter ? setter.call(el, v) : (el.value = v);
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
          };
          const tryFill = () => {
            const pw = document.querySelector('input[type=password]');
            if (!pw) return false;
            const form = pw.form || document;
            const u = form.querySelector('input[autocomplete=username], input[type=email], input[name*=user i], input[id*=user i], input[type=text]');
            set(u, user); set(pw, pass);
            return true;
          };
          if (!tryFill()) {
            const obs = new MutationObserver(() => { if (tryFill()) obs.disconnect(); });
            obs.observe(document.documentElement, { childList: true, subtree: true });
            setTimeout(() => obs.disconnect(), 15000);
          }
        })();
        """;
}
