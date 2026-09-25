using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CS2BotImprover.Localization;

public sealed class PluginI18n
{
    private readonly Dictionary<string, Dictionary<string, string>> _resources =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _fallbackLanguage;

    public PluginI18n(string moduleDirectory)
    {
        var directory = Path.Combine(moduleDirectory, "i18n");
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var language = Path.GetFileNameWithoutExtension(path);
                    var values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllText(path));
                    if (values != null)
                    {
                        _resources[language] = values;
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed optional language files and keep other languages usable.
                }
            }
        }

        _fallbackLanguage = _resources.ContainsKey("en")
            ? "en"
            : _resources.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                ?? string.Empty;
    }

    public string Get(CCSPlayerController? player, string key, params object[] args)
    {
        var culture = player?.GetLanguage()
            ?? PlayerLanguageManager.Instance.GetDefaultLanguage();
        var language = SelectLanguage(culture);
        var value = ResolveValue(language, key).ReplaceColorTags();
        return args.Length == 0 ? value : string.Format(culture, value, args);
    }

    private string SelectLanguage(CultureInfo culture)
    {
        foreach (var candidate in GetLanguageCandidates(culture))
        {
            if (_resources.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        return _fallbackLanguage;
    }

    private string ResolveValue(string language, string key)
    {
        if (_resources.TryGetValue(language, out var values)
            && values.TryGetValue(key, out var value))
        {
            return value;
        }

        if (_resources.TryGetValue(_fallbackLanguage, out var fallbackValues)
            && fallbackValues.TryGetValue(key, out value))
        {
            return value;
        }

        return key;
    }

    private static IEnumerable<string> GetLanguageCandidates(CultureInfo culture)
    {
        for (var current = culture; current != CultureInfo.InvariantCulture; current = current.Parent)
        {
            if (!string.IsNullOrEmpty(current.Name))
            {
                yield return current.Name;
            }
        }

        if (!string.IsNullOrEmpty(culture.TwoLetterISOLanguageName))
        {
            yield return culture.TwoLetterISOLanguageName;
        }
    }
}
