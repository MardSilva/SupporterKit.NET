using System.IO.Compression;
using System.Text.Json;
using Supporter.Api;

namespace Supporter.Tests;

public sealed class SetupTests
{
    private static SetupInput Example() => new()
    {
        PublicOrigin = "https://supporters.example.com",
        CampaignId = "123",
        ClientId = "example-client",
        ClientSecret = "example-secret$LITERAL#value",
        CreatorAccessToken = "example-access",
        CreatorRefreshToken = "example-refresh",
        WebhookSecret = "example-webhook",
        InstallDirectory = "/opt/supporterkit",
        KeyCertificateFile = "/opt/supporterkit/secrets/keyring.pfx",
        KeyCertificatePassword = "example-password",
        TrustedProxyIp = "172.20.0.2",
        CosmosNetwork = "cosmos-network",
        Tiers = [new("456", "flow", ["supporter"])]
    };

    [Fact]
    public void SetupBundleContainsConsistentProductionFiles()
    {
        var input = Example();
        using var buffer = new MemoryStream(SetupFiles.Generate(input));
        using var zip = new ZipArchive(buffer);
        string Read(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }
        Assert.Equal(5, zip.Entries.Count);
        Assert.Contains("PATREON_CLIENT_SECRET='example-secret$LITERAL#value'", Read(".env"));
        using var json = JsonDocument.Parse(Read("appsettings.Production.json"));
        Assert.Equal("flow", json.RootElement.GetProperty("Supporter").GetProperty("Tiers").GetProperty("456").GetProperty("ProductId").GetString());
        var compose = Read("compose.yml");
        Assert.Contains("ASPNETCORE_ENVIRONMENT: Production", compose);
        Assert.DoesNotContain("ports:", compose);
        Assert.DoesNotContain(input.ClientSecret, compose);
        Assert.Contains("external: true", compose);
        Assert.Contains("appsettings.Production.json:ro", compose);
        Assert.Contains("${PUBLIC_ORIGIN}/auth/patreon/callback", compose);
        Assert.Contains(input.PublicOrigin + "/auth/patreon/callback", Read("LEIA-ME.txt"));
        Assert.DoesNotContain(input.ClientSecret, Read("LEIA-ME.txt"));
        Assert.Contains(".env", Read(".gitignore"));
        using var englishBuffer = new MemoryStream(SetupFiles.Generate(input with { Language = "en" }));
        using var englishZip = new ZipArchive(englishBuffer);
        Assert.Null(englishZip.GetEntry("LEIA-ME.txt"));
        using var englishReader = new StreamReader(englishZip.GetEntry("README.txt")!.Open());
        Assert.Contains("This ZIP contains credentials.", englishReader.ReadToEnd());
        using var englishConfig = new StreamReader(englishZip.GetEntry("appsettings.Production.json")!.Open());
        Assert.Equal(Read("appsettings.Production.json"), englishConfig.ReadToEnd());
    }

    [Fact]
    public void SetupRejectsUnsafePathsSecretsAndWrongTierMappings()
    {
        var input = Example();
        Assert.Throws<ArgumentException>(() => SetupFiles.Generate(input with { PublicOrigin = "https://supporters.example.com/cosmos-ui/" }));
        var englishError = Assert.Throws<ArgumentException>(() => SetupFiles.Generate(input with { Language = "en", PublicOrigin = "https://supporters.example.com/cosmos-ui/" }));
        Assert.StartsWith("Enter only the public HTTPS origin", englishError.Message);
        Assert.Throws<ArgumentException>(() => SetupFiles.Generate(input with { InstallDirectory = "/opt/../other" }));
        Assert.Throws<ArgumentException>(() => SetupFiles.Generate(input with { ClientSecret = "secret\nEXTRA=value" }));
        Assert.Throws<ArgumentException>(() => SetupFiles.Generate(input with { TrustedProxyIp = "172.20.0.0/16" }));
        Assert.Throws<ArgumentException>(() => SetupFiles.Generate(input with { Tiers = [] }));
        var campaign = new SetupCampaign("123", "Example", "", [new("456", "Paid", 300, true), new("789", "Free", 0, true)]);
        SetupFiles.ValidateTiers(input, campaign);
        Assert.Throws<ArgumentException>(() => SetupFiles.ValidateTiers(input with { Tiers = [new("789", "flow", ["supporter"])] }, campaign));
        Assert.Throws<ArgumentException>(() => SetupFiles.ValidateTiers(input with { Tiers = [new("999", "flow", ["supporter"])] }, campaign));
    }
}
