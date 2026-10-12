using System.ClientModel;
using Azure;

namespace VoiceAssistant.Api.Practice;

internal static class PracticeDiagnostics
{
    internal static string SafeRule(string? category)
    {
        if (category is "empty" or "json_parse" or "completion_token_limit" or "completion_not_stopped" or
            "schema:root" or "schema:output_size" or "schema:duplicate_property" or "schema:unicode" or
            "schema:control_character" or "schema:korean" or "schema:pronunciation:null" or
            "schema:pronunciation:chunks" or "schema:pronunciation:english_chunk" or
            "schema:pronunciation:english_chunk:type_or_length" or "schema:pronunciation:hangul_chunk" or
            "schema:pronunciation:rejoin" or "schema:pronunciation:rejoin:word_sequence" or
            "schema:pronunciation:rechunk:ko_count" or "schema:pronunciation:input_word" or
            "schema:text:question" or "schema:points" or "schema:clarity" or
            "schema:feedbackKo" or "schema:headlineKo" or "schema:strengthsKo" or "schema:improveKo" or "schema:phrases")
            return category;
        string[] fields = ["text:english_style", "correctedEnglish:style", "easierEnglish:style", "english_style", "korean", "feedbackKo"];
        string[] rules = ["type", "empty", "max_characters", "control_character", "whitespace", "no_english",
            "max_words", "max_sentences", "characters", "meta_commentary", "list_prefix", "url", "no_hangul", "hangul_first", "symbols", "markup"];
        foreach (var field in fields)
            foreach (var rule in rules)
                if (category == "schema:" + field + ":" + rule) return category;
        return "schema:output";
    }
    internal static void Failure(ILogger logger, string operation, Exception error, int attempt)
    {
        var endpoint = operation is "enrich" or "speak" or "turn" or "suggest" or "feedback" or "summary" ? operation : "unknown";
        var status = error switch { ClientResultException client => client.Status, RequestFailedException azure => azure.Status, _ => 0 };
        var category = error switch
        {
            PracticeException { DiagnosticCategory: { } code } => code,
            System.Text.Json.JsonException => "json_parse",
            OperationCanceledException => "timeout",
            ClientResultException or RequestFailedException => "http_error",
            _ => "provider_error"
        };
        logger.LogWarning("Practice endpoint {Endpoint} failure {Category}, HTTP status {Status}, attempt {Attempt}.",
            endpoint, category, status, attempt);
    }
}
