param(
    [string]$Origin = 'http://localhost:5080',
    [string]$ProfileId = 'active-a'
)
$ErrorActionPreference = 'Stop'
$Origin = $Origin.TrimEnd('/')
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$page = Invoke-WebRequest "$Origin/dev" -WebSession $session
if ($page.Content -notmatch 'SIMULAÇÃO') { throw 'Este smoke test exige modo Fake local.' }
$token = [System.Net.WebUtility]::HtmlDecode(
    [regex]::Match($page.Content, 'name="__RequestVerificationToken" value="([^"]+)"').Groups[1].Value)
Invoke-WebRequest "$Origin/dev/login" -Method Post -WebSession $session -Body @{
    __RequestVerificationToken = $token
    profileId = $ProfileId
} | Out-Null
$manage = Invoke-WebRequest "$Origin/manage" -WebSession $session
$csrf = [System.Net.WebUtility]::HtmlDecode([regex]::Match($manage.Content, 'data-token="([^"]+)"').Groups[1].Value)
$headers = @{ 'X-CSRF-TOKEN' = $csrf }
$key = (Invoke-RestMethod "$Origin/api/v1/supporter/keys" -Method Post -WebSession $session -Headers $headers).key
$auth = @{ Authorization = "Bearer $key" }
$me = Invoke-RestMethod "$Origin/api/v1/supporter/me?productId=flow" -Headers $auth
Write-Output ("Perfil: {0}; histórico: {1}; estado: {2}; benefícios: {3}" -f $ProfileId, $me.hasSupported, $me.currentStatus, ($me.benefitCodes -join ', '))
Write-Output ("Tempo de apoio: {0}" -f $me.supportDurationLabel)
Invoke-RestMethod "$Origin/api/v1/supporter/public-profile" -Method Put -Headers $auth -ContentType 'application/json' -Body '{"optIn":true,"pseudonym":"Smoke fictício"}' | Out-Null
$list = Invoke-RestMethod "$Origin/api/v1/supporters?pageSize=100"
if ($me.id -notin $list.items.id) { throw 'Opt-in não apareceu na primeira página; use um banco local com até 100 perfis.' }
Invoke-RestMethod "$Origin/api/v1/supporter/public-profile" -Method Put -Headers $auth -ContentType 'application/json' -Body '{"optIn":false,"pseudonym":null}' | Out-Null
$list = Invoke-RestMethod "$Origin/api/v1/supporters?pageSize=100"
if ($me.id -in $list.items.id) { throw 'Consentimento retirado ainda aparece.' }
Invoke-RestMethod "$Origin/api/v1/supporter/keys" -Method Post -WebSession $session -Headers $headers | Out-Null
$revoked = Invoke-WebRequest "$Origin/api/v1/supporter/me?productId=flow" -Headers $auth -SkipHttpErrorCheck
if ($revoked.StatusCode -ne 401) { throw 'A chave anterior deveria estar revogada.' }
Write-Output 'Smoke passou: consulta, consentimento e rotação. Chaves não foram impressas.'
