namespace SlideKeyboard.Core.Dictionaries;

/// <summary>Holds the installed language packs, which ones are enabled, and the active one.</summary>
public sealed class LanguageManager
{
    readonly List<LanguagePack> _all;
    List<LanguagePack> _enabled;
    LanguagePack _active;

    public LanguageManager(IReadOnlyList<LanguagePack> packs)
    {
        if (packs.Count == 0) throw new ArgumentException("No language packs were found.", nameof(packs));
        _all = packs.ToList();
        _enabled = _all.ToList();
        _active = _all[0];
    }

    public IReadOnlyList<LanguagePack> All => _all;
    public IReadOnlyList<LanguagePack> Enabled => _enabled;
    public LanguagePack Active => _active;

    public event EventHandler? ActiveChanged;
    public event EventHandler? EnabledChanged;

    public LanguagePack? Find(string? code) =>
        code == null ? null : _all.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sets the enabled languages; their order is the cycling order.</summary>
    public void SetEnabled(IEnumerable<string> codes)
    {
        var list = new List<LanguagePack>();
        foreach (var code in codes)
            if (Find(code) is { } pack && !list.Contains(pack)) list.Add(pack);
        if (list.Count == 0) list.Add(_all[0]);
        _enabled = list;
        EnabledChanged?.Invoke(this, EventArgs.Empty);
        if (!_enabled.Contains(_active)) SetActive(_enabled[0].Code);
    }

    public void SetActive(string code)
    {
        var pack = Find(code);
        if (pack == null || pack == _active || !_enabled.Contains(pack)) return;
        _active = pack;
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CycleNext()
    {
        if (_enabled.Count < 2) return;
        int i = _enabled.IndexOf(_active);
        SetActive(_enabled[(i + 1) % _enabled.Count].Code);
    }
}
