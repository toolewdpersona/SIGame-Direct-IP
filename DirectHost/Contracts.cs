using SIData;

namespace SIGame.DirectHost;

public sealed record PackageDescriptor(int Type, string Uri, string? ContentServiceUri = null, string? Secret = null, string? Source = null);
public sealed record RunRequest(GameSettingsCore<AppSettingsCore> GameSettings, PackageDescriptor PackageInfo, ComputerAccount[]? ComputerAccounts);
public sealed record JoinRequest(int GameId, string UserName, string Role, string Sex, string? Password, int? Pin, string? HostToken);
public sealed record WireMessage(string Text, string? Sender, string? Receiver, bool IsSystem = true, bool IsPrivate = false,
    int? DirectAttemptId = null, double? DirectReactionMs = null, bool DirectEarlyPress = false);
public sealed record PersonInfo(bool IsOnline, string Name, string Role);
public sealed record GameInfo(string HostUri, int GameID, string GameName, string Language, string Mode, string Owner,
    string PackageName, bool PasswordRequired, PersonInfo[] Persons, int ProgressCurrent, int ProgressTotal,
    string RealStartTime, string Rules, string Stage, string StageName, bool Started, string StartTime);
