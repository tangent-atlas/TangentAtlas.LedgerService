<#
.SYNOPSIS
    Runs the ledger server on this computer with real sign-in, so the website and the programs reach it as they reach
    the one in Azure.

.DESCRIPTION
    Reads the identifiers TangentAtlas.Platform's Deploy-EntraApplications.ps1 wrote to
    %APPDATA%\TangentAtlas\ledger-client.json and runs the server (Development, at http://127.0.0.1:<Port>) trusting
    the external tenant's tokens for the Ledger API, with the operator and the script loader as its tools.  Writes
    %APPDATA%\TangentAtlas\ledger-client.local.json, the same settings pointed at this server: set
    ATLAS_LEDGER_CLIENT_SETTINGS to it and script-loader, ledger-operator and access-test sign in to this server for
    real (Set-LocalJoe10.ps1 does).

    The services: rules, broker and trading are on; with -MarketHistory, market-history too, and the paper broker fills
    at the next close.  The EODHD token is asked for (typed hidden), given to the server's process only, and not
    stored.

    The database is the server's Development connection (appsettings.Development.json: the local SQL Server).  Ctrl+C
    stops the server.

.PARAMETER Port
    The port (default 5180, the server's launch profile).

.PARAMETER MarketHistory
    Load closes, dividends and splits from EODHD and the Treasury bill rate, and fill paper orders at the close.

.PARAMETER NoBuild
    Run the server as last built.

.EXAMPLE
    .\tools\Start-LocalLedger.ps1 -MarketHistory
#>
[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)][int]$Port = 5180,
    [switch]$MarketHistory,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$folder = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TangentAtlas'
$settingsPath = Join-Path $folder 'ledger-client.json'
$localPath = Join-Path $folder 'ledger-client.local.json'
if (-not (Test-Path $settingsPath)) {
    throw "$settingsPath is missing: run TangentAtlas.Platform's Deploy-EntraApplications.ps1 first."
}

$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
foreach ($name in 'authority', 'apiUri', 'operatorClientId', 'scriptLoaderClientId') {
    if (-not $settings.$name) { throw "$settingsPath has no ${name}: run Deploy-EntraApplications.ps1 again." }
}
$server = "http://127.0.0.1:$Port"

# The programs' settings for this server: the same identifiers, another address.
$local = $settings | Select-Object *
$local.server = $server
$local | ConvertTo-Json | Set-Content -Path $localPath -Encoding utf8
Write-Host "Wrote $localPath (the programs reach $server with it: `$env:ATLAS_LEDGER_CLIENT_SETTINGS = '$localPath')." -ForegroundColor Cyan

$environment = [ordered]@{
    ASPNETCORE_ENVIRONMENT = 'Development'
    ASPNETCORE_URLS = $server
    IdentityProvider__Authority = "$($settings.authority)/v2.0"
    IdentityProvider__Audiences__0 = $settings.apiUri -replace '^api://', ''
    IdentityProvider__Audiences__1 = $settings.apiUri
    Tools__ClientIds__0 = $settings.operatorClientId
    Tools__ClientIds__1 = $settings.scriptLoaderClientId
    Services__StartOn = 'rules,broker,trading'
}
if ($MarketHistory) {
    $token = Read-Host -AsSecureString 'EODHD API token (hidden; given to the server only, not stored)'
    $environment.MarketHistory__EodhdApiToken = [Net.NetworkCredential]::new('', $token).Password
    $environment.Services__StartOn = 'rules,broker,trading,market-history'
    $environment.Broker__SimulatedFills = 'AtNextClose'
}

if (-not $NoBuild) {
    dotnet build (Join-Path $repository 'src/AtlasLedger.Server') --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Building the server failed.' }
}

$saved = @{}
foreach ($name in $environment.Keys) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
}
try {
    Write-Host "The ledger at $server, trusting $($settings.authority) for $($settings.apiUri); services: $($environment.Services__StartOn).  Ctrl+C stops it." -ForegroundColor Green
    dotnet run --project (Join-Path $repository 'src/AtlasLedger.Server') --no-build --no-launch-profile
} finally {
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
