# SupporterKit.NET

Versão atual: **0.1.2**. Veja os ajustes no [changelog](CHANGELOG.md).

Bibliotecas .NET para reconhecer apoio e disponibilizar benefícios confirmados a aplicações. A primeira integração é Patreon API v2. O host ASP.NET Core é opcional: quem já tem backend pode referenciar Core e Infrastructure e registrar os serviços nele.

Pagamentos continuam no Patreon. Esta versão reconhece o apoio, sem bloquear funcionalidades do Flow e sem atribuir compras ou licenças permanentes.

## Executar localmente sem Patreon

Requer o SDK **10.0.401**, fixado em `global.json`. O projeto usa .NET 10 LTS. Veja a [política de suporte da Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

Na raiz do repositório:

```powershell
dotnet restore --locked-mode
dotnet run --project src/Supporter.Api
```

Abra **http://localhost:5080/dev**. Escolha um perfil, emita uma chave e copie-a. A interface está marcada como SIMULAÇÃO; não cria contas ou pagamentos reais.

Os dados ficam em `src/Supporter.Api/data`. As migrações são aplicadas antes de o host iniciar. O Fake e seus endpoints só funcionam em Development ou Testing; qualquer outro ambiente recusa configuração de simulação.

Para consultar a API, use [requests/supporter.http](requests/supporter.http). Cole a chave apenas no arquivo local ou na ferramenta HTTP; não faça commit de uma chave real. O OpenAPI está em **/openapi/v1.json**.

Com a API Fake ligada, o smoke test em PowerShell 7 verifica consulta, consentimento e rotação sem imprimir chaves:

```powershell
./scripts/smoke.ps1
```

Com Docker instalado:

```powershell
docker compose -f compose.local.yml up --build
```

Esse exemplo publica apenas `127.0.0.1:5080` e persiste os dados num volume. Build e execução foram verificados com Docker Desktop em Linux amd64, incluindo smoke HTTP e a suíte de testes.

## Preparar a instalação no Patreon e Cosmos

O assistente local funciona antes da configuração de produção. Na raiz do projeto, execute:

```powershell
./scripts/setup.ps1
```

Abra **http://localhost:5100/setup**. Informe o domínio público para obter o callback e o webhook, preencha as credenciais do cliente v2 e consulte seus níveis no Patreon. Também é possível carregar seu `.env` local nos campos da página. Escolha os produtos e códigos de reconhecimento, informe os caminhos Linux, a rede do Cosmos e o IP efetivo do proxy, e baixe o ZIP de instalação.

O ZIP contém credenciais e inclui `.env`, configuração de níveis, Compose e instruções. Guarde-o em local privado e confira os arquivos antes de transferi-los à VPS. O assistente não grava esses dados em arquivos no servidor, não altera o Patreon e não instala a aplicação. A consulta valida o token do criador; o fluxo OAuth e a renovação ainda precisam ser testados na instalação real.

O seletor **Language / Idioma** oferece inglês e português brasileiro, sem apagar os campos preenchidos. O idioma inicial segue a preferência do navegador; também pode ser escolhido por `?lang=en` ou `?lang=pt-BR`. As mensagens e as instruções do ZIP seguem essa escolha: `README.txt` em inglês ou `LEIA-ME.txt` em português. Os nomes dos níveis recebidos do Patreon e os códigos de configuração são preservados.

O modo de preparação aceita apenas acesso local, exige Development ou Testing e não depende do banco nem das credenciais de produção. A aplicação normal mantém esse modo desligado. O assistente confere formatos, mas não verifica se caminhos, certificado, rede ou IP existem na VPS. A imagem Docker e o certificado PFX devem ser preparados separadamente, conforme o `LEIA-ME.txt` gerado.

## Estrutura do projeto

| Projeto | Responsabilidade |
| --- | --- |
| Supporter.Core | Contratos e regras de reconhecimento, sem dependências de servidor |
| Supporter.Infrastructure | Patreon/Fake, SQLite, proteção de tokens, sincronização e inbox |
| Supporter.Api | OAuth, cookies, chaves, HTTP, OpenAPI e página de gestão |
| Supporter.Client | Cliente HTTP sem credenciais de servidor |
| Supporter.Tests | Um projeto xUnit, com poucos testes de regras e fluxos críticos |

Os contratos não dependem de Flow ou Cosmos. Outros provedores podem implementar `ISupporterProvider`; diferenças de autenticação e evidência de contribuição precisarão de análise própria. Ko-fi ainda não está implementado.

## Regras de reconhecimento

`hasSupported` significa contribuição histórica confirmada à campanha configurada. Uma evidência positiva fica preservada quando o apoio termina. Membro gratuito, trial ou entitlement sem evidência de pagamento não comprova doação.

Benefícios atuais dependem de `currently_entitled_tiers`, valor de entitlement positivo e mapeamento explícito do ID de nível para produto/códigos. O valor só distingue entitlement gratuito de pago; não escolhe benefícios. Nomes e preços não determinam direitos. Um cancelamento com entitlement positivo continua com os benefícios confirmados, mesmo com estado `former_patron`.

A política para pagamento recusado é conservadora: `declined_patron` ou última cobrança `Declined` suspende benefícios atuais, preservando o histórico. Não há prazo de carência inferido. Campos monetários nulos não comprovam apoio nem benefício novo. IDs desconhecidos não recebem códigos.

Cada ID de nível tem um produto no mapeamento atual. Vários níveis e produtos podem coexistir na instalação. `productId` precisa estar na lista configurada; produto desconhecido recebe 400. A resposta continua válida para uma pessoa autenticada sem apoio. Chave inválida recebe 401.

`lastSuccessfulSyncAt` é por pessoa. `isStale` fica verdadeiro após `StaleAfterSeconds`; por padrão, 3600 segundos. Uma falha externa conserva os benefícios e o histórico conhecidos. Isso é reconhecimento de apoio, sem aplicação de bloqueio de acesso no desktop.

A listagem pública inclui apenas consentimentos ativos. Não expõe IDs do provedor, tokens, chaves ou valores. Quando o nome está oculto e não há pseudônimo, usa "Apoiante anônimo". O consentimento é independente do estado de apoio.

## Configurar Patreon Real

Consulte a [documentação oficial Patreon](https://docs.patreon.com/) e registre um cliente **v2** no [portal de clientes](https://www.patreon.com/portal/registration/register-clients).

Defina uma origem pública HTTPS, sem caminho, query ou barra final, por exemplo `https://supporters.example.com`. Registre exatamente:

- Callback: `https://supporters.example.com/auth/patreon/callback`
- Webhook: `https://supporters.example.com/api/v1/webhooks/patreon`

Use um webhook de membros v2: `members:create`, `members:update`, `members:delete` e/ou os três eventos `members:pledge:*`. A aplicação aceita apenas esses eventos e a campanha configurada. Nenhum webhook é criado automaticamente.

| Fluxo | Scopes e chamadas |
| --- | --- |
| Ligação da pessoa | `identity identity.memberships campaigns`; GET v2 identity com memberships.campaign e memberships.currently_entitled_tiers |
| Sincronização do criador | `campaigns.members`; GET v2 campaigns/{id}/members com campaign, user e currently_entitled_tiers |
| Descobrir campanha manualmente | `campaigns`; consulta no portal ou GET v2 campaigns, fora da sincronização |
| Criar webhook por API, se adicionado no futuro | `w:campaigns.webhook`; esta versão usa configuração manual no portal |

O scope `campaigns` no login acompanha o include de campanha documentado no identity. Não são solicitados email, endereço ou dados de cartão. O creator token fornecido pelo portal pode conter mais scopes que os usados pela sincronização.

Campos pedidos: user.full_name; member.full_name, patron_status, last_charge_status, campaign_lifetime_support_cents, currently_entitled_amount_cents; tier.title; campaign.name. Os códigos vêm de IDs, não dos títulos. A paginação usa o cursor em `meta.pagination.cursors.next`. O User-Agent é configurável.

Em desenvolvimento, configure segredos locais:

```powershell
dotnet user-secrets set "Supporter:Provider" "Real" --project src/Supporter.Api
dotnet user-secrets set "Supporter:SimulationEnabled" "false" --project src/Supporter.Api
dotnet user-secrets set "Supporter:PublicOrigin" "https://supporters.example.com" --project src/Supporter.Api
dotnet user-secrets set "Supporter:CampaignId" "SEU_CAMPAIGN_ID" --project src/Supporter.Api
dotnet user-secrets set "Supporter:Patreon:ClientId" "SEU_CLIENT_ID" --project src/Supporter.Api
dotnet user-secrets set "Supporter:Patreon:ClientSecret" "SEU_CLIENT_SECRET" --project src/Supporter.Api
dotnet user-secrets set "Supporter:Patreon:CreatorAccessToken" "SEU_CREATOR_ACCESS_TOKEN" --project src/Supporter.Api
dotnet user-secrets set "Supporter:Patreon:CreatorRefreshToken" "SEU_CREATOR_REFRESH_TOKEN" --project src/Supporter.Api
dotnet user-secrets set "Supporter:Patreon:WebhookSecret" "SEU_WEBHOOK_SECRET" --project src/Supporter.Api
dotnet user-secrets set "Supporter:Patreon:CallbackUri" "https://supporters.example.com/auth/patreon/callback" --project src/Supporter.Api
dotnet user-secrets set "Supporter:Tiers:SEU_TIER_ID:ProductId" "flow" --project src/Supporter.Api
dotnet user-secrets set "Supporter:Tiers:SEU_TIER_ID:BenefitCodes:0" "supporter" --project src/Supporter.Api
```

Esses comandos são exemplos com placeholders; ao inserir segredos, considere o histórico do terminal. User-secrets não é armazenamento criptografado. Em Development, remova os mapeamentos Fake do arquivo de configuração se for usar IDs reais. Configure o proxy confiável para encaminhar a origem HTTPS escolhida.

No primeiro uso, os tokens do criador são importados e renovados, pois sua expiração original é desconhecida. Depois, a cópia protegida no SQLite prevalece sobre os valores de bootstrap. Em caso de revogação pelo Patreon, pare o serviço e remova apenas o registro `Tokens.Owner='creator'` antes de configurar novos tokens; não apague o banco inteiro. Reautorizar a pessoa substitui seus tokens e permite emitir outra chave.

Produção também exige um certificado RSA com chave privada para proteger o key ring. Veja [operação e Cosmos](docs/operations.md).

## Contrato HTTP

| Método e rota | Autenticação |
| --- | --- |
| GET /auth/patreon/start | Navegador; inicia state e cookie de vínculo |
| GET /auth/patreon/callback | State válido, cookie da origem e código OAuth |
| POST /api/v1/supporter/keys | Cookie de gestão + X-CSRF-TOKEN |
| GET /api/v1/supporter/me?productId=flow | Authorization: Bearer CHAVE |
| PUT /api/v1/supporter/public-profile | Chave, ou cookie de gestão + X-CSRF-TOKEN |
| GET /api/v1/supporters?page=1&pageSize=25 | Público; pageSize até 100 |
| POST /api/v1/webhooks/patreon | X-Patreon-Signature e X-Patreon-Event |
| GET /health/live e /health/ready | Público; sem detalhes internos |

A página `/manage` tem sessão de 15 minutos. A chave é gerada no servidor, armazenada como hash e exibida uma vez. Rodar a chave revoga a anterior. O login OAuth recupera acesso à gestão; não recupera uma chave antiga.

`/me` devolve ID interno, nome opcional, hasSupported, currentStatus, tierIds permitidos, benefitCodes, lastSuccessfulSyncAt e isStale. Não há tokens ou valores pagos nessa resposta. Os estados são `none`, `free`, `entitled`, `former`, `declined` ou `unknown`; `entitled` descreve os dados confirmados, não uma licença comercial.

O OpenAPI gerado lista os endpoints de referência. [docs/http.md](docs/http.md) descreve os headers, o corpo de consentimento, CSRF e exemplos de resposta.

## Consumir no WinUI ou integrar num backend

O SDK usa `HttpClient` e envia a chave no header. A aplicação desktop não recebe ClientSecret ou tokens do criador. Veja o [exemplo C#](docs/integration.md), incluindo armazenamento com PasswordVault do Windows e registro de Core/Infrastructure em outro backend.

Não houve alteração no repositório FlowEngineNet. Esta entrega fornece o contrato e o SDK para a integração posterior.

## Testes e cenários

```powershell
dotnet test -c Release
```

A suíte tem 11 casos, incluindo seis linhas de uma única teoria de reconhecimento. Os testes HTTP usam WebApplicationFactory e SQLite isolado. O provider Real recebe JSON:API simulado; os testes não dependem de acesso ao Patreon.

| Cenário | Fixture ou ação | Resultado esperado |
| --- | --- | --- |
| Sem subscrição | none | Sem histórico e sem benefícios |
| Membro gratuito | free | Sem doação confirmada e sem benefícios pagos |
| Ativo A/B/C | active-a, active-b, active-c | Apenas códigos do nível configurado |
| N níveis | active-extra; acrescentar fixtures e mapeamentos | Sem limite fixo de planos |
| Antigo apoiante | former | Histórico preservado; sem benefícios |
| Pagamento recusado | declined | Sem benefícios, conforme política conservadora |
| Cancelado com entitlement | cancelled-entitled | Benefícios confirmados mantidos |
| Mudança de nível | changed-tier ou botão Aplicar estado | Substituir níveis pelos atuais |
| Outra campanha | other-campaign | Nenhum benefício ou histórico desta campanha |
| Nome oculto | anonymous | Nome privado nulo; público só por opt-in |
| Consentimento retirado | Salvar opt-in, depois desmarcar | Nome removido da lista pública |
| Chave inválida/revogada | requests .http ou rodar chave | 401 na chave inválida/antiga |
| Token expirado/refresh | Teste automatizado com tokens fictícios e HTTP simulado | Persistir refresh; falha preserva credencial anterior |
| API indisponível | Botão Simular indisponibilidade | Último estado mantido; isStale após tolerância |
| Rate limit | Resposta 429 simulada no teste HTTP | Respeitar Retry-After/header ou retry_after_seconds |
| Webhook inválido/repetido | Teste HTTP com assinatura original e corpo alterado | Rejeitar alteração; deduplicar repetição |
| Reinício da inbox | Teste de reinício SQLite/key ring | Retomar trabalho persistido |
| Eventos fora de ordem | Entregar sinais enquanto o provider mantém seu estado atual | Consultar estado atual, sem aplicar snapshot do evento |

Para verificar desatualização rapidamente, inicie com `--Supporter:SyncIntervalSeconds=10 --Supporter:StaleAfterSeconds=10`. As alterações no Fake são mantidas em memória; reiniciar recarrega as fixtures. Consentimentos, chaves, histórico e inbox são persistidos. Em testes locais, use perfis fictícios; não use tokens ou pagamentos reais.

Patreon não oferece sandbox separada. As orientações antigas de cobrança por publicação não são usadas como garantia de testes gratuitos. A validação real exige o seu cliente v2, scopes, callback, contas autorizadas e eventos de teste do portal. Operações com possível cobrança são executadas apenas por você, após confirmar o efeito. Testes locais não provam o comportamento de pagamentos reais.

## Operação e limitações

[docs/operations.md](docs/operations.md) cobre Docker, ajustes de importação no Cosmos, backup, restauração, migrações e troubleshooting. [docs/decisions.md](docs/decisions.md) registra as decisões e notas para futuros posts.

SQLite é para uma instância com volume local. Migrar para PostgreSQL exigirá provider e migrações próprios. Não há multi-tenancy, catálogo comercial, licenças permanentes, compras avulsas Patreon, Steam, itch.io ou Kickstarter. n8n não participa da validação nem sincronização.

O worker usa uma execução por vez, timeouts, paginação completa e retries limitados. A inbox guarda somente digest, campanha, tipo, timestamps e tentativas; não retém o corpo com dados pessoais. Trabalho pendente não é apagado por idade. Concluídos/dead letters seguem a retenção de sete dias. Depois de oito falhas, um trabalho vira dead letter; a sincronização periódica permanece ativa. A deduplicação não pressupõe ID único e dura até a remoção do digest.

Sem refresh válido de uma pessoa ausente da lista da campanha, sua última informação fica preservada e envelhece. A ausência numa lista, mesmo completa, não prova revogação. Reautorizar a conta permite consultar a associação novamente.

Build, testes e execução Fake foram verificados em Windows x64 e em containers Linux amd64 pelo Docker Desktop. ARM64, Cosmos e chamadas reais ao Patreon ainda precisam de validação. A CI executará build/test em Linux quando o repositório for enviado ao GitHub; não foi executada remotamente.

Os comandos executados e resultados estão em [docs/validation.md](docs/validation.md).

## Pacotes locais

```powershell
dotnet pack src/Supporter.Core -c Release -o artifacts/packages
dotnet pack src/Supporter.Infrastructure -c Release -o artifacts/packages
dotnet pack src/Supporter.Client -c Release -o artifacts/packages
```

Os pacotes se chamam SupporterKit.Core, SupporterKit.Infrastructure e SupporterKit.Client, versão 0.1.2. A licença do próprio projeto ainda deve ser escolhida antes de publicação. Nenhum pacote, imagem ou release foi publicado.
