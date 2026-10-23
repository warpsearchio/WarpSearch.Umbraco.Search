using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Search.Core.Configuration;
using Umbraco.Cms.Search.Core.Models.Configuration;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;
using Microsoft.Extensions.Options;

namespace WarpSearch.Umbraco.Search.Site.DemoData;

/// <summary>
/// Seeds a small product catalog in English and Danish on first start, so there is something to search.
/// </summary>
public sealed class DemoDataComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
        => builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, DemoDataSeeder>();
}

public sealed class DemoDataSeeder : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private static readonly (string English, string Danish, string Description, string Category, int Price)[] Products =
    [
        ("Trail running shoes", "Trailløbesko", "Light shoes with a grippy sole for muddy trails.", "shoes", 129),
        ("Road running shoes", "Vejløbesko", "Cushioned shoes for long runs on asphalt.", "shoes", 119),
        ("Hiking boots", "Vandrestøvler", "Waterproof leather boots for mountain hikes.", "shoes", 189),
        ("Rain jacket", "Regnjakke", "Breathable waterproof jacket that packs into its own pocket.", "clothing", 149),
        ("Down jacket", "Dunjakke", "Warm and light jacket for cold mornings.", "clothing", 229),
        ("Merino t-shirt", "Merino t-shirt", "Soft wool shirt that does not smell after a long day.", "clothing", 59),
        ("Running shorts", "Løbeshorts", "Shorts with a zip pocket for keys.", "clothing", 39),
        ("Headlamp", "Pandelampe", "Bright rechargeable headlamp for night runs.", "gear", 49),
        ("Water bottle", "Vandflaske", "Insulated steel bottle that keeps drinks cold.", "gear", 24),
        ("Trekking poles", "Vandrestave", "Folding carbon poles for steep climbs.", "gear", 99),
        ("Backpack 30L", "Rygsæk 30L", "Day pack with a rain cover and hip belt.", "gear", 119),
        ("Running watch", "Løbeur", "GPS watch with heart rate and trail maps.", "electronics", 349)
    ];

    private readonly IContentService _contentService;
    private readonly IContentTypeService _contentTypeService;
    private readonly IDataTypeService _dataTypeService;
    private readonly ILanguageService _languageService;
    private readonly IShortStringHelper _shortStringHelper;
    private readonly IRuntimeState _runtimeState;
    private readonly IContentIndexingService _contentIndexingService;
    private readonly IOriginProvider _originProvider;
    private readonly IndexOptions _indexOptions;
    private readonly ILogger<DemoDataSeeder> _logger;

    public DemoDataSeeder(
        IContentService contentService,
        IContentTypeService contentTypeService,
        IDataTypeService dataTypeService,
        ILanguageService languageService,
        IShortStringHelper shortStringHelper,
        IRuntimeState runtimeState,
        IContentIndexingService contentIndexingService,
        IOriginProvider originProvider,
        IOptions<IndexOptions> indexOptions,
        ILogger<DemoDataSeeder> logger)
    {
        _contentService = contentService;
        _contentTypeService = contentTypeService;
        _dataTypeService = dataTypeService;
        _languageService = languageService;
        _shortStringHelper = shortStringHelper;
        _runtimeState = runtimeState;
        _contentIndexingService = contentIndexingService;
        _originProvider = originProvider;
        _indexOptions = indexOptions.Value;
        _logger = logger;
    }

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (_runtimeState.Level is not RuntimeLevel.Run || _contentTypeService.Get("catalog") is not null)
        {
            return;
        }

        _logger.LogInformation("Seeding the demo product catalog");

        if (await _languageService.GetAsync("da-DK") is null)
        {
            await _languageService.CreateAsync(new Language("da-DK", "Danish"), Constants.Security.SuperUserKey);
        }

        IDataType[] dataTypes = (await _dataTypeService.GetAllAsync()).ToArray();
        IDataType DataType(string editorAlias) => dataTypes.First(dataType => dataType.EditorAlias == editorAlias);

        var catalogType = new ContentType(_shortStringHelper, Constants.System.Root)
        {
            Alias = "catalog",
            Name = "Catalog",
            Icon = "icon-shopping-basket",
            AllowedAsRoot = true,
            Variations = ContentVariation.Culture
        };
        await _contentTypeService.CreateAsync(catalogType, Constants.Security.SuperUserKey);

        var productType = new ContentType(_shortStringHelper, Constants.System.Root)
        {
            Alias = "product",
            Name = "Product",
            Icon = "icon-box",
            Variations = ContentVariation.Culture
        };
        productType.AddPropertyType(new PropertyType(_shortStringHelper, DataType(Constants.PropertyEditors.Aliases.TextArea), "description")
        {
            Name = "Description",
            Variations = ContentVariation.Culture
        }, "content", "Content");
        productType.AddPropertyType(new PropertyType(_shortStringHelper, DataType(Constants.PropertyEditors.Aliases.TextBox), "category")
        {
            Name = "Category"
        }, "content", "Content");
        productType.AddPropertyType(new PropertyType(_shortStringHelper, DataType(Constants.PropertyEditors.Aliases.Integer), "price")
        {
            Name = "Price"
        }, "content", "Content");
        await _contentTypeService.CreateAsync(productType, Constants.Security.SuperUserKey);

        catalogType.AllowedContentTypes = [new ContentTypeSort(productType.Key, 0, productType.Alias)];
        await _contentTypeService.UpdateAsync(catalogType, Constants.Security.SuperUserKey);

        IContent catalog = _contentService.Create("Catalog", Constants.System.Root, catalogType);
        catalog.SetCultureName("Catalog", "en-US");
        catalog.SetCultureName("Katalog", "da-DK");
        _contentService.Save(catalog);
        _contentService.Publish(catalog, ["*"]);

        foreach ((string english, string danish, string description, string category, int price) in Products)
        {
            IContent product = _contentService.Create(english, catalog.Key, productType.Alias);
            product.SetCultureName(english, "en-US");
            product.SetCultureName(danish, "da-DK");
            product.SetValue("description", description, "en-US");
            product.SetValue("description", $"{danish}. {description}", "da-DK");
            product.SetValue("category", category);
            product.SetValue("price", price);
            _contentService.Save(product);
            _contentService.Publish(product, ["*"]);
        }

        // content changes reach the search indexes through distributed cache notifications, which are not flushed
        // outside a request, so rebuild the indexes on this server instead
        foreach (ContentIndexRegistration registration in _indexOptions.GetContentIndexRegistrations())
        {
            _contentIndexingService.Rebuild(registration.IndexAlias, _originProvider.GetCurrent());
        }
    }
}
