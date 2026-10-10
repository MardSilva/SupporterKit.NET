using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Supporter.Infrastructure;

public sealed class WebhookInbox(SupporterDb db, SupporterOptions options, TimeProvider time, ILogger<WebhookInbox> logger)
{
    private bool Reject(string reason)
    {
        logger.LogWarning("Webhook rejected: reason={Reason}", reason);
        return false;
    }

    public static bool Verify(byte[] body, string signature, string secret)
    {
        if (string.IsNullOrEmpty(secret) || signature.Length != 32 || !signature.All(Uri.IsHexDigit)) return false;
        // HMAC-MD5 is the protocol required by Patreon, not a choice for new application cryptography.
        var expected = HMACMD5.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature));
    }

    public async Task<bool> AcceptAsync(byte[] body, string signature, string eventType, CancellationToken ct)
    {
        if (!Verify(body, signature, options.Patreon.WebhookSecret)) return Reject("signature_invalid");
        if (eventType is not ("members:create" or "members:update" or "members:delete" or
            "members:pledge:create" or "members:pledge:update" or "members:pledge:delete")) return Reject("event_unsupported");
        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        if (data.GetProperty("type").GetString() != "member") return Reject("resource_unsupported");
        var campaign = data.GetProperty("relationships").GetProperty("campaign").GetProperty("data").GetProperty("id").GetString();
        if (campaign != options.CampaignId) return Reject("campaign_mismatch");
        // No provider event ID is assumed. Deduplication lasts only until retention purges this digest.
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventType).Concat(body).ToArray()));
        if (await db.Inbox.AnyAsync(e => e.Digest == digest, ct)) return true;
        db.Inbox.Add(new InboxEvent
        {
            Digest = digest,
            CampaignId = campaign,
            EventType = eventType,
            ReceivedAt = time.GetUtcNow(),
            NextAttemptAt = time.GetUtcNow()
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await db.Inbox.AnyAsync(e => e.Digest == digest, ct)) throw;
        }
        return true;
    }
}
