using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.ViewModels;

/// <summary>How history and presence read in the UI: "changed host and password", "Yesterday 09:41", "1 h 12 m".</summary>
public static class TeamText
{
    /// <summary>What a save did, as a phrase after the person's name. With <paramref name="name"/>, it names the entry too.</summary>
    public static string Describe(HistoryRecord h, string? name = null)
    {
        var on = name is null ? "" : $" on {name}";
        var it = name ?? "it";
        switch (h.Action)
        {
            case HistoryAction.Added: return $"created {it}";
            case HistoryAction.Deleted: return $"deleted {it}";
            case HistoryAction.Restored: return $"restored {it}";
        }
        var fields = h.Fields.Distinct().ToList();
        if (fields.Count == 0) return $"saved {it}";
        if (fields is ["Reservation"])
            return h.Snapshot.Reservation is { } r && r.Until > h.Snapshot.UpdatedAt
                ? $"reserved {it} until {Clock(r.Until)}" : $"released the reservation{on}";
        if (fields is ["Name"]) return name is null ? $"renamed it to \"{h.Snapshot.Name}\"" : $"renamed {name}";
        if (fields is ["ParentId"]) return $"moved {it}";
        var labels = fields.Where(f => f != "Reservation").Select(f => EntryDiff.Label(f).ToLowerInvariant()).Distinct().ToList();
        var shown = labels.Take(3).ToList();
        var list = shown.Count switch
        {
            1 => shown[0],
            2 => $"{shown[0]} and {shown[1]}",
            _ => $"{shown[0]}, {shown[1]} and {shown[2]}",
        };
        if (labels.Count > 3) list = $"{string.Join(", ", shown)} and {labels.Count - 3} more";
        return $"changed {list}{on}";
    }

    /// <summary>"14:02" today, "Yesterday 09:41", otherwise "18 Nov 21:15".</summary>
    public static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm");
        return local.ToString(local.Year == today.Year ? "d MMM HH:mm" : "d MMM yyyy");
    }

    /// <summary>A time later today as "16:00", otherwise with the day.</summary>
    public static string Clock(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("ddd d MMM HH:mm");
    }

    /// <summary>"6 m", "1 h 12 m", "2 d".</summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} m";
        if (d < TimeSpan.FromDays(1)) return d.Minutes == 0 ? $"{(int)d.TotalHours} h" : $"{(int)d.TotalHours} h {d.Minutes} m";
        return $"{(int)d.TotalDays} d";
    }

    /// <summary>"in 47 minutes" / "in 2 h".</summary>
    public static string Remaining(DateTimeOffset until)
    {
        var d = until - DateTimeOffset.UtcNow;
        return d < TimeSpan.FromHours(1) ? $"in {Math.Max(1, (int)Math.Ceiling(d.TotalMinutes))} minutes" : $"in {Duration(d)}";
    }

    /// <summary>The user part of DOMAIN\user, for compact lists.</summary>
    public static string Short(string? user) =>
        string.IsNullOrEmpty(user) ? "someone" : user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;
}
