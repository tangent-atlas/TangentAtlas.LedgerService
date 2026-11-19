<#
.SYNOPSIS
    Clears tenants of the ledger (the one on this computer, or with -Production the one in Azure): takes every holding
    out, account by account, then deletes every account.  Open the website on the tenant first to watch it happen.

.DESCRIPTION
    For each tenant, script-loader clear does it in two stages, each account a step of its own:
      1. The holdings leave each account the way a real account is closed: its securities are sold at their current
         prices and all of its cash is withdrawn, as events (the log is never changed or deleted).  What it was paid in
         (deposits less withdrawals) stays true.  The account stays, holding nothing.
      2. Each account is deleted, with its node, its custodian number and its links to other nodes.  Its institution,
         model and assets stay (shared reference data), and so do its events.
    With -RemoveUsers it then removes the tenant's users, so no person sees the tenant any more.

    Clearing a clear tenant changes nothing.  Loading the same scripts again brings the accounts back (the scripts supply
    every key), holding what the log adds up to: nothing.  To watch holdings arrive again, load the Joe 10 into a new
    tenant: .\tools\Set-Joe10.ps1 -TenantId ([guid]::NewGuid()).

    The rules service must be on: it is what turns the events into holdings (it starts on with the server).

.PARAMETER TenantId
    The tenants to clear.

.PARAMETER SampleFirms
    Clear instead every tenant of the signed-in test person (access-test tenants) that holds the sample firms' accounts
    (firm-a.json, firm-b.json: Smith Trust, Lee 401k, Jones IRA, Garcia Trust): the tenants earlier walkthroughs
    (Test-LedgerDeployment.ps1) left behind.  Removes their users too.  Never touches another tenant.

.PARAMETER HoldingsOnly
    Take the holdings out and keep the accounts.

.PARAMETER RemoveUsers
    Also remove each tenant's users.

.PARAMETER PauseSeconds
    How long to wait between accounts, so each change can be watched in the website (default 2; 0 for none).

.PARAMETER NoBuild
    Use the programs as last built.

.PARAMETER Production
    Clear tenants of the ledger in Azure instead of the one on this computer.

.EXAMPLE
    .\tools\Clear-Tenant.ps1 -TenantId 4a6f6531-3020-7061-7065-720000000001

.EXAMPLE
    .\tools\Clear-Tenant.ps1 -SampleFirms -Production
#>
[CmdletBinding(DefaultParameterSetName = 'Tenants')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Tenants')][guid[]]$TenantId,
    [Parameter(Mandatory, ParameterSetName = 'SampleFirms')][switch]$SampleFirms,
    [switch]$HoldingsOnly,
    [switch]$RemoveUsers,
    [ValidateRange(0, 600)][double]$PauseSeconds = 2,
    [switch]$NoBuild,
    [switch]$Production
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$settingsPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "TangentAtlas\$(if ($Production) { 'ledger-client.json' } else { 'ledger-client.local.json' })"
if (-not (Test-Path $settingsPath)) {
    throw "$settingsPath is missing: $(if ($Production) { "run TangentAtlas.Platform's Deploy-EntraApplications.ps1 (it writes it)" } else { 'run .\tools\Start-LocalLedger.ps1 first (it writes it)' })."
}
Write-Host "The ledger: $((Get-Content $settingsPath -Raw | ConvertFrom-Json).server)" -ForegroundColor Cyan
$env:ATLAS_LEDGER_CLIENT_SETTINGS = $settingsPath

$programs = @{
    Loader = @{ Project = 'AtlasLedger.ScriptLoader'; Assembly = 'script-loader' }
    Operator = @{ Project = 'AtlasLedger.Operator'; Assembly = 'ledger-operator' }
    AccessTest = @{ Project = 'AtlasLedger.AccessTest'; Assembly = 'access-test' }
}
foreach ($program in $programs.Values) {
    $program.Path = Join-Path $repository "src/$($program.Project)/bin/Release/net11.0/$($program.Assembly).dll"
    if (-not $NoBuild) {
        dotnet build (Join-Path $repository "src/$($program.Project)") --configuration Release --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "Building $($program.Project) failed." }
    }
}

if ($SampleFirms) {
    # The sample firms' account keys, from the scripts themselves.
    $data = Join-Path $repository 'src/AtlasLedger.Client/Data'
    $sampleAccounts = 'firm-a.json', 'firm-b.json', 'jones-ira.json' |
        ForEach-Object { (Get-Content (Join-Path $data $_) -Raw | ConvertFrom-Json).operations } |
        Where-Object { $_.table -eq 'Account' } | ForEach-Object { "$($_.row.accountId)".ToLowerInvariant() }

    Write-Host "`n== The test person's tenants that hold the sample firms" -ForegroundColor Cyan
    $mine = & dotnet $programs.AccessTest.Path tenants
    if ($LASTEXITCODE -ne 0) { throw 'access-test tenants failed.' }
    $work = Join-Path ([IO.Path]::GetTempPath()) "clear-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $TenantId = foreach ($tenant in $mine | Where-Object { $_ -match '^[0-9a-fA-F-]{36}$' }) {
            $export = Join-Path $work "$tenant.json"
            & dotnet $programs.Operator.Path export $tenant $export | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "ledger-operator export $tenant failed." }
            $accounts = (Get-Content $export -Raw | ConvertFrom-Json).operations | Where-Object { $_.table -eq 'Account' } | ForEach-Object { "$($_.row.accountId)".ToLowerInvariant() }
            if ($accounts | Where-Object { $sampleAccounts -contains $_ }) {
                Write-Host "   $tenant  ($(@($accounts).Count) account(s))"
                [guid]$tenant
            }
        }
    } finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
    if (-not $TenantId) {
        Write-Host 'None: nothing to clear.' -ForegroundColor Green
        return
    }
    $RemoveUsers = $true
}

$arguments = @()
if ($HoldingsOnly) { $arguments += '--holdings-only' }
if ($RemoveUsers) { $arguments += '--remove-users' }
$arguments += @('--pause', $PauseSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
foreach ($tenant in $TenantId) {
    Write-Host "`n== Clearing $tenant" -ForegroundColor Cyan
    & dotnet $programs.Loader.Path clear "$tenant" @arguments
    if ($LASTEXITCODE -ne 0) { throw "script-loader clear $tenant failed (exit code $LASTEXITCODE)." }
}
Write-Host "`nDone: $(@($TenantId).Count) tenant(s) clear." -ForegroundColor Green
