using System.Text;
using GigaReplace.Core.Apply;
using GigaReplace.Core.Pdf;
using GigaReplace.Core.Planning;

namespace GigaReplace.Core.Tests;

public class PdfTests
{
    /// <summary>
    /// A one- or more-page PDF with an Info title. /F1 is Helvetica (not embedded, so any letter
    /// can be drawn); /F2 claims to be a subset, so only letters the document uses are available.
    /// </summary>
    private static byte[] BuildPdf(string title, params string[] pageContents)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", pageContents.Select((_, i) => $"{6 + i * 2} 0 R"))}] /Count {pageContents.Length} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /ABCDEF+Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Title ({title}) /Producer (GigaReplace tests) >>",
        };
        foreach (var content in pageContents)
        {
            int contentNumber = objects.Count + 2;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentNumber} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f\r\n");
        foreach (int offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n\r\n");
        }

        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private const string Page = "BT /F1 12 Tf 72 700 Td (Welcome to OrderHub today) Tj ET";

    [Fact]
    public void Finds_matches_in_page_text_and_document_properties()
    {
        using var tree = new TempTree().AddBytes("docs/Guide.pdf", BuildPdf("OrderHub Guide", Page));

        var scan = tree.Scan(("OrderHub", "SalesDesk"));

        var title = scan.Items.Single(i => i.Context == MatchContext.PdfProperty);
        Assert.Equal("document title", title.ContextDetail);
        Assert.Equal(Decision.Replace, title.Decision);

        var text = scan.Items.Single(i => i.Context == MatchContext.PdfText);
        Assert.Equal("page 1", text.Place);
        Assert.Equal("Welcome to OrderHub today", text.LineText);
        Assert.True(text.CanChange);
        Assert.Empty(text.Risks);
        Assert.Equal(Decision.Replace, text.Decision);
    }

    [Fact]
    public void Replacements_that_must_be_squeezed_a_lot_are_flagged()
    {
        using var tree = new TempTree().AddBytes("Guide.pdf", BuildPdf("Guide", Page));

        var item = tree.Scan(("OrderHub", "SalesDeskManager")).Items.Single();

        Assert.True(item.Has(RiskCode.PdfFitted));
        Assert.Contains("squeezed", item.Risks.Single().Message);
        Assert.Equal(Decision.Pending, item.Decision);
    }

    [Fact]
    public void Applying_appends_an_incremental_update_with_the_new_text()
    {
        using var tree = new TempTree().AddBytes("Guide.pdf", BuildPdf("OrderHub Guide", Page));
        var original = File.ReadAllBytes(tree.Full("Guide.pdf"));
        var output = tree.Root + "-out";
        try
        {
            var scan = tree.Scan(("OrderHub", "SalesDesk"));
            scan.ApproveAll();
            Assert.Empty(PlanValidator.Validate(scan));

            CopyApplier.Apply(ApplyPlan.Build(scan), output, copyGitFolder: false);

            var written = File.ReadAllBytes(Path.Combine(output, "Guide.pdf"));
            Assert.Equal(original, written[..original.Length]);
            var reread = PdfContent.Load(written);
            Assert.Contains("Title: SalesDesk Guide", reread.Text);
            Assert.Contains("Welcome to SalesDesk today", reread.Text);
            Assert.DoesNotContain("OrderHub", reread.Text);

            using var pig = UglyToad.PdfPig.PdfDocument.Open(written);
            Assert.Equal("SalesDesk Guide", pig.Information.Title);
            Assert.Equal("Welcome to SalesDesk today", pig.GetPage(1).Text);
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, true);
            }
        }
    }

    [Fact]
    public void Text_in_a_subset_font_without_the_needed_letters_is_listed_but_never_changed()
    {
        // The subset font only has the letters of "OrderHub works": no S, a, l, s, D or k.
        using var tree = new TempTree().AddBytes("Guide.pdf", BuildPdf("Guide", "BT /F2 12 Tf 72 700 Td (OrderHub works) Tj ET"));

        var item = tree.Scan(("OrderHub", "SalesDesk")).Items.Single();

        Assert.False(item.CanChange);
        Assert.True(item.Has(RiskCode.PdfEditByHand));
        Assert.Contains("only embeds the letters it uses", item.Risks.Single(r => r.Code == RiskCode.PdfEditByHand).Message);
        Assert.Equal(Decision.Skip, item.Decision);

        item.Decision = Decision.Replace;
        item.SetDecision(Decision.Custom, "Other");
        Assert.Equal(Decision.Skip, item.Decision);
        Assert.False(item.WillChange);
    }

    [Fact]
    public void Subset_fonts_can_still_draw_replacements_made_of_letters_the_document_uses()
    {
        using var tree = new TempTree().AddBytes("Guide.pdf", BuildPdf("Guide", "BT /F2 12 Tf 72 700 Td (OrderHub works) Tj ET"));

        var item = tree.Scan(("OrderHub", "HubOrder")).Items.Single();

        Assert.True(item.CanChange);
    }

    [Fact]
    public void Page_text_is_only_listed_when_editing_it_is_turned_off()
    {
        using var tree = new TempTree().AddBytes("Guide.pdf", BuildPdf("OrderHub Guide", Page));
        var profile = tree.Profile(("OrderHub", "SalesDesk"));
        profile.EditPdfText = false;

        var scan = Scanner.Scan(profile);

        Assert.False(scan.Items.Single(i => i.Context == MatchContext.PdfText).CanChange);
        Assert.True(scan.Items.Single(i => i.Context == MatchContext.PdfProperty).CanChange);
    }

    [Fact]
    public void Pdfs_are_skipped_when_searching_them_is_turned_off()
    {
        using var tree = new TempTree().AddBytes("Guide.pdf", BuildPdf("OrderHub Guide", Page));
        var profile = tree.Profile(("OrderHub", "SalesDesk"));
        profile.SearchPdfs = false;

        Assert.Empty(Scanner.Scan(profile).Items);
    }

    [Fact]
    public void Custom_text_the_font_cannot_draw_is_a_plan_error()
    {
        using var tree = new TempTree().AddBytes("Guide.pdf", BuildPdf("Guide", "BT /F2 12 Tf 72 700 Td (OrderHub works) Tj ET"));
        var scan = tree.Scan(("OrderHub", "HubOrder"));
        var item = scan.Items.Single();

        item.SetDecision(Decision.Custom, "Zebra");

        var problem = Assert.Single(PlanValidator.Validate(scan));
        Assert.Equal("Text the PDF can't draw", problem.Title);
        Assert.True(problem.IsError);
    }

    [Fact]
    public void Kerned_word_spaces_and_split_words_are_read_as_text()
    {
        var content = "BT /F1 12 Tf 72 700 Td [(TeX)-333(style:)-333(OrderHub)-333(spacing)] TJ ET\n"
            + "BT /F1 12 Tf 72 680 Td (Order) Tj 30.672 0 Td (Hub split) Tj ET";
        using var tree = new TempTree().AddBytes("Guide.pdf", BuildPdf("Guide", content));

        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        var pdf = scan.Files["Guide.pdf"].Pdf!;

        Assert.Contains("TeX style: OrderHub spacing", pdf.Text);
        Assert.Contains("OrderHub split", pdf.Text);
        Assert.Equal(2, scan.Items.Count);
        Assert.All(scan.Items, i => Assert.True(i.CanChange));
    }

    [Fact]
    public void In_place_pdf_changes_can_be_undone_exactly()
    {
        using var tree = new TempTree().AddBytes("Guide.pdf", BuildPdf("OrderHub Guide", Page));
        var before = tree.Snapshot();
        var journals = new JournalStore(tree.Root + "-journals");
        try
        {
            var scan = tree.Scan(("OrderHub", "SalesDesk"));
            scan.ApproveAll();
            var result = InPlaceApplier.Apply(ApplyPlan.Build(scan), journals);
            Assert.NotEqual(before["Guide.pdf"], File.ReadAllBytes(tree.Full("Guide.pdf")));

            var undo = UndoService.Undo(result.Journal!, overwriteChangedFiles: false);

            Assert.True(undo.Success, string.Join("; ", undo.Errors));
            Assert.Equal(before["Guide.pdf"], File.ReadAllBytes(tree.Full("Guide.pdf")));
        }
        finally
        {
            if (Directory.Exists(journals.BaseFolder))
            {
                Directory.Delete(journals.BaseFolder, true);
            }
        }
    }
}
