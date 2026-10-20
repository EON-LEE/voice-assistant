namespace VoiceAssistant.WebOverlay;

/// <summary>The trust boundary of the host: only the hosted page's own origin may use host features.</summary>
internal static class PageOrigin
{
    /// <summary>True when <paramref name="uri"/> has the same scheme, host and port as the hosted page.</summary>
    public static bool Matches(Uri uri, Uri page) =>
        Uri.Compare(uri, page, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    public static bool Matches(string? uri, Uri page) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && Matches(parsed, page);
}
