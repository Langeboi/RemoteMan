using System.IO.Ports;
using System.Text;
using RemoteMan.Core.Model;

namespace RemoteMan.Protocols.Terminal;

/// <summary>Serial console (COM port or USB console cable) for switches, routers and firewalls.</summary>
public sealed class SerialChannel : ITerminalChannel
{
    private SerialPort? _port;

    public event Action<string>? Output;
    public event Action<string>? Info;
    public event Action<Exception?>? Closed;

    public static string[] PortNames()
    {
        try { return SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p).ToArray(); }
        catch (Exception) { return []; }
    }

    /// <summary>Character set of the device (set before opening).</summary>
    public Encoding Encoding { get; set; } = Encoding.UTF8;

    public void Open(SerialSettings s)
    {
        var port = new SerialPort(s.PortName, s.BaudRate, Enum.TryParse<Parity>(s.Parity, true, out var parity) ? parity : Parity.None,
            s.DataBits is >= 5 and <= 8 ? s.DataBits : 8, Enum.TryParse<StopBits>(s.StopBits, true, out var stop) && stop != StopBits.None ? stop : StopBits.One)
        {
            Handshake = Enum.TryParse<Handshake>(s.FlowControl, true, out var hs) ? hs : Handshake.None,
            Encoding = Encoding,
            DtrEnable = true,
            RtsEnable = s.FlowControl != "RequestToSend",
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 3000,
        };
        port.DataReceived += (_, _) =>
        {
            try { if (port.IsOpen && port.ReadExisting() is { Length: > 0 } text) Output?.Invoke(text); }
            catch (Exception ex) { Closed?.Invoke(ex); }
        };
        port.ErrorReceived += (_, e) => Info?.Invoke($"[serial: {e.EventType}]");
        try { port.Open(); }
        catch (UnauthorizedAccessException) { port.Dispose(); throw new IOException($"{s.PortName} is in use by another program."); }
        catch (FileNotFoundException)
        {
            port.Dispose();
            var ports = string.Join(", ", PortNames());
            throw new IOException($"{s.PortName} wasn't found. Is the cable plugged in? Ports on this PC: {(ports.Length > 0 ? ports : "none")}.");
        }
        _port = port;
        // Wake the console so its prompt shows.
        Send("\r");
    }

    public void Send(string data)
    {
        try { _port?.Write(data); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { Closed?.Invoke(ex); }
    }

    public void Resize(uint cols, uint rows) { }

    public void Dispose()
    {
        try { _port?.Close(); } catch (IOException) { }
        _port?.Dispose();
        _port = null;
    }
}
