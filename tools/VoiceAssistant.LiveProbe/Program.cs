using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using VoiceAssistant.Api;
using VoiceAssistant.LiveProbe;

return await ProbeCommand.RunAsync(args, Console.Out, CancellationToken.None);

namespace VoiceAssistant.LiveProbe
{
    public static class ProbeCommand
    {
        public const string Help = """
            Azure Speech + OpenAI live-provider acceptance probe (no API/JWT/browser/Search test).
            Usage: VoiceAssistant.LiveProbe --live --audio <original.wav> --metadata <original.json> [--timeout-seconds 90]
                   VoiceAssistant.LiveProbe --help
                   VoiceAssistant.LiveProbe --diagnose-auth
                   VoiceAssistant.LiveProbe --diagnose-default-auth
                   VoiceAssistant.LiveProbe --live --chat-only [--timeout-seconds 90]
            Requires explicit --live, approved synthetic en-US fixture metadata with SHA256,
            PCM16LE 16000 Hz mono WAV <=30 seconds and >=1 second zero tail, Azure__SpeechRegion,
            Azure__SpeechResourceId, Azure__OpenAIEndpoint, Azure__ChatDeployment; optional Azure__SpeechEndpoint.
            Standard noninteractive DefaultAzureCredential must already authenticate. No login or resource mutation.
            Optional metadata speechEndSample is exclusive sample-aligned ground truth; null means UNKNOWN.
            stdout: one content-free JSON evidence object. No tokens, transcript, source content or paths.
            Exit: 0 SUCCESS; 1 FAILED; 2 BLOCKED (opt-in/config/fixture/auth unavailable); 3 CANCELLED.
            --help exits0 but is not acceptance evidence. Offline test doubles are never labelled Azure.
            --diagnose-auth performs ONLY bounded AzurePowerShellCredential token acquisition and reports safe
            categories (AUTHENTICATED is NOT service acceptance). No tokens, accounts or raw exceptions are printed.
            --diagnose-default-auth checks the standard noninteractive DefaultAzureCredential chain instead.
            --live --chat-only sends one fixed original generic English prompt to OpenAI, with no Speech/Search.
            """;

        public static Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellation) =>
            args is ["--diagnose-auth"] ? AuthDiagnostic.RunAsync(output, cancellation) :
            args is ["--diagnose-default-auth"] ? AuthDiagnostic.RunAsync(output, cancellation, CreateDefaultCredential()) :
            RunCoreAsync(args, output, cancellation,
                () => new ConfigurationBuilder().AddEnvironmentVariables().Build(),
                CreateDefaultCredential);

        internal static DefaultAzureCredential CreateDefaultCredential() => new(new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
            ExcludeBrokerCredential = true,
            CredentialProcessTimeout = TimeSpan.FromSeconds(20)
        });

        internal static async Task<int> RunCoreAsync(string[] args, TextWriter output, CancellationToken cancellation,
            Func<IConfiguration> configuration, Func<TokenCredential> createCredential)
        {
            var startedAt = DateTimeOffset.UtcNow;
            var clock = new ProbeClock();
            async Task<int> Emit(string status, string reason)
            {
                var evidence = new ProbeEvidence(status, reason, "Azure", startedAt, clock.ElapsedMs);
                await output.WriteLineAsync(evidence.ToJson());
                return evidence.ExitCode;
            }
            if (args is ["--help"]) { await output.WriteLineAsync(Help); return 0; }
            if (!args.Contains("--live")) return await Emit("BLOCKED", "live_opt_in_required");
            string? audioPath = null, metadataPath = null;
            var timeoutSeconds = 90;
            var chatOnly = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                if (!seen.Add(args[i])) return await Emit("BLOCKED", "invalid_arguments");
                if (args[i] == "--live") continue;
                if (args[i] == "--chat-only") { chatOnly = true; continue; }
                if (i + 1 >= args.Length) return await Emit("BLOCKED", "invalid_arguments");
                switch (args[i])
                {
                    case "--audio": audioPath = args[++i]; break;
                    case "--metadata": metadataPath = args[++i]; break;
                    case "--timeout-seconds":
                        if (!int.TryParse(args[++i], out timeoutSeconds) || timeoutSeconds is < 1 or > 180)
                            return await Emit("BLOCKED", "invalid_arguments");
                        break;
                    default: return await Emit("BLOCKED", "invalid_arguments");
                }
            }
            var config = configuration();
            string Get(string key) => config[key] ?? "";
            int chatTokens;
            try { chatTokens = ServiceSettings.ReadChatTokenLimit(config); }
            catch (InvalidOperationException) { return await Emit("BLOCKED", "configuration_unavailable"); }
            var settings = new ServiceSettings
            {
                Mode = config["Provider:Mode"] ?? "Azure",
                SpeechRegion = Get("Azure:SpeechRegion"),
                SpeechResourceId = Get("Azure:SpeechResourceId"),
                SpeechEndpoint = Get("Azure:SpeechEndpoint"),
                OpenAIEndpoint = Get("Azure:OpenAIEndpoint"),
                ChatDeployment = Get("Azure:ChatDeployment"),
                ChatMaxOutputTokens = chatTokens
            };
            try { settings.ValidateAzureProvider(); }
            catch (InvalidOperationException) { return await Emit("BLOCKED", "configuration_unavailable"); }
            AudioFixture? fixture = null;
            if (!chatOnly)
            {
                if (audioPath is null || metadataPath is null) return await Emit("BLOCKED", "fixture_required");
                try { fixture = AudioFixture.Load(audioPath, metadataPath); }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                { return await Emit("BLOCKED", "fixture_invalid"); }
            }
            else if (audioPath is not null || metadataPath is not null) return await Emit("BLOCKED", "invalid_arguments");

            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            ConsoleCancelEventHandler stopHandler = (_, e) => { e.Cancel = true; stopped.Cancel(); };
            Console.CancelKeyPress += stopHandler;
            try
            {
                var credential = createCredential();
                using var authentication = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                authentication.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), authentication.Token)
                        .AsTask().WaitAsync(authentication.Token);
                }
                catch (OperationCanceledException) when (stopped.IsCancellationRequested) { return await Emit("CANCELLED", "cancelled"); }
                catch (Exception) { return await Emit("BLOCKED", "authentication_unavailable"); }
                var provider = new AzureMeetingProvider(settings, credential);
                if (chatOnly) return await ChatDiagnostic.RunAsync(provider, TimeSpan.FromSeconds(timeoutSeconds), output, stopped.Token);
                var result = await new ProbeRunner(provider)
                    .RunAsync(fixture!, TimeSpan.FromSeconds(timeoutSeconds), stopped.Token);
                await output.WriteLineAsync(result.ToJson());
                return result.ExitCode;
            }
            catch (Exception) { return await Emit("FAILED", "provider_initialization_failed"); }
            finally { Console.CancelKeyPress -= stopHandler; }
        }
    }
}
