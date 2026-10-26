using System.Threading;
using System.Threading.Tasks;

namespace CarHunt.Services.Translation;

public interface ITranslationService
{
    Task<string> TranslateToEnglishAsync(string text, string sourceLanguage = "auto", CancellationToken ct = default);
}

