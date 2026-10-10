using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Supporter.Core;
using Supporter.Infrastructure;

namespace Supporter.Api;

internal static class Pages
{
    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Options(FakeProfile[] profiles) => string.Join("", profiles.Select(p =>
        $"<option value=\"{Encode(p.Id)}\">{Encode(p.Label)}</option>"));
    private static string Tiers(FakeProfile[] profiles)
    {
        var tiers = profiles.Where(p => p.TierName is not null && p.MonthlyAmountCents is >= 0)
            .OrderBy(p => p.MonthlyAmountCents).ToArray();
        if (tiers.Length == 0) return "";
        var rows = string.Join("", tiers.Select(p =>
            $"<tr><th scope=\"row\">{Encode(p.TierName)}</th><td>" +
            (p.MonthlyAmountCents == 0 ? "Gratuito" : "US$ " +
                (p.MonthlyAmountCents!.Value / 100m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " / mês") +
            $"</td><td>{Encode(p.Label)}</td></tr>"));
        return "<section><h2>Níveis para testar</h2>" +
            "<p>Exemplos com os nomes e valores configurados no Patreon. Os perfis abaixo são fictícios; esta tela não faz uma consulta ao vivo.</p>" +
            "<table><thead><tr><th>Nível</th><th>Valor</th><th>Perfil de exemplo</th></tr></thead><tbody>" + rows + "</tbody></table></section>";
    }
    private static string Token(HttpContext context, IAntiforgery antiforgery) =>
        Encode(antiforgery.GetAndStoreTokens(context).RequestToken);

    public static IResult Simulation(HttpContext context, IAntiforgery antiforgery, FakeProfile[] profiles) =>
        Html(context, "Simulação local", $$"""
        <p class="badge">SIMULAÇÃO — nenhum pagamento ou OAuth real</p>
        <h1>Testar reconhecimento de apoio</h1>
        <p>Escolha um perfil fictício. Depois você poderá emitir uma chave e alterar o estado.</p>
        <p>Os perfis “Tempo” permitem conferir a duração. Suas datas de início são fictícias e o texto avança com o calendário.</p>
        {{Tiers(profiles)}}
        <form action="/dev/login" method="post">
          <input type="hidden" name="__RequestVerificationToken" value="{{Token(context, antiforgery)}}">
          <label for="profile">Perfil</label>
          <select id="profile" name="profileId">{{Options(profiles)}}</select>
          <button type="submit">Entrar na simulação</button>
        </form>
        """);

    public static IResult Manage(HttpContext context, IAntiforgery antiforgery, SupporterStatus status,
        Person person, bool fake, FakeProfile[] profiles)
    {
        var nonce = SupporterService.RandomSecret();
        var simulation = fake ? $$"""
            <section><h2>Alterar simulação</h2>
              <label for="template">Aplicar estado ao perfil atual</label>
              <select id="template">{{Options(profiles)}}</select>
              <button id="apply" type="button">Aplicar estado</button>
              <button id="offline" type="button">Simular indisponibilidade</button>
              <button id="online" type="button">Restaurar provedor</button>
              <p>O estado anterior fica disponível durante falhas. A alteração para antigo apoiante preserva o histórico.</p>
              <a href="/dev">Escolher outro perfil</a>
            </section>
            """ : "";
        var page = $$"""
        {{(fake ? "<p class=\"badge\">SIMULAÇÃO — nenhum pagamento ou OAuth real</p>" : "")}}
        <h1>Gestão do apoio</h1>
        <p>Conta: {{Encode(status.DisplayName ?? "Nome oculto")}}</p>
        <p>Nome para exibir na aplicação: <strong id="recognition-name" data-account-name="{{Encode(status.DisplayName)}}">{{Encode(status.RecognitionDisplayName)}}</strong></p>
        <dl><dt>Estado atual</dt><dd>{{Encode(status.CurrentStatus)}}</dd>
          <dt>Tempo do período atual de apoio</dt><dd>{{Encode(status.SupportDurationLabel)}}</dd>
          <dt>Início do período atual (UTC)</dt><dd>{{Encode(status.CurrentSupportStartedAt?.ToString("yyyy-MM-dd") ?? "Indisponível")}}</dd>
          <dt>Contribuição histórica confirmada</dt><dd>{{(status.HasSupported ? "Sim" : "Não")}}</dd>
          <dt>Benefícios</dt><dd>{{Encode(string.Join(", ", status.BenefitCodes))}}</dd>
          <dt>Última sincronização</dt><dd>{{Encode(status.LastSuccessfulSyncAt?.ToString("u") ?? "Nunca")}}</dd>
          <dt>Dados desatualizados</dt><dd>{{(status.IsStale ? "Sim" : "Não")}}</dd>
        </dl>
        <section><h2>Chave da aplicação</h2>
          <p>Uma nova chave revoga a anterior. Ela aparece apenas nesta resposta; guarde-a antes de sair.</p>
          <button id="issue" type="button">Emitir ou rodar chave</button>
          <label for="key">Chave emitida</label><input id="key" readonly autocomplete="off" spellcheck="false">
          <button id="copy" type="button">Copiar chave</button>
          <p>Cole a chave nas configurações de apoio da aplicação WinUI quando essa integração estiver disponível.</p>
        </section>
        <section><h2>Reconhecimento público</h2>
          <label><input id="optin" type="checkbox" {{(person.PublicOptIn ? "checked" : "")}}> Autorizar meu nome na lista pública</label>
          <label for="name">Pseudônimo opcional</label><input id="name" maxlength="80" value="{{Encode(person.Pseudonym)}}">
          <p>Sem pseudônimo, será usado o nome disponível na conta; se estiver oculto, aparecerá “Apoiante anônimo”.</p>
          <button id="save" type="button">Salvar consentimento</button>
        </section>
        {{simulation}}
        <button id="logout" type="button">Encerrar sessão</button>
        <p id="feedback" role="status" aria-live="polite"></p>
        <div id="csrf" data-token="{{Token(context, antiforgery)}}" hidden></div>
        <script nonce="{{nonce}}">
        const csrf = document.getElementById('csrf').dataset.token;
        const feedback = document.getElementById('feedback');
        async function send(path, method, body) {
          const response = await fetch(path, {method, headers: {'X-CSRF-TOKEN': csrf, 'Content-Type': 'application/json'}, body: body === undefined ? undefined : JSON.stringify(body)});
          if (!response.ok) {
            const problem = await response.json().catch(() => null);
            throw new Error(problem?.title || ('Não foi possível concluir (' + response.status + ').'));
          }
          return response.status === 204 ? null : response.json();
        }
        function action(id, callback) {
          const button = document.getElementById(id);
          if (button) button.addEventListener('click', async () => {
            button.disabled = true;
            try { await callback(); } catch (error) { feedback.textContent = error.message; }
            finally { button.disabled = false; }
          });
        }
        action('issue', async () => { const result = await send('/api/v1/supporter/keys', 'POST'); document.getElementById('key').value = result.key; feedback.textContent = 'Chave emitida. Guarde-a agora.'; });
        action('copy', async () => { const key = document.getElementById('key').value; if (!key) throw new Error('Emita uma chave primeiro.'); await navigator.clipboard.writeText(key); feedback.textContent = 'Chave copiada.'; });
        action('save', async () => {
          const optIn = document.getElementById('optin').checked;
          const pseudonym = document.getElementById('name').value.trim();
          await send('/api/v1/supporter/public-profile', 'PUT', {optIn, pseudonym});
          const name = document.getElementById('recognition-name');
          name.textContent = optIn ? (pseudonym || name.dataset.accountName.trim() || 'Apoiante anônimo') : 'Apoiante anônimo';
          feedback.textContent = 'Consentimento salvo.';
        });
        action('apply', async () => { await send('/dev/membership', 'POST', {profileId: document.getElementById('template').value}); location.reload(); });
        action('offline', async () => { await send('/dev/availability', 'POST', {unavailable: true}); feedback.textContent = 'Provedor indisponível. Aguarde o prazo de desatualização.'; });
        action('online', async () => { await send('/dev/availability', 'POST', {unavailable: false}); feedback.textContent = 'Provedor restaurado; a sincronização será retomada.'; });
        action('logout', async () => { await send('/manage/logout', 'POST'); location.href = '/'; });
        </script>
        """;
        return Html(context, "Gestão do apoio", page, nonce);
    }

    private static IResult Html(HttpContext context, string title, string body, string? nonce = null)
    {
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; script-src " +
            (nonce is null ? "'none'" : "'nonce-" + nonce + "'") +
            "; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        return Results.Content($$"""
        <!doctype html><html lang="pt-BR"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{Encode(title)}} · SupporterKit.NET</title>
        <style>
        body{font:17px/1.5 system-ui,sans-serif;margin:0;background:#f5f6f8;color:#182133}
        main{max-width:760px;margin:36px auto;padding:24px;background:white;border-radius:12px}
        h1{font-size:1.8rem}h2{font-size:1.2rem}section{border-top:1px solid #d8dde6;margin-top:24px;padding-top:16px}
        label{display:block;margin-top:12px}input:not([type=checkbox]),select{box-sizing:border-box;width:100%;padding:10px;font:inherit}
        button{font:inherit;padding:9px 14px;margin:12px 8px 0 0;cursor:pointer;background:#263e68;color:white;border:0;border-radius:5px}
        button:disabled{opacity:.6}a{color:#204b89}.badge{padding:10px;background:#fff1c4;font-weight:700}
        dt{font-weight:600}dd{margin:0 0 12px}#feedback{min-height:1.5em}
        table{width:100%;border-collapse:collapse;font-size:.95rem}th,td{text-align:left;padding:10px 6px;border-bottom:1px solid #d8dde6}
        :focus-visible{outline:3px solid #b45b00;outline-offset:3px}
        @media(max-width:800px){main{margin:12px;padding:20px} }
        </style></head><body><main>{{body}}</main></body></html>
        """, "text/html; charset=utf-8");
    }
}
