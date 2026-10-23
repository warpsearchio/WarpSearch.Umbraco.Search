namespace WarpSearch.Umbraco.Search;

public static class WarpSearchConstants
{
    public const string ProviderName = "WarpSearch";

    /// <summary>
    /// The WarpSearch API.
    /// </summary>
    public const string ApiUrl = "https://api.warpsearch.io";

    public const string DefaultCollection = "umbraco";

    public const string ConfigurationSection = "WarpSearch";

    public const string HttpClientName = "WarpSearch";

    internal static class Cultures
    {
        public const string InvariantCulture = "inv";
    }

    internal static class SystemFields
    {
        // all indexes share one collection; each document records the index it belongs to
        public const string Index = "umb_index_s";

        public const string Key = "umb_key_s";

        public const string ObjectType = "umb_objecttype_s";

        public const string Culture = "umb_culture_s";

        public const string AccessKeys = "umb_access_ss";

        // all-texts fields are prefixed "all_" and typed by the configured text suffix
        public const string AllTextsPrefix = "all";
    }

    // the type of a field is chosen by WarpSearch from the suffix after the last underscore
    internal static class FieldKinds
    {
        public const string Texts = "tx";

        public const string TextsR1 = "r1";

        public const string TextsR2 = "r2";

        public const string TextsR3 = "r3";

        public const string Keywords = "kw_ss";

        public const string Integers = "in_is";

        public const string Decimals = "dc_ds";

        // decimals are also kept as keywords, which exact decimal facets count
        public const string DecimalKeywords = "dk_ss";

        public const string DateTimeOffsets = "dt_dts";

        public const string TextSort = "so_s";
    }
}
