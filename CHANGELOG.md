# Histórico de versões

## 0.1.1 — 2026-10-10

- Separados os limites de chamadas do início do login e do callback Patreon, para que iniciar novas tentativas não esgote o limite do retorno.
- Corrigida a identidade usada na validação CSRF ao salvar o consentimento público pela sessão de gestão.
- Definido explicitamente o caminho `/` do cookie CSRF.
- Adicionados logs de bloqueios por limite e de falhas CSRF, sem registrar tokens, códigos OAuth ou valores de cookies.
- A página de gestão passou a mostrar o título do erro retornado pela API.
- Ampliada a verificação do fluxo OAuth para cobrir emissão de chave e consentimento no ambiente Production, usando certificado local e respostas Patreon simuladas.
- Adicionado o ícone do projeto para uso no Cosmos.

Na implantação Cosmos, as rotas precisam preservar seus prefixos: `Strip Prefix` deve ficar desativado. Essa configuração corrigiu o ciclo de login observado em `/manage`; depende do painel e não de uma atualização do código.

## 0.1.0

Implementação inicial: reconhecimento de apoio Patreon v2, OAuth, chaves de aplicação, consentimento público, duração do apoio, sincronização, webhooks, persistência SQLite, SDK .NET, Docker e setup local em português e inglês.
