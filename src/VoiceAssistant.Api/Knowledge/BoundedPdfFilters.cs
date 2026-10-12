using System.IO.Compression;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace VoiceAssistant.Api.Knowledge;

internal sealed class BoundedPdfFilters(CancellationToken cancellation) : IFilterProvider
{
    private readonly IFilterProvider defaults = DefaultFilterProvider.Instance;
    private long expanded;

    public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) =>
        defaults.GetFilters(dictionary).Select(Wrap).ToArray();
    public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) =>
        defaults.GetNamedFilters(names).Select(Wrap).ToArray();
    public IReadOnlyList<IFilter> GetAllFilters() => defaults.GetAllFilters().Select(Wrap).ToArray();
    private IFilter Wrap(IFilter filter) => new Filter(this, filter, cancellation);

    private sealed class Filter(BoundedPdfFilters owner, IFilter inner, CancellationToken cancellation) : IFilter
    {
        public bool IsSupported => inner.IsSupported;
        public Memory<byte> Decode(Memory<byte> input, DictionaryToken dictionary, IFilterProvider provider, int index)
        {
            cancellation.ThrowIfCancellationRequested();
            if (input.Length > MaterialExtractor.MaxEntryBytes) throw KnowledgeException.TooLarge();
            if (inner is FlateFilter)
            {
                if (dictionary.TryGet(NameToken.DecodeParms, out var parameters)) ValidateParameters(parameters);
                using var compressed = new MemoryStream(input.ToArray(), false);
                using var decoded = new ZLibStream(compressed, CompressionMode.Decompress);
                var buffer = new byte[16384];
                long count = 0;
                int read;
                while ((read = decoded.Read(buffer)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    count += read;
                    if (count > MaterialExtractor.MaxEntryBytes || owner.expanded + count > MaterialExtractor.MaxExpandedBytes)
                        throw KnowledgeException.TooLarge();
                }
            }
            else if (inner is not (Ascii85Filter or AsciiHexDecodeFilter))
                throw KnowledgeException.Unsupported();
            var result = inner.Decode(input, dictionary, provider, index);
            owner.expanded += result.Length;
            if (result.Length > MaterialExtractor.MaxEntryBytes || owner.expanded > MaterialExtractor.MaxExpandedBytes)
                throw KnowledgeException.TooLarge();
            cancellation.ThrowIfCancellationRequested();
            return result;
        }

        private static void ValidateParameters(IToken value)
        {
            if (value is NullToken) return;
            if (value is ArrayToken array)
            {
                if (array.Data.Count > 16) throw KnowledgeException.TooLarge();
                foreach (var item in array.Data)
                {
                    if (item is ArrayToken) throw KnowledgeException.Invalid();
                    ValidateParameters(item);
                }
                return;
            }
            if (value is not DictionaryToken parameters) throw KnowledgeException.Unsupported();
            foreach (var (key, token) in parameters.Data)
            {
                if (key is not ("Columns" or "Colors" or "BitsPerComponent" or "Predictor")) continue;
                if (token is not NumericToken number || !double.IsFinite(number.Data)) throw KnowledgeException.Invalid();
                var maximum = key switch { "Columns" => 8192, "Colors" => 4, "BitsPerComponent" => 16, _ => 15 };
                if (number.Data < 1 || number.Data > maximum) throw KnowledgeException.TooLarge();
            }
        }
    }
}
