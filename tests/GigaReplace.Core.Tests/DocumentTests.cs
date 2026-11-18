using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using GigaReplace.Core.Apply;
using GigaReplace.Core.Documents;
using GigaReplace.Core.Planning;

namespace GigaReplace.Core.Tests;

public class DocumentTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>A minimal .docx: the given body XML (paragraphs), optionally a header and a title.</summary>
    private static byte[] BuildDocx(string body, string? header = null, string? title = null, bool signed = false)
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>",
            ["_rels/.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/document.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{W}\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><w:body>{body}</w:body></w:document>",
        };
        if (header is not null)
        {
            parts["word/header1.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:hdr xmlns:w=\"{W}\">{header}</w:hdr>";
        }

        if (title is not null)
        {
            parts["docProps/core.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{title}</dc:title></cp:coreProperties>";
        }

        if (signed)
        {
            parts["_xmlsignatures/sig1.xml"] = "<Signature/>";
        }

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, xml) in parts)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(xml));
            }
        }

        return stream.ToArray();
    }

    private static string P(string runs) => $"<w:p>{runs}</w:p>";

    private static string R(string text, string props = "") => $"<w:r>{(props.Length > 0 ? $"<w:rPr>{props}</w:rPr>" : "")}<w:t xml:space=\"preserve\">{text}</w:t></w:r>";

    private static string Part(byte[] docx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static byte[] ApplyAll(TempTree tree, string file, params (string, string)[] rules)
    {
        var scan = tree.Scan(rules);
        scan.ApproveAll();
        Assert.DoesNotContain(PlanValidator.Validate(scan), p => p.IsError);
        var write = Assert.Single(ApplyPlan.Build(scan).ContentWrites, w => w.RelativePath == file);
        return write.NewBytes;
    }

    [Fact]
    public void Docx_text_headers_and_properties_are_found_and_changed()
    {
        var docx = BuildDocx(P(R("Welcome to OrderHub.")) + P(R("Second paragraph")), header: P(R("OrderHub confidential")), title: "OrderHub Guide");
        using var tree = new TempTree().AddBytes("Guide.docx", docx);

        var scan = tree.Scan(("OrderHub", "SalesDesk"));

        Assert.Equal(3, scan.Items.Count);
        Assert.Contains(scan.Items, i => i.Context == MatchContext.DocumentProperty && i.ContextDetail == "document title");
        Assert.Contains(scan.Items, i => i.Place == "body" && i.LineText == "Welcome to OrderHub.");
        Assert.Contains(scan.Items, i => i.Place!.StartsWith("header"));
        Assert.All(scan.Items, i => Assert.Equal(Decision.Replace, i.Decision));

        var written = ApplyAll(tree, "Guide.docx", ("OrderHub", "SalesDesk"));
        var reread = DocxContent.Load(written);
        Assert.Contains("Title: SalesDesk Guide", reread.Text);
        Assert.Contains("Welcome to SalesDesk.", reread.Text);
        Assert.Contains("SalesDesk confidential", reread.Text);
        Assert.Equal(Part(docx, "[Content_Types].xml"), Part(written, "[Content_Types].xml"));
    }

    [Fact]
    public void A_word_split_into_identically_formatted_runs_is_replaced_without_a_flag()
    {
        // Word splits runs for revision ids and spell checking; that isn't a formatting difference.
        var docx = BuildDocx(P("<w:r w:rsidR=\"00A1\"><w:t>Order</w:t></w:r><w:r w:rsidR=\"00B2\"><w:t>Hub works</w:t></w:r>"));
        using var tree = new TempTree().AddBytes("a.docx", docx);

        var item = tree.Scan(("OrderHub", "SalesDesk")).Items.Single();
        Assert.Empty(item.Risks);

        var xml = Part(ApplyAll(tree, "a.docx", ("OrderHub", "SalesDesk")), "word/document.xml");
        Assert.Contains("<w:t>SalesDesk</w:t>", xml);
        Assert.Contains("<w:t> works</w:t>".Replace("<w:t>", "<w:t xml:space=\"preserve\">"), xml);
    }

    [Fact]
    public void A_word_in_two_formats_is_flagged_and_takes_the_first_format()
    {
        var docx = BuildDocx(P(R("Order", "<w:b/>") + R("Hub is half bold")));
        using var tree = new TempTree().AddBytes("a.docx", docx);

        var item = tree.Scan(("OrderHub", "SalesDesk")).Items.Single();
        Assert.True(item.Has(RiskCode.DocMixedFormatting));
        Assert.Equal(Decision.Pending, item.Decision);

        var reread = DocxContent.Load(ApplyAll(tree, "a.docx", ("OrderHub", "SalesDesk")));
        Assert.Contains("SalesDesk is half bold", reread.Text);
    }

    [Fact]
    public void Tracked_insertions_and_field_results_are_flagged_but_hyperlinks_are_not()
    {
        var body = P("<w:ins w:id=\"1\" w:author=\"A\">" + R("OrderHub added") + "</w:ins>")
            + P("<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText> DOCPROPERTY Title </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" + R("OrderHub title") + "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>")
            + P("<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText> HYPERLINK \"https://x\" </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" + R("OrderHub site") + "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>");
        using var tree = new TempTree().AddBytes("a.docx", BuildDocx(body));

        var items = tree.Scan(("OrderHub", "SalesDesk")).Items;

        Assert.True(items.Single(i => i.LineText == "OrderHub added").Has(RiskCode.DocTrackedChange));
        Assert.Contains("DOCPROPERTY Title", items.Single(i => i.LineText == "OrderHub title").Risks.Single(r => r.Code == RiskCode.DocField).Message);
        Assert.Empty(items.Single(i => i.LineText == "OrderHub site").Risks);
    }

    [Fact]
    public void The_fallback_copy_of_a_text_box_is_ignored()
    {
        var box = "<w:r><mc:AlternateContent><mc:Choice><w:txbxContent>" + P(R("OrderHub in a box")) + "</w:txbxContent></mc:Choice><mc:Fallback><w:txbxContent>" + P(R("OrderHub in a box")) + "</w:txbxContent></mc:Fallback></mc:AlternateContent></w:r>";
        using var tree = new TempTree().AddBytes("a.docx", BuildDocx(P(box)));

        Assert.Single(tree.Scan(("OrderHub", "SalesDesk")).Items);
    }

    [Fact]
    public void Applying_twice_gives_the_same_result()
    {
        using var tree = new TempTree().AddBytes("a.docx", BuildDocx(P(R("OrderHub here"))));
        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        scan.ApproveAll();

        var first = ApplyPlan.Build(scan).ContentWrites.Single().NewBytes;
        var second = ApplyPlan.Build(scan).ContentWrites.Single().NewBytes;

        Assert.Equal(Part(first, "word/document.xml"), Part(second, "word/document.xml"));
        Assert.Contains("SalesDesk here", DocxContent.Load(second).Text);
    }

    [Fact]
    public void Signed_documents_are_listed_but_not_changed()
    {
        using var tree = new TempTree().AddBytes("a.docx", BuildDocx(P(R("OrderHub")), signed: true));

        var item = tree.Scan(("OrderHub", "SalesDesk")).Items.Single();

        Assert.False(item.CanChange);
        Assert.Contains("digitally signed", item.Risks.Single().Message);
    }

    [Fact]
    public void Rtf_markup_is_never_matched_and_split_formatting_keeps_its_braces()
    {
        const string rtf = @"{\rtf1\ansi\ansicpg1252{\fonttbl{\f0 OrderHub Sans;}}{\info{\title OrderHub Notes}}\f0 Mixed: {\b Order}Hub here.\par}";
        using var tree = new TempTree().Add("notes.rtf", rtf);

        var scan = tree.Scan(("OrderHub", "SalesDesk"));

        Assert.Equal(2, scan.Items.Count);
        Assert.Contains(scan.Items, i => i.ContextDetail == "document title");
        var text = scan.Items.Single(i => i.Context == MatchContext.DocumentText);
        Assert.True(text.Has(RiskCode.DocMixedFormatting));

        var written = Encoding.ASCII.GetString(ApplyAll(tree, "notes.rtf", ("OrderHub", "SalesDesk")));
        Assert.Equal(@"{\rtf1\ansi\ansicpg1252{\fonttbl{\f0 OrderHub Sans;}}{\info{\title SalesDesk Notes}}\f0 Mixed: {\b SalesDesk} here.\par}", written);
    }

    [Fact]
    public void Rtf_escaped_letters_are_found_and_written_back_escaped()
    {
        const string rtf = @"{\rtf1\ansi\ansicpg1252 Caf\'e9 OrderHub and \u8364?uro.\par}";
        using var tree = new TempTree().Add("menu.rtf", rtf);

        var scan = tree.Scan(("Café", "Bistro"), ("OrderHub", "Zoë"), ("€uro", "Pound"));

        Assert.True(scan.Items.Count == 3, $"Text: {scan.Files["menu.rtf"].Document?.Text}; items: {string.Join(", ", scan.Items.Select(i => $"{i.OriginalText}→{i.ProposedText}"))}");
        var written = ApplyAll(tree, "menu.rtf", ("Café", "Bistro"), ("OrderHub", "Zoë"), ("€uro", "Pound"));
        Assert.Equal(@"{\rtf1\ansi\ansicpg1252 Bistro Zo\'eb and Pound.\par}", Encoding.ASCII.GetString(written));
        Assert.Contains("Bistro Zoë and Pound.", RtfContent.Load(written).Text);
    }

    [Fact]
    public void Old_doc_files_are_listed_for_editing_by_hand()
    {
        using var tree = new TempTree().AddBytes("old.doc", BuildDoc("Welcome to OrderHub.\rField: \u0013 DOCPROPERTY Title \u0014OrderHub\u0015 done\r"));

        var scan = tree.Scan(("OrderHub", "SalesDesk"));

        Assert.Contains("Welcome to OrderHub.", scan.Files["old.doc"].Document!.Text);
        Assert.Contains("Field: OrderHub done", scan.Files["old.doc"].Document!.Text);
        Assert.DoesNotContain("DOCPROPERTY", scan.Files["old.doc"].Document!.Text);
        Assert.Equal(2, scan.Items.Count);
        Assert.All(scan.Items, i => Assert.False(i.CanChange));
        Assert.Contains("save it as .docx", scan.Items[0].Risks.Single().Message);
    }

    [Fact]
    public void Documents_are_skipped_when_their_option_is_off()
    {
        using var tree = new TempTree()
            .AddBytes("a.docx", BuildDocx(P(R("OrderHub"))))
            .Add("b.rtf", @"{\rtf1 OrderHub\par}");
        var profile = tree.Profile(("OrderHub", "SalesDesk"));
        profile.SearchWordDocuments = false;
        profile.SearchRtfDocuments = false;

        Assert.Empty(Scanner.Scan(profile).Items);
    }

    /// <summary>
    /// A minimal Word 97–2003 file: a Compound File holding a WordDocument stream (FIB + the
    /// text as one uncompressed piece at 0x800) and a 1Table stream with the piece table.
    /// </summary>
    private static byte[] BuildDoc(string text)
    {
        const int StreamSize = 4096;
        var word = new byte[StreamSize];
        BinaryPrimitives.WriteUInt16LittleEndian(word, 0xA5EC);
        BinaryPrimitives.WriteUInt16LittleEndian(word.AsSpan(0x0A), 0x0200);
        Encoding.Unicode.GetBytes(text).CopyTo(word, 0x800);

        var table = new byte[StreamSize];
        table[0] = 0x02;
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(1), 16);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(5), 0);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(9), text.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(13 + 2), 0x800);
        BinaryPrimitives.WriteInt32LittleEndian(word.AsSpan(0x01A2), 0);
        BinaryPrimitives.WriteInt32LittleEndian(word.AsSpan(0x01A6), 21);

        // Sectors: 0 = FAT, 1 = directory, 2..9 = WordDocument, 10..17 = 1Table.
        const uint End = 0xFFFFFFFE, Free = 0xFFFFFFFF;
        var file = new byte[512 * 19];
        BinaryPrimitives.WriteUInt64LittleEndian(file, 0xE11AB1A1E011CFD0);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x18), 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x1A), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x1C), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x1E), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x20), 6);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x2C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x30), 1);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x38), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x3C), End);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x44), End);
        for (int i = 0; i < 109; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x4C + i * 4), i == 0 ? 0 : Free);
        }

        var fat = file.AsSpan(512, 512);
        fat.Fill(0xFF);
        BinaryPrimitives.WriteUInt32LittleEndian(fat, 0xFFFFFFFD);
        BinaryPrimitives.WriteUInt32LittleEndian(fat[4..], End);
        for (uint s = 2; s <= 17; s++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fat[(int)(s * 4)..], s is 9 or 17 ? End : s + 1);
        }

        void Entry(int index, string name, byte type, uint start, uint size)
        {
            var e = file.AsSpan(1024 + index * 128, 128);
            Encoding.Unicode.GetBytes(name).CopyTo(e);
            BinaryPrimitives.WriteUInt16LittleEndian(e[0x40..], (ushort)((name.Length + 1) * 2));
            e[0x42] = type;
            BinaryPrimitives.WriteUInt32LittleEndian(e[0x44..], Free);
            BinaryPrimitives.WriteUInt32LittleEndian(e[0x48..], Free);
            BinaryPrimitives.WriteUInt32LittleEndian(e[0x4C..], Free);
            BinaryPrimitives.WriteUInt32LittleEndian(e[0x74..], start);
            BinaryPrimitives.WriteUInt32LittleEndian(e[0x78..], size);
        }

        Entry(0, "Root Entry", 5, End, 0);
        Entry(1, "WordDocument", 2, 2, StreamSize);
        Entry(2, "1Table", 2, 10, StreamSize);
        word.CopyTo(file, 512 * 3);
        table.CopyTo(file, 512 * 11);
        return file;
    }
}
