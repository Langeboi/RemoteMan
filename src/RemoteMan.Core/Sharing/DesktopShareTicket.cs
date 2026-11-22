using System.Xml.Linq;

namespace RemoteMan.Core.Sharing;

/// <summary>
/// Windows Desktop Sharing invitations ("connection strings") are a small XML document listing
/// where the sharer listens: <c>&lt;E&gt;&lt;A .../&gt;&lt;C&gt;&lt;T ...&gt;&lt;L P="port" N="address"/&gt;…</c>.
/// The viewer tries the listed addresses in order. We put the guest's local tunnel first, so the
/// picture flows through the shared-session link (direct or relay), and keep the host's own
/// addresses after it as a fallback.
/// </summary>
public static class DesktopShareTicket
{
    public static string PointAt(string connectionString, int localPort, string address = "127.0.0.1")
    {
        XDocument doc;
        try { doc = XDocument.Parse(connectionString); }
        catch (System.Xml.XmlException) { return connectionString; }
        var targets = doc.Descendants("T").ToList();
        if (targets.Count == 0) return connectionString;
        foreach (var t in targets)
        {
            var first = t.Elements("L").FirstOrDefault();
            var tunnel = new XElement("L", new XAttribute("P", localPort), new XAttribute("N", address));
            if (first is null) t.Add(tunnel); else first.AddBeforeSelf(tunnel);
        }
        return doc.Root!.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The ports the sharer listed (for tests and diagnostics).</summary>
    public static IReadOnlyList<(string Address, int Port)> Listeners(string connectionString)
    {
        try
        {
            return XDocument.Parse(connectionString).Descendants("L")
                .Select(l => ((string?)l.Attribute("N") ?? "", int.TryParse((string?)l.Attribute("P"), out var p) ? p : 0)).ToList();
        }
        catch (System.Xml.XmlException) { return []; }
    }
}
