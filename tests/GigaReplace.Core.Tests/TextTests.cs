using System.Text;
using GigaReplace.Core.Text;

namespace GigaReplace.Core.Tests;

public class CasePreserverTests
{
    [Theory]
    [InlineData("OrderHub", "OrderHub", "SalesDesk")]
    [InlineData("orderHub", "orderHub", "salesDesk")]
    [InlineData("ORDERHUB", "ORDERHUB", "SALESDESK")]
    [InlineData("orderhub", "orderhub", "salesdesk")]
    [InlineData("Orderhub", "Orderhub", "Salesdesk")]
    [InlineData("ORDERHub", "ORDERHub", "SALESDesk")]
    public void Mirrors_the_matched_casing(string _, string matched, string expected)
    {
        var result = CasePreserver.Apply("OrderHub", "SalesDesk", matched);
        Assert.Equal(expected, result.Text);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public void CamelCase_works_when_word_counts_differ()
    {
        Assert.Equal("desk", CasePreserver.Apply("OrderHub", "Desk", "orderHub").Text);
        Assert.Equal("xmlDesk", CasePreserver.Apply("OrderHub", "XMLDesk", "orderHub").Text);
    }

    [Fact]
    public void Unusual_casing_is_flagged_as_a_guess()
    {
        var result = CasePreserver.Apply("OrderHub", "SalesDesk", "oRdErHuB");
        Assert.True(result.Ambiguous);
        Assert.Equal("SalesDesk", result.Text);
    }

    [Fact]
    public void Strict_or_non_preserving_rules_use_the_replacement_as_typed()
    {
        var strict = new ReplacementRule { Find = "OrderHub", Replace = "SalesDesk", MatchCase = true };
        var plain = new ReplacementRule { Find = "OrderHub", Replace = "SalesDesk", PreserveCase = false };
        Assert.Equal("SalesDesk", CasePreserver.Propose(strict, "OrderHub").Text);
        Assert.Equal("SalesDesk", CasePreserver.Propose(plain, "ORDERHUB").Text);
    }
}

public class WordShapeTests
{
    [Theory]
    [InlineData("OrderHubService", 0, 8, true)]
    [InlineData("MyOrderHub", 2, 8, true)]
    [InlineData("IOrderHub", 1, 8, true)]
    [InlineData("XMLOrderHub", 3, 8, true)]
    [InlineData("ORDERHUB_PROD", 0, 8, true)]
    [InlineData("OrderHubs", 0, 8, true)]
    [InlineData("ORDERHUBS", 0, 8, true)]
    [InlineData("order_hub", 0, 5, true)]
    [InlineData("Thumbnail", 5, 4, false)]
    [InlineData("OrderHubspot", 0, 8, false)]
    [InlineData("orderhubservice", 0, 8, false)]
    public void Detects_matches_inside_other_words(string text, int start, int length, bool aligned) =>
        Assert.Equal(aligned, WordShape.IsAligned(text, start, length));

    [Fact]
    public void Splits_words_at_humps_acronyms_and_digits()
    {
        Assert.Equal(["XML", "Parser"], Words("XMLParser"));
        Assert.Equal(["order", "Hub", "2", "Go"], Words("orderHub2Go"));
        Assert.Equal(["ORDER", "HUB"], Words("ORDER_HUB"));
    }

    private static string[] Words(string s) => WordShape.Words(s).Select(w => s.Substring(w.Start, w.Length)).ToArray();
}

public class RuleMatcherTests
{
    [Fact]
    public void Case_insensitive_rules_find_every_casing()
    {
        var rule = new ReplacementRule { Find = "OrderHub", Replace = "X" };
        var matches = RuleMatcher.FindAll("OrderHub orderhub ORDERHUB", [rule]);
        Assert.Equal([0, 9, 18], matches.Select(m => m.Start));
    }

    [Fact]
    public void Strict_rules_only_find_the_exact_casing()
    {
        var rule = new ReplacementRule { Find = "OrderHub", Replace = "X", MatchCase = true };
        Assert.Single(RuleMatcher.FindAll("OrderHub orderhub ORDERHUB", [rule]));
    }

    [Fact]
    public void Earlier_rules_claim_text_first()
    {
        var first = new ReplacementRule { Find = "OrderHub", Replace = "A" };
        var second = new ReplacementRule { Find = "Hub", Replace = "B" };
        var matches = RuleMatcher.FindAll("OrderHub Hub", [first, second]);
        Assert.Collection(matches,
            m => Assert.Equal((0, 8, first), (m.Start, m.Length, m.Rule)),
            m => Assert.Equal((9, 3, second), (m.Start, m.Length, m.Rule)));
    }

    [Fact]
    public void Disabled_and_empty_rules_are_ignored()
    {
        var disabled = new ReplacementRule { Find = "a", Replace = "b", Enabled = false };
        var empty = new ReplacementRule { Find = "", Replace = "b" };
        Assert.Empty(RuleMatcher.FindAll("aaa", [disabled, empty]));
    }
}

public class TextFileReaderTests
{
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public void Round_trips_bytes_exactly(string kind)
    {
        Encoding encoding = kind switch
        {
            "utf8" => new UTF8Encoding(false),
            "utf8bom" => new UTF8Encoding(true),
            "utf16le" => new UnicodeEncoding(false, true),
            _ => new UnicodeEncoding(true, true),
        };
        const string text = "namespace OrderHub;\r\n// café ✓\nclass A {}\r";
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

        var content = TextFileReader.TryDecode(bytes);

        Assert.NotNull(content);
        Assert.Equal(text, content.Text);
        Assert.Equal(bytes, content.Encode(content.Text));
    }

    [Fact]
    public void Falls_back_to_byte_preserving_8_bit_for_legacy_files()
    {
        var bytes = new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)' ', (byte)'O', (byte)'H' };
        var content = TextFileReader.TryDecode(bytes);
        Assert.NotNull(content);
        Assert.True(content.IsLegacy8Bit);
        Assert.Equal(bytes, content.Encode(content.Text));
        Assert.False(content.CanEncode("✓"));
    }

    [Fact]
    public void Treats_nul_bytes_as_binary() =>
        Assert.Null(TextFileReader.TryDecode([0x4D, 0x5A, 0x00, 0x01]));

    [Fact]
    public void Line_endings_survive_a_replacement()
    {
        var bytes = Encoding.UTF8.GetBytes("a OrderHub\r\nb\nc\r");
        var content = TextFileReader.TryDecode(bytes)!;
        var updated = content.Encode(content.Text.Replace("OrderHub", "SalesDesk"));
        Assert.Equal("a SalesDesk\r\nb\nc\r", Encoding.UTF8.GetString(updated));
    }
}
