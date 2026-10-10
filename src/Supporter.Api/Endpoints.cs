using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Supporter.Core;
using Supporter.Infrastructure;

namespace Supporter.Api;

public static class Endpoints
{
    private static Guid PersonId(HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static void MapSupporterEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<SupporterOptions>();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (SupporterDb db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
        app.MapGet("/", () => Results.Redirect(options.IsFake ? "/dev" : "/auth/patreon/start")).ExcludeFromDescription();
        app.MapGet("/auth/patreon/start", async (HttpContext context, SupporterService service, CancellationToken ct) =>
        {
            if (options.IsFake) return Results.Problem(statusCode: 400, title: "Use a página de simulação.");
            var attempt = await service.StartOAuthAsync(ct);
            context.Response.Cookies.Append("supporter.oauth", attempt.Session, new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = "/auth/patreon/callback",
                IsEssential = true
            });
            return Results.Redirect("https://www.patreon.com/oauth2/authorize?response_type=code&client_id=" +
                Uri.EscapeDataString(options.Patreon.ClientId) + "&redirect_uri=" + Uri.EscapeDataString(options.Patreon.CallbackUri) +
                "&scope=" + Uri.EscapeDataString("identity identity.memberships campaigns") + "&state=" + attempt.State);
        }).RequireRateLimiting("sensitive");
        app.MapGet("/auth/patreon/callback", async (HttpContext context, SupporterService service,
            ISupporterProvider provider, TokenService tokens, CancellationToken ct) =>
        {
            if (options.IsFake) return Results.Problem(statusCode: 400, title: "OAuth não está ativo na simulação.");
            var state = context.Request.Query["state"].ToString();
            var session = context.Request.Cookies["supporter.oauth"];
            context.Response.Cookies.Delete("supporter.oauth", new CookieOptions { Path = "/auth/patreon/callback" });
            if (state.Length != 64 || session is null || session.Length != 64 || await service.ConsumeOAuthAsync(state, session, ct) != 1)
                return Results.Problem(statusCode: 400, title: "Autorização expirada, inválida ou já utilizada.");
            var code = context.Request.Query["code"].ToString();
            if (code.Length is < 1 or > 2048 || context.Request.Query.ContainsKey("error"))
                return Results.Problem(statusCode: 400, title: "Autorização não concluída.");
            var granted = await provider.ExchangeCodeAsync(code, ct);
            var identity = await provider.GetIdentityAsync(granted.AccessToken, ct);
            var person = await service.ApplyIdentityAsync(identity, ct);
            await tokens.SaveAsync("person:" + person.Id, granted, ct);
            // Replace any previous browser identity with the account just authorized.
            await SignInAsync(context, person.Id);
            return Results.Redirect("/manage");
        }).RequireRateLimiting("sensitive");
        app.MapGet("/manage", async (HttpContext context, SupporterService service, SupporterDb db,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            var id = PersonId(context);
            var status = await service.GetStatusAsync(id, options.Products[0], ct);
            var person = await db.People.SingleAsync(p => p.Id == id, ct);
            return Pages.Manage(context, antiforgery, status!, person, options.IsFake,
                options.IsFake ? app.Services.GetRequiredService<FakeProvider>().Profiles : []);
        }).RequireAuthorization("Management").RequireRateLimiting("public").ExcludeFromDescription();
        app.MapPost("/api/v1/supporter/keys", async (HttpContext context, IAntiforgery antiforgery,
            SupporterService service, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            return Results.Ok(new IssuedKey(await service.RotateKeyAsync(PersonId(context), ct)));
        }).RequireAuthorization("Management").RequireRateLimiting("sensitive").Produces<IssuedKey>()
            .WithDescription("Emissão ou rotação. Exige cookie supporter.management e header X-CSRF-TOKEN da página /manage. Revoga a chave anterior; a resposta mostra a nova chave uma vez.");
        app.MapGet("/api/v1/supporter/me", async (HttpContext context, string productId, SupporterService service, CancellationToken ct) =>
        {
            var status = await service.GetStatusAsync(PersonId(context), productId, ct);
            return status is null ? Results.Unauthorized() : Results.Ok(status);
        }).RequireAuthorization().RequireRateLimiting("public").Produces<SupporterStatus>()
            .WithDescription("Authorization: Bearer CHAVE. productId deve ser configurado na instalação. Pessoa autenticada sem apoio recebe 200; chave inválida recebe 401.");
        app.MapPut("/api/v1/supporter/public-profile", async (HttpContext context, IAntiforgery antiforgery,
            SupporterService service, CancellationToken ct) =>
        {
            Guid? id;
            if (context.Request.Headers.Authorization.Count > 0)
            {
                var auth = await context.AuthenticateAsync("ApiKey");
                id = auth.Succeeded ? Guid.Parse(auth.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!) : null;
            }
            else
            {
                var auth = await context.AuthenticateAsync("Management");
                id = auth.Succeeded ? Guid.Parse(auth.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!) : null;
                if (id is not null) await antiforgery.ValidateRequestAsync(context);
            }
            if (id is null) return Results.Problem(statusCode: 401, title: "Autenticação necessária.");
            var update = await context.Request.ReadFromJsonAsync<PublicProfileUpdate>(ct) ?? throw new ArgumentException();
            await service.UpdatePublicProfileAsync(id.Value, update, ct);
            return Results.NoContent();
        }).RequireRateLimiting("sensitive").Accepts<PublicProfileUpdate>("application/json").Produces(StatusCodes.Status204NoContent)
            .WithDescription("Authorization: Bearer CHAVE, ou cookie supporter.management com X-CSRF-TOKEN. Opt-in explícito, inicialmente desativado; pseudonym opcional até 80 caracteres.");
        app.MapGet("/api/v1/supporters", async (int? page, int? pageSize, SupporterService service, CancellationToken ct) =>
            Results.Ok(await service.GetPublicAsync(page ?? 1, pageSize ?? 25, ct)))
            .RequireRateLimiting("public").Produces<PublicSupporterPage>();
        app.MapPost("/api/v1/webhooks/patreon", async (HttpContext context, WebhookInbox inbox, CancellationToken ct) =>
        {
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > 256 * 1024) return Results.Problem(statusCode: 413, title: "Payload muito grande.");
                body.Write(buffer, 0, read);
            }
            try
            {
                var accepted = await inbox.AcceptAsync(body.ToArray(), context.Request.Headers["X-Patreon-Signature"].ToString(),
                    context.Request.Headers["X-Patreon-Event"].ToString(), ct);
                return accepted ? Results.Accepted() : Results.Problem(statusCode: 400, title: "Webhook inválido.");
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            { return Results.Problem(statusCode: 400, title: "Payload inválido."); }
        }).RequireRateLimiting("public").Produces(StatusCodes.Status202Accepted)
            .WithDescription("Corpo JSON:API original até 256 KiB. Headers X-Patreon-Event e X-Patreon-Signature (HMAC-MD5 hexadecimal). Retorna 202 somente após persistir a inbox; processamento assíncrono.");
        app.MapPost("/manage/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await context.SignOutAsync("Management");
            return Results.NoContent();
        }).RequireAuthorization("Management").RequireRateLimiting("sensitive").ExcludeFromDescription();

        if (options.IsFake && (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")))
            MapSimulation(app);
    }

    private static void MapSimulation(WebApplication app)
    {
        app.MapGet("/dev", (HttpContext context, IAntiforgery anti, FakeProvider fake) =>
            Pages.Simulation(context, anti, fake.Profiles)).RequireRateLimiting("public").ExcludeFromDescription();
        app.MapPost("/dev/login", async (HttpContext context, IAntiforgery anti, FakeProvider fake,
            SupporterService service, CancellationToken ct) =>
        {
            await anti.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(ct);
            var identity = await fake.GetIdentityAsync(form["profileId"].ToString(), ct);
            var person = await service.ApplyIdentityAsync(identity, ct);
            await SignInAsync(context, person.Id);
            return Results.Redirect("/manage");
        }).RequireRateLimiting("sensitive").ExcludeFromDescription();
        app.MapPost("/dev/membership", async (HttpContext context, IAntiforgery anti, FakeProvider fake,
            SupporterDb db, SupporterService service, CancellationToken ct) =>
        {
            await anti.ValidateRequestAsync(context);
            var change = await context.Request.ReadFromJsonAsync<SimulationChange>(ct) ?? throw new ArgumentException();
            var template = fake.Profiles.SingleOrDefault(p => p.Id == change.ProfileId) ?? throw new ArgumentException();
            var person = await db.People.SingleAsync(p => p.Id == PersonId(context), ct);
            var current = fake.Profiles.Single(p => p.Identity.UserId == person.ExternalUserId);
            var member = template.Identity.Membership is { } m ? m with
            {
                UserId = current.Identity.UserId,
                DisplayName = current.Identity.DisplayName,
                MemberId = "fake-member-" + current.Id
            } : null;
            fake.SetMembership(current.Id, member);
            var identity = await fake.GetIdentityAsync(current.Id, ct);
            await service.ApplyIdentityAsync(identity, ct);
            return Results.NoContent();
        }).RequireAuthorization("Management").RequireRateLimiting("sensitive").ExcludeFromDescription();
        app.MapPost("/dev/availability", async (HttpContext context, IAntiforgery anti, FakeProvider fake,
            CancellationToken ct) =>
        {
            await anti.ValidateRequestAsync(context);
            var change = await context.Request.ReadFromJsonAsync<SimulationAvailability>(ct) ?? throw new ArgumentException();
            fake.Unavailable = change.Unavailable;
            return Results.NoContent();
        }).RequireAuthorization("Management").RequireRateLimiting("sensitive").ExcludeFromDescription();
    }
    private static Task SignInAsync(HttpContext context, Guid id) => context.SignInAsync("Management",
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "Management")),
        new AuthenticationProperties { IsPersistent = false });
}
public sealed record SimulationChange(string ProfileId);
public sealed record SimulationAvailability(bool Unavailable);
