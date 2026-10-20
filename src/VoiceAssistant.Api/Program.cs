using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VoiceAssistant.Api;
using VoiceAssistant.Api.Knowledge;
using VoiceAssistant.Api.Practice;

var app = ApiApplication.Build(args);
app.Run();

public partial class Program;

namespace VoiceAssistant.Api
{
    public static class ApiApplication
    {
        public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
        {
            var builder = WebApplication.CreateBuilder(args);
            configure?.Invoke(builder);
            // Hosting request logs contain the WebSocket query. Never register HTTP/body logging.
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
            builder.Logging.AddFilter("Microsoft.IdentityModel", LogLevel.None);
            builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
            var settings = ServiceSettings.Read(builder.Configuration, builder.Environment);
            builder.Services.AddSingleton(settings);
            ApiTelemetry.Configure(builder.Services, builder.Configuration);
            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<TicketStore>();
            builder.Services.AddSingleton<SessionSlots>();
            builder.Services.AddSingleton<AzureServiceClients>();
            // Azure mode takes traffic only after the dependencies a reply needs were warmed once; Fake mode is ready at once.
            builder.Services.AddSingleton(new Readiness(ready: settings.Fake));
            if (!settings.Fake)
            {
                builder.Services.TryAddSingleton<IDependencyWarmup>(services =>
                    new AzureDependencyWarmup(settings, services.GetRequiredService<AzureServiceClients>()));
                builder.Services.AddHostedService<StartupWarmup>();
            }
            builder.Services.TryAddSingleton<IMeetingProvider>(services => settings.Fake
                ? new FakeMeetingProvider()
                : new AzureMeetingProvider(settings, services.GetRequiredService<AzureServiceClients>()));
            builder.Services.TryAddSingleton<IKnowledgeStore>(services =>
            {
                if (settings.Fake) return new InMemoryKnowledgeStore();
                var clients = services.GetRequiredService<AzureServiceClients>();
                return new AzureKnowledgeStore(clients.Search,
                    settings.EmbeddingDeployment.Length == 0 ? null : clients.OpenAI.GetEmbeddingClient(settings.EmbeddingDeployment),
                    services.GetRequiredService<ILogger<AzureKnowledgeStore>>());
            });
            builder.Services.AddSingleton<KnowledgeService>();
            builder.Services.AddSingleton<MaterialPhraseHints>();
            builder.Services.AddSingleton<MaterialExtractor>();
            builder.Services.AddSingleton<KnowledgeRequestReader>();
            builder.Services.TryAddSingleton<IPracticeModel>(services => settings.Fake
                ? new FakePracticeModel()
                : new AzurePracticeModel(services.GetRequiredService<AzureServiceClients>().OpenAI, settings));
            builder.Services.TryAddSingleton<IPracticeSpeech>(services => settings.Fake
                ? new FakePracticeSpeech()
                : new AzurePracticeSpeech(settings, services.GetRequiredService<AzureServiceClients>(),
                    services.GetRequiredService<ILogger<AzurePracticeSpeech>>()));
            builder.Services.AddSingleton<PracticeService>();
            builder.Services.AddSingleton<PracticeLimiter>();
            var demo = DemoLoginSettings.Read(builder.Configuration);
            builder.Services.AddSingleton(demo);
            builder.Services.AddSingleton<DemoLoginLimiter>();
            var authentication = builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                if (!settings.Fake)
                {
                    options.Authority = $"https://login.microsoftonline.com/{settings.TenantId}/v2.0";
                    options.Audience = settings.Audience;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateAudience = true,
                        ValidateIssuer = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        RequireSignedTokens = true,
                        ValidIssuer = $"https://login.microsoftonline.com/{settings.TenantId}/v2.0",
                        ValidAudience = settings.Audience,
                        ClockSkew = TimeSpan.FromSeconds(30)
                    };
                }
            });
            if (demo.Enabled) authentication.AddJwtBearer(DemoLoginSettings.Scheme, options => DemoLoginSettings.Configure(options, demo));
            builder.Services.AddAuthorization(options => options.AddPolicy("Meeting", policy =>
            {
                // Entra tokens always; demo-login tokens only when DemoLogin is configured.
                policy.AuthenticationSchemes.Add(JwtBearerDefaults.AuthenticationScheme);
                if (demo.Enabled) policy.AuthenticationSchemes.Add(DemoLoginSettings.Scheme);
                policy.RequireAuthenticatedUser().RequireAssertion(context =>
                    MeetingIdentity.HasScope(context.User) && MeetingIdentity.ObjectId(context.User) is not null);
            }));

            var app = builder.Build();
            var features = new MeetingFeatures
            {
                EarlyStartAtFinal = settings.EarlyStartAtFinal,
                PhraseHints = settings.MaterialPhraseHints ? app.Services.GetRequiredService<MaterialPhraseHints>() : null
            };
            // Content-free: which experiment switches this process runs with, so a rejected request can be traced to one.
            app.Logger.LogInformation("Live reply switches: answer reasoning effort {AnswerEffort}, classifier/translation reasoning effort " +
                "{AuxiliaryEffort}, chat api-version {ApiVersion}, early start at final {EarlyStart}, material phrase hints {PhraseHints}, " +
                "commitment policy {CommitmentPolicy}.",
                settings.ChatReasoningEffort ?? "unset", settings.ChatAuxiliaryReasoningEffort ?? "unset", settings.ChatApiVersion ?? "sdk default",
                features.EarlyStartAtFinal, features.PhraseHints is not null, settings.CommitmentPolicy);
            var strictTransport = !app.Environment.IsDevelopment();
            app.Use(async (context, next) =>
            {
                var headers = context.Response.Headers;
                headers.CacheControl = "no-store";
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), geolocation=(), microphone=(self), display-capture=(self)";
                headers.ContentSecurityPolicy = "script-src 'self'; frame-ancestors 'self'; object-src 'none'; base-uri 'self'";
                if (strictTransport && SecurityHeaders.IsHttps(context.Request))
                    headers.StrictTransportSecurity = SecurityHeaders.StrictTransportSecurity;
                await next(context);
            });
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
            app.MapGet("/health/ready", (Readiness readiness) => readiness.IsReady
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = readiness.Status }, statusCode: StatusCodes.Status503ServiceUnavailable));
            app.MapGet("/api/client-config", () => Results.Ok(new
            {
                clientId = settings.ClientId,
                authority = settings.Fake ? "" : $"https://login.microsoftonline.com/{settings.TenantId}",
                scope = settings.Scope,
                mode = settings.Mode,
                login = demo.Enabled && !settings.Fake ? "demo" : "entra",
                webSocketPath = "/api/meeting"
            }));
            app.MapDemoLogin(settings, demo);
            app.MapPost("/api/session/ticket", (HttpContext context, TicketStore store) =>
            {
                if (!OriginPolicy.Allows(context, settings)) return Results.StatusCode(403);
                var objectId = MeetingIdentity.ObjectId(context.User)!;
                var ticket = store.Issue(objectId, context.Request.Headers.Origin.ToString());
                // A session follows the ticket within seconds; its phrase hints can be built meanwhile.
                if (ticket is not null) features.PhraseHints?.Warm(objectId);
                return ticket is null ? Results.StatusCode(429) : Results.Ok(ticket);
            }).RequireAuthorization("Meeting");
            app.Map("/api/meeting", async (HttpContext context, TicketStore store, IMeetingProvider provider, SessionSlots slots) =>
            {
                if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
                if (!OriginPolicy.Allows(context, settings)) { context.Response.StatusCode = 403; return; }
                string objectId;
                if (settings.Fake) objectId = "00000000-0000-0000-0000-000000000001";
                else
                {
                    var values = context.Request.Query["ticket"];
                    var identity = values.Count == 1 ? store.Consume(values[0] ?? "", context.Request.Headers.Origin.ToString()) : null;
                    if (identity is null) { context.Response.StatusCode = 401; return; }
                    objectId = identity.ObjectId;
                }
                using var slot = await slots.AcquireAsync(objectId, context.RequestAborted);
                if (slot is null) { context.Response.StatusCode = 429; return; }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await new MeetingSession(socket, provider, objectId, app.Logger, settings.MaxSessionMinutes,
                    context.RequestServices.GetRequiredService<TimeProvider>(), features).RunAsync(context.RequestAborted, slot.Superseded);
            });
            app.MapKnowledge(settings);
            app.MapPractice(settings);
            app.MapFallbackToFile("index.html");
            return app;
        }
    }
}
