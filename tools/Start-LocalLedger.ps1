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
    at the next close.  The EODHD token comes from the EODHD_API_TOKEN environment variable if it is set (as
    TangentAtlas.BackTest reads it); otherwise from %APPDATA%\TangentAtlas\eodhd-token.xml, where it is kept encrypted
    by Windows for this user on this computer (DPAPI: no other account or computer can read it); otherwise it is asked
    for once (typed hidden) and kept there.  It is given to the server's process only.

    The database is this computer's SQL Server (the Developer Edition's default instance, database AtlasLedger): the
    script applies the schema's migrations and prepares it first, so what is loaded survives a restart.  -InMemory keeps
    everything in memory instead, gone when the server stops.  Ctrl+C stops the server.

.PARAMETER Port
    The port (default 5180, the server's launch profile).

.PARAMETER MarketHistory
    Load closes, dividends and splits from EODHD and the Treasury bill rate, and fill paper orders at the close.

.PARAMETER NoBuild
    Run the server as last built.

.PARAMETER Connection
    The SQL Server database (default: the local default instance's AtlasLedger, signed in as you).

.PARAMETER InMemory
    Keep the ledger in memory: nothing survives the server stopping.

.PARAMETER ReplaceToken
    Ask for the EODHD token again and keep the new one (after it changes at EODHD).

.EXAMPLE
    .\tools\Start-LocalLedger.ps1 -MarketHistory
#>
[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)][int]$Port = 5180,
    [switch]$MarketHistory,
    [switch]$NoBuild,
    [string]$Connection = 'Server=localhost;Database=AtlasLedger;Trusted_Connection=True;TrustServerCertificate=True',
    [switch]$InMemory,
    [switch]$ReplaceToken
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
    $tokenPath = Join-Path $folder 'eodhd-token.xml'
    if ($env:EODHD_API_TOKEN -and -not $ReplaceToken) {
        $plain = $env:EODHD_API_TOKEN
        Write-Host 'The EODHD token: from the EODHD_API_TOKEN environment variable.' -ForegroundColor Cyan
    } else {
        if ($ReplaceToken -or -not (Test-Path $tokenPath)) {
            # Export-Clixml keeps a SecureString encrypted with Windows' data protection, for this user on this computer.
            Read-Host -AsSecureString 'EODHD API token (typed hidden; kept encrypted for you on this computer)' | Export-Clixml -Path $tokenPath
            Write-Host "Kept the EODHD token, encrypted, in $tokenPath (-ReplaceToken asks again)." -ForegroundColor Cyan
        }
        $plain = [Net.NetworkCredential]::new('', (Import-Clixml -Path $tokenPath)).Password
    }
    if (-not $plain) { throw 'The EODHD token is empty: run again with -ReplaceToken.' }
    $environment.MarketHistory__EodhdApiToken = $plain
    $environment.Services__StartOn = 'rules,broker,trading,market-history'
    $environment.Broker__SimulatedFills = 'AtNextClose'
}

if (-not $NoBuild) {
    dotnet build (Join-Path $repository 'src/AtlasLedger.Server') --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Building the server failed.' }
}

if (-not $InMemory) {
    # The schema first (Entity Framework's migrations), then the writer's table types: what the release pipeline does in Azure.
    Write-Host 'Applying the schema to the database...' -ForegroundColor Cyan
    Push-Location $repository
    try {
        dotnet tool restore | Out-Null
        $env:LEDGER_SCHEMA_CONNECTION = $Connection
        dotnet ef database update --project (Join-Path $repository 'src/AtlasLedger.Schema')
        if ($LASTEXITCODE -ne 0) { throw "Applying the migrations failed: is SQL Server running, and can you sign in to it?  ($Connection)" }
    } finally {
        Remove-Item Env:\LEDGER_SCHEMA_CONNECTION -ErrorAction SilentlyContinue
        Pop-Location
    }
    dotnet run --project (Join-Path $repository 'src/AtlasLedger.Server') --no-build --no-launch-profile -- prepare-database --ConnectionStrings:Ledger=$Connection
    if ($LASTEXITCODE -ne 0) { throw 'Preparing the database failed.' }
    $environment.ConnectionStrings__Ledger = $Connection
}

$saved = @{}
foreach ($name in $environment.Keys) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
}
try {
    $store = if ($InMemory) { 'in memory' } else { 'in SQL Server' }
    Write-Host "The ledger at $server ($store), trusting $($settings.authority) for $($settings.apiUri); services: $($environment.Services__StartOn).  Ctrl+C stops it." -ForegroundColor Green
    dotnet run --project (Join-Path $repository 'src/AtlasLedger.Server') --no-build --no-launch-profile
} finally {
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
