using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WarpSearch.Umbraco.Search.Configuration;
using static WarpSearch.Umbraco.Search.WarpSearchConstants;

namespace WarpSearch.Umbraco.Search.Services;

/// <summary>
/// Maps Umbraco Search field names to WarpSearch field names. WarpSearch has no dynamic mapping, so every field
/// carries a type suffix: "{field}_kw_ss" is a multi-valued keyword, "{field}_r1_un" is text, and so on.
/// Field names may only hold ASCII letters, digits and single underscores and must start with a letter.
/// </summary>
internal sealed partial class FieldNames
{
    private const int MaxFieldNameLength = 127;

    private readonly string _textSuffix;

    public FieldNames(IOptions<WarpSearchOptions> options)
        => _textSuffix = options.Value.TextAnalysis is TextAnalysis.English ? "t" : "un";

    public string Texts(string fieldName, string relevance, string? segment = null)
        => Field(fieldName, $"{relevance}_{_textSuffix}", segment);

    public string AllTexts(string relevance, string? segment = null)
        => Limit(segment is null
            ? $"{SystemFields.AllTextsPrefix}_{relevance}_{_textSuffix}"
            : $"{SystemFields.AllTextsPrefix}_sg_{Sanitize(segment)}_{relevance}_{_textSuffix}");

    public static string Field(string fieldName, string kind, string? segment = null)
        => Limit(segment is null
            ? $"f_{Sanitize(fieldName)}_{kind}"
            : $"f_{Sanitize(fieldName)}_sg_{Sanitize(segment)}_{kind}");

    internal static string Sanitize(string value)
    {
        var sanitized = InvalidCharacters().Replace(value, "_").Trim('_');
        return sanitized.Length is 0 ? "x" : sanitized;
    }

    // keeps the type suffix and makes over-long names unique with a short hash
    private static string Limit(string name)
    {
        if (name.Length <= MaxFieldNameLength)
        {
            return name;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12];
        var suffixStart = name.LastIndexOf('_', name.LastIndexOf('_') - 1);
        var suffix = name[suffixStart..];
        return $"{name[..(MaxFieldNameLength - suffix.Length - hash.Length - 1)].TrimEnd('_')}_{hash}{suffix}";
    }

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex InvalidCharacters();
}
