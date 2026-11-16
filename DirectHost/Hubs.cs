using Microsoft.AspNetCore.SignalR;

namespace SIGame.DirectHost;

public sealed class LobbyHub(DirectGames games) : Hub
{
    private string Origin => $"{Context.GetHttpContext()!.Request.Scheme}://{Context.GetHttpContext()!.Request.Host}";
    public object GetGamesSlice(int fromId) => new { Data = games.GetGames(fromId, Origin), IsLastSlice = true };
    public override Task OnConnectedAsync() { games.AddLobby(Context.ConnectionId, Origin); return base.OnConnectedAsync(); }
    public override Task OnDisconnectedAsync(Exception? exception) { games.RemoveLobby(Context.ConnectionId); return base.OnDisconnectedAsync(exception); }
}

public sealed class GameHub(DirectGames games, DirectMetrics metrics) : Hub
{
    public Task DirectPong(string nonce) => metrics.PongAsync(Context.ConnectionId, nonce);
    public void ReportEarlyPress(int attemptId, double reactionMs) => metrics.RecordEarlyReaction(Context.ConnectionId, attemptId, reactionMs);
    public GameInfo? TryGetGameInfo(int gameId) => games.GetInfo(gameId,
        $"{Context.GetHttpContext()!.Request.Scheme}://{Context.GetHttpContext()!.Request.Host}");
    public Task<string> JoinGame2(JoinRequest request) => games.JoinAsync(Context, request);
    public Task LeaveGame() => games.LeaveAsync(Context.ConnectionId);
    public void SendMessage(WireMessage message) => games.Send(Context.ConnectionId, message);
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await games.DisconnectAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
