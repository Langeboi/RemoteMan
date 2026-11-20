using System.Net.Sockets;
using RemoteMan.Core.Model;

namespace RemoteMan.Core.Networking;

/// <summary>
/// Why a connection failed, in plain words, for the notice in the corner of the window:
/// a short headline, one sentence on what happened, a hint on what to check, and the raw error.
/// </summary>
public sealed record ConnectionProblem(string Title, string Why, string? Hint, string? Details, bool IsWarning = false)
{
    public static ConnectionProblem SignInFailed(string? details) => new("Sign-in failed",
        "The user name or password was rejected.", "Check the login on the entry, or whether the account is locked or its password expired.", details);

    /// <summary>The probe before connecting found no host. Says whether it was DNS, a refusal or silence.</summary>
    public static ConnectionProblem FromAttempts(IReadOnlyList<EndpointAttempt> attempts)
    {
        var tried = string.Join("; ", attempts.Select(a => $"{a.Endpoint} {a.Error}"));
        if (attempts.Count == 0) return new("No host to connect to", "The entry has no host name or address.", "Edit the entry and add a host.", null);
        var errors = attempts.Select(a => (a.Error ?? "").ToLowerInvariant()).ToList();
        if (errors.All(e => e.Contains("no such host") || e.Contains("not known") || e.Contains("name or service")))
            return new("Name not found", $"DNS couldn't find {Hosts(attempts)}.", "Check the spelling of the host name, or your DNS / VPN.", tried);
        if (errors.All(e => e.Contains("refused") || e.Contains("actively")))
            return new("Connection refused", $"{Hosts(attempts)} answered, but nothing accepts connections on that port.",
                "Check the port on the entry, or that the service is running and allowed through the firewall.", tried);
        return new("Host didn't answer", attempts.Count == 1 ? $"{attempts[0].Endpoint} didn't respond." : $"None of {attempts.Count} hosts responded.",
            "The host may be off, or a firewall or VPN is blocking the port.", tried);
    }

    public static ConnectionProblem Fallback(EndpointAttempt failed, HostEndpoint used) =>
        new("Connected on the fallback host", $"{failed.Endpoint} didn't answer, so {used} was used.", null, failed.Error, IsWarning: true);

    /// <summary>Best guess from an exception (SSH, FTP, sockets, TLS, serial, HTTP).</summary>
    public static ConnectionProblem FromException(Exception ex, EntryKind kind)
    {
        var details = ex.GetBaseException().Message;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var type = e.GetType().Name;
            var msg = e.Message.ToLowerInvariant();
            var tlsFailure = e is System.Security.Authentication.AuthenticationException;
            if (type.Contains("Authentication") && !tlsFailure || e is UnauthorizedAccessException && kind != EntryKind.Serial
                || msg.Contains("permission denied") || msg.Contains("login incorrect") || msg.Contains("authentication failed") || msg.Contains("530 "))
                return SignInFailed(details);
            if (msg.Contains("host key"))
                return new("Host key changed", "The server's SSH key is different from last time.",
                    "This happens after a reinstall, but could also mean someone is in between. Check with whoever runs the server.", details);
            if (e is System.Security.Authentication.AuthenticationException || msg.Contains("certificate") || msg.Contains("ssl") || msg.Contains("tls"))
                return new("Certificate problem", "The secure connection couldn't be set up.",
                    "The server's certificate isn't trusted or doesn't match. Turn on \"Ignore certificate errors\" on the entry if you trust it.", details);
            if (e is SocketException se)
                return se.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                        new("Name not found", "DNS couldn't find the host.", "Check the spelling of the host name, or your DNS / VPN.", details),
                    SocketError.ConnectionRefused =>
                        new("Connection refused", "The host answered, but nothing accepts connections on that port.", "Check the port, or that the service is running.", details),
                    SocketError.ConnectionReset or SocketError.ConnectionAborted =>
                        new("Connection dropped", "The other side closed the connection.", "The server or a firewall in between ended it. Try again.", details),
                    _ => new("Host didn't answer", "The host didn't respond in time.", "The host may be off, or a firewall or VPN is blocking the port.", details),
                };
            if (e is TimeoutException || e is OperationCanceledException || msg.Contains("timed out") || msg.Contains("timeout"))
                return new("Host didn't answer", "The host didn't respond in time.", "The host may be off, or a firewall or VPN is blocking the port.", details);
            if (kind == EntryKind.Serial && e is IOException)
                return new(msg.Contains("in use") ? "Port in use" : "Port not found", e.Message,
                    msg.Contains("in use") ? "Close the other program using the port (PuTTY, a previous tab) and try again." : "Check the COM port on the entry and that the cable is plugged in.", details);
        }
        return new("Connection failed", ex.Message, null, ex.GetBaseException() == ex ? null : details);
    }

    /// <summary>RDP ActiveX disconnect reason codes (discReason); <paramref name="description"/> is the control's own text.</summary>
    public static ConnectionProblem? FromRdpDisconnect(int reason, string? description)
    {
        var code = $"Code 0x{reason:X} ({reason}).";
        var details = string.IsNullOrWhiteSpace(description) ? code : $"{description.Trim()} {code}";
        switch (reason)
        {
            case 0 or 1: return null;   // you closed it
            case 2: return new("Signed out", "The remote session was signed out or disconnected by a user.", "Another user may have taken over the session.", details, IsWarning: true);
            case 3: return new("Disconnected by the server", "The server ended the session.", "An admin logged you off, an idle limit was reached, or another session took over.", details, IsWarning: true);
            case 260 or 520: return new("Name not found", "DNS couldn't find the host.", "Check the spelling of the host name, or your DNS / VPN.", details);
            case 264 or 516 or 772 or 1028 or 2308:
                return new("Host didn't answer", "The connection couldn't be made or was lost.", "The host may be off, or a firewall or VPN is blocking port 3389.", details);
            case 2825: return new("Network Level Authentication needed", "The server requires NLA, which needs a stored login.", "Add a login to the entry, or ask for NLA to be relaxed on the server.", details);
            case 2055: return SignInFailed(details);
            case 2567: return new("Unknown user", "The server has no account with that user name.", "Check the user name and domain on the entry.", details);
            case 2823 or 4615: return new("Password expired", "The account's password has expired or must be changed.", "Change the password (for example with Ctrl+Alt+End in another session), then update the credential.", details);
            case 3335: return new("Account locked", "The account is locked out.", "Wait for the lockout to end or ask an admin to unlock it.", details);
            case 3591 or 3079: return new("Account disabled or expired", "The account can't sign in.", "Ask an admin to enable or extend the account.", details);
        }
        if (reason >= 0x3000000) return new("Gateway problem", "The RD Gateway refused the connection or couldn't reach the host.", "Check the gateway host and its login, and that the gateway can reach this server.", details);
        if ((reason & 0xFF) == 0x07) return new("Sign-in or account problem", "The server didn't accept the login.", "Check the login on the entry, and whether the account is locked or expired.", details);
        if ((reason & 0xFF) == 0x08 || (reason & 0xFF) == 0x09) return new("Security or certificate problem", "The secure connection couldn't be set up.", "Check certificate and NLA settings on the server and entry.", details);
        return new("Disconnected", "The remote desktop session ended unexpectedly.", null, details);
    }

    private static string Hosts(IReadOnlyList<EndpointAttempt> attempts) => string.Join(", ", attempts.Select(a => a.Endpoint.Host).Distinct());
}
