<#
.SYNOPSIS
    Clears the load test's tenants out of the ledger's database on this computer, after a load test (ledger-load load).

.DESCRIPTION
    ledger-load clear-database finds every tenant ledger-load made (their keys are derived from their index, so any
    range it was run with is found) and deletes all of their rows, in every table with a TenantId (Bellerophon's
    watermark among them), in one transaction: all of it or none.  Other tenants (the Joe 10, the walkthrough's, any
    you loaded by hand) are not touched.  -All deletes every tenant instead: an empty ledger, the schema kept.

    It works on the database, not through the ledger: the event log is append-only, so no API deletes a tenant's
    history.  So the ledger must be stopped first (Ctrl+C in Start-LocalLedger.ps1's window); it refuses while a ledger
    answers at 127.0.0.1:5180 or :5100 (or -Server).  Start the ledger again afterwards and it loads what is left.

.PARAMETER Connection
    The database (default: Start-LocalLedger.ps1's, the local default instance's AtlasLedger, signed in as you).

.PARAMETER Server
    A ledger address to check is stopped, if not 127.0.0.1:5180 and :5100.

.PARAMETER All
    Delete every tenant, not only the load test's.  Asks first.

.PARAMETER DryRun
    Count what would be deleted, table by table, and delete nothing.

.PARAMETER NoBuild
    Use ledger-load as last built.

.EXAMPLE
    .\tools\Clear-LoadTest.ps1 -DryRun

.EXAMPLE
    .\tools\Clear-LoadTest.ps1
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [string]$Connection = 'Server=localhost;Database=AtlasLedger;Trusted_Connection=True;TrustServerCertificate=True',
    [string]$Server,
    [switch]$All,
    [switch]$DryRun,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repository 'tools/AtlasLedger.LoadTest'
if (-not $NoBuild) {
    dotnet build $project --configuration Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Building ledger-load failed.' }
}

$arguments = @('clear-database', '--connection', $Connection)
if ($Server) { $arguments += @('--server', $Server) }
if ($DryRun) { $arguments += '--dry-run' }
if ($All) {
    if (-not $DryRun -and -not $PSCmdlet.ShouldContinue("Delete EVERY tenant in $Connection (the Joe 10 and any other data too)?", 'Clear the whole ledger database')) {
        Write-Host 'Nothing deleted.'
        return
    }
    $arguments += '--all'
}

& dotnet (Join-Path $project 'bin/Release/net11.0/ledger-load.dll') @arguments
if ($LASTEXITCODE -ne 0) { throw "ledger-load clear-database failed (exit code $LASTEXITCODE)." }
