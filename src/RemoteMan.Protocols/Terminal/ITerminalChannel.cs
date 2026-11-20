namespace RemoteMan.Protocols.Terminal;

/// <summary>A text stream the terminal tab drives: SSH shell, Telnet or a serial port.</summary>
public interface ITerminalChannel : IDisposable
{
    event Action<string>? Output;
    event Action<string>? Info;
    event Action<Exception?>? Closed;
    void Send(string data);
    void Resize(uint cols, uint rows);
}
