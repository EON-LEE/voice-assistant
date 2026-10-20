namespace VoiceAssistant.Desktop.Tests;

public sealed class ClientSettingsTests
{
    private static readonly string Shipped = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    [Fact]
    public void ShippedSettingsHoldOnlyPlaceholdersAndRefuseProduction()
    {
        var shipped = ClientSettings.Load(Shipped);
        Assert.Equal(ConnectionMode.Production, shipped.Mode);
        Assert.True(shipped.ContainsPlaceholders());
        Assert.True(new ClientSettings().ContainsPlaceholders());
        Assert.False(TestSettings.Production.ContainsPlaceholders());
        var error = Assert.Throws<InvalidOperationException>(() => shipped.Validate());
        Assert.Contains(ClientSettings.LocalFileName, error.Message);
        // The offline preview still starts without any deployment values.
        Assert.Equal("your-app-host", (shipped with { Mode = ConnectionMode.Demo }).Validate().Host);
    }

    [Fact]
    public void LocalFileReplacesDeploymentValuesWithTheSameStrictSchema()
    {
        var folder = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "settings-tests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var local = Path.Combine(folder, ClientSettings.LocalFileName);
            File.WriteAllText(local, """
                {"Endpoint":"wss://coach.example.test/api/meeting","Origin":"https://coach.example.test",
                 "TenantId":"11111111-1111-4111-8111-111111111111","ClientId":"22222222-2222-4222-8222-222222222222",
                 "Scope":"api://33333333-3333-4333-8333-333333333333/Meeting.Access"}
                """);
            var settings = ClientSettings.Load(Shipped, local);
            Assert.Equal(TestSettings.Production, settings);
            Assert.Equal("https://coach.example.test/", settings.ApiBase.AbsoluteUri);
            Assert.Equal(ClientSettings.Load(Shipped), ClientSettings.Load(Shipped, Path.Combine(folder, "absent.json")));
            File.WriteAllText(local, """{"endpoint":"wss://coach.example.test/api/meeting"}""");
            Assert.Throws<System.Text.Json.JsonException>(() => ClientSettings.Load(Shipped, local));
            File.WriteAllText(local, "[]");
            Assert.Throws<InvalidDataException>(() => ClientSettings.Load(Shipped, local));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
