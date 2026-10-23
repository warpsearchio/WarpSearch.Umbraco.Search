using System.Globalization;
using System.Text.Json.Nodes;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using static WarpSearch.Umbraco.Search.WarpSearchConstants;

namespace WarpSearch.Umbraco.Search.Services;

/// <summary>
/// Builds one WarpSearch document per content variation (culture). Segment variant values are stored in their own
/// fields within the culture document.
/// </summary>
internal sealed class DocumentBuilder
{
    // the number of texts used to build a sortable value for a text field
    private const int MaxSortableTexts = 5;

    private readonly FieldNames _fieldNames;

    public DocumentBuilder(FieldNames fieldNames)
        => _fieldNames = fieldNames;

    // all indexes share the collection, so the id leads with the index
    public static string DocumentId(string indexAlias, Guid key, string? culture)
        => $"{indexAlias.ToLowerInvariant()}.{key:D}.{IndexCulture(culture)}";

    public static string IndexCulture(string? culture)
        => culture?.ToLowerInvariant() ?? Cultures.InvariantCulture;

    public static string FormatDate(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // 1.50 and 1.5 are the same facet value
    public static string FormatDecimal(decimal value)
        => (value / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns the documents with their ids; the ids are not part of the documents.
    /// </summary>
    public (string Id, JsonObject Document)[] Build(
        string indexAlias,
        Guid key,
        UmbracoObjectTypes objectType,
        IEnumerable<Variation> variations,
        IEnumerable<IndexField> fields,
        ContentProtection? protection)
    {
        IndexField[] fieldsAsArray = fields as IndexField[] ?? fields.ToArray();

        // content without access restrictions is indexed with the empty key, which every search may see
        Guid[] accessKeys = protection?.AccessIds.Any() is true ? protection.AccessIds.ToArray() : [Guid.Empty];

        // several variations can share a culture when only segments differ; one document per culture
        return variations
            .Select(variation => variation.Culture)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(culture => (DocumentId(indexAlias, key, culture), BuildDocument(indexAlias, key, objectType, culture, accessKeys, fieldsAsArray)))
            .ToArray();
    }

    private JsonObject BuildDocument(string indexAlias, Guid key, UmbracoObjectTypes objectType, string? culture, Guid[] accessKeys, IndexField[] fields)
    {
        var document = new JsonObject
        {
            [SystemFields.Index] = indexAlias,
            [SystemFields.Key] = key.ToString("D"),
            [SystemFields.ObjectType] = objectType.ToString(),
            [SystemFields.Culture] = IndexCulture(culture),
            [SystemFields.AccessKeys] = StringArray(accessKeys.Select(accessKey => accessKey.ToString("D")))
        };

        // the fields of this culture, including invariant fields, merged by name and segment
        IndexField[] variationFields = fields
            .Where(field => field.Culture is null || string.Equals(field.Culture, culture, StringComparison.OrdinalIgnoreCase))
            .GroupBy(field => (field.FieldName, field.Segment))
            .Select(group => new IndexField(group.Key.FieldName, Merge(group.Select(field => field.Value)), culture, group.Key.Segment))
            .ToArray();

        foreach (IndexField field in variationFields)
        {
            AddFieldValues(document, field);
        }

        // "all texts" fields back the free text query; segment fields also hold the default segment texts,
        // so a search in a segment finds texts that do not vary by segment
        IndexField[] defaultSegmentFields = variationFields.Where(field => field.Segment is null).ToArray();
        AddAllTexts(document, defaultSegmentFields, null);

        foreach (IGrouping<string?, IndexField> segmentFields in variationFields.Where(field => field.Segment is not null).GroupBy(field => field.Segment))
        {
            AddAllTexts(document, defaultSegmentFields.Concat(segmentFields).ToArray(), segmentFields.Key);
        }

        return document;
    }

    private void AddFieldValues(JsonObject document, IndexField field)
    {
        IndexValue value = field.Value;
        var segment = field.Segment;

        AddText(document, _fieldNames.Texts(field.FieldName, FieldKinds.Texts, segment), value.Texts);
        AddText(document, _fieldNames.Texts(field.FieldName, FieldKinds.TextsR1, segment), value.TextsR1);
        AddText(document, _fieldNames.Texts(field.FieldName, FieldKinds.TextsR2, segment), value.TextsR2);
        AddText(document, _fieldNames.Texts(field.FieldName, FieldKinds.TextsR3, segment), value.TextsR3);

        if (value.Keywords?.Any() is true)
        {
            document[FieldNames.Field(field.FieldName, FieldKinds.Keywords, segment)] = StringArray(value.Keywords);
        }

        if (value.Integers?.Any() is true)
        {
            document[FieldNames.Field(field.FieldName, FieldKinds.Integers, segment)] = new JsonArray(value.Integers.Select(i => (JsonNode?)i).ToArray());
        }

        if (value.Decimals?.Any() is true)
        {
            document[FieldNames.Field(field.FieldName, FieldKinds.Decimals, segment)] = new JsonArray(value.Decimals.Select(d => (JsonNode?)(double)d).ToArray());
            document[FieldNames.Field(field.FieldName, FieldKinds.DecimalKeywords, segment)] = StringArray(value.Decimals.Select(FormatDecimal));
        }

        if (value.DateTimeOffsets?.Any() is true)
        {
            document[FieldNames.Field(field.FieldName, FieldKinds.DateTimeOffsets, segment)] = StringArray(value.DateTimeOffsets.Select(FormatDate));
        }

        // a keyword holding the leading texts, for sorting by a text field
        var sortableTexts = (value.TextsR1 ?? [])
            .Concat(value.TextsR2 ?? [])
            .Concat(value.TextsR3 ?? [])
            .Concat(value.Texts ?? [])
            .Where(text => string.IsNullOrWhiteSpace(text) is false)
            .Take(MaxSortableTexts)
            .ToArray();
        if (sortableTexts.Length > 0)
        {
            document[FieldNames.Field(field.FieldName, FieldKinds.TextSort, segment)] = string.Join(" ", sortableTexts).ToLowerInvariant();
        }
    }

    private void AddAllTexts(JsonObject document, IndexField[] fields, string? segment)
    {
        AddText(document, _fieldNames.AllTexts(FieldKinds.Texts, segment), fields.SelectMany(field => field.Value.Texts ?? []));
        AddText(document, _fieldNames.AllTexts(FieldKinds.TextsR1, segment), fields.SelectMany(field => field.Value.TextsR1 ?? []));
        AddText(document, _fieldNames.AllTexts(FieldKinds.TextsR2, segment), fields.SelectMany(field => field.Value.TextsR2 ?? []));
        AddText(document, _fieldNames.AllTexts(FieldKinds.TextsR3, segment), fields.SelectMany(field => field.Value.TextsR3 ?? []));
    }

    // multiple texts are joined into one value; the text analysis splits them into words anyway
    private static void AddText(JsonObject document, string fieldName, IEnumerable<string>? texts)
    {
        var values = texts?.Where(text => string.IsNullOrWhiteSpace(text) is false).ToArray() ?? [];
        if (values.Length > 0)
        {
            document[fieldName] = string.Join("\n", values);
        }
    }

    private static JsonArray StringArray(IEnumerable<string> values)
        => new(values.Select(value => (JsonNode?)value).ToArray());

    private static IndexValue Merge(IEnumerable<IndexValue> values)
    {
        IndexValue[] valuesAsArray = values as IndexValue[] ?? values.ToArray();
        return valuesAsArray.Length is 1
            ? valuesAsArray[0]
            : new IndexValue
            {
                Texts = NullIfEmpty(valuesAsArray.SelectMany(v => v.Texts ?? [])),
                TextsR1 = NullIfEmpty(valuesAsArray.SelectMany(v => v.TextsR1 ?? [])),
                TextsR2 = NullIfEmpty(valuesAsArray.SelectMany(v => v.TextsR2 ?? [])),
                TextsR3 = NullIfEmpty(valuesAsArray.SelectMany(v => v.TextsR3 ?? [])),
                Keywords = NullIfEmpty(valuesAsArray.SelectMany(v => v.Keywords ?? [])),
                Integers = NullIfEmpty(valuesAsArray.SelectMany(v => v.Integers ?? [])),
                Decimals = NullIfEmpty(valuesAsArray.SelectMany(v => v.Decimals ?? [])),
                DateTimeOffsets = NullIfEmpty(valuesAsArray.SelectMany(v => v.DateTimeOffsets ?? []))
            };
    }

    private static T[]? NullIfEmpty<T>(IEnumerable<T> values)
    {
        T[] array = values.ToArray();
        return array.Length > 0 ? array : null;
    }
}
