using System.Diagnostics;
using RemoteMan.Core.Model;

namespace RemoteMan.Protocols.Vnc;

/// <summary>
/// MVP: VNC opens in an installed viewer. An embedded viewer (e.g. RemoteViewing / a
/// managed RFB client) can replace this later without changing entries.
/// </summary>
public static class VncLauncher
{
    private static readonly Dictionary<VncViewer, string[]> Candidates = new()
    {
        [VncViewer.TigerVnc] = [@"%ProgramFiles%\TigerVNC\vncviewer.exe", @"%ProgramFiles(x86)%\TigerVNC\vncviewer.exe"],
        [VncViewer.TightVnc] = [@"%ProgramFiles%\TightVNC\tvnviewer.exe"],
        [VncViewer.RealVnc] = [@"%ProgramFiles%\RealVNC\VNC Viewer\vncviewer.exe"],
        [VncViewer.UltraVnc] = [@"%ProgramFiles%\uvnc bvba\UltraVNC\vncviewer.exe"],
    };

    public static void Open(HostEndpoint ep, VncSettings s)
    {
        var exe = Candidates[s.Viewer].Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists)
                  ?? throw new FileNotFoundException($"{s.Viewer} viewer not found.");
        var target = $"{ep.Host}::{ep.Port}";
        var args = s.Viewer switch
        {
            VncViewer.TigerVnc => (s.ViewOnly ? "-ViewOnly " : "") + target,
            VncViewer.TightVnc => $"{target}" + (s.ViewOnly ? " -viewonly" : ""),
            VncViewer.UltraVnc => $"{target}" + (s.ViewOnly ? " /viewonly" : ""),
            _ => target,
        };
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
    }
}
