$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\Supporter.Api\Supporter.Api.csproj'
Write-Host 'Assistente local: http://localhost:5100/setup'
Write-Host 'Use Ctrl+C para encerrar. A aplicação de produção não precisa estar configurada.'
dotnet run --project $project --no-launch-profile -- --environment Development --Setup:Enabled=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
