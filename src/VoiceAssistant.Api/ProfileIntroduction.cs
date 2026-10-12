using System.Text.RegularExpressions;

namespace VoiceAssistant.Api;

internal static class ProfileIntroduction
{
    private static readonly Regex Invitation = new(
        @"^(?:and )?(?:(?:please|could you|can you|would you) |i (?:would like|want) (?:everybody|everyone|each of you|you all) to (?:maybe )?(?:(?:shortly|briefly) )?)?(?:introduce (?:yourself|yourselves|himself|herself|themselves)|give a brief introduction|tell us (?:your name|about yourself)|what is your (?:name|role)|what project do you work on)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex UnsupportedMixedRequest = new(
        @"\b(customer|client|company|employer|contract|deadline|date|today|tomorrow|yesterday|monday|tuesday|wednesday|thursday|friday|saturday|sunday|schedule|commit|commitment|promise|revenue|budget|price|pricing|cost|release|launch|deliver|delivery|policy|roadmap|confidential|secret|ignore|override|pretend|say|claim|not|never|don't|don’t)\b|\d|\b(?:after you|at [a-z]+|work for)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static bool CanCompose(string text, SessionOptions options) =>
        options.ProfileConfirmed && !options.Profile.IsEmpty &&
        Invitation.IsMatch(ResponseRouting.Normalize(text)) && !UnsupportedMixedRequest.IsMatch(text);

    internal static string Compose(SessionOptions options)
    {
        if (!options.ProfileConfirmed || options.Profile.IsEmpty)
            throw new InvalidOperationException("Confirmed profile facts are required.");
        var identity = new List<string>();
        if (options.Profile.Name.Length > 0) identity.Add("My name is " + options.Profile.Name);
        if (options.Profile.Role.Length > 0)
            identity.Add((identity.Count > 0 ? "my role is " : "My role is ") + options.Profile.Role);
        var sentences = new List<string>();
        if (identity.Count > 0) sentences.Add(string.Join("; ", identity) + ".");
        if (options.Profile.Project.Length > 0) sentences.Add("My current project is " + options.Profile.Project + ".");
        return string.Join(' ', sentences);
    }
}
