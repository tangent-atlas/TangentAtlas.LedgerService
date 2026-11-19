<#
.SYNOPSIS
    Empties tenants of the ledger in its database, so they can be loaded again from their scripts and watched as they
    load: maintenance, with the ledger stopped.  By default, the Joe 10's tenant; with -All, every tenant, which leaves
    a known, empty environment after load, stress and health tests.

.DESCRIPTION
    The event log is append-only: no API deletes an event.  So this deletes the tenants' rows in the database itself,
    every table with a TenantId (events, accounts, users, Bellerophon's watermark), in one transaction, with the ledger
    stopped (it holds every tenant in memory; it would go on serving what is deleted).  Other tenants are not touched.
    Afterwards the tenants don't exist: load them again (.\tools\Set-Joe10.ps1) and watch the dashboard.

    With -Production, against the ledger in Azure:
      1. finds the ledger's App Service and its database (from the app's connection string), and lets this computer's
         address through the SQL server's firewall (as TangentAtlas.Platform's scripts do);
      2. asks you to confirm, then stops the App Service and waits until the ledger no longer answers;
      3. deletes the tenants, signed in as you (Authentication=Active Directory Default: you are the server's Entra
         administrator; run Connect-AzAccount first if Azure asks);
      4. starts the App Service again, always (even if step 3 failed), and waits until the ledger answers.  It loads
         every tenant before it takes traffic, so this takes a few minutes.  The service switches go back to their
         start-up settings (the penny feed is off: Set-MarketFeed.ps1 -On -Production).
    Pages open on the website lose the ledger while it is stopped: reload them after.

    Without -Production, against the database Start-LocalLedger.ps1 uses: stop the local ledger first (Ctrl+C in its
    window); this refuses while it answers.  Start it again after.

.PARAMETER TenantId
    The tenants to empty (default: the Joe 10's, 4a6f6531-3020-7061-7065-720000000001).

.PARAMETER All
    Empty every tenant in the database: every artifact of every test (load-test tenants, the health test's firms, the
    Joe 10).  The schema stays.  Then load the Joe 10 again: Set-Joe10.ps1 -Account, then -Holdings.  While there are no
    customers this is safe; it must be removed or locked down before the first real account data.

.PARAMETER DryRun
    Count what would be deleted, table by table; delete nothing, stop nothing.

.PARAMETER Connection
    The local database (default: Start-LocalLedger.ps1's).  Not used with -Production.

.PARAMETER NoBuild
    Use ledger-load as last built.

.PARAMETER Production
    Empty the tenants in Azure.

.EXAMPLE
    .\tools\Reset-Tenant.ps1 -Production -All -DryRun

.EXAMPLE
    .\tools\Reset-Tenant.ps1 -Production -DryRun

.EXAMPLE
    .\tools\Reset-Tenant.ps1 -Production; .\tools\Set-Joe10.ps1 -Production
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [guid[]]$TenantId = @('4a6f6531-3020-7061-7065-720000000001'),
    [switch]$All,
    [switch]$DryRun,
    [string]$Connection = 'Server=localhost;Database=AtlasLedger;Trusted_Connection=True;TrustServerCertificate=True',
    [switch]$NoBuild,
    [switch]$Production
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repository 'tools/AtlasLedger.LoadTest'
if (-not $NoBuild) {
    dotnet build $project --configuration Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Building ledger-load failed.' }
}
$tool = Join-Path $project 'bin/Release/net11.0/ledger-load.dll'
$tenantArguments = if ($All) { @('--all') } else { @($TenantId | ForEach-Object { '--tenant', "$_" }) }
$which = if ($All) { 'EVERY tenant' } else { "tenant(s) $($TenantId -join ', ')" }

function Invoke-Delete {
    param([Parameter(Mandatory)][string]$ConnectionString, [string]$Server, [switch]$Count)
    $arguments = @('clear-database', '--connection', $ConnectionString) + $tenantArguments
    if ($Server) { $arguments += @('--server', $Server) }
    if ($DryRun -or $Count) { $arguments += '--dry-run' }
    # Captured, both streams, and shown here: some terminals drop or reorder a child program's output.
    $said = @(& dotnet $tool @arguments 2>&1 | ForEach-Object { "$_" })
    $exitCode = $LASTEXITCODE
    $said | ForEach-Object { Write-Host "   $_" }
    if ($exitCode -ne 0) { throw "Deleting the tenants failed (exit code $exitCode): nothing was deleted.  It said: $($said -join ' ')" }
}

if (-not $Production) {
    Invoke-Delete -ConnectionString $Connection
    if (-not $DryRun) { Write-Host "`nDone.  Start the ledger again (.\tools\Start-LocalLedger.ps1), then load: .\tools\Set-Joe10.ps1 -Account, then -Holdings" -ForegroundColor Green }
    return
}

# --- Production ---
$settingsPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TangentAtlas\ledger-client.json'
if (-not (Test-Path $settingsPath)) { throw "$settingsPath is missing: run TangentAtlas.Platform's Deploy-EntraApplications.ps1 (it writes it)." }
$server = (Get-Content $settingsPath -Raw | ConvertFrom-Json).server.TrimEnd('/')
$appName = ([Uri]$server).Host.Split('.')[0]
if (-not (Get-AzContext)) { Connect-AzAccount | Out-Null }
$app = Get-AzWebApp | Where-Object { $_.Name -eq $appName } | Select-Object -First 1
if (-not $app) { throw "No App Service named '$appName' (from $server) in this Azure subscription: Set-AzContext to the Tangent Atlas subscription." }
$app = Get-AzWebApp -ResourceGroupName $app.ResourceGroup -Name $app.Name
$setting = $app.SiteConfig.AppSettings | Where-Object { $_.Name -eq 'ConnectionStrings__Ledger' } | Select-Object -First 1
if (-not $setting) { throw "$appName has no ConnectionStrings__Ledger setting." }

# The app's connection string, signed in as you instead of as the app's managed identity.  Parsed by hand: PowerShell
# treats a DbConnectionStringBuilder as a dictionary, so setting its ConnectionString adds an entry instead of parsing.
$parts = [ordered]@{}   # keys compare without case
foreach ($pair in $setting.Value -split ';') {
    if ($pair -match '^\s*([^=]+?)\s*=\s*(.*?)\s*$') { $parts[$Matches[1]] = $Matches[2] }
}
foreach ($key in 'Authentication', 'User ID', 'UserID', 'UID') { $parts.Remove($key) }
$parts['Authentication'] = 'Active Directory Default'
# Azure SQL's free offer pauses an idle database; resuming takes up to a minute, longer than the default 15 seconds.
$parts['Connect Timeout'] = '120'
$databaseConnection = ($parts.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ';'
$sqlAddress = if ($parts.Contains('Server')) { $parts['Server'] } elseif ($parts.Contains('Data Source')) { $parts['Data Source'] } else { '' }
$databaseName = if ($parts.Contains('Database')) { $parts['Database'] } elseif ($parts.Contains('Initial Catalog')) { $parts['Initial Catalog'] } else { '' }
$sqlServerName = ($sqlAddress -replace '^tcp:', '' -replace ',\d+$', '').Split('.')[0]
if (-not $sqlServerName -or -not $databaseName) { throw "Couldn't read the SQL server and database from $appName's ConnectionStrings__Ledger setting." }
Write-Host "The ledger: $server (App Service $appName in $($app.ResourceGroup)); database $databaseName on $sqlServerName" -ForegroundColor Cyan

# This computer's address through the SQL server's firewall, as TangentAtlas.Platform's Set-SqlClientFirewallRule does.
$sqlServer = Get-AzSqlServer | Where-Object { $_.ServerName -eq $sqlServerName } | Select-Object -First 1
if (-not $sqlServer) { throw "No SQL server named '$sqlServerName' in this subscription." }
$myIp = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 10).Trim()
$ruleName = "Deployer-$env:COMPUTERNAME"
$rule = Get-AzSqlServerFirewallRule -ResourceGroupName $sqlServer.ResourceGroupName -ServerName $sqlServerName -FirewallRuleName $ruleName -ErrorAction SilentlyContinue
if (-not $rule -or $rule.StartIpAddress -ne $myIp) {
    Write-Host "   Letting this computer ($myIp) through the SQL server's firewall ('$ruleName')..."
    if ($rule) {
        Set-AzSqlServerFirewallRule -ResourceGroupName $sqlServer.ResourceGroupName -ServerName $sqlServerName -FirewallRuleName $ruleName -StartIpAddress $myIp -EndIpAddress $myIp | Out-Null
    } else {
        New-AzSqlServerFirewallRule -ResourceGroupName $sqlServer.ResourceGroupName -ServerName $sqlServerName -FirewallRuleName $ruleName -StartIpAddress $myIp -EndIpAddress $myIp | Out-Null
    }
    Start-Sleep -Seconds 20
}

# Count first, with the ledger still running: it shows what will go, and wakes a paused database before the ledger stops.
Write-Host "`n== What would be deleted (the ledger is still running)" -ForegroundColor Cyan
Invoke-Delete -ConnectionString $databaseConnection -Count
if ($DryRun) {
    return
}

function Test-Ledger {
    try { (Invoke-WebRequest -Uri "$server/version" -TimeoutSec 10 -UseBasicParsing).StatusCode -eq 200 } catch { $false }
}

# Waits until the ledger answers (or, stopping, has stopped answering: three checks in a row, 5 seconds apart, since an App
# Service being stopped can miss one answer and then answer again while it shuts down).
function Wait-Ledger {
    param([Parameter(Mandatory)][bool]$Answering, [Parameter(Mandatory)][int]$Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    $needed = if ($Answering) { 1 } else { 3 }
    $inARow = 0
    while ($true) {
        $inARow = if ((Test-Ledger) -eq $Answering) { $inARow + 1 } else { 0 }
        if ($inARow -ge $needed) { return $true }
        if ((Get-Date) -gt $deadline) { return $false }
        Start-Sleep -Seconds 5
    }
}

$what = "Stop the production ledger ($appName), delete $which from $databaseName, and start it again"
if (-not $PSCmdlet.ShouldContinue("$what?  It is down for a few minutes.", 'Reset tenants in production')) {
    Write-Host 'Nothing done.'
    return
}

Write-Host "`n== Stopping the ledger" -ForegroundColor Cyan
Stop-AzWebApp -ResourceGroupName $app.ResourceGroup -Name $app.Name | Out-Null
try {
    if (-not (Wait-Ledger -Answering $false -Seconds 180)) { throw 'The ledger still answers three minutes after stopping: nothing was deleted.' }
    Write-Host "`n== Deleting the tenants" -ForegroundColor Cyan
    for ($attempt = 1; ; $attempt++) {
        try {
            Invoke-Delete -ConnectionString $databaseConnection -Server $server
            break
        } catch {
            # The tool refuses while the ledger answers: still shutting down.  Wait for it, then try again.
            if ($attempt -ge 6 -or "$_" -notmatch 'A ledger answers at') { throw }
            Write-Host "   The ledger is still shutting down; trying again in 15 seconds (attempt $attempt of 6)."
            Start-Sleep -Seconds 15
        }
    }
} finally {
    Write-Host "`n== Starting the ledger (it loads every tenant first: a few minutes)" -ForegroundColor Cyan
    Start-AzWebApp -ResourceGroupName $app.ResourceGroup -Name $app.Name | Out-Null
    if (Wait-Ledger -Answering $true -Seconds 600) {
        Write-Host '   The ledger answers again.' -ForegroundColor Green
    } else {
        Write-Warning "The ledger hasn't answered within ten minutes: check the App Service's log stream in the portal."
    }
}

Write-Host "`nDone.  The service switches are back to their start-up settings.  Reload the dashboard, then load: .\tools\Set-Joe10.ps1 -Production -Account, then -Holdings" -ForegroundColor Green
