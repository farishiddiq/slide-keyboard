using SlideKeyboard.Core.Geometry;

namespace SlideKeyboard.Core.Layout;

public enum KeyKind
{
    Character,
    Shift,
    Backspace,
    Enter,
    Space,
    Symbols,
    SymbolsMore,
    Letters,
    Language,
}

public enum KeyboardPage
{
    Letters,
    Symbols,
    SymbolsMore,
}

public sealed class KeyDef
{
    public required KeyKind Kind { get; init; }

    /// <summary>Text produced by a <see cref="KeyKind.Character"/> key.</summary>
    public string Text { get; init; } = "";

    public string Label { get; init; } = "";

    /// <summary>Key rectangle in keyboard units.</summary>
    public required RectU Bounds { get; init; }

    /// <summary>Long-press alternates (accents, digits, punctuation).</summary>
    public IReadOnlyList<string> Alternates { get; init; } = Array.Empty<string>();

    /// <summary>Small corner hint, usually the digit reachable by long-press.</summary>
    public string? Hint { get; init; }

    /// <summary>True for letter keys that take part in swipe typing.</summary>
    public bool IsLetter { get; init; }

    public Vec2 Center => Bounds.Center;

    public override string ToString() => $"{Kind}:{(Label.Length > 0 ? Label : Text)}";
}
