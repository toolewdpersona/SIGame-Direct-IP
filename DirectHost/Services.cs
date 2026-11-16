using Microsoft.AspNetCore.SignalR;
using SICore;
using SICore.Clients;
using SICore.Connections;
using SICore.Contracts;
using SICore.Models;
using SICore.PlatformSpecific;
using SICore.Results;
using SIData;

namespace SIGame.DirectHost;

/// <summary>Bridges one authenticated SignalR connection to the existing game engine.</summary>
internal sealed class GameConnection : ConnectionBase
{
    private readonly IHubContext<GameHub> _hub;
    private readonly string _origin;
    private readonly string _address;
    private readonly Action _abort;
    private readonly DirectMetrics _metrics;
    private bool _disposed;
    public GameConnection(IHubContext<GameHub> hub, string connectionId, string origin, string address, Action abort, DirectMetrics metrics)
    { _hub = hub; ConnectionId = connectionId; _origin = origin; _address = address; _abort = abort; _metrics = metrics; }
    public override string RemoteAddress => _address;
    public override ValueTask SendMessageAsync(Message message) => new(_hub.Clients.Client(ConnectionId!).SendAsync("Receive", new
    {
        Text = message.Text.Replace(LocalFileShare.Placeholder, _origin, StringComparison.Ordinal),
        DirectAttemptId = _metrics.ObserveSent(ConnectionId!, message),
        message.Sender, message.Receiver, message.IsSystem, message.IsPrivate
    }));
    public void Receive(Message message)
    {
        // The engine stores media under a stable internal origin. Each peer sees its own
        // reachable origin (loopback, LAN or public DNS), so translate completion replies back.
        if (message.IsSystem && message.Text.StartsWith("ATOM\n", StringComparison.Ordinal))
        {
            var args = message.Text.Split('\n');
            var prefix = _origin + "/direct/media/";
            if (args.Length == 3 && args[2].StartsWith(prefix, StringComparison.Ordinal))
            {
                args[2] = LocalFileShare.Placeholder + args[2][_origin.Length..];
                message = new Message(string.Join('\n', args), message.Sender, message.Receiver, true, message.IsPrivate);
            }
        }
        OnMessageReceived(message);
    }
    public void Disconnect() => CloseCore(true);
    public void Kick()
    {
        IsAuthenticated = false;
        _ = _hub.Clients.Client(ConnectionId!).SendAsync("Disconnect");
        _abort();
        CloseCore(true);
    }
    protected override ValueTask DisposeAsync(bool disposing)
    {
        if (!_disposed) { _disposed = true; IsAuthenticated = false; }
        return ValueTask.CompletedTask;
    }
}

internal sealed class LocalFileShare(string packageId) : IFileShare
{
    public const string Placeholder = "http://sigame.direct.invalid";
    public string CreateResourceUri(ResourceKind resourceKind, Uri relativePath) =>
        resourceKind == ResourceKind.Package ? $"{Placeholder}/direct/media/{packageId}/{relativePath}" : "";
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class LocalGameHost(ILogger logger) : IGameHost
{
    public SICore.Models.HostOptions Options { get; } = new();
    public int MaxImageSizeKb => int.MaxValue;
    public int MaxAudioSizeKb => int.MaxValue;
    public int MaxVideoSizeKb => int.MaxValue;
    public bool AreCustomAvatarsSupported => false;
    public void SendError(Exception exc, bool isWarning = false) => logger.LogWarning(exc, "Local game engine error");
    public void LogWarning(string message) => logger.LogWarning("{Message}", message);
    public void SaveReport(GameResult result) { }
    public string? GetAd(string localization, out int adId) { adId = -1; return null; }
}

internal sealed class NoAvatars : IAvatarHelper
{
    public bool FileExists(string fileName) => false;
    public (ErrorCode, string)? ExtractAvatarData(string base64data, string fileName) => throw new NotSupportedException("Avatars disabled for direct hosts.");
    public void AddFile(string sourceFilePath, string fileName) => throw new NotSupportedException("Avatars disabled for direct hosts.");
}

internal sealed class LocalCoreManager : CoreManager
{
    public override byte[]? GetData(string filename) => null;
    public override bool FileExists(string filePath) => false;
}
