using System.Text.RegularExpressions;

namespace WarpSearch.Umbraco.Search.Configuration;

/// <summary>
/// WarpSearch settings, bound from the "WarpSearch" configuration section.
/// </summary>
public sealed partial class WarpSearchOptions
{
    /// <summary>
    /// The WarpSearch API key (ws_live_...). The writer role on the collection is all it needs.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The WarpSearch collection holding all Umbraco indexes. Use one collection per site and environment, and create
    /// it in the WarpSearch console; the provider does not create collections.
    /// </summary>
    public string Collection { get; set; } = WarpSearchConstants.DefaultCollection;

    /// <summary>
    /// How text is analyzed. <see cref="TextAnalysis.Standard"/> folds case and keeps words as written, which works
    /// for every language. <see cref="TextAnalysis.English"/> adds accent folding and English stemming.
    /// </summary>
    public TextAnalysis TextAnalysis { get; set; } = TextAnalysis.Standard;

    /// <summary>
    /// Timeout for a single HTTP request to WarpSearch, before retries.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The collection name as WarpSearch requires it: ^[a-z][a-z0-9_]*$.
    /// </summary>
    internal string CollectionName
    {
        get
        {
            var name = InvalidCharacters().Replace(Collection.ToLowerInvariant(), "_").Trim('_');
            return name.Length > 0 && char.IsAsciiLetterLower(name[0]) ? name : $"c_{name}";
        }
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex InvalidCharacters();
}

public enum TextAnalysis
{
    Standard,
    English
}
