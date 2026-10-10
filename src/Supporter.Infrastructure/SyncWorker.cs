using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Supporter.Core;

namespace Supporter.Infrastructure;

public sealed class SyncGate
{
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

public sealed class CampaignSynchronizer(SupporterDb db, ISupporterProvider provider,
    TokenService tokens, SupporterService supporters, SupporterOptions options, TimeProvider time, SyncGate gate)
{
    public async Task RunAsync(CancellationToken ct)
    {
        await gate.Lock.WaitAsync(ct);
        try
        {
            var access = options.IsFake ? "simulation" : await tokens.GetAccessTokenAsync("creator", false, ct);
            IReadOnlyList<MemberSnapshot> members;
            try { members = await provider.GetCampaignMembersAsync(access, ct); }
            catch (ProviderException ex) when (ex.Code == "unauthorized" && !options.IsFake)
            {
                access = await tokens.GetAccessTokenAsync("creator", true, ct);
                members = await provider.GetCampaignMembersAsync(access, ct);
            }
            // The provider returns only after all pages succeed. No partial snapshot is applied.
            var linked = await db.People.Where(p => p.Provider == options.Provider).ToArrayAsync(ct);
            var byUser = members.ToDictionary(m => m.UserId);
            foreach (var person in linked)
            {
                if (byUser.TryGetValue(person.ExternalUserId, out var member))
                    await supporters.ApplyIdentityAsync(new(member.UserId, member.DisplayName, member), ct);
                else
                {
                    // Absence from the campaign list does not revoke a person. Use their identity token.
                    try
                    {
                        string personalAccess;
                        if (options.IsFake)
                        {
                            personalAccess = ((FakeProvider)provider).Profiles.Single(p => p.Identity.UserId == person.ExternalUserId).Id;
                        }
                        else personalAccess = await tokens.GetAccessTokenAsync("person:" + person.Id, false, ct);
                        ProviderIdentity identity;
                        try { identity = await provider.GetIdentityAsync(personalAccess, ct); }
                        catch (ProviderException ex) when (ex.Code == "unauthorized" && !options.IsFake)
                        {
                            personalAccess = await tokens.GetAccessTokenAsync("person:" + person.Id, true, ct);
                            identity = await provider.GetIdentityAsync(personalAccess, ct);
                        }
                        if (identity.UserId != person.ExternalUserId) throw new ProviderException("identity_mismatch");
                        await supporters.ApplyIdentityAsync(identity, ct);
                    }
                    catch (Exception ex) when (ex is ProviderException or HttpRequestException or InvalidOperationException)
                    {
                        // Keep this person's timestamp and last snapshot; staleness is evaluated per person.
                    }
                }
            }
            var state = await db.SyncStates.SingleOrDefaultAsync(s => s.Id == "campaign", ct);
            if (state is null) { state = new SyncState(); db.SyncStates.Add(state); }
            state.LastSuccessfulSyncAt = time.GetUtcNow();
            state.LastErrorCode = null;
            await db.SaveChangesAsync(ct);
        }
        finally { gate.Lock.Release(); }
    }
}

public sealed class SyncWorker(IServiceScopeFactory scopes, SupporterOptions options, TimeProvider time,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await TickAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Supporter worker failed ({ErrorType}); last known state retained", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task TickAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<SupporterDb>();
        var now = time.GetUtcNow();
        await db.OAuthAttempts.Where(a => a.ExpiresAt < now).ExecuteDeleteAsync(ct);
        var cutoff = now.AddDays(-options.InboxRetentionDays);
        await db.Inbox.Where(e => e.ReceivedAt < cutoff && (e.CompletedAt != null || e.DeadLetter)).ExecuteDeleteAsync(ct);
        var state = await db.SyncStates.SingleOrDefaultAsync(s => s.Id == "campaign", ct);
        if (state is null) { state = new SyncState(); db.SyncStates.Add(state); await db.SaveChangesAsync(ct); }
        if (state.NextAllowedAt > now) return;
        var pending = await db.Inbox.Where(e => e.CompletedAt == null && !e.DeadLetter && e.NextAttemptAt <= now)
            .OrderBy(e => e.ReceivedAt).Take(25).ToArrayAsync(ct);
        if (pending.Length == 0 && state.LastAttemptAt > now.AddSeconds(-options.SyncIntervalSeconds)) return;
        state.LastAttemptAt = now;
        await db.SaveChangesAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            await services.GetRequiredService<CampaignSynchronizer>().RunAsync(timeout.Token);
            state.NextAllowedAt = null;
            foreach (var item in pending) item.CompletedAt = time.GetUtcNow();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            state.LastErrorCode = ex is ProviderException p ? p.Code : ex.GetType().Name;
            state.NextAllowedAt = ex is ProviderException { RetryAt: { } retryAt } ? retryAt : now.AddSeconds(30);
            foreach (var item in pending)
            {
                item.Attempts++;
                item.DeadLetter = item.Attempts >= 8;
                item.NextAttemptAt = state.NextAllowedAt.Value > now.AddSeconds(Math.Min(3600, Math.Pow(2, item.Attempts) * 5))
                    ? state.NextAllowedAt.Value : now.AddSeconds(Math.Min(3600, Math.Pow(2, item.Attempts) * 5));
            }
            logger.LogWarning("Supporter synchronization failed ({ErrorCode}); snapshot retained", state.LastErrorCode);
        }
        await db.SaveChangesAsync(ct);
    }
}
