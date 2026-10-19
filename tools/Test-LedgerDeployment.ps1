<#
.SYNOPSIS
    The walkthrough: exercises a running ledger server end to end with its three programs (script-loader,
    ledger-operator, access-test), and reports every step.  Doubles as the smoke test of a deployment.

.DESCRIPTION
    Builds the three programs (unless -NoBuild), then, against the server in ledger-client.json (or -Server):
      1. ledger-operator turns the services on (compliance, market feed) for 15 minutes.
      2. script-loader loads firm A (firm-a.json) and firm B (firm-b.json) into new tenants.
      3. access-test whoami signs the test person in (a browser opens the first time) and reports who they are;
         script-loader makes them a user of firm A.
      4. access-test run checks what the person may and may not do: their firm, 404 for firm B, 403 outside their
         permissions, 401 without signing in, the services hidden from them, and the data rules they meet.
      5. ledger-operator exports firm A; script-loader loads the export into firm C; the two match.
      6. script-loader adds the Jones IRA with no custodian number; ledger-operator waits for compliance to raise
         MissingCustodian; script-loader adds the number; the alert clears.
      7. ledger-operator waits for the market feed's bad print (InvalidPrice) and its correction.
      8. ledger-operator shows its local copy of firm A.
      9. ledger-operator turns the services off (always, even after a failure).
    Each run uses new tenants, so it can run against the same server any number of times.

    Against a server on this computer (no settings file, or -Server naming another server), every program uses
    development sign-in instead, and access-test acts as the development advisor.

.PARAMETER Server
    The server, if not the settings file's.

.PARAMETER MarketWaitSeconds
    How long to wait for the market feed's bad print (default 30; 0 skips step 7).

.PARAMETER NoBuild
    Use the programs as last built.

.EXAMPLE
    .\tools\Test-LedgerDeployment.ps1

.NOTES
    Exit code: the number of steps that failed (0: every step got what it expected).
#>
[CmdletBinding()]
param(
    [string]$Server,
    [ValidateRange(0, 600)][int]$MarketWaitSeconds = 30,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$programs = @{
    Loader = @{ Project = 'AtlasLedger.ScriptLoader'; Assembly = 'script-loader' }
    Operator = @{ Project = 'AtlasLedger.Operator'; Assembly = 'ledger-operator' }
    AccessTest = @{ Project = 'AtlasLedger.AccessTest'; Assembly = 'access-test' }
}
foreach ($program in $programs.Values) {
    $program.Path = Join-Path $repository "src/$($program.Project)/bin/Release/net10.0/$($program.Assembly).dll"
}
$data = Join-Path $repository 'src/AtlasLedger.ScriptLoader/bin/Release/net10.0/Data'
$work = Join-Path ([IO.Path]::GetTempPath()) "ledger-walkthrough-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$serverArguments = if ($Server) { @('--server', $Server) } else { @() }
$script:failures = 0

function Invoke-Program {
    param([Parameter(Mandatory)][hashtable]$Program, [Parameter(Mandatory)][string[]]$Arguments)
    & dotnet $Program.Path @Arguments @serverArguments
}

# Runs one step and counts it as a failure if its last program exits with anything but 0.
function Invoke-Step {
    param([Parameter(Mandatory)][string]$Description, [Parameter(Mandatory)][scriptblock]$Action)
    Write-Host "`n== $Description" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    try {
        & $Action
        if ($LASTEXITCODE -ne 0) { throw "exit code $LASTEXITCODE" }
    } catch {
        $script:failures++
        Write-Host "   FAILED: $_" -ForegroundColor Red
    }
}

# Rows per table in an exported script, leaving out alerts, which the rules raise and clear on their own.
function Get-RowCount {
    param([Parameter(Mandatory)][string]$Path)
    $operations = (Get-Content $Path -Raw | ConvertFrom-Json).operations | Where-Object { $_.table -ne 'Alert' }
    ($operations | Group-Object table | Sort-Object Name | ForEach-Object { "$($_.Name) $($_.Count)" }) -join ', '
}

if (-not $NoBuild) {
    Write-Host 'Building the programs...' -ForegroundColor Cyan
    foreach ($program in $programs.Values) {
        dotnet build (Join-Path $repository "src/$($program.Project)") --configuration Release --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "Building $($program.Project) failed." }
    }
}

New-Item -ItemType Directory -Path $work | Out-Null
$firmA = [guid]::NewGuid(); $firmB = [guid]::NewGuid(); $firmC = [guid]::NewGuid()
Write-Host "Firm A $firmA, firm B $firmB, firm C $firmC."

Invoke-Step 'The operator turns the services on for the walkthrough' { Invoke-Program $programs.Operator @('services', 'on', '--for', '0.25') }
try {
    Invoke-Step 'The script loader loads firm A and firm B' {
        Invoke-Program $programs.Loader @("$firmA", (Join-Path $data 'firm-a.json'))
        if ($LASTEXITCODE -eq 0) { Invoke-Program $programs.Loader @("$firmB", (Join-Path $data 'firm-b.json')) }
    }

    Invoke-Step 'The test person signs in; the script loader makes them a user of firm A' {
        $identity = (Invoke-Program $programs.AccessTest @('whoami')) -join '' | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { return }
        Write-Host "   Signed in: $($identity.subject) ($($identity.issuer))"
        $member = Join-Path $work 'member.json'
        @{ operations = @(@{ action = 'Insert'; table = 'User'; row = @{ userId = [guid]::NewGuid(); identityIssuer = $identity.issuer; identitySubject = $identity.subject } }) } |
            ConvertTo-Json -Depth 5 | Set-Content -Path $member -Encoding utf8
        Invoke-Program $programs.Loader @("$firmA", $member)
    }

    Invoke-Step 'access-test checks what the person may and may not do' { Invoke-Program $programs.AccessTest @('run', '--member', "$firmA", '--other', "$firmB") }

    Invoke-Step 'An export of firm A loads unchanged into firm C' {
        $exportA = Join-Path $work 'firm-a-export.json'; $exportC = Join-Path $work 'firm-c-export.json'
        Invoke-Program $programs.Operator @('export', "$firmA", $exportA)
        if ($LASTEXITCODE -eq 0) { Invoke-Program $programs.Loader @("$firmC", $exportA) }
        if ($LASTEXITCODE -eq 0) { Invoke-Program $programs.Operator @('export', "$firmC", $exportC) }
        if ($LASTEXITCODE -ne 0) { return }
        $countsA = Get-RowCount $exportA; $countsC = Get-RowCount $exportC
        Write-Host "   Firm A: $countsA"; Write-Host "   Firm C: $countsC"
        if ($countsA -ne $countsC) { throw 'firm C does not match firm A' }
    }

    Invoke-Step 'Compliance raises MissingCustodian for the Jones IRA and clears it' {
        Invoke-Program $programs.Loader @("$firmA", (Join-Path $data 'jones-ira.json'))
        if ($LASTEXITCODE -eq 0) { Invoke-Program $programs.Operator @('alerts', "$firmA", '--wait-for', 'MissingCustodian') }
        if ($LASTEXITCODE -eq 0) { Invoke-Program $programs.Loader @("$firmA", (Join-Path $data 'jones-ira-custodian.json')) }
        if ($LASTEXITCODE -eq 0) { Invoke-Program $programs.Operator @('alerts', "$firmA", '--wait-cleared', 'MissingCustodian') }
    }

    if ($MarketWaitSeconds -gt 0) {
        Invoke-Step "The market feed's bad print raises InvalidPrice, and the next tick clears it" {
            Invoke-Program $programs.Operator @('alerts', "$firmA", '--wait-for', 'InvalidPrice', '--timeout', "$MarketWaitSeconds")
            if ($LASTEXITCODE -eq 0) { Invoke-Program $programs.Operator @('alerts', "$firmA", '--wait-cleared', 'InvalidPrice') }
        }
    }

    Invoke-Step "The operator's local copy of firm A" { Invoke-Program $programs.Operator @('mirror', "$firmA") }
} finally {
    Invoke-Step 'The operator turns the services off' { Invoke-Program $programs.Operator @('services', 'off') }
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host 'Every step got what it expected.' -ForegroundColor Green
} else {
    Write-Host "$($script:failures) step(s) did not get what they expected." -ForegroundColor Red
}
exit $script:failures
