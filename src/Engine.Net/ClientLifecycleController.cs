namespace DeadGround.Engine.Net;

public enum ClientLifecycleState
{
    Inactive,
    Connecting,
    Handshaking,
    WaitingForFirstSnapshot,
    Active,
    Failed,
    Disconnected
}

public sealed class ClientLifecycleController : IDisposable
{
    private readonly Func<string, int, MatchSetup, ClientSession> _sessionFactory;
    private ClientSession? _networkSession;
    private int _connectionAttemptId;

    public ClientSession? NetworkSession => _networkSession;
    public ClientLifecycleState State { get; private set; } = ClientLifecycleState.Inactive;
    public string StatusDescription { get; private set; } = "Ready to connect.";
    public string? LastError { get; private set; }
    public int ConnectionAttemptId => _connectionAttemptId;
    public bool IsConnecting => State is ClientLifecycleState.Connecting
        or ClientLifecycleState.Handshaking
        or ClientLifecycleState.WaitingForFirstSnapshot;

    public event Action<ClientSession>? OnSessionReady;
    public event Action<string>? OnConnectionFailed;
    public event Action<string>? OnConnectionLost;

    public ClientLifecycleController(Func<string, int, MatchSetup, ClientSession>? sessionFactory = null)
    {
        _sessionFactory = sessionFactory ?? ClientSession.Connect;
    }

    public bool TryConnect(string target, MatchSetup setup, out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(setup);

        if (IsConnecting)
        {
            errorMessage = "Connection already in progress.";
            return false;
        }

        if (!NetworkAddressValidator.TryParse(target, out string host, out int port, out string? valError))
        {
            errorMessage = valError ?? "Invalid address.";
            LastError = errorMessage;
            State = ClientLifecycleState.Failed;
            StatusDescription = errorMessage;
            return false;
        }

        DisconnectInternal();

        _connectionAttemptId++;
        int thisAttempt = _connectionAttemptId;

        try
        {
            _networkSession = _sessionFactory(host, port, setup);
            State = ClientLifecycleState.Connecting;
            StatusDescription = $"Connecting to {host}:{port}...";
            LastError = null;
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            _connectionAttemptId++;
            State = ClientLifecycleState.Failed;
            StatusDescription = $"Failed to initiate connection: {ex.Message}";
            LastError = StatusDescription;
            errorMessage = StatusDescription;
            return false;
        }
    }

    public void Cancel()
    {
        _connectionAttemptId++;
        if (_networkSession is not null)
        {
            _networkSession.Dispose();
            _networkSession = null;
        }

        State = ClientLifecycleState.Inactive;
        StatusDescription = "Connection cancelled.";
        LastError = null;
    }

    public void Disconnect()
    {
        DisconnectInternal();
        State = ClientLifecycleState.Inactive;
        StatusDescription = "Disconnected.";
    }

    private void DisconnectInternal()
    {
        _connectionAttemptId++;
        if (_networkSession is not null)
        {
            _networkSession.Dispose();
            _networkSession = null;
        }
    }

    public void Advance(double elapsedSeconds)
    {
        if (_networkSession is null) return;

        int attempt = _connectionAttemptId;
        _networkSession.Advance(elapsedSeconds);

        if (attempt != _connectionAttemptId)
        {
            // The connection was cancelled or replaced during advance.
            return;
        }

        if (State == ClientLifecycleState.Active)
        {
            if (_networkSession.State == SessionConnectionState.Disconnected)
            {
                State = ClientLifecycleState.Disconnected;
                StatusDescription = "Connection to server lost.";
                LastError = StatusDescription;
                OnConnectionLost?.Invoke(StatusDescription);
            }
            else if (_networkSession.State == SessionConnectionState.Failed)
            {
                State = ClientLifecycleState.Disconnected;
                StatusDescription = TranslateFailureReason(_networkSession.StatusDescription);
                LastError = StatusDescription;
                OnConnectionLost?.Invoke(StatusDescription);
            }
            return;
        }

        if (_networkSession.State == SessionConnectionState.Failed)
        {
            State = ClientLifecycleState.Failed;
            StatusDescription = TranslateFailureReason(_networkSession.StatusDescription);
            LastError = StatusDescription;
            OnConnectionFailed?.Invoke(StatusDescription);
            return;
        }

        if (_networkSession.State == SessionConnectionState.Disconnected)
        {
            State = ClientLifecycleState.Failed;
            StatusDescription = "Server unavailable or connection timed out.";
            LastError = StatusDescription;
            OnConnectionFailed?.Invoke(StatusDescription);
            return;
        }

        if (_networkSession.State == SessionConnectionState.HandshakePending)
        {
            State = ClientLifecycleState.Handshaking;
            StatusDescription = "Authenticating with server...";
            return;
        }

        if (_networkSession.State == SessionConnectionState.Connected)
        {
            if (!_networkSession.Replica.HasSnapshots)
            {
                State = ClientLifecycleState.WaitingForFirstSnapshot;
                StatusDescription = $"Connected (Player {_networkSession.AssignedPlayerId}). Waiting for world snapshot...";
            }
            else
            {
                State = ClientLifecycleState.Active;
                StatusDescription = $"Connected (Player {_networkSession.AssignedPlayerId})";
                OnSessionReady?.Invoke(_networkSession);
            }
        }
    }

    public static string TranslateFailureReason(string? statusDescription)
    {
        if (string.IsNullOrEmpty(statusDescription))
        {
            return "Connection failed.";
        }

        if (statusDescription.Contains("ServerFull", StringComparison.OrdinalIgnoreCase))
        {
            return "Server is full (maximum 2 players).";
        }

        if (statusDescription.Contains("ProtocolVersionMismatch", StringComparison.OrdinalIgnoreCase) ||
            statusDescription.Contains("ProtocolMismatch", StringComparison.OrdinalIgnoreCase))
        {
            return $"Protocol version mismatch with server. Server requires protocol v{MatchSetup.CurrentProtocolVersion}.";
        }

        if (statusDescription.Contains("ContentMismatch", StringComparison.OrdinalIgnoreCase))
        {
            return "Game content mismatch with server.";
        }

        if (statusDescription.Contains("MapMismatch", StringComparison.OrdinalIgnoreCase))
        {
            return "Map mismatch with server.";
        }

        if (statusDescription.Contains("RuleSetMismatch", StringComparison.OrdinalIgnoreCase))
        {
            return "Rule set mismatch with server.";
        }

        if (statusDescription.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
            statusDescription.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection timed out.";
        }

        if (statusDescription.Contains("closed by server", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection closed by server.";
        }

        return statusDescription;
    }

    public static readonly string[] ExplicitCliFlags =
    [
        "--scenario",
        "--connect",
        "--smoke-test",
        "--map-preview",
        "--los-sandbox",
        "--vehicle-preview",
        "--perf-microbench",
        "--combat-feedback-probe",
        "--combat-feedback-capture",
        "--vehicle-effects-capture",
        "--order-preview-capture",
        "--minimap-demo"
    ];

    public static bool HasExplicitCliMode(string[]? userArgs, string[]? allArgs)
    {
        if (userArgs is not null)
        {
            for (int i = 0; i < userArgs.Length; i++)
            {
                if (Array.IndexOf(ExplicitCliFlags, userArgs[i]) >= 0)
                {
                    return true;
                }
            }
        }

        if (allArgs is not null)
        {
            for (int i = 0; i < allArgs.Length; i++)
            {
                if (Array.IndexOf(ExplicitCliFlags, allArgs[i]) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
