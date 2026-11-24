using Pulse.Telemetry;

namespace Pulse.Models;

public sealed class ConnectionRow : ObservableRow
{
    private string _host = "", _pid = "", _protocol = "", _local = "", _remote = "", _state = "";

    internal ConnectionRow() { }
    internal int ProcessId { get; private set; }

    // The process that owns the socket.
    public string Host => _host;
    public string Pid => _pid;
    public string Protocol => _protocol;
    public string Local => _local;
    public string Remote => _remote;
    public string State => _state;

    private string _received = "", _sent = "";
    public string Received => _received;
    public string Sent => _sent;

    internal void Update(TcpConnection connection, string process, (double? Received, double? Sent) rate = default)
    {
        ProcessId = connection.Pid;
        Set(ref _received, Format.Bytes(rate.Received, true), nameof(Received));
        Set(ref _sent, Format.Bytes(rate.Sent, true), nameof(Sent));
        Set(ref _host, process, nameof(Host));
        Set(ref _pid, connection.Pid.ToString(), nameof(Pid));
        Set(ref _protocol, connection.Protocol, nameof(Protocol));
        Set(ref _local, connection.Local, nameof(Local));
        Set(ref _remote, connection.Remote, nameof(Remote));
        Set(ref _state, connection.Udp ? "" : StateName(connection.State), nameof(State));
    }

    // Protocol state names are identifiers and stay untranslated, as in netstat.
    internal static string StateName(TcpState state) => state switch
    {
        TcpState.Closed => "CLOSED",
        TcpState.Listening => "LISTENING",
        TcpState.SynSent => "SYN_SENT",
        TcpState.SynReceived => "SYN_RECEIVED",
        TcpState.Established => "ESTABLISHED",
        TcpState.FinWait1 => "FIN_WAIT_1",
        TcpState.FinWait2 => "FIN_WAIT_2",
        TcpState.CloseWait => "CLOSE_WAIT",
        TcpState.Closing => "CLOSING",
        TcpState.LastAck => "LAST_ACK",
        TcpState.TimeWait => "TIME_WAIT",
        _ => "DELETE_TCB"
    };
}
