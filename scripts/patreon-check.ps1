param(
    [string]$EnvFile = (Join-Path $PSScriptRoot '..\.env')
)

$ErrorActionPreference = 'Stop'
$settings = @{}
foreach ($line in [System.IO.File]::ReadAllLines($EnvFile)) {
    if ($line -match '^\s*([A-Z_]+)=(.*)$') {
        $settings[$Matches[1]] = $Matches[2].Trim().Trim('"').Trim("'")
    }
}
if ([string]::IsNullOrWhiteSpace($settings['PATREON_CREATOR_ACCESS_TOKEN'])) {
    throw 'Preencha PATREON_CREATOR_ACCESS_TOKEN no .env local.'
}

$uri = 'https://www.patreon.com/api/oauth2/v2/campaigns?include=tiers&fields%5Bcampaign%5D=creation_name,url&fields%5Btier%5D=title,amount_cents,published'
try {
    $response = Invoke-RestMethod -Method Get -Uri $uri -Headers @{
        Authorization = 'Bearer ' + $settings['PATREON_CREATOR_ACCESS_TOKEN']
        'User-Agent' = 'SupporterKit.NET/0.1.1 (creator setup)'
    } -TimeoutSec 30 -MaximumRedirection 0
} catch {
    $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    throw "Consulta ao Patreon falhou (HTTP $status). Confira o token e a conexão."
}

$campaigns = @($response.data | Where-Object {
    [string]::IsNullOrWhiteSpace($settings['PATREON_CAMPAIGN_ID']) -or
    $_.id -eq $settings['PATREON_CAMPAIGN_ID']
})
if ($campaigns.Count -eq 0) { throw 'Nenhuma campanha correspondente foi retornada.' }
Write-Host ('Consulta REAL ao Patreon em {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
foreach ($campaign in $campaigns) {
    Write-Host ('Campanha: {0} (ID {1})' -f $campaign.attributes.creation_name, $campaign.id)
    Write-Host $campaign.attributes.url
    $tierIds = @($campaign.relationships.tiers.data | ForEach-Object { $_.id })
    $response.included | Where-Object { $_.type -eq 'tier' -and $_.id -in $tierIds } |
        Sort-Object { $_.attributes.amount_cents } |
        ForEach-Object {
            [pscustomobject]@{
                Nivel = $_.attributes.title
                ID = $_.id
                'Valor (centavos)' = $_.attributes.amount_cents
                Publicado = $_.attributes.published
            }
        } | Format-Table -AutoSize
}
Write-Host 'Consulta concluída. Credenciais e dados dos membros não foram exibidos.'
