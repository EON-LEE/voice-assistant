using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace VoiceAssistant.Api.Knowledge;

public sealed class MaterialExtractor
{
    private readonly SemaphoreSlim workers = new(2, 2);
    private readonly TimeSpan extractionTimeout;
    public MaterialExtractor() : this(TimeSpan.FromSeconds(10)) { }
    internal MaterialExtractor(TimeSpan extractionTimeout) => this.extractionTimeout = extractionTimeout;
    public const int MaxZipEntries = 1024;
    public const int MaxEntryBytes = 4 * 1024 * 1024;
    public const int MaxExpandedBytes = 20 * 1024 * 1024;
    public const int MaxPdfPages = 200;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static readonly IReadOnlyList<string> Extensions = Array.AsReadOnly(
        (".txt .md .markdown .rst .csv .tsv .json .jsonl .yaml .yml .toml .ini .xml .html .htm .log .vtt .srt .sql .sh .ps1 .bat " +
        ".cs .ts .tsx .js .jsx .mjs .py .java .go .rs .c .h .cpp .hpp .kt .swift .rb .php .scala .bicep .tf .docx .pptx .pdf").Split(' '));

    public async Task<string> ExtractAsync(string filename, byte[] data, CancellationToken cancellation)
    {
        if (!await workers.WaitAsync(0, cancellation)) throw KnowledgeException.Busy();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (extractionTimeout == TimeSpan.Zero) timeout.Cancel();
        else timeout.CancelAfter(extractionTimeout);
        var token = timeout.Token;
        // A timed-out parser retains its worker slot until it unwinds; repeated requests cannot create unbounded workers.
        var operation = Task.Run(() =>
        {
            try { return Extract(filename, data, token); }
            finally { workers.Release(); }
        }, CancellationToken.None);
        try { return await operation.WaitAsync(token); }
        catch (KnowledgeException) { throw; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new KnowledgeException(422, "no_text", "Text extraction exceeded its time limit."); }
        catch (OperationCanceledException) { throw; }
        catch (DecoderFallbackException) { throw KnowledgeException.Unsupported(); }
        catch (RegexMatchTimeoutException) { throw KnowledgeException.Invalid(); }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or ArgumentException or IOException)
        { throw KnowledgeException.Invalid(); }
    }

    internal static string Extract(string filename, byte[] data, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (data.Length > KnowledgeLimits.MaxFileBytes) throw KnowledgeException.TooLarge();
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (!Extensions.Contains(extension, StringComparer.Ordinal)) throw KnowledgeException.Unsupported();
        string text;
        if (extension is ".docx" or ".pptx") text = Office(data, extension, cancellation);
        else if (extension == ".pdf") text = Pdf(data, cancellation);
        else
        {
            text = Utf8.GetString(data).TrimStart('\uFEFF');
            if (text.Length > KnowledgeLimits.MaxCharactersPerDocument) throw KnowledgeException.TooLarge();
            if (extension is ".html" or ".htm")
                text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromSeconds(1)));
        }
        cancellation.ThrowIfCancellationRequested();
        if (text.Length > KnowledgeLimits.MaxCharactersPerDocument) throw KnowledgeException.TooLarge();
        if (string.IsNullOrWhiteSpace(text)) throw KnowledgeException.NoText();
        return text;
    }

    private static string Office(byte[] bytes, string extension, CancellationToken cancellation)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxZipEntries) throw KnowledgeException.TooLarge();
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!names.Add(entry.FullName) || entry.FullName.Contains("..", StringComparison.Ordinal) ||
                entry.FullName.StartsWith('/') || entry.FullName.Contains('\\')) throw KnowledgeException.Invalid();
            total += entry.Length;
            if (entry.Length > MaxEntryBytes || total > MaxExpandedBytes ||
                (entry.Length > 65536 && entry.Length > Math.Max(1, entry.CompressedLength) * 200))
                throw KnowledgeException.TooLarge();
        }
        var text = new StringBuilder();
        if (extension == ".docx")
        {
            var entry = archive.GetEntry("word/document.xml") ?? throw KnowledgeException.Invalid();
            AppendXml(entry, text, cancellation);
        }
        else
        {
            var presentation = archive.GetEntry("ppt/presentation.xml") ?? throw KnowledgeException.Invalid();
            var relationships = archive.GetEntry("ppt/_rels/presentation.xml.rels") ?? throw KnowledgeException.Invalid();
            var slideTargets = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var relations = Reader(relationships))
            {
                while (relations.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (relations.NodeType != XmlNodeType.Element || relations.LocalName != "Relationship" ||
                        !(relations.GetAttribute("Type")?.EndsWith("/slide", StringComparison.Ordinal) ?? false)) continue;
                    var target = relations.GetAttribute("Target") ?? "";
                    if (relations.GetAttribute("TargetMode") == "External" ||
                        !Regex.IsMatch(target, @"^slides/slide[1-9][0-9]*\.xml$")) throw KnowledgeException.Invalid();
                    slideTargets.Add(relations.GetAttribute("Id") ?? throw KnowledgeException.Invalid(), "ppt/" + target);
                }
            }
            var slides = new List<ZipArchiveEntry>();
            using (var order = Reader(presentation))
                while (order.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (order.NodeType != XmlNodeType.Element || order.LocalName != "sldId") continue;
                    var relationshipId = order.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    if (relationshipId is null || !slideTargets.TryGetValue(relationshipId, out var path))
                        throw KnowledgeException.Invalid();
                    slides.Add(archive.GetEntry(path) ?? throw KnowledgeException.Invalid());
                }
            if (slides.Count == 0) throw KnowledgeException.Invalid();
            var slideNumber = 0;
            foreach (var slide in slides)
            {
                Append(text, $"# Slide {++slideNumber}\n");
                AppendXml(slide, text, cancellation);
                // Resolve notes via the slide relationship, rather than assuming matching numbering.
                var rel = archive.GetEntry($"ppt/slides/_rels/{Path.GetFileName(slide.FullName)}.rels");
                if (rel is null) continue;
                using var reader = Reader(rel);
                while (reader.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship" ||
                        !(reader.GetAttribute("Type")?.EndsWith("/notesSlide", StringComparison.Ordinal) ?? false)) continue;
                    if (reader.GetAttribute("TargetMode") == "External") throw KnowledgeException.Invalid();
                    var target = reader.GetAttribute("Target") ?? "";
                    if (!Regex.IsMatch(target, @"^\.\./notesSlides/notesSlide[1-9][0-9]*\.xml$", RegexOptions.CultureInvariant))
                        throw KnowledgeException.Invalid();
                    var notes = archive.GetEntry("ppt/" + target[3..]) ?? throw KnowledgeException.Invalid();
                    Append(text, "# Notes\n");
                    AppendXml(notes, text, cancellation);
                }
            }
        }
        return text.ToString();
    }

    private static XmlReader Reader(ZipArchiveEntry entry) => XmlReader.Create(
        new CappedReadStream(entry.Open(), MaxEntryBytes), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = true,
            MaxCharactersInDocument = MaxEntryBytes, IgnoreComments = true
        });

    private static void AppendXml(ZipArchiveEntry entry, StringBuilder output, CancellationToken cancellation)
    {
        using var reader = Reader(entry);
        while (reader.Read())
        {
            cancellation.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                using var text = reader.ReadSubtree();
                while (text.Read())
                    if (text.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) Append(output, text.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p") Append(output, "\n");
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "tab") Append(output, "\t");
        }
    }

    private static string Pdf(byte[] data, CancellationToken cancellation)
    {
        try
        {
            using var document = PdfDocument.Open(data, new ParsingOptions
            {
                UseLenientParsing = false, FilterProvider = new BoundedPdfFilters(cancellation)
            });
            if (document.NumberOfPages > MaxPdfPages) throw KnowledgeException.TooLarge();
            var text = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                cancellation.ThrowIfCancellationRequested();
                var content = ContentOrderTextExtractor.GetText(page);
                if (!string.IsNullOrWhiteSpace(content)) Append(text, $"# Page {page.Number}\n{content}\n");
            }
            return text.ToString();
        }
        catch (KnowledgeException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw KnowledgeException.Invalid(); }
    }

    private static void Append(StringBuilder output, string text)
    {
        if (output.Length + text.Length > KnowledgeLimits.MaxCharactersPerDocument) throw KnowledgeException.TooLarge();
        output.Append(text);
    }
}
