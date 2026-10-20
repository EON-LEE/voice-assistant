namespace VoiceAssistant.Desktop.Tests;

/// <summary>Synthetic production settings; real deployment values live only in the git-ignored appsettings.local.json.</summary>
internal static class TestSettings
{
    public static ClientSettings Production { get; } = new()
    {
        Mode = ConnectionMode.Production,
        Endpoint = "wss://coach.example.test/api/meeting",
        Origin = "https://coach.example.test",
        TenantId = "11111111-1111-4111-8111-111111111111",
        ClientId = "22222222-2222-4222-8222-222222222222",
        Scope = "api://33333333-3333-4333-8333-333333333333/Meeting.Access"
    };
}
