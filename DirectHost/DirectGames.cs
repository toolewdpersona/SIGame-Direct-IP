using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using SICore;
using SICore.Network;
using SICore.Network.Configuration;
using SICore.Network.Servers;
using SICore.Special;
using SIData;
using SIPackages;

namespace SIGame.DirectHost;

/// <summary>Owns local packages, game engines and authenticated connection associations.</summary>
public sealed class DirectGames : IAsyncDisposable
{
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SIGame.DirectHost", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<string, UploadedPackage> _packages = new();
    private readonly ConcurrentDictionary<int, HostedGame> _games = new();
    private readonly ConcurrentDictionary<string, GameConnection> _connections = new();
    private readonly ConcurrentDictionary<string, string> _lobbyOrigins = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private long _packageBytes;
    private readonly IHubContext<GameHub> _gameHub;
    private readonly IHubContext<LobbyHub> _lobbyHub;
    private readonly ILogger<DirectGames> _logger;
    private readonly DirectAvatars _avatars;
    private readonly DirectMetrics _metrics;
    private int _nextId;

    public DirectGames(IHubContext<GameHub> gameHub, IHubContext<LobbyHub> lobbyHub, ILogger<DirectGames> logger, DirectAvatars avatars, DirectMetrics metrics)
    {
        _gameHub = gameHub; _lobbyHub = lobbyHub; _logger = logger;
        _avatars = avatars;
        _metrics = metrics;
        Directory.CreateDirectory(_root);
        _ = new LocalCoreManager();
    }

    public static string[] Bots(string culture) => StoredPersonsRegistry.GetDefaultPlayers(Culture(culture), "")
        .Select(p => p.Name).ToArray();

    internal static string Culture(string? value) => value?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true ? "ru-RU" : "en-US";

    public async Task<PackageDescriptor> UploadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await _uploadGate.WaitAsync(cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(_root, id);
        SIDocument? document = null;
        var accepted = false;
        try
        {
            if (_packages.Count >= 16 || _packageBytes >= 4L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Хранилище пакетов этого сеанса заполнено. Перезапустите хост.");
            Directory.CreateDirectory(folder);
            var archive = Path.Combine(folder, "package.siq");
            await using (var target = File.Create(archive)) await file.CopyToAsync(target, cancellationToken);
            DirectSecurity.CheckPackage(archive);
            // The upstream extractor hashes media names and bounds actual expanded bytes.
            var media = Path.Combine(folder, "media");
            document = await SIDocument.ExtractToFolderAndLoadAsync(archive, media,
                Math.Min(2L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024 - _packageBytes), cancellationToken);
            File.Delete(archive);
            if (document.Package.Rounds.Count == 0) throw new InvalidDataException("В пакете нет раундов.");
            var bytes = Directory.EnumerateFiles(media, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            _packages[id] = new UploadedPackage(id, media, document, bytes);
            _packageBytes += bytes;
            accepted = true;
            return new PackageDescriptor(1, "direct:" + id);
        }
        finally
        {
            try
            {
                if (!accepted)
                {
                    document?.Dispose();
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                }
            }
            finally { _uploadGate.Release(); }
        }
    }

    public string? MediaPath(string id, string category, string name)
    {
        if (!_packages.TryGetValue(id, out var package) || category is not ("Images" or "Audio" or "Video" or "Html")) return null;
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or "..") return null;
        var root = Path.GetFullPath(package.MediaDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, category, name));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    public async Task<object> CreateAsync(RunRequest request, string origin)
    {
        await _gate.WaitAsync();
        try
        {
            if (_games.Count >= 8) throw new InvalidOperationException("Достигнут предел: 8 локальных игр.");
            var settings = request.GameSettings ?? throw new ArgumentException("Нет настроек игры.");
            if (!ValidName(settings.HumanPlayerName) || settings.Showman == null || settings.Players.Length is < 1 or > 24)
                throw new ArgumentException("Некорректный ведущий или состав игроков.");
            if (settings.NetworkGamePassword.Length > 128 || settings.NetworkGameName.Length > 100)
                throw new ArgumentException("Слишком длинное название или пароль.");
            var key = request.PackageInfo?.Uri;
            if (key == null || !key.StartsWith("direct:", StringComparison.Ordinal) || !_packages.TryGetValue(key[7..], out var package) || package.InUse)
                throw new ArgumentException("Сначала загрузите локальный пакет .siq.");

            var id = Interlocked.Increment(ref _nextId);
            var node = new PrimaryNode(NodeConfiguration.Default);
            node.Error += (error, warning) => _logger.LogWarning(error, "Game {Id} engine message", id);
            var room = SettingsMapper.Room(settings);
            var culture = Culture(settings.AppSettings.Culture);
            var game = GameRunner.CreateGame(node, room, SettingsMapper.Times(settings.AppSettings.TimeSettings),
                SettingsMapper.Rules(settings.AppSettings), culture, package.Document,
                new LocalGameHost(_logger), new LocalFileShare(package.Id),
                StoredPersonsRegistry.GetDefaultPlayers(culture, ""), StoredPersonsRegistry.GetDefaultShowmans(culture, ""),
                request.ComputerAccounts ?? [], new NoAvatars(), null, null, settings.NetworkGameName);
            var hosted = new HostedGame(id, game, node, package, settings, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            _games[id] = hosted;
            package.InUse = true;
            game.PersonsChanged += (_, _, _) => _ = BroadcastChangedAsync(hosted);
            game.DisconnectRequested += (_, name) =>
            {
                foreach (var connection in _connections.Values.Where(c => c.GameId == id && c.UserName == name))
                    connection.Kick();
            };
            game.Run();
            ScheduleOwnerTimeout(hosted);
            await BroadcastLobbyAsync("GameCreated", hosted);
            return new { isSuccess = true, hostUri = origin, gameId = id, isHost = true, errorType = 0, hostToken = hosted.HostToken };
        }
        finally { _gate.Release(); }
    }

    public GameInfo? GetInfo(int id, string origin) => _games.TryGetValue(id, out var game) ? Info(game, origin) : null;
    public GameInfo[] GetGames(int fromId, string origin) => _games.Values.Where(g => g.Id >= fromId).OrderBy(g => g.Id).Select(g => Info(g, origin)).ToArray();
    public void AddLobby(string connectionId, string origin) => _lobbyOrigins[connectionId] = origin;
    public void RemoveLobby(string connectionId) => _lobbyOrigins.TryRemove(connectionId, out _);
    private Task BroadcastLobbyAsync(string method, HostedGame game) => Task.WhenAll(_lobbyOrigins.Select(pair =>
        _lobbyHub.Clients.Client(pair.Key).SendAsync(method, Info(game, pair.Value))));

    private static GameInfo Info(HostedGame game, string origin)
    {
        var data = game.Game.ClientData;
        var persons = new List<PersonInfo> { new(data.ShowMan.IsConnected, data.ShowMan.Name, "Showman") };
        persons.AddRange(data.Players.Select(p => new PersonInfo(p.IsConnected, p.Name, "Player")));
        persons.AddRange(data.Viewers.Select(p => new PersonInfo(p.IsConnected, p.Name, "Viewer")));
        var stage = data.Stage switch { GameStage.Before => "Created", GameStage.Begin => "Started", GameStage.Round => "Round", _ => "Finished" };
        var started = data.Stage != GameStage.Before;
        return new GameInfo(origin, game.Id, game.Settings.NetworkGameName, Culture(game.Settings.AppSettings.Culture),
            game.Settings.AppSettings.GameMode.ToString(), game.Settings.HumanPlayerName,
            game.Package.Document.Package.Name, !string.IsNullOrEmpty(game.Settings.NetworkGamePassword), persons.ToArray(),
            0, 1, game.Created.ToString("O"), "", stage, "", started, game.Created.ToString("O"));
    }

    private async Task BroadcastChangedAsync(HostedGame game)
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                // Serialize notifications with deletion so a late engine event cannot resurrect a closed room in the lobby.
                if (!_games.ContainsKey(game.Id)) return;
                var info = Info(game, "");
                await BroadcastLobbyAsync("GameChanged", game);
                await _gameHub.Clients.Group("game:" + game.Id).SendAsync("GamePersonsChanged", game.Id, info.Persons);
            }
            finally { _gate.Release(); }
        }
        catch (Exception error) { _logger.LogDebug(error, "Game info broadcast failed"); }
    }

    public async Task<string> JoinAsync(HubCallerContext context, JoinRequest request)
    {
        if (!ValidName(request.UserName)) return "InvalidUserName";
        if (!Enum.TryParse<GameRole>(request.Role, false, out var role) || !Enum.IsDefined(role)) return "InvalidRole";
        await _gate.WaitAsync();
        try
        {
            if (!_games.TryGetValue(request.GameId, out var hosted)) return "GameNotFound";
            if (_connections.ContainsKey(context.ConnectionId)) return "NameIsOccupied";
            if (request.UserName == hosted.Settings.HumanPlayerName && request.HostToken != hosted.HostToken) return "AuthorizationFailed";
            var http = context.GetHttpContext()!;
            var origin = $"{http.Request.Scheme}://{http.Request.Host}";
            var connection = new GameConnection(_gameHub, context.ConnectionId, origin, http.Connection.RemoteIpAddress?.ToString() ?? "", context.Abort, _metrics)
            { UserName = request.UserName, GameId = request.GameId, IsAuthenticated = true };
            connection.Clients.Add(request.UserName);
            if (!await hosted.Node.AddConnectionAsync(connection)) return "Forbidden";
            var result = await hosted.Game.AuthenticateAsync(request.UserName, request.Sex == "Male", role, request.Password);
            if (result != AuthenticationResult.Ok)
            {
                await hosted.Node.RemoveConnectionAsync(connection, false);
                return result.ToString();
            }
            _connections[context.ConnectionId] = connection;
            if (connection.UserName == hosted.Settings.HumanPlayerName)
            {
                hosted.OwnerTimeout?.Cancel();
                hosted.OwnerTimeout?.Dispose();
                hosted.OwnerTimeout = null;
            }
            await _gameHub.Groups.AddToGroupAsync(context.ConnectionId, "game:" + hosted.Id);
            _metrics.Register(context.ConnectionId, hosted.Id, request.UserName);
            await _metrics.PublishAsync(hosted.Id);
            return "Success";
        }
        finally { _gate.Release(); }
    }

    public void Send(string connectionId, WireMessage message)
    {
        if (!_connections.TryGetValue(connectionId, out var connection) || !connection.IsAuthenticated)
            throw new HubException("Сначала подключитесь к игре.");
        if (message.Text == null || message.Text.Length > 65536) throw new HubException("Сообщение слишком длинное.");
        // Never trust the wire sender, nor accept node/server commands from players.
        var command = message.Text.Split('\n', 2)[0];
        if (message.IsSystem && command is "CONNECT" or "DISCONNECT" or "GAME_CLOSED" or "REFUSE")
            throw new HubException("Недопустимая команда.");
        var receiver = message.IsSystem ? message.Receiver ?? NetworkConstants.GameName : NetworkConstants.Everybody;
        if (message.IsSystem && receiver != NetworkConstants.GameName)
            throw new HubException("Системные команды адресуются игровому движку.");
        var text = message.Text;
        if (message.IsSystem && command == "AVATAR")
        {
            var args = text.Split('\n');
            // Direct clients use the single avatar slot for both images and local video files.
            // The validated, host-owned URL extension tells the recipient how to render it.
            if (args.Length != 3 || args[1] != "image") throw new HubException("Выберите файл аватарки PNG, JPEG, GIF, MP4 или WebM.");
            try { text = "AVATAR\nimage\n" + _avatars.NormalizeUri(args[2]); }
            catch (InvalidDataException error) { throw new HubException(error.Message); }
        }
        if (message.IsSystem && command == "I" && _games.TryGetValue(connection.GameId, out var room) &&
            room.Game.ClientData.Players.Any(p => p.Name == connection.UserName) && !room.Game.ClientData.TInfo.Pause)
        {
            if (message.DirectEarlyPress) _metrics.MarkEarlyPress(connectionId);
            else _metrics.RecordPress(connectionId, message.DirectAttemptId, message.DirectReactionMs);
        }
        connection.Receive(new Message(text, connection.UserName, receiver, message.IsSystem, false));
        if (message.IsSystem && text == "AVATAR\nimage\n")
            _ = _gameHub.Clients.Group("game:" + connection.GameId).SendAsync("Receive", new
            {
                Text = $"AVATAR\n{connection.UserName}\nimage\n", Sender = NetworkConstants.GameName,
                Receiver = NetworkConstants.Everybody, IsSystem = true, IsPrivate = false
            });
    }

    public async Task DisconnectAsync(string connectionId)
    {
        await _gate.WaitAsync();
        try
        {
            if (_connections.TryRemove(connectionId, out var connection))
            {
                _metrics.Remove(connectionId);
                connection.Disconnect();
                await _gameHub.Groups.RemoveFromGroupAsync(connectionId, "game:" + connection.GameId);
                await _metrics.PublishAsync(connection.GameId);
                if (_games.TryGetValue(connection.GameId, out var hosted) && connection.UserName == hosted.Settings.HumanPlayerName)
                    ScheduleOwnerTimeout(hosted);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Explicit exit closes only the authenticated owner's room; a guest can only leave their own seat.</summary>
    public async Task LeaveAsync(string connectionId)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_connections.TryGetValue(connectionId, out var connection)) return;
            if (_games.TryGetValue(connection.GameId, out var hosted) && connection.UserName == hosted.Settings.HumanPlayerName)
                await CloseGameAsync(hosted, connectionId);
            // Guests disconnect through the regular transport path after the invocation completes.
        }
        finally { _gate.Release(); }
    }

    private void ScheduleOwnerTimeout(HostedGame hosted)
    {
        hosted.OwnerTimeout?.Cancel();
        hosted.OwnerTimeout?.Dispose();
        hosted.OwnerTimeout = new CancellationTokenSource();
        _ = ExpireOwnerAsync(hosted, hosted.OwnerTimeout.Token);
    }

    private async Task ExpireOwnerAsync(HostedGame hosted, CancellationToken token)
    {
        try
        {
            // A temporary transport loss may reconnect with the existing owner token. Explicit exits do not wait.
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            await _gate.WaitAsync(token);
            try
            {
                if (_games.ContainsKey(hosted.Id) && !_connections.Values.Any(c =>
                    c.GameId == hosted.Id && c.UserName == hosted.Settings.HumanPlayerName))
                    await CloseGameAsync(hosted, null);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _logger.LogWarning(error, "Could not expire room {Id}", hosted.Id); }
    }

    // Called under _gate: detach the room before disposing the engine and its extracted media.
    private async Task CloseGameAsync(HostedGame hosted, string? leavingConnectionId)
    {
        if (!_games.TryRemove(hosted.Id, out _)) return;
        hosted.OwnerTimeout?.Cancel();
        hosted.OwnerTimeout?.Dispose();
        hosted.OwnerTimeout = null;
        var peers = _connections.Values.Where(c => c.GameId == hosted.Id).ToArray();
        foreach (var peer in peers)
        {
            var id = peer.ConnectionId!;
            _connections.TryRemove(id, out _);
            _metrics.Remove(id);
            if (id != leavingConnectionId)
                await _gameHub.Clients.Client(id).SendAsync("Receive", new
                {
                    Text = "GAME_CLOSED", Sender = NetworkConstants.GameName,
                    Receiver = NetworkConstants.Everybody, IsSystem = true, IsPrivate = false
                });
            await _gameHub.Groups.RemoveFromGroupAsync(id, "game:" + hosted.Id);
        }
        await Task.WhenAll(_lobbyOrigins.Keys.Select(id => _lobbyHub.Clients.Client(id).SendAsync("GameDeleted", hosted.Id)));
        try { hosted.Game.Dispose(); }
        finally { await hosted.Node.DisposeAsync(); }

        await _uploadGate.WaitAsync();
        try
        {
            hosted.Package.Document.Dispose();
            var parent = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
            var folder = Path.GetFullPath(Path.Combine(_root, hosted.Package.Id));
            if (!folder.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid package path");
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            if (_packages.TryRemove(hosted.Package.Id, out _)) _packageBytes -= hosted.Package.Bytes;
        }
        catch (IOException error) { _logger.LogWarning(error, "Could not clean package for room {Id}", hosted.Id); }
        finally { _uploadGate.Release(); }
    }

    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 50 &&
        name is not ("@" or "*" or "_") && !name.Any(char.IsControl);

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _games.Values)
        {
            hosted.OwnerTimeout?.Cancel();
            hosted.OwnerTimeout?.Dispose();
            hosted.Game.Dispose();
            await hosted.Node.DisposeAsync();
        }
        foreach (var package in _packages.Values) package.Document.Dispose();
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SIGame.DirectHost")) + Path.DirectorySeparatorChar;
        var ownedRoot = Path.GetFullPath(_root);
        if (ownedRoot.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(ownedRoot))
        {
            try { Directory.Delete(ownedRoot, recursive: true); }
            catch (IOException error) { _logger.LogWarning(error, "Could not clean local session files"); }
        }
        _gate.Dispose();
    }

    private sealed record HostedGame(int Id, Game Game, PrimaryNode Node, UploadedPackage Package,
        GameSettingsCore<AppSettingsCore> Settings, string HostToken)
    {
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public CancellationTokenSource? OwnerTimeout { get; set; }
    }
    private sealed record UploadedPackage(string Id, string MediaDirectory, SIDocument Document, long Bytes)
    {
        public bool InUse { get; set; }
    }
}
