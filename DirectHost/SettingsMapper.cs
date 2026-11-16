using SIData;

namespace SIGame.DirectHost;

internal static class SettingsMapper
{
    public static SI.Contracts.RoomSettings Room(GameSettingsCore<AppSettingsCore> settings) => new()
    {
        HostName = settings.HumanPlayerName, Showman = Account(settings.Showman), Players = settings.Players.Select(Account).ToArray(),
        Name = settings.NetworkGameName, Password = settings.NetworkGamePassword,
        VoiceChatUri = settings.NetworkVoiceChat, IsPrivate = settings.IsPrivate, IsAutomatic = settings.IsAutomatic
    };

    private static SI.Contracts.Models.Account Account(Account account) => new()
    {
        Name = account.Name, Type = account.IsHuman ? SI.Contracts.Models.AccountType.Human : SI.Contracts.Models.AccountType.Bot,
        Gender = account.IsMale ? SI.Contracts.Models.Gender.Male : SI.Contracts.Models.Gender.Female, AvatarUri = ""
    };

    public static SI.Contracts.TimeSettings Times(TimeSettings time) => new()
    {
        QuestionSelection = time.TimeForChoosingQuestion, ThemeSelection = time.TimeForChoosingFinalTheme,
        PlayerSelection = time.TimeForGivingACat, ButtonPressing = time.TimeForThinkingOnQuestion,
        ButtonsAccepting = time.ButtonsAccepting, Answering = time.TimeForPrintingAnswer,
        SoloAnswering = time.TimeForThinkingOnSpecial, HiddenAnswering = time.TimeForFinalThinking,
        StakeMaking = time.TimeForMakingStake, ShowmanDecision = time.TimeForShowmanDecisions,
        Appellation = time.TimeForShowmanDecisions, Round = time.TimeOfRound,
        ButtonBlocking = time.TimeForBlockingButton, Reflection = time.TimeForRightAnswer,
        Image = time.ImageTime, PartialImage = time.PartialImageTime
    };

    public static SI.Contracts.RulesSettings Rules(AppSettingsCore app) => new()
    {
        ReadingSpeed = app.ReadingSpeed, FalseStart = app.FalseStart, PartialText = app.PartialText,
        PartialImages = app.PartialImages, HideImageOnAnswerAndPause = app.HideImageOnAnswerAndPause,
        PlayAllThemesInThemesRemovalRound = app.PlayAllQuestionsInFinalRound,
        AllowEveryoneToPlayHiddenStakes = app.AllowEveryoneToPlayHiddenStakes,
        Oral = app.Oral, OralPlayersActions = app.OralPlayersActions, Managed = app.Managed,
        QuestionWithButtonPenalty = (SI.Contracts.PenaltyType)app.QuestionWithButtonPenalty,
        QuestionForYourselfPenalty = (SI.Contracts.PenaltyType)app.QuestionForYourselfPenalty,
        QuestionForAllPenalty = (SI.Contracts.PenaltyType)app.QuestionForAllPenalty,
        ValidateEqualAnswersSeparately = app.ValidateEqualAnswersSeparately,
        QuestionForYourselfFactor = app.QuestionForYourselfFactor,
        ButtonPressMode = (SI.Contracts.ButtonPressMode)app.ButtonPressMode,
        PreloadRoundContent = app.PreloadRoundContent,
        GameMode = app.GameMode switch
        {
            GameModes.Sport => SI.Contracts.GameMode.Sequential,
            GameModes.Quiz => SI.Contracts.GameMode.Quiz,
            GameModes.TurnTaking => SI.Contracts.GameMode.TurnTaking,
            _ => SI.Contracts.GameMode.Classic
        },
        UseAppellations = app.UseApellations, DisplayAnswerOptionsOneByOne = app.DisplayAnswerOptionsOneByOne,
        DisplayAnswerOptionsLabels = app.DisplayAnswerOptionsLabels
    };
}
