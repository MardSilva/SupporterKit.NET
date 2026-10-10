using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Supporter.Core;

namespace Supporter.Infrastructure;

public sealed class ProviderException(string code, DateTimeOffset? retryAt = null) : Exception(code)
{
    public string Code { get; } = code;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}

public sealed class PatreonProvider(HttpClient http, SupporterOptions options, TimeProvider time) : ISupporterProvider
{
    private const string MemberFields = "full_name,patron_status,last_charge_status,campaign_lifetime_support_cents,currently_entitled_amount_cents,pledge_relationship_start";
    private static string Query(string includes) =>
        "?include=" + Uri.EscapeDataString(includes) +
        "&fields%5Buser%5D=full_name&fields%5Bmember%5D=" + MemberFields +
        "&fields%5Btier%5D=title&fields%5Bcampaign%5D=name";

    public async Task<ProviderIdentity> GetIdentityAsync(string accessToken, CancellationToken ct)
    {
        using var document = await GetAsync("/api/oauth2/v2/identity" +
            Query("memberships.campaign,memberships.currently_entitled_tiers"), accessToken, ct);
        var root = document.RootElement;
        var user = root.GetProperty("data");
        var userId = RequiredString(user, "id");
        var name = AttributeString(user, "full_name");
        // Use only members referenced by this identity; included resources alone are not proof.
        var membershipIds = Relationship(user, "memberships").EnumerateArray()
            .Select(m => RequiredString(m, "id")).ToHashSet();
        MemberSnapshot? membership = null;
        var resolvedMembershipIds = new HashSet<string>();
        if (root.TryGetProperty("included", out var included))
            foreach (var resource in included.EnumerateArray().Where(r => Text(r, "type") == "member"))
            {
                if (!membershipIds.Contains(RequiredString(resource, "id"))) continue;
                resolvedMembershipIds.Add(RequiredString(resource, "id"));
                var campaign = RequiredString(Relationship(resource, "campaign"), "id");
                if (campaign != options.CampaignId) continue;
                if (membership is not null) throw new ProviderException("ambiguous_membership");
                membership = ParseMember(resource, userId, name, campaign);
            }
        if (!membershipIds.SetEquals(resolvedMembershipIds))
            throw new ProviderException("missing_included_memberships");
        return new(userId, name, membership);
    }

    public async Task<IReadOnlyList<MemberSnapshot>> GetCampaignMembersAsync(string accessToken, CancellationToken ct)
    {
        var path = "/api/oauth2/v2/campaigns/" + Uri.EscapeDataString(options.CampaignId) + "/members" +
            Query("campaign,user,currently_entitled_tiers") + "&page%5Bcount%5D=100";
        var all = new Dictionary<string, MemberSnapshot>();
        var cursors = new HashSet<string>();
        string? cursor = null;
        do
        {
            using var document = await GetAsync(path + (cursor is null ? "" : "&page%5Bcursor%5D=" + Uri.EscapeDataString(cursor)), accessToken, ct);
            var root = document.RootElement;
            var users = root.TryGetProperty("included", out var inc) ? inc.EnumerateArray()
                .Where(r => Text(r, "type") == "user").ToDictionary(r => RequiredString(r, "id"), r => AttributeString(r, "full_name")) : [];
            foreach (var resource in root.GetProperty("data").EnumerateArray())
            {
                var campaign = RequiredString(Relationship(resource, "campaign"), "id");
                if (campaign != options.CampaignId) throw new ProviderException("wrong_campaign");
                var userId = RequiredString(Relationship(resource, "user"), "id");
                if (all.ContainsKey(userId)) throw new ProviderException("duplicate_member_identity");
                all.Add(userId, ParseMember(resource, userId, users.GetValueOrDefault(userId), campaign));
            }
            // Rebuild the URL from the opaque cursor: never send credentials to a supplied links.next host.
            cursor = Text(root.GetProperty("meta").GetProperty("pagination").GetProperty("cursors"), "next");
            if (cursor is not null && (!cursors.Add(cursor) || cursors.Count > 10000))
                throw new ProviderException("invalid_pagination");
        } while (cursor is not null);
        return all.Values.ToArray();
    }

    public Task<OAuthTokens> ExchangeCodeAsync(string code, CancellationToken ct) =>
        TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = options.Patreon.CallbackUri }, ct);

    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, ct);

    private async Task<OAuthTokens> TokenAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        fields["client_id"] = options.Patreon.ClientId;
        fields["client_secret"] = options.Patreon.ClientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/oauth2/token") { Content = new FormUrlEncodedContent(fields) };
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ProviderException("token_" + (int)response.StatusCode);
        using var json = await ReadJsonAsync(response, ct);
        var root = json.RootElement;
        var seconds = root.GetProperty("expires_in").GetInt32();
        if (seconds <= 0) throw new ProviderException("invalid_token_expiry");
        return new(RequiredString(root, "access_token"), RequiredString(root, "refresh_token"), time.GetUtcNow().AddSeconds(seconds));
    }

    private async Task<JsonDocument> GetAsync(string path, string token, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return await ReadJsonAsync(response, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new ProviderException("unauthorized");
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - time.GetUtcNow());
                if (delay is null && response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    try
                    {
                        using var error = await ReadJsonAsync(response, ct);
                        if (error.RootElement.TryGetProperty("errors", out var errors))
                            foreach (var item in errors.EnumerateArray())
                                if (item.TryGetProperty("retry_after_seconds", out var seconds) && seconds.TryGetInt32(out var value) && value >= 0)
                                    delay = TimeSpan.FromSeconds(Math.Max(delay?.TotalSeconds ?? 0, value));
                    }
                    catch (Exception ex) when (ex is JsonException or ProviderException or InvalidOperationException) { }
                }
                delay ??= TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                if (attempt >= 2 || delay > TimeSpan.FromSeconds(30))
                    throw new ProviderException("http_" + (int)response.StatusCode, time.GetUtcNow().Add(delay.Value));
                await Task.Delay(delay.Value, time, ct);
                continue;
            }
            throw new ProviderException("http_" + (int)response.StatusCode);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Patreon payloads are bounded to keep a bad upstream response from exhausting the process.
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > 8 * 1024 * 1024) throw new ProviderException("response_too_large");
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new ProviderException("invalid_json"); }
    }

    private static MemberSnapshot ParseMember(JsonElement member, string userId, string? name, string campaign)
    {
        var attributes = member.GetProperty("attributes");
        // Missing fields signal a contract/scope problem; null remains a legitimate external value.
        var lifetime = Number(attributes, "campaign_lifetime_support_cents");
        var amount = Number(attributes, "currently_entitled_amount_cents");
        var tiers = Relationship(member, "currently_entitled_tiers").EnumerateArray().Select(t => RequiredString(t, "id")).ToArray();
        return new(userId, name ?? AttributeString(member, "full_name"), campaign, RequiredString(member, "id"),
            Text(attributes, "patron_status", true), Text(attributes, "last_charge_status", true), lifetime, amount, tiers,
            ParseDate(attributes, "pledge_relationship_start"));
    }
    private static DateTimeOffset? ParseDate(JsonElement attributes, string field)
    {
        var value = Text(attributes, field);
        if (value is null) return null;
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date))
            throw new ProviderException("invalid_" + field);
        return date;
    }
    private static long? Number(JsonElement element, string field)
    {
        var value = element.GetProperty(field);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
    }
    private static JsonElement Relationship(JsonElement resource, string name) =>
        resource.GetProperty("relationships").GetProperty(name).GetProperty("data");
    private static string? AttributeString(JsonElement element, string name) =>
        element.TryGetProperty("attributes", out var attrs) ? Text(attrs, name) : null;
    private static string RequiredString(JsonElement element, string name) =>
        Text(element, name, true) is { Length: > 0 } value ? value : throw new ProviderException("missing_" + name);
    private static string? Text(JsonElement element, string name, bool required = false)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            if (required) throw new ProviderException("missing_" + name);
            return null;
        }
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }
}
