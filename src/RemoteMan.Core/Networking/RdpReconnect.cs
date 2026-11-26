namespace RemoteMan.Core.Networking;

/// <summary>
/// When a dropped RDP session should be picked up again quietly in the background.
/// Only sessions that were signed in, and only drops nobody asked for: a sign-out, a "Disconnect"
/// from the remote Start menu, another client taking the session over, or a login the server
/// turned down all end the tab as before.
/// </summary>
public static class RdpReconnect
{
    /// <summary>How long a dropped session keeps trying before its tab closes.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Wait before the given try (1-based): quick at first, then every few seconds.</summary>
    public static TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(attempt switch { <= 1 => 1, 2 => 2, 3 => 3, _ => 5 });

    /// <param name="reason">The control's disconnect reason (discReason).</param>
    /// <param name="extended">Its ExtendedDisconnectReason (0 when unknown).</param>
    public static bool ShouldRetry(int reason, int extended)
    {
        // 0 no info / 1 closed here / 2 signed out or disconnected by the remote user.
        if (reason is 0 or 1 or 2) return false;

        switch (extended)
        {
            case 2:   // an admin tool logged the session off
            case 5:   // another connection took the session over (reconnecting would take it back)
            case 7 or 8 or 9 or 10: // server denied the connection / FIPS / privileges / fresh credentials needed
            case 11:  // user picked Disconnect inside the session
            case 12:  // user signed out
                return false;
        }
        if (extended is >= 256 and < 4096) return false; // licensing

        // Login and security failures won't get better by trying again.
        switch (reason)
        {
            case 260 or 520:          // DNS: the name is gone
            case 2055 or 2567 or 2823 or 4615 or 3335 or 3591 or 3079 or 2825:
                return false;
            case 264 or 516 or 772 or 1028 or 2308: // timed out, socket closed, network lost
                return true;
        }
        if (reason >= 0x3000000) return true;          // RD Gateway dropped: worth another go
        if ((reason & 0xFF) is 0x07 or 0x08 or 0x09) return false; // sign-in, security, certificate
        return true;
    }
}
