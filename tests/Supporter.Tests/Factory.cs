using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Supporter.Core;
using Supporter.Infrastructure;

namespace Supporter.Tests;

internal sealed class Factory(string? data = null, bool realOAuth = false, bool worker = false, string environment = "Testing") : WebApplicationFactory<Program>
{
    public string DataPath { get; } = data ?? Path.Combine(Path.GetTempPath(), "SupporterKit.Tests", Guid.NewGuid().ToString("N"));
    public static string RepoRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "SupporterKit.NET.slnx"))) current = current.Parent;
            return current?.FullName ?? throw new InvalidOperationException("Repository not found.");
        }
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Supporter:Provider"] = realOAuth ? "Real" : "Fake",
            ["Supporter:SimulationEnabled"] = realOAuth ? "false" : "true",
            ["Supporter:CampaignId"] = "fake-campaign",
            ["Supporter:PublicOrigin"] = "https://supporter.test",
            ["Supporter:DataDirectory"] = DataPath,
            ["Supporter:KeyCertificatePath"] = File.Exists(Path.Combine(DataPath, "keyring.pfx")) ? Path.Combine(DataPath, "keyring.pfx") : null,
            ["Supporter:KeyCertificatePassword"] = "test-key-password",
            ["Supporter:FixturesPath"] = Path.Combine(RepoRoot, "fixtures", "profiles.json"),
            ["Supporter:Products:0"] = "flow",
            ["Supporter:Tiers:tier-a:ProductId"] = "flow",
            ["Supporter:Tiers:tier-a:BenefitCodes:0"] = "supporter-a",
            ["Supporter:SyncIntervalSeconds"] = "10",
            ["Supporter:StaleAfterSeconds"] = "10",
            ["Supporter:Patreon:ClientId"] = "test-client",
            ["Supporter:Patreon:ClientSecret"] = "test-secret",
            ["Supporter:Patreon:WebhookSecret"] = "test-webhook-secret",
            ["Supporter:Patreon:CallbackUri"] = "https://supporter.test/auth/patreon/callback",
        }));
        builder.ConfigureServices(services =>
        {
            if (!worker) services.RemoveAll<IHostedService>();
            if (realOAuth)
            {
                services.RemoveAll<ISupporterProvider>();
                services.AddSingleton<ISupporterProvider>(sp => new OAuthStub(new FakeProvider(
                    sp.GetRequiredService<SupporterOptions>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IHostEnvironment>())));
            }
        });
    }
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://supporter.test"),
        AllowAutoRedirect = false
    });
    private sealed class OAuthStub(FakeProvider fake) : ISupporterProvider
    {
        public Task<ProviderIdentity> GetIdentityAsync(string token, CancellationToken ct) => fake.GetIdentityAsync(token, ct);
        public Task<IReadOnlyList<MemberSnapshot>> GetCampaignMembersAsync(string token, CancellationToken ct) => fake.GetCampaignMembersAsync(token, ct);
        public Task<OAuthTokens> ExchangeCodeAsync(string code, CancellationToken ct) => Task.FromResult(new OAuthTokens("active-a", "active-a", DateTimeOffset.UtcNow.AddHours(1)));
        public Task<OAuthTokens> RefreshAsync(string token, CancellationToken ct) => fake.RefreshAsync(token, ct);
    }
}
