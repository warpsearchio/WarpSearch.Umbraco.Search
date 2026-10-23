using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace WarpSearch.Umbraco.Search.Services;

/// <summary>
/// The cultures content can be indexed in. Updating a content item removes its documents in the other cultures, so
/// a culture that is no longer published disappears from search.
/// </summary>
public interface IIndexCultureProvider
{
    Task<IReadOnlyCollection<string>> GetCulturesAsync();
}

internal sealed class LanguageIndexCultureProvider : IIndexCultureProvider
{
    private readonly ILanguageService _languageService;

    public LanguageIndexCultureProvider(ILanguageService languageService)
        => _languageService = languageService;

    public async Task<IReadOnlyCollection<string>> GetCulturesAsync()
    {
        IEnumerable<ILanguage> languages = await _languageService.GetAllAsync();
        return languages.Select(language => language.IsoCode).ToArray();
    }
}
