namespace SlideKeyboard.Core.Input;

public sealed class EngineSettings
{
    public bool AutoCorrect { get; set; } = true;
    public bool AutoCapitalize { get; set; } = true;
    public bool Suggestions { get; set; } = true;
    public int SuggestionCount { get; set; } = 3;
    public bool DoubleSpacePeriod { get; set; } = true;

    /// <summary>Let swipes and suggestions use the other enabled Latin language and switch to it automatically.</summary>
    public bool AutoDetectLanguage { get; set; } = true;

    /// <summary>Show a dedicated digit row above the letters.</summary>
    public bool NumberRow { get; set; } = true;
}
