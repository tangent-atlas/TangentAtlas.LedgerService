<#
.SYNOPSIS
    Loads the Joe 10 paper account into a tenant of the ledger on this computer and makes the test person a user of it,
    so the website shows it live when they sign in.

.DESCRIPTION
    Against the server Start-LocalLedger.ps1 runs (its settings, %APPDATA%\TangentAtlas\ledger-client.local.json):
      1. test-data joe10 writes the Joe 10's scripts (from -StatePath, the forward test's state, or a generic Joe 10).
      2. script-loader loads the reference (the account at the simulated broker, its model and assets), the history
         (the deposit and the purchases at the state's close), the jobs (orders at 3:45 pm New York on market days) and
         the model's weights.
      3. access-test whoami signs the test person in (a browser opens the first time: sign in as the test person,
         ledger-tester@tangentatlas.com); script-loader makes them a user of the tenant.
    It loads a new tenant: if the tenant already has events, it stops and says so (name another with -TenantId, or
    start from an empty database).

.PARAMETER TenantId
    The tenant (default: the Joe 10's own, the same every time).

.PARAMETER StatePath
    The Joe 10's state: by default the forward test's, state\joe10.json in TangentAtlas.ForwardTest beside this
    repository (repos\tangent-atlas\TangentAtlas.ForwardTest).  The scripts are written to a temporary folder and
    deleted afterwards.

.PARAMETER Generic
    Load the generic Joe 10 instead (bought at $100 a share: real closes value it as a large gain or loss).

.PARAMETER NoBuild
    Use the programs as last built.

.PARAMETER SignOut
    Forget access-test's remembered sign-in first, so the browser opens and you can sign in as the test person.

.EXAMPLE
    .\tools\Set-LocalJoe10.ps1 -SignOut
#>
[CmdletBinding()]
param(
    [guid]$TenantId = '4a6f6531-3020-7061-7065-720000000001',
    [string]$StatePath = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'TangentAtlas.ForwardTest\state\joe10.json'),
    [switch]$Generic,
    [switch]$NoBuild,
    [switch]$SignOut
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$localPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TangentAtlas\ledger-client.local.json'
if (-not (Test-Path $localPath)) {
    throw "$localPath is missing: run .\tools\Start-LocalLedger.ps1 first (it writes it)."
}
$env:ATLAS_LEDGER_CLIENT_SETTINGS = $localPath

$programs = @{
    TestData = @{ Project = 'AtlasLedger.TestData'; Assembly = 'test-data' }
    Loader = @{ Project = 'AtlasLedger.ScriptLoader'; Assembly = 'script-loader' }
    Operator = @{ Project = 'AtlasLedger.Operator'; Assembly = 'ledger-operator' }
    AccessTest = @{ Project = 'AtlasLedger.AccessTest'; Assembly = 'access-test' }
}
foreach ($program in $programs.Values) {
    $program.Path = Join-Path $repository "src/$($program.Project)/bin/Release/net10.0/$($program.Assembly).dll"
    if (-not $NoBuild) {
        dotnet build (Join-Path $repository "src/$($program.Project)") --configuration Release --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "Building $($program.Project) failed." }
    }
}

function Invoke-Program {
    param([Parameter(Mandatory)][hashtable]$Program, [Parameter(Mandatory)][string[]]$Arguments)
    & dotnet $Program.Path @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$($Program.Assembly) $($Arguments -join ' ') failed (exit code $LASTEXITCODE)." }
}

$work = Join-Path ([IO.Path]::GetTempPath()) "joe10-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Path $work | Out-Null
try {
    Write-Host "`n== The Joe 10's scripts" -ForegroundColor Cyan
    $testDataArguments = @('joe10', '--out', $work)
    if (-not $Generic) {
        if (-not (Test-Path $StatePath)) {
            throw "The forward test's state isn't at ${StatePath}: clone tangent-atlas/TangentAtlas.ForwardTest beside this repository, name it with -StatePath, or load the generic Joe 10 with -Generic."
        }
        $testDataArguments += @('--state', (Resolve-Path $StatePath).Path)
    }
    Invoke-Program $programs.TestData $testDataArguments

    Write-Host "`n== Loading tenant $TenantId" -ForegroundColor Cyan
    $existing = (& dotnet $programs.Operator.Path events "$TenantId" --json --last 1) -join ''
    if ($LASTEXITCODE -eq 0 -and $existing.Trim()) {
        throw "Tenant $TenantId already has events: it is loaded.  Name another with -TenantId, or start from an empty database."
    }
    Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'joe10-reference.json'))
    Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'joe10-history.json'))
    Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'joe10-jobs.json'))
    Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'joe10-weights.json'))

    Write-Host "`n== The test person signs in; they become a user of the tenant" -ForegroundColor Cyan
    if ($SignOut) { Invoke-Program $programs.AccessTest @('sign-out') }
    $identity = (& dotnet $programs.AccessTest.Path whoami) -join '' | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $identity.subject) { throw 'access-test whoami failed.' }
    Write-Host "   Signed in: $($identity.subject) ($($identity.issuer))"

    $member = Join-Path $work 'member.json'
    @{ operations = @(@{ action = 'Insert'; table = 'User'; row = @{ userId = [guid]::NewGuid(); identityIssuer = $identity.issuer; identitySubject = $identity.subject } }) } |
        ConvertTo-Json -Depth 5 | Set-Content -Path $member -Encoding utf8
    Invoke-Program $programs.Loader @("$TenantId", $member)

    Write-Host "`nDone: the Joe 10 is tenant $TenantId, and $($identity.subject) is its user.  Sign in to the website as the test person." -ForegroundColor Green
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
