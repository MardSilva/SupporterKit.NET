using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Supporter.Infrastructure;

namespace Supporter.Api;

internal static class SetupPage
{
    public static IResult Render(HttpContext context, IAntiforgery anti)
    {
        var token = WebUtility.HtmlEncode(anti.GetAndStoreTokens(context).RequestToken);
        var nonce = SupporterService.RandomSecret();
        var language = SetupText.RequestLanguage(context);
        var translations = JsonSerializer.Serialize(SetupText.English);
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-" +
            nonce + "'; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        return Results.Content($$$"""
        <!doctype html><html lang="{{{language}}}"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Preparar instalação · SupporterKit.NET</title>
        <style>
        *{box-sizing:border-box}body{font:16px/1.55 system-ui,sans-serif;background:#f3f5f9;color:#19243a;margin:0}
        main{max-width:900px;margin:32px auto;padding:32px;background:white;border-radius:16px}
        h1{font-size:2rem;margin:8px 0}h2{font-size:1.35rem}p{max-width:75ch}.badge{color:#17485e;font-weight:650}
        nav{display:flex;gap:8px;flex-wrap:wrap;margin:24px 0}nav span{padding:6px 12px;border-radius:20px;background:#edf0f5}
        nav .active{background:#224d79;color:white}label{display:block;font-weight:600;margin-top:16px}
        input,select{display:block;width:100%;font:inherit;padding:10px;border:1px solid #aeb8c8;border-radius:6px;margin-top:5px}
        input[type=checkbox]{width:auto;display:inline;margin-right:8px}input[type=file]{font-weight:400}
        .language-picker{display:flex;justify-content:flex-end;align-items:center;gap:8px;margin-bottom:12px}
        .language-picker label{margin:0;font-size:.85rem;font-weight:400}
        .language-picker select{width:auto;margin:0;padding:5px 8px;font-size:.85rem}
        button{font:inherit;background:#224d79;color:white;border:0;border-radius:6px;padding:10px 16px;cursor:pointer;margin:16px 8px 0 0}
        button.secondary{background:#e8edf4;color:#19243a}button:disabled{opacity:.5;cursor:wait}a{color:#17578f}
        small{display:block;color:#506079;font-weight:400;margin-top:4px}.note{background:#f0f5fc;border-left:4px solid #6291c2;padding:12px 16px}
        .grid{display:grid;grid-template-columns:1fr 1fr;gap:0 20px}code{overflow-wrap:anywhere}#status{min-height:28px;padding-top:12px}
        #status.error{color:#9e2525}#levels article{padding:12px 16px;background:#f6f8fc;border-radius:8px;margin:12px 0}
        #levels article>label:first-child{margin:0}.summary{white-space:pre-wrap;overflow-wrap:anywhere}
        :focus-visible{outline:3px solid #b46600;outline-offset:3px}[hidden]{display:none!important}
        @media(max-width:650px){main{margin:12px;padding:20px}.grid{grid-template-columns:1fr}h1{font-size:1.65rem}}
        </style></head><body><main>
        <div class="language-picker">
          <label for="language">Language / Idioma</label>
          <select id="language"><option value="en">English</option><option value="pt-BR">Português (BR)</option></select>
        </div>
        <div class="badge">SupporterKit.NET · Assistente local</div>
        <h1>Prepare sua instalação</h1>
        <p>Configure a integração Patreon e baixe os arquivos para instalar na sua VPS com Cosmos.</p>
        <details><summary>Já preencheu um arquivo .env?</summary>
          <label for="import">Carregar configuração existente</label><input id="import" type="file" accept=".env,text/plain">
          <small>O arquivo preenche os campos desta página. Confira os caminhos: eles devem apontar para a VPS, não para seu computador.</small>
        </details>
        <nav aria-label="Etapas"><span class="active">1. Endereço</span><span>2. Patreon</span><span>3. Níveis</span><span>4. Cosmos</span><span>5. Download</span></nav>
        <form id="wizard" autocomplete="off">
        <section data-step="0">
          <h2>Onde a API ficará disponível?</h2>
          <label for="publicOrigin">Domínio HTTPS da API</label>
          <input id="publicOrigin" type="url" required placeholder="https://seu-dominio.duckdns.org">
          <small>Informe somente o endereço principal, sem /cosmos-ui/ ou outros caminhos.</small>
          <p>Cadastre este callback no cliente Patreon:</p><code id="callback">Preencha o domínio acima.</code>
          <p>Endereço do webhook:</p><code id="webhook">Preencha o domínio acima.</code>
          <button id="copyUrls" type="button" class="secondary">Copiar URLs</button>
        </section>
        <section data-step="1" hidden>
          <h2>Cadastre seu cliente Patreon v2</h2>
          <p>Abra o <a href="https://www.patreon.com/portal/registration/register-clients" target="_blank" rel="noopener noreferrer">cadastro de clientes Patreon</a>. Use a categoria <strong>Reconhecimento aos membros</strong> e o callback da etapa anterior.</p>
          <p>Você pode usar “SupporterKit.NET” como nome e “Integração para reconhecer apoiantes dos meus projetos e consultar benefícios confirmados” como descrição. No Website, informe sua página pública ou o site do projeto.</p>
          <div class="grid">
          <label>ID do cliente<input id="clientId" required maxlength="4096"></label>
          <label>Senha do cliente (Client Secret)<input id="clientSecret" type="password" required maxlength="4096"></label>
          <label>Token de acesso do criador<input id="creatorAccessToken" type="password" required maxlength="4096"></label>
          <label>Token de atualização do criador<input id="creatorRefreshToken" type="password" required maxlength="4096"></label>
          </div>
          <small>Use os tokens do criador fornecidos pelo portal. Não use chaves de aplicação ou tokens dos apoiantes.</small>
          <p><a href="https://docs.patreon.com/#step-1-registering-your-client" target="_blank" rel="noopener noreferrer">Consultar instruções oficiais do Patreon</a></p>
        </section>
        <section data-step="2" hidden>
          <h2>Consulte a campanha e associe seus níveis</h2>
          <button id="lookup" type="button">Buscar no Patreon</button>
          <small>A busca valida o token de acesso do criador. Client ID, senha do cliente e token de atualização serão confirmados no fluxo real de login e renovação.</small>
          <label for="campaign">Campanha</label><select id="campaign" required><option value="">Faça a consulta primeiro</option></select>
          <label for="product">Produto padrão dos níveis</label><input id="product" value="flow" maxlength="64" required>
          <small>Use um código interno, como flow. Cada nível pode ser associado a um produto e a um ou mais códigos de reconhecimento.</small>
          <div id="levels"></div>
        </section>
        <section data-step="3" hidden>
          <h2>Prepare os caminhos e a rede do Cosmos</h2>
          <div class="grid">
          <label>Pasta de instalação na VPS<input id="installDirectory" required value="/opt/supporterkit"></label>
          <label>Caminho do certificado PFX na VPS<input id="keyCertificateFile" required value="/opt/supporterkit/secrets/keyring.pfx"></label>
          <label>Senha do certificado PFX<input id="keyCertificatePassword" type="password" required maxlength="4096"></label>
          <label>Rede Docker do Cosmos<input id="cosmosNetwork" required placeholder="Nome da rede existente"></label>
          <label>IP efetivo do proxy Cosmos<input id="trustedProxyIp" required placeholder="Ex.: 172.20.0.2"></label>
          <label>Segredo do webhook Patreon<input id="webhookSecret" type="password" required maxlength="4096"></label>
          </div>
          <p>Cadastre no Patreon o webhook mostrado na primeira etapa, para os eventos <code>members:create</code>, <code>members:update</code> e <code>members:delete</code>. Copie o segredo fornecido para esse webhook.</p>
          <p class="note">O PFX protege os tokens armazenados; ele é diferente do certificado HTTPS do Cosmos. Se ainda não existe, use a senha escolhida aqui ao criá-lo. O arquivo precisará ser legível pelo usuário app do container.</p>
          <small>O assistente confere o formato dos caminhos, mas não verifica arquivos ou redes na VPS. A entrega do webhook depende da API estar acessível.</small>
          <p><a href="https://cosmos-cloud.io/docs/servapps/" target="_blank" rel="noopener noreferrer">Instruções de ServApps</a> · <a href="https://docs.patreon.com/#webhooks" target="_blank" rel="noopener noreferrer">Instruções de webhooks Patreon</a></p>
        </section>
        <section data-step="4" hidden>
          <h2>Confira e baixe os arquivos</h2>
          <div id="summary" class="note summary"></div>
          <p>O ZIP inclui <code>.env</code>, <code>appsettings.Production.json</code>, <code>compose.yml</code>, <code>.gitignore</code> e <code>LEIA-ME.txt</code>.</p>
          <p><strong>O ZIP contém suas credenciais.</strong> Guarde em local privado e transfira apenas para sua VPS. Os dados desta página não são gravados em arquivos pelo assistente.</p>
          <p>A imagem Docker ainda precisa ser transferida ou construída na VPS. O download não publica a aplicação nem modifica sua conta Patreon.</p>
          <button id="download" type="button">Validar e baixar instalação</button>
          <button id="clear" type="button" class="secondary">Limpar credenciais da página</button>
        </section>
        <div><button id="back" type="button" class="secondary" hidden>Voltar</button><button id="next" type="button">Continuar</button></div>
        <div id="status" role="status" aria-live="polite"></div>
        </form><div id="csrf" data-token="{{{token}}}" hidden></div>
        <script nonce="{{{nonce}}}">
        const translations = {{{translations}}};
        let language = '{{{language}}}';
        const t = text => language === 'en' ? (translations[text] || text) : text;
        const field = id => document.getElementById(id);
        const sections = [...document.querySelectorAll('[data-step]')];
        const status = field('status');
        let step = 0, campaigns = [], verifiedToken = '';
        const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
        const textNodes = [];
        for (let node; node = walker.nextNode();) {
          if (!['SCRIPT','STYLE'].includes(node.parentElement.tagName) && node.textContent.trim()) textNodes.push([node, node.textContent]);
        }
        const attributes = [...document.querySelectorAll('[placeholder],[aria-label]')].flatMap(element =>
          ['placeholder','aria-label'].filter(name => element.hasAttribute(name)).map(name => [element, name, element.getAttribute(name)]));
        function translatePage() {
          document.documentElement.lang = language;
          document.title = t('Preparar instalação · SupporterKit.NET');
          for (const [node, original] of textNodes) node.textContent = original.replace(original.trim(), t(original.trim()));
          for (const [element, name, original] of attributes) element.setAttribute(name, t(original));
          field('language').value = language;
          urls(); refreshSummary();
        }
        function message(text, error = false) { status.textContent = text; status.classList.toggle('error', error); }
        function origin() { return field('publicOrigin').value.trim().replace(/\/$/, ''); }
        function urls() {
          field('callback').textContent = origin() ? origin() + '/auth/patreon/callback' : t('Preencha o domínio acima.');
          field('webhook').textContent = origin() ? origin() + '/api/v1/webhooks/patreon' : t('Preencha o domínio acima.');
        }
        function validateStep() {
          for (const input of sections[step].querySelectorAll('input,select')) {
            if (!input.disabled && !input.checkValidity()) { input.focus(); message(t('Preencha os campos obrigatórios com valores válidos.'), true); return false; }
          }
          if (step === 0) {
            const url = new URL(origin());
            if (url.protocol !== 'https:' || url.pathname !== '/' || url.search || url.hash || url.username || url.password) throw new Error(t('Informe uma origem HTTPS sem caminhos.'));
          }
          if (step === 2 && (verifiedToken !== field('creatorAccessToken').value || mappings().length === 0)) throw new Error(t('Consulte a campanha e selecione pelo menos um nível pago.'));
          return true;
        }
        function showStep() {
          sections.forEach((section, index) => { section.hidden = index !== step; });
          document.querySelectorAll('nav span').forEach((item, index) => item.classList.toggle('active', index === step));
          field('back').hidden = step === 0; field('next').hidden = step === 4;
          refreshSummary();
          message(''); sections[step].querySelector('h2').setAttribute('tabindex', '-1'); sections[step].querySelector('h2').focus();
        }
        function refreshSummary() {
          const campaign = campaigns.find(c => c.id === field('campaign').value);
          field('summary').textContent = 'API: ' + origin() + '\n' + t('Campanha:') + ' ' + (campaign?.name || t('Não selecionada')) +
            '\n' + t('Níveis associados:') + ' ' + mappings().length + '\n' + t('Instalação:') + ' ' + field('installDirectory').value +
            '\n' + t('Certificado:') + ' ' + field('keyCertificateFile').value + '\n' + t('Rede Cosmos:') + ' ' + field('cosmosNetwork').value;
        }
        function mappings() {
          return [...field('levels').querySelectorAll('article')].filter(row => row.querySelector('input[type=checkbox]').checked).map(row => ({
            tierId: row.dataset.id,
            productId: row.querySelector('[data-product]').value.trim(),
            benefitCodes: row.querySelector('[data-codes]').value.split(',').map(code => code.trim()).filter(Boolean)
          }));
        }
        function levelState() {
          return [...field('levels').querySelectorAll('article')].filter(row => row.querySelector('[data-product]')).map(row => ({
            tierId: row.dataset.id, selected: row.querySelector('input[type=checkbox]').checked,
            productId: row.querySelector('[data-product]').value, benefitCodes: row.querySelector('[data-codes]').value
          }));
        }
        function renderTiers(saved) {
          field('levels').replaceChildren();
          const campaign = campaigns.find(c => c.id === field('campaign').value);
          for (const tier of campaign?.tiers || []) {
            const row = document.createElement('article'); row.dataset.id = tier.id;
            const label = document.createElement('label'), check = document.createElement('input'); check.type = 'checkbox';
            const paid = tier.published && tier.amountCents > 0;
            check.checked = paid; check.disabled = !paid;
            const amount = campaign.currency && /^[A-Z]{3}$/.test(campaign.currency) ?
              (tier.amountCents / 100).toLocaleString(language, {style: 'currency', currency: campaign.currency}) :
              (tier.amountCents / 100).toLocaleString(language) + ' ' + t('(valor da campanha)');
            label.append(check, document.createTextNode(tier.title + ' · ' + (tier.amountCents === 0 ? t('Gratuito') : amount)));
            row.append(label);
            const note = document.createElement('small'); note.textContent = 'ID ' + tier.id + (paid ? '' : ' · ' + t('Sem mapeamento de apoio pago')); row.append(note);
            if (paid) {
              const productLabel = document.createElement('label'); productLabel.textContent = t('Produto');
              const product = document.createElement('input'); product.dataset.product = ''; product.value = field('product').value; product.required = true; product.maxLength = 64; productLabel.append(product);
              const codesLabel = document.createElement('label'); codesLabel.textContent = t('Códigos de reconhecimento, separados por vírgula');
              const codes = document.createElement('input'); codes.dataset.codes = ''; codes.value = 'supporter'; codes.required = true; codesLabel.append(codes);
              if (saved) {
                const existing = saved.find(mapping => mapping.tierId === tier.id);
                check.checked = existing?.selected || false;
                if (existing) { product.value = existing.productId; codes.value = existing.benefitCodes; }
                product.disabled = codes.disabled = !check.checked;
              }
              row.append(productLabel, codesLabel);
              check.addEventListener('change', () => { product.disabled = codes.disabled = !check.checked; });
            }
            field('levels').append(row);
          }
        }
        async function post(path, data) {
          const response = await fetch(path + '?lang=' + language, {method: 'POST', headers: {'Content-Type': 'application/json', 'X-CSRF-TOKEN': field('csrf').dataset.token}, body: JSON.stringify(data)});
          if (!response.ok) { const error = await response.json().catch(() => ({})); throw new Error(error.title || t('Não foi possível concluir') + ' (' + response.status + ').'); }
          return response;
        }
        function action(id, callback) {
          field(id).addEventListener('click', async () => {
            field(id).disabled = true;
            try { await callback(); } catch (error) { message(error.message, true); }
            finally { field(id).disabled = false; }
          });
        }
        action('next', () => { if (validateStep()) { step++; showStep(); } });
        field('language').addEventListener('change', () => {
          const saved = levelState();
          language = field('language').value;
          translatePage(); renderTiers(saved);
          const first = field('campaign').options[0];
          if (first) first.textContent = t(campaigns.length ? 'Selecione a campanha' : 'Faça a consulta primeiro');
          const url = new URL(location.href); url.searchParams.set('lang', language); history.replaceState(null, '', url);
          message('');
        });
        action('back', () => { step--; showStep(); });
        field('wizard').addEventListener('submit', event => event.preventDefault());
        field('publicOrigin').addEventListener('input', urls);
        field('campaign').addEventListener('change', () => renderTiers());
        field('product').addEventListener('input', () => { for (const input of field('levels').querySelectorAll('[data-product]')) input.value = field('product').value; });
        field('creatorAccessToken').addEventListener('input', () => { campaigns = []; verifiedToken = ''; field('levels').replaceChildren(); field('campaign').replaceChildren(new Option(t('Faça a consulta novamente'), '')); });
        action('copyUrls', async () => { await navigator.clipboard.writeText('Callback: ' + origin() + '/auth/patreon/callback\nWebhook: ' + origin() + '/api/v1/webhooks/patreon'); message(t('URLs copiadas.')); });
        action('lookup', async () => {
          message(t('Consultando o Patreon…'));
          const token = field('creatorAccessToken').value;
          const result = await post('/setup/campaigns', {accessToken: token});
          campaigns = await result.json(); verifiedToken = token;
          field('campaign').replaceChildren(new Option(t('Selecione a campanha'), ''));
          for (const campaign of campaigns) field('campaign').append(new Option(campaign.name, campaign.id));
          if (campaigns.length === 1) field('campaign').value = campaigns[0].id;
          renderTiers(); message(campaigns.length ? t('Campanha consultada. Confira os níveis e códigos.') : t('Este token não retornou campanhas.'));
        });
        action('download', async () => {
          const input = {};
          for (const id of ['publicOrigin','clientId','clientSecret','creatorAccessToken','creatorRefreshToken','webhookSecret','installDirectory','keyCertificateFile','keyCertificatePassword','cosmosNetwork','trustedProxyIp']) input[id] = field(id).value;
          input.publicOrigin = origin(); input.campaignId = field('campaign').value; input.tiers = mappings(); input.language = language;
          message(t('Validando a campanha e preparando os arquivos…'));
          const response = await post('/setup/download', input);
          const url = URL.createObjectURL(await response.blob());
          const link = document.createElement('a'); link.href = url; link.download = 'supporterkit-install.zip'; document.body.append(link); link.click(); link.remove();
          setTimeout(() => URL.revokeObjectURL(url), 10000);
          message(t('Download preparado. Confira o LEIA-ME.txt antes de instalar.'));
        });
        action('clear', () => { for (const input of document.querySelectorAll('input[type=password]')) input.value = ''; campaigns = []; verifiedToken = ''; field('levels').replaceChildren(); field('campaign').replaceChildren(new Option(t('Faça a consulta primeiro'), '')); step = 1; showStep(); message(t('Credenciais removidas dos campos. O ZIP baixado continua no seu computador.')); });
        field('import').addEventListener('change', async () => {
          try {
            const file = field('import').files[0]; if (!file) return;
            if (file.size > 65536) throw new Error(t('Escolha um .env de até 64 KB.'));
            const names = {PUBLIC_ORIGIN:'publicOrigin', PATREON_CLIENT_ID:'clientId', PATREON_CLIENT_SECRET:'clientSecret', PATREON_CREATOR_ACCESS_TOKEN:'creatorAccessToken', PATREON_CREATOR_REFRESH_TOKEN:'creatorRefreshToken', PATREON_WEBHOOK_SECRET:'webhookSecret', INSTALL_DIRECTORY:'installDirectory', KEY_CERTIFICATE_FILE:'keyCertificateFile', KEY_CERTIFICATE_PASSWORD:'keyCertificatePassword', TRUSTED_PROXY_IP:'trustedProxyIp', COSMOS_NETWORK:'cosmosNetwork'};
            for (const line of (await file.text()).split(/\r?\n/)) {
              const match = line.match(/^\s*([A-Z_]+)=(.*)$/); if (!match || !names[match[1]]) continue;
              let value = match[2].trim();
              if ((value.startsWith("'") && value.endsWith("'")) || (value.startsWith('"') && value.endsWith('"'))) value = value.slice(1, -1);
              if (value && !(match[1] === 'KEY_CERTIFICATE_FILE' && !value.startsWith('/'))) field(names[match[1]]).value = value;
            }
            campaigns = []; verifiedToken = ''; field('levels').replaceChildren(); field('campaign').replaceChildren(new Option(t('Faça a consulta primeiro'), '')); urls(); message(t('Campos importados. Confira o domínio e os caminhos da VPS.'));
          } catch (error) { message(error.message, true); }
          finally { field('import').value = ''; }
        });
        translatePage();
        </script></main></body></html>
        """, "text/html; charset=utf-8");
    }
}
