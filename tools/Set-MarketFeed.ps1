<#
.SYNOPSIS
    Starts or stops the simulated price feed on the ledger (the one on this computer, or with -Production the one in Azure).

.DESCRIPTION
    The feed moves the prices of the tenants' holdings a few pennies at a time (MarketFeed:MoveChance and MaximumMove),
    and the website's pages show each move as it happens.  It is the ledger's market-feed service, switched by the web
    service PUT v1/system/services/market-feed (a tool with Services.Manage only), which ledger-operator calls; turned on,
    it turns itself off after -Hours (at most a day, Services:MaximumDuration).  Prices start from where the market left
    them (the last close), and the next close resets them.

.PARAMETER On
    Start it.

.PARAMETER Off
    Stop it.

.PARAMETER Hours
    How long it runs before it turns itself off (default 4; at most 24).

.PARAMETER Production
    The ledger in Azure (%APPDATA%\TangentAtlas\ledger-client.json, which TangentAtlas.Platform's Deploy-EntraApplications.ps1
    writes) instead of the one on this computer (Start-LocalLedger.ps1's).

.PARAMETER NoBuild
    Use ledger-operator as last built.

.EXAMPLE
    .\tools\Set-MarketFeed.ps1 -On -Production

.EXAMPLE
    .\tools\Set-MarketFeed.ps1 -Off -Production
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(Mandatory, ParameterSetName = 'On')][switch]$On,
    [Parameter(Mandatory, ParameterSetName = 'Off')][switch]$Off,
    [Parameter(ParameterSetName = 'On')][ValidateRange(0.01, 24)][double]$Hours = 4,
    [switch]$Production,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$settingsPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "TangentAtlas\$(if ($Production) { 'ledger-client.json' } else { 'ledger-client.local.json' })"
if (-not (Test-Path $settingsPath)) {
    throw "$settingsPath is missing: $(if ($Production) { "run TangentAtlas.Platform's Deploy-EntraApplications.ps1 (it writes it)" } else { 'run .\tools\Start-LocalLedger.ps1 first (it writes it)' })."
}
Write-Host "The ledger: $((Get-Content $settingsPath -Raw | ConvertFrom-Json).server)" -ForegroundColor Cyan
$env:ATLAS_LEDGER_CLIENT_SETTINGS = $settingsPath

$project = Join-Path $repository 'src/AtlasLedger.Operator'
if (-not $NoBuild) {
    dotnet build $project --configuration Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Building AtlasLedger.Operator failed.' }
}
$operator = Join-Path $project 'bin/Release/net11.0/ledger-operator.dll'
$arguments = switch ($PSCmdlet.ParameterSetName) {
    'On' { @('services', 'on', 'market-feed', '--for', $Hours.ToString([Globalization.CultureInfo]::InvariantCulture)) }
    'Off' { @('services', 'off', 'market-feed') }
    default { @('services') }
}
& dotnet $operator @arguments
if ($LASTEXITCODE -ne 0) { throw "ledger-operator $($arguments -join ' ') failed (exit code $LASTEXITCODE)." }
