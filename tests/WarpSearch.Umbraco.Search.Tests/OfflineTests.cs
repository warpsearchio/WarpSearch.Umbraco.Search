using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using WarpSearch.Umbraco.Search.Configuration;
using WarpSearch.Umbraco.Search.Services;

namespace WarpSearch.Umbraco.Search.Tests;

/// <summary>
/// Tests that need no WarpSearch: naming rules and document layout.
/// </summary>
public class OfflineTests
{
    [TestCase("umbraco", "umbraco")]
    [TestCase("My-Site Prod", "my_site_prod")]
    [TestCase("42", "c_42")]
    public void CollectionNames_AreValid(string collection, string expected)
        => Assert.That(new WarpSearchOptions { Collection = collection }.CollectionName, Is.EqualTo(expected));

    [TestCase("title", null, "f_title_kw_ss")]
    [TestCase("__a.b-c d", null, "f_a_b_c_d_kw_ss")]
    [TestCase("title", "vip-segment", "f_title_sg_vip_segment_kw_ss")]
    public void FieldNames_AreValid(string fieldName, string? segment, string expected)
        => Assert.That(FieldNames.Field(fieldName, WarpSearchConstants.FieldKinds.Keywords, segment), Is.EqualTo(expected));

    [Test]
    public void FieldNames_LongNamesKeepTheirTypeSuffixAndStayUnique()
    {
        var first = FieldNames.Field(new string('a', 200) + "1", WarpSearchConstants.FieldKinds.Keywords);
        var second = FieldNames.Field(new string('a', 200) + "2", WarpSearchConstants.FieldKinds.Keywords);

        Assert.That(first.Length, Is.LessThanOrEqualTo(127));
        Assert.That(first, Does.EndWith("_kw_ss"));
        Assert.That(first, Is.Not.EqualTo(second));
    }

    [Test]
    public void Documents_OnePerCultureWithInvariantFields()
    {
        var builder = new DocumentBuilder(new FieldNames(Options.Create(new WarpSearchOptions())));
        Guid key = Guid.NewGuid();

        var documents = builder.Build(
            "Umb_Content",
            key,
            UmbracoObjectTypes.Document,
            [new Variation("en-US", null), new Variation("en-US", "vip"), new Variation("da-DK", null)],
            [
                new IndexField("title", new IndexValue { TextsR1 = ["Hello"] }, "en-US", null),
                new IndexField("title", new IndexValue { TextsR1 = ["Hej"] }, "da-DK", null),
                new IndexField("title", new IndexValue { TextsR1 = ["Hello VIP"] }, "en-US", "vip"),
                new IndexField("price", new IndexValue { Decimals = [1.50m] }, null, null)
            ],
            null);

        Assert.That(documents.Select(d => d.Id), Is.EqualTo(new[] { $"umb_content.{key:D}.en-us", $"umb_content.{key:D}.da-dk" }));

        JsonObject english = documents[0].Document;
        Assert.That(english["umb_index_s"]!.GetValue<string>(), Is.EqualTo("Umb_Content"));
        Assert.That(english["f_title_r1_un"]!.GetValue<string>(), Is.EqualTo("Hello"));
        Assert.That(english["f_title_sg_vip_r1_un"]!.GetValue<string>(), Is.EqualTo("Hello VIP"));
        Assert.That(english["all_r1_un"]!.GetValue<string>(), Is.EqualTo("Hello"));
        Assert.That(english["all_sg_vip_r1_un"]!.GetValue<string>(), Is.EqualTo("Hello\nHello VIP"));
        Assert.That(english["f_price_dk_ss"]!.AsArray().Single()!.GetValue<string>(), Is.EqualTo("1.5"));
        Assert.That(english["umb_access_ss"]!.AsArray().Single()!.GetValue<string>(), Is.EqualTo(Guid.Empty.ToString("D")));
        Assert.That(documents[1].Document["f_title_r1_un"]!.GetValue<string>(), Is.EqualTo("Hej"));
    }
}
