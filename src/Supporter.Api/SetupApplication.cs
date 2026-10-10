using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Supporter.Infrastructure;

namespace Supporter.Api;

public sealed record SetupTier(string Id, string Title, long AmountCents, bool Published);
public sealed record SetupCampaign(string Id, string Name, string Url, SetupTier[] Tiers, string? Currency = null);
public sealed record SetupLookup(string AccessToken);

internal static class SetupApplication
{
    public static async Task RunAsync(WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
            throw new InvalidOperationException("O assistente exige Development e acesso local.");
        builder.WebHost.UseUrls("http://127.0.0.1:5100");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
        builder.Services.AddProblemDetails();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddFixedWindowLimiter("setup", limiter =>
            {
                limiter.PermitLimit = 20;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
        });
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "supporter.setup.csrf";
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.AddHttpClient("SetupPatreon", http =>
        {
            http.BaseAddress = new Uri("https://www.patreon.com");
            http.Timeout = TimeSpan.FromSeconds(30);
            http.MaxResponseContentBufferSize = 8 * 1024 * 1024;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SupporterKit.NET/0.1.0 (local setup)");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        var app = builder.Build();
        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            var language = SetupText.RequestLanguage(context);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var host = context.Request.Host;
            var peer = context.Connection.RemoteIpAddress;
            var local = peer is not null && IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer);
            if (!local || host.Port != 5100 || host.Host is not ("localhost" or "127.0.0.1"))
            {
                await Results.Problem(statusCode: 403, title: SetupText.Translate("O assistente aceita apenas acesso local.", language)).ExecuteAsync(context);
                return;
            }
            var expectedOrigin = "http://" + host.Value;
            if (HttpMethods.IsPost(context.Request.Method))
            {
                if (context.Request.Headers.Origin != expectedOrigin)
                {
                    await Results.Problem(statusCode: 403, title: SetupText.Translate("Origem da solicitação inválida.", language)).ExecuteAsync(context);
                    return;
                }
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException)
                {
                    await Results.Problem(statusCode: 400, title: SetupText.Translate("Atualize a página e tente novamente.", language)).ExecuteAsync(context);
                    return;
                }
            }
            try { await next(context); }
            catch (ArgumentException ex)
            {
                await Results.Problem(statusCode: 400, title: SetupText.Translate(ex.Message, language)).ExecuteAsync(context);
            }
            catch (ProviderException ex)
            {
                var status = ex.Code == "setup_401" ? 401 : ex.Code == "setup_429" ? 429 : 503;
                var message = status == 401 ? "Token do criador recusado. Confira o token no portal Patreon." :
                    status == 429 ? "O Patreon pediu uma pausa. Aguarde antes de consultar novamente." :
                    "Não foi possível consultar o Patreon. Confira a conexão e tente novamente.";
                await Results.Problem(statusCode: status, title: SetupText.Translate(message, language)).ExecuteAsync(context);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (!context.RequestAborted.IsCancellationRequested)
                    await Results.Problem(statusCode: 503, title: SetupText.Translate("A consulta ao Patreon falhou. Tente novamente.", language)).ExecuteAsync(context);
            }
        });
        app.UseRouting();
        app.UseRateLimiter();
        app.MapGet("/", (HttpContext context, IAntiforgery anti) => SetupPage.Render(context, anti));
        app.MapGet("/setup", (HttpContext context, IAntiforgery anti) => SetupPage.Render(context, anti));
        app.MapPost("/setup/campaigns", async (HttpContext context, SetupLookup input, IHttpClientFactory clients, CancellationToken ct) =>
            Results.Ok(await FetchCampaignsAsync(clients.CreateClient("SetupPatreon"), input.AccessToken, ct, SetupText.RequestLanguage(context)))).RequireRateLimiting("setup");
        app.MapPost("/setup/download", async (SetupInput input, IHttpClientFactory clients, CancellationToken ct) =>
        {
            SetupFiles.Validate(input);
            var campaigns = await FetchCampaignsAsync(clients.CreateClient("SetupPatreon"), input.CreatorAccessToken, ct, SetupText.Language(input.Language));
            var campaign = campaigns.SingleOrDefault(c => c.Id == input.CampaignId)
                ?? throw new ArgumentException("Escolha uma campanha pertencente ao token informado.");
            SetupFiles.ValidateTiers(input, campaign);
            return Results.File(SetupFiles.Generate(input), "application/zip", "supporterkit-install.zip");
        }).RequireRateLimiting("setup");
        await app.RunAsync();
    }

    internal static async Task<SetupCampaign[]> FetchCampaignsAsync(HttpClient http, string token, CancellationToken ct, string language = "pt-BR")
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(char.IsControl))
            throw new ArgumentException("Informe um token de acesso do criador válido.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        ct = timeout.Token;
        const string path = "/api/oauth2/v2/campaigns?include=tiers&fields%5Bcampaign%5D=creation_name,url,currency&fields%5Btier%5D=title,amount_cents,published";
        var campaigns = new List<SetupCampaign>();
        var seenCursors = new HashSet<string>();
        string? cursor = null;
        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path +
                (cursor is null ? "" : "&page%5Bcursor%5D=" + Uri.EscapeDataString(cursor)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new ProviderException("setup_" + (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            var root = json.RootElement;
            foreach (var campaign in root.GetProperty("data").EnumerateArray())
            {
                var ids = campaign.GetProperty("relationships").GetProperty("tiers").GetProperty("data")
                    .EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToHashSet();
                var tiers = root.TryGetProperty("included", out var included) ? included.EnumerateArray()
                    .Where(t => t.GetProperty("type").GetString() == "tier" && ids.Contains(t.GetProperty("id").GetString()!))
                    .Select(t => new SetupTier(t.GetProperty("id").GetString()!,
                        t.GetProperty("attributes").GetProperty("title").GetString() ?? SetupText.Translate("Nível sem nome", language),
                        t.GetProperty("attributes").GetProperty("amount_cents").GetInt64(),
                        t.GetProperty("attributes").GetProperty("published").ValueKind == JsonValueKind.True))
                    .OrderBy(t => t.AmountCents).ToArray() : [];
                if (!ids.SetEquals(tiers.Select(t => t.Id))) throw new ProviderException("setup_missing_tiers");
                var attrs = campaign.GetProperty("attributes");
                campaigns.Add(new(campaign.GetProperty("id").GetString()!,
                    attrs.GetProperty("creation_name").GetString() ?? SetupText.Translate("Campanha sem nome", language),
                    attrs.GetProperty("url").GetString() ?? "", tiers,
                    attrs.TryGetProperty("currency", out var currency) ? currency.GetString() : null));
            }
            cursor = root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("pagination", out var pagination) &&
                pagination.TryGetProperty("cursors", out var cursors) && cursors.TryGetProperty("next", out var next)
                ? next.GetString() : null;
            if (cursor is not null && (!seenCursors.Add(cursor) || seenCursors.Count > 50))
                throw new ProviderException("setup_pagination");
        } while (cursor is not null);
        return campaigns.ToArray();
    }
}
