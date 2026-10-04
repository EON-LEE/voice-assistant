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
            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<TicketStore>();
            builder.Services.AddSingleton<SessionSlots>();
            builder.Services.AddSingleton<AzureServiceClients>();
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
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
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
            builder.Services.AddAuthorization(options => options.AddPolicy("Meeting", policy =>
                policy.RequireAuthenticatedUser().RequireAssertion(context =>
                    MeetingIdentity.HasScope(context.User) && MeetingIdentity.ObjectId(context.User) is not null)));

            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                await next(context);
            });
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
            app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
            app.MapGet("/api/client-config", () => Results.Ok(new
            {
                clientId = settings.ClientId,
                authority = settings.Fake ? "" : $"https://login.microsoftonline.com/{settings.TenantId}",
                scope = settings.Scope,
                mode = settings.Mode,
                webSocketPath = "/api/meeting"
            }));
            app.MapPost("/api/session/ticket", (HttpContext context, TicketStore store) =>
            {
                if (!OriginPolicy.Allows(context, settings)) return Results.StatusCode(403);
                var ticket = store.Issue(MeetingIdentity.ObjectId(context.User)!, context.Request.Headers.Origin.ToString());
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
                using var slot = slots.TryAcquire(objectId);
                if (slot is null) { context.Response.StatusCode = 429; return; }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await new MeetingSession(socket, provider, objectId, app.Logger, settings.MaxSessionMinutes,
                    context.RequestServices.GetRequiredService<TimeProvider>()).RunAsync(context.RequestAborted);
            });
            app.MapKnowledge(settings);
            app.MapPractice(settings);
            app.MapFallbackToFile("index.html");
            return app;
        }
    }
}
