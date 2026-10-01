using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using VoiceAssistant.Api.Knowledge;
using Xunit;
using UglyToad.PdfPig.Tokens;

namespace VoiceAssistant.Api.Tests;

public sealed class MaterialExtractionTests
{
    [Theory]
    [InlineData("notes.md")]
    [InlineData("source.cs")]
    [InlineData("module.tsx")]
    [InlineData("notes.vtt")]
    public async Task RealUtf8TextPreservesUnicodeAndSpeakerNames(string filename)
    {
        var text = "Speaker Mina: original notes.\n한국어\n";
        Assert.Equal(text, await new MaterialExtractor().ExtractAsync(filename, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray(), CancellationToken.None));
    }

    [Fact]
    public async Task HtmlStripsTagsAndDecodesEntities()
    {
        var text = await Extract("notes.html", "<h1>Planning &amp; scope</h1><p>Original paragraph</p>");
        Assert.DoesNotContain("<", text);
        Assert.Contains("Planning & scope", text);
    }

    [Theory]
    [InlineData(".doc")]
    [InlineData(".ppt")]
    [InlineData(".xls")]
    [InlineData(".exe")]
    [InlineData(".png")]
    public async Task LegacyBinaryAndExecutableTypesAreRejected(string extension)
    {
        var error = await Assert.ThrowsAsync<KnowledgeException>(() => Extract("test" + extension, "not executed"));
        Assert.Equal(415, error.Status);
        Assert.Contains("docx", error.Message);
    }

    [Fact]
    public async Task InvalidUtf8OversizeWhitespaceAndCancellationFailExplicitly()
    {
        var extractor = new MaterialExtractor();
        Assert.Equal(415, (await Assert.ThrowsAsync<KnowledgeException>(() => extractor.ExtractAsync("bad.txt", [0xC3, 0x28], CancellationToken.None))).Status);
        Assert.Equal(413, (await Assert.ThrowsAsync<KnowledgeException>(() => extractor.ExtractAsync("big.txt", new byte[KnowledgeLimits.MaxFileBytes + 1], CancellationToken.None))).Status);
        Assert.Equal(413, (await Assert.ThrowsAsync<KnowledgeException>(() => Extract("big.txt", new string('x', 400001)))).Status);
        Assert.Equal(422, (await Assert.ThrowsAsync<KnowledgeException>(() => Extract("empty.txt", " \n\t"))).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync("a.txt", [65], cancelled.Token));
    }

    [Fact]
    public async Task MinimalRealDocxExtractsParagraphsWithoutExecutingAnything()
    {
        var zip = Zip(new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types/>",
            ["word/document.xml"] = """<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>First original paragraph</w:t></w:r></w:p><w:p><w:r><w:t>Second original paragraph</w:t></w:r></w:p></w:body></w:document>"""
        });
        var text = await new MaterialExtractor().ExtractAsync("minutes.docx", zip, CancellationToken.None);
        Assert.Equal("First original paragraph\nSecond original paragraph\n", text);
    }

    [Fact]
    public async Task MinimalRealPptxUsesPresentationOrderAndSlideNotesRelationships()
    {
        var zip = Zip(new Dictionary<string, string>
        {
            ["ppt/presentation.xml"] = """<p:presentation xmlns:p="p" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><p:sldIdLst><p:sldId r:id="second"/><p:sldId r:id="first"/></p:sldIdLst></p:presentation>""",
            ["ppt/_rels/presentation.xml.rels"] = """<Relationships><Relationship Id="first" Type="x/slide" Target="slides/slide1.xml"/><Relationship Id="second" Type="x/slide" Target="slides/slide2.xml"/></Relationships>""",
            ["ppt/slides/slide1.xml"] = "<slide><p><t>Original first</t></p></slide>",
            ["ppt/slides/slide2.xml"] = "<slide><p><t>Original second</t></p></slide>",
            ["ppt/slides/_rels/slide2.xml.rels"] = """<Relationships><Relationship Id="note" Type="x/notesSlide" Target="../notesSlides/notesSlide9.xml"/></Relationships>""",
            ["ppt/notesSlides/notesSlide9.xml"] = "<notes><p><t>Original note</t></p></notes>"
        });
        var text = await new MaterialExtractor().ExtractAsync("slides.pptx", zip, CancellationToken.None);
        Assert.True(text.IndexOf("Original second", StringComparison.Ordinal) < text.IndexOf("Original first", StringComparison.Ordinal));
        Assert.Contains("Original note", text);
        Assert.Contains("# Slide 2", text);
    }

    [Theory]
    [InlineData("""<!DOCTYPE a [<!ENTITY xxe SYSTEM "file:///C:/private.txt">]><document><p><t>&xxe;</t></p></document>""")]
    [InlineData("""<!DOCTYPE a [<!ENTITY a "many"><!ENTITY b "&a;&a;&a;">]><document><t>&b;</t></document>""")]
    public async Task DtdAndExternalEntitiesAreRejectedWithoutResolving(string xml)
    {
        var error = await Assert.ThrowsAsync<KnowledgeException>(() => new MaterialExtractor().ExtractAsync(
            "bad.docx", Zip(new Dictionary<string, string> { ["word/document.xml"] = xml }), CancellationToken.None));
        Assert.Equal(400, error.Status);
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task ZipBombEntryCountExpandedSizeAndTraversalAreRejected()
    {
        var extractor = new MaterialExtractor();
        foreach (var files in new[]
        {
            new Dictionary<string,string> { ["word/document.xml"] = new string('x', MaterialExtractor.MaxEntryBytes + 1) },
            new Dictionary<string,string> { ["word/document.xml"] = new string('x', 100000) },
            Enumerable.Range(0, MaterialExtractor.MaxZipEntries + 1).ToDictionary(i => i.ToString(), _ => ""),
            Enumerable.Range(0, 6).ToDictionary(i => i + ".bin", _ => new string('x', MaterialExtractor.MaxEntryBytes))
        })
            Assert.Equal(413, (await Assert.ThrowsAsync<KnowledgeException>(() => extractor.ExtractAsync("bomb.docx", Zip(files), CancellationToken.None))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<KnowledgeException>(() => extractor.ExtractAsync("bad.docx",
            Zip(new Dictionary<string, string> { ["../escape"] = "x" }), CancellationToken.None))).Status);
    }

    [Fact]
    public async Task RealGeneratedPdfExtractsTextLayerAndBlankPdfReturnsNoText()
    {
        var text = await new MaterialExtractor().ExtractAsync("original.pdf", Pdf(1, true), CancellationToken.None);
        Assert.Contains("Original planning notes", text);
        Assert.Equal(422, (await Assert.ThrowsAsync<KnowledgeException>(() => new MaterialExtractor().ExtractAsync("blank.pdf", Pdf(1, false), CancellationToken.None))).Status);
        Assert.Equal(413, (await Assert.ThrowsAsync<KnowledgeException>(() => new MaterialExtractor().ExtractAsync("pages.pdf", Pdf(201, false), CancellationToken.None))).Status);
    }

    [Fact]
    public async Task RealCompressedPdfTextIsSupportedAndDecompressionBombFailsBoundedly()
    {
        var pdf = CompressedPdf("BT /F1 12 Tf 30 700 Td (Original compressed PDF notes) Tj ET");
        var text = await new MaterialExtractor().ExtractAsync("original.pdf", pdf, CancellationToken.None);
        Assert.Contains("Original compressed PDF notes", text);
        var bomb = CompressedPdf(new string(' ', MaterialExtractor.MaxEntryBytes + 1));
        Assert.Equal(413, (await Assert.ThrowsAsync<KnowledgeException>(() =>
            new MaterialExtractor().ExtractAsync("bomb.pdf", bomb, CancellationToken.None))).Status);
    }

    [Fact]
    public async Task ExtractionTimeoutIsExplicitAndDoesNotEchoInput()
    {
        var error = await Assert.ThrowsAsync<KnowledgeException>(() => new MaterialExtractor(TimeSpan.Zero)
            .ExtractAsync("private-name.pdf", Pdf(1, true), CancellationToken.None));
        Assert.Equal(422, error.Status);
        Assert.DoesNotContain("private-name", error.Message);
    }

    [Fact]
    public void ChunkingUsesContextBoundariesOverlapAndPreservesEveryCharacter()
    {
        var id = new string('a', 32);
        var text = "# Original heading\n" + string.Concat(Enumerable.Range(0, 100).Select(i => $"Paragraph {i:D3}: " + new string((char)('a' + i % 20), 90) + "\n\n"));
        var chunks = KnowledgeChunker.Split(id, "source.cs", text);
        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            Assert.InRange(chunk.Content.Length, 1, 1400);
            Assert.StartsWith("source.cs", chunk.Content);
            Assert.Equal(id, KnowledgeIds.DocumentOf(chunk.Id));
        }
        Assert.Contains("Original heading", chunks[^1].Content);
        for (var i = 0; i < 100; i++) Assert.Contains(chunks, chunk => chunk.Content.Contains($"Paragraph {i:D3}:"));
        var firstBody = chunks[0].Content[(chunks[0].Content.IndexOf('\n') + 1)..];
        Assert.Contains(firstBody[^150..], chunks[1].Content);
    }

    [Fact]
    public void ChunkLimitsHandleLongUnbrokenLinesUnicodeAndEmptyInput()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 1000));
        var chunks = KnowledgeChunker.Split(new string('a', 32), "original.txt", text);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Content.Length <= 1400);
            Assert.False(char.IsHighSurrogate(chunk.Content[^1]));
        }
        Assert.Equal(422, Assert.Throws<KnowledgeException>(() => KnowledgeChunker.Split(new string('a', 32), "a.txt", " ")).Status);
        Assert.Equal(413, Assert.Throws<KnowledgeException>(() => KnowledgeChunker.Split(new string('a', 32), "a.txt", new string('x', 400001))).Status);
    }

    private static Task<string> Extract(string name, string text) => new MaterialExtractor().ExtractAsync(name, Encoding.UTF8.GetBytes(text), CancellationToken.None);
    internal static byte[] Zip(Dictionary<string, string> files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        return output.ToArray();
    }
    internal static byte[] Pdf(int pages, bool text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 0; i < pages; i++)
        {
            var page = builder.AddPage(PageSize.A4);
            if (text) page.AddText("Original planning notes", 12, new PdfPoint(30, 700), font);
        }
        return builder.Build();
    }

    private static byte[] CompressedPdf(string content)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
            zlib.Write(Encoding.ASCII.GetBytes(content));
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"
        ];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        offsets.Add(output.Position);
        Write($"4 0 obj\n<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
        output.Write(compressed.ToArray());
        Write("\nendstream\nendobj\n");
        offsets.Add(output.Position);
        Write("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        var xref = output.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return output.ToArray();
    }
}
