using System.Text;

namespace VoiceAssistant.Api;

public sealed record ReplyUpdate(string Text, string? Alternative = null);

internal sealed class ReplySuggestions
{
    private readonly StringBuilder alternative = new();
    private bool secondLine;
    private int length;

    internal string Append(string delta)
    {
        length += delta.Length;
        if (length > 8000) throw new ProviderException("response_limit", "Response exceeded its size limit.");
        if (secondLine)
        {
            alternative.Append(delta);
            return "";
        }
        var separator = delta.IndexOfAny(['\r', '\n']);
        if (separator < 0) return delta;
        secondLine = true;
        alternative.Append(delta.AsSpan(separator + 1));
        return delta[..separator];
    }

    internal string? Alternative => CleanAlternative(alternative.ToString());

    internal static string[] Complete(string primary, string? alternative)
    {
        var clean = CleanAlternative(alternative);
        return clean is not null && !string.Equals(primary.Trim(), clean, StringComparison.OrdinalIgnoreCase)
            ? [primary, clean] : [primary];
    }

    private static string? CleanAlternative(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1000 || value.Any(char.IsControl) ||
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 25)
            return null;
        return value;
    }
}
