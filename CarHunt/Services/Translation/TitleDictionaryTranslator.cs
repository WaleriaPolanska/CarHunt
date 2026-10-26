using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CarHunt.Services.Translation;

/// <summary>
/// Translates listing titles from a small built-in dictionary instead of calling a
/// translation service.
///
/// Measured against 256 real OtoMoto titles: only four non-English words ever appear
/// — Seria, Klasa, Limousine, Kombi — because a title is otherwise make, model and
/// engine designations, which must not be translated at all. The network service was
/// sending every title abroad to change one word per listing at best, and it corrupted
/// model codes doing so ("M340d" came back as "M340D", "mHEV" as "MHEV").
///
/// Word-by-word substitution keeps everything it does not recognise exactly as it was.
/// </summary>
public class TitleDictionaryTranslator : ITranslationService
{
    private static readonly Dictionary<string, string> Words = new(System.StringComparer.OrdinalIgnoreCase)
    {
        // Polish (OtoMoto)
        ["Seria"] = "Series",
        ["Klasa"] = "Class",
        ["Kombi"] = "Estate",
        ["Osobowe"] = "Car",
        ["Benzyna"] = "Petrol",
        ["Elektryczny"] = "Electric",
        ["Hybryda"] = "Hybrid",
        ["Automatyczna"] = "Automatic",
        ["Manualna"] = "Manual",

        // German (AutoScout24 listings from DE/AT)
        ["Limousine"] = "Saloon",
        ["Benzin"] = "Petrol",
        ["Elektro"] = "Electric",
        ["Automatik"] = "Automatic",
        ["Schaltgetriebe"] = "Manual",
        ["Gebraucht"] = "Used",
        ["Neuwagen"] = "New",
    };

    public Task<string> TranslateToEnglishAsync(string text, string sourceLanguage = "auto", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(text);

        // Split on spaces only, so punctuation and casing of everything else survive.
        var parts = text.Split(' ');
        var changed = false;

        for (var i = 0; i < parts.Length; i++)
        {
            if (!Words.TryGetValue(parts[i], out var replacement)) continue;
            parts[i] = replacement;
            changed = true;
        }

        return Task.FromResult(changed ? string.Join(' ', parts) : text);
    }
}
