namespace DeadGround.Engine.Net;

public interface IServerTransport : IDisposable
{
    void Poll();
    bool TrySend(int player, ReadOnlySpan<byte> message, bool reliable = true);
    bool TryReceive(out int player, out long connectionId, Span<byte> destination, out int length);
    bool TryReceive(out int player, Span<byte> destination, out int length) =>
        TryReceive(out player, out _, destination, out length);
    bool IsPlayerConnected(int player);
    long GetPlayerConnectionId(int player);
    void DisconnectPlayer(int player, ReadOnlySpan<byte> reason = default);
    int ConnectedPlayerCount { get; }
    int InitialAdmittedPlayer { get; }
}
