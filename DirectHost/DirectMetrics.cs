using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using SIData;

namespace SIGame.DirectHost;

/// <summary>Room-local RTT probes and informational button timings; never changes answer arbitration.</summary>
public sealed class DirectMetrics(IHubContext<GameHub> hub, ILogger<DirectMetrics> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, ConnectionMetrics> _connections = new();
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    public void Register(string connectionId, int gameId, string name) =>
        _connections[connectionId] = new ConnectionMetrics(gameId, name);

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public Task PongAsync(string connectionId, string nonce)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) return Task.CompletedTask;
        lock (state)
        {
            if (state.Nonce != nonce) return Task.CompletedTask;
            state.PingMs = (int)Math.Round(Stopwatch.GetElapsedTime(state.PingSentAt).TotalMilliseconds);
            state.LastPongAt = Stopwatch.GetTimestamp();
            state.Nonce = null;
        }
        // The periodic room snapshot batches all probe replies. Reactions still publish immediately.
        return Task.CompletedTask;
    }

    public int? ObserveSent(string connectionId, Message message)
    {
        if (!message.IsSystem || message.Sender != "@" || !_connections.TryGetValue(connectionId, out var state)) return null;
        var command = message.Text.Split('\n', 2)[0];
        int? attemptId = null;
        var changed = false;
        lock (state)
        {
            if (command == "TRY")
            {
                attemptId = ++state.AttemptId;
                state.OpenedAt = Stopwatch.GetTimestamp();
                state.ClosedAt = null;
                state.ReactionMs = null;
                changed = true;
            }
            else if (command is "ENDTRY" or "QUESTION_END") state.ClosedAt = Stopwatch.GetTimestamp();
            else if (command is "CHOICE" or "QUESTION" or "STAGE")
            {
                state.AttemptId++;
                state.OpenedAt = null;
                state.ClosedAt = null;
                state.ReactionMs = null;
                state.QuestionAt = command == "STAGE" ? null : Stopwatch.GetTimestamp();
                state.EarlyAttemptId = null;
                changed = true;
            }
        }
        if (changed) _ = PublishAsync(state.GameId);
        return attemptId;
    }

    public void MarkEarlyPress(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) return;
        lock (state)
        {
            if (state.QuestionAt == null || state.EarlyAttemptId != null || state.ReactionMs != null) return;
            if (state.OpenedAt is { } opened && Stopwatch.GetElapsedTime(opened).TotalMilliseconds > Math.Max(1000, (state.PingMs ?? 1000) * 2)) return;
            state.EarlyAttemptId = state.OpenedAt == null ? state.AttemptId + 1 : state.AttemptId;
        }
    }

    public void RecordEarlyReaction(string connectionId, int attemptId, double duration)
    {
        if (!_connections.TryGetValue(connectionId, out var state) || !double.IsFinite(duration) || duration >= 0 || duration < -1_200_000) return;
        lock (state)
        {
            // A separate report is accepted only after a real, authenticated button message.
            if (attemptId != state.AttemptId || state.EarlyAttemptId != attemptId || state.ReactionMs != null || state.QuestionAt is not { } questionAt) return;
            if (-duration > Stopwatch.GetElapsedTime(questionAt).TotalMilliseconds + 1000) return;
            state.ReactionMs = (int)Math.Round(duration);
            state.EarlyAttemptId = null;
        }
        _ = PublishAsync(state.GameId);
    }

    public void RecordPress(string connectionId, int? attemptId, double? reactionMs)
    {
        if (!_connections.TryGetValue(connectionId, out var state) || reactionMs is not { } duration ||
            !double.IsFinite(duration) || duration < 0 || duration > 1_200_000) return;
        lock (state)
        {
            if (attemptId != state.AttemptId || state.OpenedAt is not { } opened || state.ReactionMs != null) return;
            // A button pressed before ENDTRY may still be travelling to the host.
            if (state.ClosedAt is { } closed && Stopwatch.GetElapsedTime(closed).TotalMilliseconds > Math.Max(1000, (state.PingMs ?? 1000) * 2)) return;
            if (duration > Stopwatch.GetElapsedTime(opened).TotalMilliseconds + 1000) return;
            state.ReactionMs = (int)Math.Round(duration);
        }
        _ = PublishAsync(state.GameId);
    }

    public async Task PublishAsync(int gameId)
    {
        await _publishGate.WaitAsync();
        try
        {
            var values = _connections.Values.Where(c => c.GameId == gameId).Select(c =>
            {
                lock (c) return new PlayerMetrics(c.Name, c.PingMs, c.ReactionMs);
            }).ToArray();
            await hub.Clients.Group("game:" + gameId).SendAsync("DirectMetrics", values);
        }
        catch (Exception error) { logger.LogDebug(error, "Could not publish direct connection metrics"); }
        finally { _publishGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var pair in _connections)
                {
                    string nonce;
                    lock (pair.Value)
                    {
                        var state = pair.Value;
                        if (state.Nonce != null && Stopwatch.GetElapsedTime(state.PingSentAt).TotalSeconds < 8) continue;
                        if (state.LastPongAt == 0 || Stopwatch.GetElapsedTime(state.LastPongAt).TotalSeconds > 8) state.PingMs = null;
                        state.Nonce = nonce = Guid.NewGuid().ToString("N");
                        state.PingSentAt = Stopwatch.GetTimestamp();
                    }
                    await hub.Clients.Client(pair.Key).SendAsync("DirectPing", nonce, stoppingToken);
                }
                foreach (var gameId in _connections.Values.Select(c => c.GameId).Distinct()) await PublishAsync(gameId);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private sealed class ConnectionMetrics(int gameId, string name)
    {
        public int GameId { get; } = gameId;
        public string Name { get; } = name;
        public string? Nonce;
        public long PingSentAt;
        public long LastPongAt;
        public int? PingMs;
        public int AttemptId;
        public long? OpenedAt;
        public long? ClosedAt;
        public int? ReactionMs;
        public long? QuestionAt;
        public int? EarlyAttemptId;
    }

    public sealed record PlayerMetrics(string Name, int? PingMs, int? ReactionMs);
}
