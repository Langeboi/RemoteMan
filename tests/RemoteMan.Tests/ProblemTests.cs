using System.Net.Sockets;
using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using Xunit;

namespace RemoteMan.Tests;

public class ProblemTests
{
    [Fact]
    public void RdpCodesMapToPlainCauses()
    {
        Assert.Null(ConnectionProblem.FromRdpDisconnect(1, null));
        Assert.Equal("Sign-in failed", ConnectionProblem.FromRdpDisconnect(2055, "The logon attempt failed")!.Title);
        Assert.Equal("Name not found", ConnectionProblem.FromRdpDisconnect(260, null)!.Title);
        Assert.Equal("Gateway problem", ConnectionProblem.FromRdpDisconnect(50331651, null)!.Title);
        Assert.Contains("0x807", ConnectionProblem.FromRdpDisconnect(2055, null)!.Details);
    }

    [Fact]
    public void ExceptionsMapToPlainCauses()
    {
        Assert.Equal("Connection refused", ConnectionProblem.FromException(new SocketException((int)SocketError.ConnectionRefused), EntryKind.Ssh).Title);
        Assert.Equal("Sign-in failed", ConnectionProblem.FromException(new Exception("x", new InvalidOperationException("Permission denied (password).")), EntryKind.Ssh).Title);
        Assert.Equal("Certificate problem", ConnectionProblem.FromException(new System.Security.Authentication.AuthenticationException("bad cert"), EntryKind.Ftp).Title);
        Assert.Equal("Port in use", ConnectionProblem.FromException(new IOException("COM3 is in use by another program."), EntryKind.Serial).Title);
    }

    [Fact]
    public void ProbeAttemptsExplainWhy()
    {
        var a = new EndpointAttempt(new HostEndpoint("nope.local", 22), false, TimeSpan.Zero, "No such host is known.");
        Assert.Equal("Name not found", ConnectionProblem.FromAttempts([a]).Title);
        var t = new EndpointAttempt(new HostEndpoint("10.0.0.1", 22), false, TimeSpan.Zero, "timed out");
        Assert.Equal("Host didn't answer", ConnectionProblem.FromAttempts([t, t]).Title);
    }
}
