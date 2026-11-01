using System.Text.Json;

namespace CubeRadar.Cli;

/// <summary>Interface strings for Russian and English. The catalog is wwwroot/i18n.json, shared with the web view.</summary>
internal static class UiText
{
    public const string Russian = "ru";
    public const string English = "en";

    private static Dictionary<string, Dictionary<string, string>> _all = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _current = new(StringComparer.Ordinal);

    public static string Language { get; private set; } = Russian;

    /// <summary>Raised after the active language changes, on the thread that changed it.</summary>
    public static event Action? Changed;

    public static bool IsLanguage(string? language) => language is Russian or English;

    /// <summary>Reads the catalog and the saved language. Safe to call again.</summary>
    public static void LoadInstalled()
    {
        if (AppPaths.WebRoot.Length == 0) AppPaths.Init();
        if (AppPaths.WebRoot.Length > 0)
        {
            try
            {
                var json = File.ReadAllText(Path.Combine(AppPaths.WebRoot, "i18n.json"));
                _all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _all = new();
            }
        }
        var language = Russian;
        try
        {
            language = RadarSettings.Load(Path.Combine(AppPaths.DataDir, "settings.json")).Language;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        Apply(IsLanguage(language) ? language : Russian, notify: false);
    }

    public static void Set(string? language)
    {
        var next = IsLanguage(language) ? language! : Russian;
        if (next == Language && _current.Count > 0) return;
        Apply(next, notify: true);
    }

    public static string T(string key, params object[] args)
    {
        if (!_current.TryGetValue(key, out var text)
            && !(_all.GetValueOrDefault(English)?.TryGetValue(key, out text) ?? false))
            text = key;
        return args.Length == 0 ? text! : string.Format(System.Globalization.CultureInfo.CurrentCulture, text!, args);
    }

    private static void Apply(string language, bool notify)
    {
        Language = language;
        _current = _all.TryGetValue(language, out var pack) ? pack : _all.GetValueOrDefault(English) ?? new();
        if (notify) Changed?.Invoke();
    }
}
