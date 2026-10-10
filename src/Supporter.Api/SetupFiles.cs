using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Supporter.Core;

namespace Supporter.Api;

public sealed record SetupMapping(string TierId, string ProductId, string[] BenefitCodes);
public sealed record SetupInput
{
    public string Language { get; init; } = "pt-BR";
    public string PublicOrigin { get; init; } = "";
    public string CampaignId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string CreatorAccessToken { get; init; } = "";
    public string CreatorRefreshToken { get; init; } = "";
    public string WebhookSecret { get; init; } = "";
    public string InstallDirectory { get; init; } = "";
    public string KeyCertificateFile { get; init; } = "";
    public string KeyCertificatePassword { get; init; } = "";
    public string TrustedProxyIp { get; init; } = "";
    public string CosmosNetwork { get; init; } = "";
    public SetupMapping[] Tiers { get; init; } = [];
}

public static class SetupFiles
{
    private static bool Code(string? value) => value is not null &&
        Regex.IsMatch(value, "^[a-z][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static bool ServerPath(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith('/') && value.Length <= 512 &&
        value.Split('/').Skip(1).All(part => part.Length > 0 && part is not ("." or "..") &&
            Regex.IsMatch(part, "^[a-zA-Z0-9._-]+$", RegexOptions.CultureInvariant));

    public static void Validate(SetupInput input)
    {
        if (!Uri.TryCreate(input.PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.IsLoopback || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            origin.UserInfo.Length != 0)
            throw new ArgumentException(SetupText.Translate("Informe apenas a origem HTTPS pública, sem caminhos: https://seu-dominio.org.", SetupText.Language(input.Language)));
        foreach (var value in new[] { input.PublicOrigin, input.CampaignId, input.ClientId, input.ClientSecret,
            input.CreatorAccessToken, input.CreatorRefreshToken, input.WebhookSecret, input.KeyCertificatePassword })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) || value.Contains('\'') || value.Contains('\\'))
                throw new ArgumentException(SetupText.Translate("Preencha as credenciais e a senha do certificado em uma única linha, sem aspas simples ou barras invertidas.", SetupText.Language(input.Language)));
        if (!Regex.IsMatch(input.CampaignId, "^[0-9]+$", RegexOptions.CultureInvariant))
            throw new ArgumentException(SetupText.Translate("Selecione um ID de campanha válido.", SetupText.Language(input.Language)));
        if (!ServerPath(input.InstallDirectory) || !ServerPath(input.KeyCertificateFile) ||
            !input.KeyCertificateFile.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(SetupText.Translate("Use caminhos absolutos Linux sem espaços, como /opt/supporterkit e /opt/supporterkit/secrets/keyring.pfx.", SetupText.Language(input.Language)));
        if (!IPAddress.TryParse(input.TrustedProxyIp, out _))
            throw new ArgumentException(SetupText.Translate("Informe o IP efetivo do proxy Cosmos que conecta à API.", SetupText.Language(input.Language)));
        if (string.IsNullOrWhiteSpace(input.CosmosNetwork) || input.CosmosNetwork.Length > 128 ||
            !Regex.IsMatch(input.CosmosNetwork, "^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant))
            throw new ArgumentException(SetupText.Translate("Informe o nome real da rede Docker usada pelo Cosmos.", SetupText.Language(input.Language)));
        if (input.Tiers is null || input.Tiers.Length == 0 || input.Tiers.Length > 100 ||
            input.Tiers.Any(t => t is null || string.IsNullOrWhiteSpace(t.TierId) ||
                !Regex.IsMatch(t.TierId, "^[0-9]+$", RegexOptions.CultureInvariant) || !Code(t.ProductId) ||
                t.BenefitCodes is null || t.BenefitCodes.Length == 0 || t.BenefitCodes.Any(c => !Code(c))) ||
            input.Tiers.Select(t => t.TierId).Distinct().Count() != input.Tiers.Length)
            throw new ArgumentException(SetupText.Translate("Selecione níveis pagos e informe produtos e códigos sem espaços, usando letras minúsculas, números, ponto, hífen ou sublinhado.", SetupText.Language(input.Language)));
    }

    public static void ValidateTiers(SetupInput input, SetupCampaign campaign)
    {
        foreach (var mapping in input.Tiers)
            if (!campaign.Tiers.Any(t => t.Id == mapping.TierId && t.Published && t.AmountCents > 0))
                throw new ArgumentException(SetupText.Translate("Mapeie apenas níveis pagos publicados da campanha selecionada. O nível gratuito não confirma doação.", SetupText.Language(input.Language)));
    }

    public static byte[] Generate(SetupInput input)
    {
        Validate(input);
        var origin = input.PublicOrigin.TrimEnd('/');
        var fields = new Dictionary<string, string>
        {
            ["PUBLIC_ORIGIN"] = origin,
            ["PATREON_CAMPAIGN_ID"] = input.CampaignId,
            ["PATREON_CLIENT_ID"] = input.ClientId,
            ["PATREON_CLIENT_SECRET"] = input.ClientSecret,
            ["PATREON_CREATOR_ACCESS_TOKEN"] = input.CreatorAccessToken,
            ["PATREON_CREATOR_REFRESH_TOKEN"] = input.CreatorRefreshToken,
            ["PATREON_WEBHOOK_SECRET"] = input.WebhookSecret,
            ["INSTALL_DIRECTORY"] = input.InstallDirectory,
            ["KEY_CERTIFICATE_FILE"] = input.KeyCertificateFile,
            ["KEY_CERTIFICATE_PASSWORD"] = input.KeyCertificatePassword,
            ["TRUSTED_PROXY_IP"] = input.TrustedProxyIp,
            ["COSMOS_NETWORK"] = input.CosmosNetwork
        };
        var env = SetupText.Translate("# Configuração privada gerada pelo assistente local. Não publicar.\n", SetupText.Language(input.Language)) +
            string.Join("\n", fields.Select(pair => pair.Key + "='" + pair.Value + "'")) + "\n";
        var config = JsonSerializer.Serialize(new
        {
            Supporter = new
            {
                Products = input.Tiers.Select(t => t.ProductId).Distinct().ToArray(),
                Tiers = input.Tiers.ToDictionary(t => t.TierId, t => new TierRule(t.ProductId, t.BenefitCodes.Distinct().ToArray())),
                SyncIntervalSeconds = 900,
                StaleAfterSeconds = 3600,
                InboxRetentionDays = 7
            }
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        const string compose = """
        services:
          api:
            image: supporterkit-api:0.1.1
            container_name: supporterkit-api
            restart: unless-stopped
            environment:
              ASPNETCORE_ENVIRONMENT: Production
              Supporter__Provider: Real
              Supporter__SimulationEnabled: "false"
              Supporter__DataDirectory: /data
              Supporter__PublicOrigin: ${PUBLIC_ORIGIN:?Set PUBLIC_ORIGIN}
              Supporter__CampaignId: ${PATREON_CAMPAIGN_ID:?Set PATREON_CAMPAIGN_ID}
              Supporter__Patreon__ClientId: ${PATREON_CLIENT_ID:?Set PATREON_CLIENT_ID}
              Supporter__Patreon__ClientSecret: ${PATREON_CLIENT_SECRET:?Set PATREON_CLIENT_SECRET}
              Supporter__Patreon__CreatorAccessToken: ${PATREON_CREATOR_ACCESS_TOKEN:?Set PATREON_CREATOR_ACCESS_TOKEN}
              Supporter__Patreon__CreatorRefreshToken: ${PATREON_CREATOR_REFRESH_TOKEN:?Set PATREON_CREATOR_REFRESH_TOKEN}
              Supporter__Patreon__WebhookSecret: ${PATREON_WEBHOOK_SECRET:?Set PATREON_WEBHOOK_SECRET}
              Supporter__Patreon__CallbackUri: ${PUBLIC_ORIGIN}/auth/patreon/callback
              Supporter__TrustedProxies__0: ${TRUSTED_PROXY_IP:?Set TRUSTED_PROXY_IP}
              Supporter__KeyCertificatePath: /run/secrets/keyring.pfx
              Supporter__KeyCertificatePassword: ${KEY_CERTIFICATE_PASSWORD:?Set KEY_CERTIFICATE_PASSWORD}
            volumes:
              - supporter-data:/data
              - ${KEY_CERTIFICATE_FILE}:/run/secrets/keyring.pfx:ro
              - ${INSTALL_DIRECTORY}/appsettings.Production.json:/app/appsettings.Production.json:ro
            networks:
              - cosmos-proxy
            logging:
              driver: json-file
              options:
                max-size: "10m"
                max-file: "3"
        volumes:
          supporter-data:
        networks:
          cosmos-proxy:
            external: true
            name: ${COSMOS_NETWORK:?Set COSMOS_NETWORK}
        """;
        var certificateDirectory = input.KeyCertificateFile[..input.KeyCertificateFile.LastIndexOf('/')];
        var certificateBase = input.KeyCertificateFile[..^4];
        var instructions = $$"""
        Instalação SupporterKit.NET no Cosmos

        Este ZIP contém credenciais. Guarde-o em local privado; não faça commit nem compartilhe.
        Extraia .env, compose.yml e appsettings.Production.json em {{input.InstallDirectory}} na VPS.
        A imagem supporterkit-api:0.1.1 precisa estar carregada no Docker da VPS, com arquitetura compatível.
        O assistente não publica imagens, cria certificados, altera o Patreon ou configura a VPS.

        Antes de iniciar:
        - Confira a existência da rede {{input.CosmosNetwork}} e o IP do proxy {{input.TrustedProxyIp}}.
        - Instale seu certificado RSA com chave privada em {{input.KeyCertificateFile}}.
        - Use a senha informada no assistente ao exportar o PFX. Dê leitura ao usuário app do container.
        - A configuração montada precisa ser legível pelo container. Proteja .env e o PFX contra outros usuários.
        - O volume /data deve permitir escrita ao usuário app; ele guarda SQLite e as chaves de proteção.
        - Faça backup do volume e do PFX antes de atualizações. Não remova o volume ao recriar o container.

        Se ainda não existe um PFX, prepare-o na VPS com OpenSSL:
          mkdir -p {{certificateDirectory}}
          openssl req -x509 -newkey rsa:3072 -keyout {{certificateBase}}.key -out {{certificateBase}}.crt -days 3650 -subj '/CN=SupporterKit Key Ring'
          openssl pkcs12 -export -out {{input.KeyCertificateFile}} -inkey {{certificateBase}}.key -in {{certificateBase}}.crt
          chmod 600 {{certificateBase}}.key {{input.KeyCertificateFile}}
        Os comandos pedem senhas interativamente. Na exportação PFX, use a senha informada no assistente.
        Eles criam um certificado de proteção dos dados, sem alterar o HTTPS do Cosmos.
        Não sobrescreva um certificado já usado por uma instalação: ele é necessário para recuperar tokens.
        Confira o usuário da imagem:
          docker run --rm --entrypoint id supporterkit-api:0.1.1
        Ajuste propriedade ou ACL do PFX para permitir leitura somente ao administrador e ao UID app.

        No diretório de instalação, valide sem imprimir credenciais:
          docker compose config --quiet
        Depois inicie:
          docker compose up -d
        O arquivo usa uma imagem pronta, não executa build nem publica portas HTTP.

        No Cosmos, encaminhe os caminhos abaixo para supporterkit-api na porta interna 8080:
          /auth/patreon
          /manage
          /api/v1/supporter (incluindo /api/v1/supporters)
          /api/v1/webhooks/patreon
          /health
          /openapi
        Host: {{origin}}
        Preserve os caminhos completos; não remova prefixos. Preserve as rotas de outros serviços.
        Use HTTPS e a autenticação da API, sem exigir login do Cosmos no callback, SDK ou webhook.
        Não habilite parada automática por inatividade: a sincronização precisa continuar em execução.

        Callback exato no cadastro Patreon: {{origin}}/auth/patreon/callback
        Webhook no Patreon: {{origin}}/api/v1/webhooks/patreon
        Eventos: members:create, members:update e members:delete.
        Use em PATREON_WEBHOOK_SECRET o segredo fornecido pelo Patreon para esse webhook.
        O webhook só terá entregas bem-sucedidas quando a API e a rota estiverem acessíveis.

        Teste {{origin}}/health/ready e abra {{origin}}/auth/patreon/start.
        A saúde confirma o banco; o login e as consultas autorizadas validam a integração real.
        A página /dev e o assistente local não ficam disponíveis nesta instalação Production.

        Cadastro e API Patreon: https://docs.patreon.com/
        ServApps Cosmos: https://cosmos-cloud.io/docs/servapps/
        """;
        if (SetupText.Language(input.Language) == "en") instructions = $$"""
        SupporterKit.NET installation on Cosmos

        This ZIP contains credentials. Keep it private; do not commit or share it.
        Extract .env, compose.yml and appsettings.Production.json to {{input.InstallDirectory}} on your VPS.
        The supporterkit-api:0.1.1 image must be loaded into Docker on the VPS, with a compatible architecture.
        Setup does not publish images, create certificates, change Patreon or configure your VPS.

        Before starting:
        - Confirm that network {{input.CosmosNetwork}} exists and the proxy IP is {{input.TrustedProxyIp}}.
        - Install your RSA certificate with its private key at {{input.KeyCertificateFile}}.
        - Use the password entered in setup when exporting the PFX. Allow the container's app user to read it.
        - The mounted configuration must be readable by the container. Keep .env and the PFX private.
        - The app user needs write access to /data, which stores SQLite and the protection keys.
        - Back up the volume and PFX before updates. Do not delete the volume when recreating the container.

        If you do not have a PFX yet, create one on the VPS with OpenSSL:
          mkdir -p {{certificateDirectory}}
          openssl req -x509 -newkey rsa:3072 -keyout {{certificateBase}}.key -out {{certificateBase}}.crt -days 3650 -subj '/CN=SupporterKit Key Ring'
          openssl pkcs12 -export -out {{input.KeyCertificateFile}} -inkey {{certificateBase}}.key -in {{certificateBase}}.crt
          chmod 600 {{certificateBase}}.key {{input.KeyCertificateFile}}
        These commands prompt for passwords. When exporting the PFX, use the password entered in setup.
        This certificate protects stored data and does not change the Cosmos HTTPS certificate.
        Do not overwrite a certificate already used by an installation: it is needed to recover tokens.
        Check the image user:
          docker run --rm --entrypoint id supporterkit-api:0.1.1
        Set ownership or an ACL so that only the administrator and the app UID can read the PFX.

        From the installation directory, validate without printing credentials:
          docker compose config --quiet
        Then start:
          docker compose up -d
        This file uses a prebuilt image. It does not build the image or publish HTTP ports.

        In Cosmos, forward these paths to supporterkit-api on internal port 8080:
          /auth/patreon
          /manage
          /api/v1/supporter (including /api/v1/supporters)
          /api/v1/webhooks/patreon
          /health
          /openapi
        Host: {{origin}}
        Keep the complete paths; do not strip prefixes. Preserve the routes of other services.
        Use HTTPS and the API's authentication. Do not require Cosmos login for the callback, SDK or webhook.
        Do not enable automatic idle shutdown: synchronization must keep running.

        Exact callback in the Patreon client registration: {{origin}}/auth/patreon/callback
        Patreon webhook URL: {{origin}}/api/v1/webhooks/patreon
        Events: members:create, members:update and members:delete.
        Set PATREON_WEBHOOK_SECRET to the secret provided by Patreon for this webhook.
        Webhook deliveries only succeed once the API and its route are reachable.

        Test {{origin}}/health/ready and open {{origin}}/auth/patreon/start.
        The health check verifies the database; login and authenticated queries verify the real integration.
        Neither /dev nor local setup is available in this Production installation.

        Patreon client registration and API: https://docs.patreon.com/
        Cosmos ServApps: https://cosmos-cloud.io/docs/servapps/
        """;
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, ".env", env);
            Add(zip, "appsettings.Production.json", config);
            Add(zip, "compose.yml", compose + "\n");
            Add(zip, SetupText.Translate("LEIA-ME.txt", SetupText.Language(input.Language)), instructions + "\n");
            Add(zip, ".gitignore", ".env\nappsettings.Production.json\nsecrets/\n*.pfx\n*.key\n*.pem\n*.zip\n");
        }
        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
