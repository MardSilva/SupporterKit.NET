using Microsoft.Extensions.Hosting;

namespace Supporter.Infrastructure;

public sealed class SupporterOptions
{
    public string Provider { get; set; } = "Real";
    public bool SimulationEnabled { get; set; }
    public string CampaignId { get; set; } = "";
    public string[] Products { get; set; } = [];
    public Dictionary<string, Supporter.Core.TierRule> Tiers { get; set; } = [];
    public string DataDirectory { get; set; } = "data";
    public string PublicOrigin { get; set; } = "";
    public string FixturesPath { get; set; } = "fixtures/profiles.json";
    public int SyncIntervalSeconds { get; set; } = 900;
    public int StaleAfterSeconds { get; set; } = 3600;
    public int InboxRetentionDays { get; set; } = 7;
    public string[] TrustedProxies { get; set; } = [];
    public PatreonOptions Patreon { get; set; } = new();

    public bool IsFake => Provider == "Fake";

    public void Validate(IHostEnvironment environment)
    {
        var local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        if (Provider is not ("Fake" or "Real")) throw new InvalidOperationException("Provider must be Fake or Real.");
        if (!local && (IsFake || SimulationEnabled)) throw new InvalidOperationException("Simulation is forbidden outside Development/Testing.");
        if (IsFake != SimulationEnabled) throw new InvalidOperationException("Fake requires SimulationEnabled; Real forbids it.");
        if (!Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) || !string.IsNullOrEmpty(origin.UserInfo) ||
            (origin.Scheme != "https" && !(local && origin.Scheme == "http" && origin.IsLoopback)))
            throw new InvalidOperationException("PublicOrigin must be an HTTPS origin, or localhost HTTP for development.");
        if (string.IsNullOrWhiteSpace(CampaignId) || Products.Length == 0 || Products.Any(string.IsNullOrWhiteSpace) ||
            Products.Distinct().Count() != Products.Length || SyncIntervalSeconds < 10 || StaleAfterSeconds < 10 ||
            InboxRetentionDays is < 1 or > 90 || string.IsNullOrWhiteSpace(DataDirectory))
            throw new InvalidOperationException("Invalid campaign, products, paths or intervals.");
        if (Tiers.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !Products.Contains(pair.Value.ProductId) ||
            pair.Value.BenefitCodes.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidOperationException("Tier mappings must reference configured products and nonempty codes.");
        foreach (var proxy in TrustedProxies)
            if (!System.Net.IPAddress.TryParse(proxy, out _)) throw new InvalidOperationException("TrustedProxies must contain IP addresses.");
        if (!IsFake && (string.IsNullOrWhiteSpace(Patreon.ClientId) || string.IsNullOrWhiteSpace(Patreon.ClientSecret) ||
            string.IsNullOrWhiteSpace(Patreon.WebhookSecret) || string.IsNullOrWhiteSpace(Patreon.UserAgent)))
            throw new InvalidOperationException("Real requires Patreon ClientId, ClientSecret, WebhookSecret and UserAgent.");
        if (!IsFake && Patreon.CallbackUri != PublicOrigin.TrimEnd('/') + "/auth/patreon/callback")
            throw new InvalidOperationException("CallbackUri must match PublicOrigin/auth/patreon/callback.");
    }
}
public sealed class PatreonOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string CallbackUri { get; set; } = "";
    public string CreatorAccessToken { get; set; } = "";
    public string CreatorRefreshToken { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string UserAgent { get; set; } = "SupporterKit.NET/0.1.2";
}
