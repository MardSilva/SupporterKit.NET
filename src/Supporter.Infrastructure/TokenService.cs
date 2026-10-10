using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Supporter.Core;

namespace Supporter.Infrastructure;

// The host is intentionally a single instance. This lock serializes refresh-token rotation.
public sealed class TokenRefreshGate
{
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

public sealed class TokenService(SupporterDb db, SupporterProtection protection, ISupporterProvider provider,
    SupporterOptions options, TimeProvider time, TokenRefreshGate gate)
{
    private readonly IDataProtector protector = protection.Provider.CreateProtector("SupporterKit.OAuthTokens.v1");

    public async Task SaveAsync(string owner, OAuthTokens tokens, CancellationToken ct)
    {
        await gate.Lock.WaitAsync(ct);
        try { await WriteAsync(owner, tokens, ct); }
        finally { gate.Lock.Release(); }
    }
    private async Task WriteAsync(string owner, OAuthTokens tokens, CancellationToken ct)
    {
        var record = await db.Tokens.SingleOrDefaultAsync(t => t.Owner == owner, ct);
        if (record is null) { record = new TokenRecord { Owner = owner }; db.Tokens.Add(record); }
        record.ProtectedTokens = protector.Protect(JsonSerializer.Serialize(tokens));
        record.ExpiresAt = tokens.ExpiresAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> GetAccessTokenAsync(string owner, bool forceRefresh, CancellationToken ct)
    {
        await gate.Lock.WaitAsync(ct);
        try
        {
            var record = await db.Tokens.SingleOrDefaultAsync(t => t.Owner == owner, ct);
            if (record is not null) await db.Entry(record).ReloadAsync(ct);
            if (record is null && owner == "creator" && !string.IsNullOrWhiteSpace(options.Patreon.CreatorAccessToken) &&
                !string.IsNullOrWhiteSpace(options.Patreon.CreatorRefreshToken))
            {
                // Bootstrap once. An unknown expiry forces a refresh instead of guessing its lifetime.
                await WriteAsync(owner, new(options.Patreon.CreatorAccessToken, options.Patreon.CreatorRefreshToken, time.GetUtcNow()), ct);
                record = await db.Tokens.SingleAsync(t => t.Owner == owner, ct);
            }
            if (record is null) throw new ProviderException("missing_tokens");
            var tokens = JsonSerializer.Deserialize<OAuthTokens>(protector.Unprotect(record.ProtectedTokens))!;
            if (forceRefresh || tokens.ExpiresAt <= time.GetUtcNow().AddMinutes(1))
            {
                tokens = await provider.RefreshAsync(tokens.RefreshToken, ct);
                await WriteAsync(owner, tokens, ct);
            }
            return tokens.AccessToken;
        }
        finally { gate.Lock.Release(); }
    }
}
