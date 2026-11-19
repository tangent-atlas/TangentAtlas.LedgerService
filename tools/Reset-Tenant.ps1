<#
.SYNOPSIS
    Empties tenants of the ledger in its database, so they can be loaded again from their scripts and watched as they
    load: maintenance, with the ledger stopped.  By default, the Joe 10's tenant.

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

.PARAMETER DryRun
    Count what would be deleted, table by table; delete nothing, stop nothing.

.PARAMETER Connection
    The local database (default: Start-LocalLedger.ps1's).  Not used with -Production.

.PARAMETER NoBuild
    Use ledger-load as last built.

.PARAMETER Production
    Empty the tenants in Azure.

.EXAMPLE
    .\tools\Reset-Tenant.ps1 -Production -DryRun

.EXAMPLE
    .\tools\Reset-Tenant.ps1 -Production; .\tools\Set-Joe10.ps1 -Production
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [guid[]]$TenantId = @('4a6f6531-3020-7061-7065-720000000001'),
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
$tenantArguments = @($TenantId | ForEach-Object { '--tenant', "$_" })

function Invoke-Delete {
    param([Parameter(Mandatory)][string]$ConnectionString, [string]$Server)
    $arguments = @('clear-database', '--connection', $ConnectionString) + $tenantArguments
    if ($Server) { $arguments += @('--server', $Server) }
    if ($DryRun) { $arguments += '--dry-run' }
    & dotnet $tool @arguments
    if ($LASTEXITCODE -ne 0) { throw "Deleting the tenants failed (exit code $LASTEXITCODE): nothing was deleted." }
}

if (-not $Production) {
    Invoke-Delete -ConnectionString $Connection
    if (-not $DryRun) { Write-Host "`nDone.  Start the ledger again (.\tools\Start-LocalLedger.ps1), then load: .\tools\Set-Joe10.ps1" -ForegroundColor Green }
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

if ($DryRun) {
    Invoke-Delete -ConnectionString $databaseConnection
    return
}

function Test-Ledger {
    try { (Invoke-WebRequest -Uri "$server/version" -TimeoutSec 10 -UseBasicParsing).StatusCode -eq 200 } catch { $false }
}

function Wait-Ledger {
    param([Parameter(Mandatory)][bool]$Answering, [Parameter(Mandatory)][int]$Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Test-Ledger) -ne $Answering) {
        if ((Get-Date) -gt $deadline) { return $false }
        Start-Sleep -Seconds 5
    }
    return $true
}

$what = "Stop the production ledger ($appName), delete tenant(s) $($TenantId -join ', ') from $databaseName, and start it again"
if (-not $PSCmdlet.ShouldContinue("$what?  It is down for a few minutes.", 'Reset tenants in production')) {
    Write-Host 'Nothing done.'
    return
}

Write-Host "`n== Stopping the ledger" -ForegroundColor Cyan
Stop-AzWebApp -ResourceGroupName $app.ResourceGroup -Name $app.Name | Out-Null
try {
    if (-not (Wait-Ledger -Answering $false -Seconds 120)) { throw 'The ledger still answers two minutes after stopping: nothing was deleted.' }
    Write-Host "`n== Deleting the tenants" -ForegroundColor Cyan
    Invoke-Delete -ConnectionString $databaseConnection -Server $server
} finally {
    Write-Host "`n== Starting the ledger (it loads every tenant first: a few minutes)" -ForegroundColor Cyan
    Start-AzWebApp -ResourceGroupName $app.ResourceGroup -Name $app.Name | Out-Null
    if (Wait-Ledger -Answering $true -Seconds 600) {
        Write-Host '   The ledger answers again.' -ForegroundColor Green
    } else {
        Write-Warning "The ledger hasn't answered within ten minutes: check the App Service's log stream in the portal."
    }
}

Write-Host "`nDone.  The service switches are back to their start-up settings.  Reload the dashboard, then load: .\tools\Set-Joe10.ps1 -Production" -ForegroundColor Green
