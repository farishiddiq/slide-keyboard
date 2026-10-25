namespace SlideKeyboard.Core.Input;

public enum SpecialKey
{
    Enter,
    Tab,
    Left,
    Right,
    Delete,
}

/// <summary>
/// Destination for typed text. The Windows implementation injects input into the focused application; tests use
/// an in-memory buffer. Keeps the engine independent of the platform.
/// </summary>
public interface ITextOutput
{
    void SendText(string text);

    /// <summary>Deletes <paramref name="count"/> characters (text elements) before the caret.</summary>
    void SendBackspace(int count);

    void SendKey(SpecialKey key);
}
