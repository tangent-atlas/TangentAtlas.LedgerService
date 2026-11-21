<#
.SYNOPSIS
    Loads the Joe 10 paper account into a tenant of the ledger (the one on this computer, or with -Production the one in
    Azure) and makes the test person a user of it, so the website shows it live when they sign in.

.DESCRIPTION
    Against the server Start-LocalLedger.ps1 runs (its settings, %APPDATA%\TangentAtlas\ledger-client.local.json), or
    with -Production the server in Azure (%APPDATA%\TangentAtlas\ledger-client.json, which TangentAtlas.Platform's
    Deploy-EntraApplications.ps1 writes; the tools sign in with their certificate, from this computer's address):
      1. test-data joe10 writes the Joe 10's scripts (from -StatePath, the forward test's state, or a generic Joe 10).
      2. access-test whoami signs the test person in (a browser opens the first time: sign in as the test person,
         ledger-tester@tangentatlas.com); script-loader makes them a user of the tenant, first, so a page they have open
         shows the account as it loads (within the website's tenant refresh, ten seconds).
      3. The account (-Account): script-loader loads the reference (the household, the Joe 10 portfolio with its
         model, the account at the simulated broker in that portfolio, and the assets), stars the portfolio for the
         test person (the first portfolio added is a favorite by default), the jobs (orders at 3:45 pm New York on market days) and the model's weights.  The account
         appears at $0.
      4. The holdings (-Holdings): script-loader loads the history (the deposit, then the purchases at the state's
         close), one transaction every -PauseSeconds, so a page open on the account shows each arrive.
    Without -Account or -Holdings, it does both, one after the other.  Separately, the account can be opened in the
    website before its holdings load:
      .\tools\Set-Joe10.ps1 -Production -Account      (then open the Joe 10 in the website)
      .\tools\Set-Joe10.ps1 -Production -Holdings
    The account is created in an empty tenant: if the tenant already has events, it stops and says so.  To load it again
    and watch it arrive, empty the tenant first with .\tools\Reset-Tenant.ps1 (in the database, with the ledger
    stopped: the event log is append-only).  The holdings need the account; loading them twice changes nothing.

.PARAMETER TenantId
    The tenant (default: the Joe 10's own, the same every time).

.PARAMETER StatePath
    The Joe 10's state: by default the forward test's, state\joe10.json in TangentAtlas.ForwardTest beside this
    repository (repos\tangent-atlas\TangentAtlas.ForwardTest).  The scripts are written to a temporary folder and
    deleted afterwards.

.PARAMETER Generic
    Load the generic Joe 10 instead (bought at $100 a share: real closes value it as a large gain or loss).

.PARAMETER Account
    Only create the household, the Joe 10 portfolio and its account (with the model, assets, jobs and weights), empty.

.PARAMETER Holdings
    Only load the holdings (the deposit and the purchases) into the account already created.

.PARAMETER PauseSeconds
    How long to wait between the history's transactions, to watch each arrive in the website (default 1; 0 loads them
    at once).

.PARAMETER NoBuild
    Use the programs as last built.

.PARAMETER Production
    Load it into the ledger in Azure instead of the one on this computer.

.PARAMETER SignOut
    Forget access-test's remembered sign-in first, so the browser opens and you can sign in as the test person.

.EXAMPLE
    .\tools\Set-Joe10.ps1 -SignOut

.EXAMPLE
    .\tools\Set-Joe10.ps1 -Production

.EXAMPLE
    .\tools\Reset-Tenant.ps1 -Production; .\tools\Set-Joe10.ps1 -Production -Account; .\tools\Set-Joe10.ps1 -Production -Holdings
    Empty the Joe 10's tenant, create the account, then (with the account open in the website) load its holdings.
#>
[CmdletBinding()]
param(
    [guid]$TenantId = '4a6f6531-3020-7061-7065-720000000001',
    [string]$StatePath = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'TangentAtlas.ForwardTest\state\joe10.json'),
    [switch]$Generic,
    [switch]$Account,
    [switch]$Holdings,
    [ValidateRange(0, 60)][double]$PauseSeconds = 1,
    [switch]$NoBuild,
    [switch]$Production,
    [switch]$SignOut
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$settingsPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "TangentAtlas\$(if ($Production) { 'ledger-client.json' } else { 'ledger-client.local.json' })"
if (-not (Test-Path $settingsPath)) {
    throw "$settingsPath is missing: $(if ($Production) { "run TangentAtlas.Platform's Deploy-EntraApplications.ps1 (it writes it)" } else { 'run .\tools\Start-LocalLedger.ps1 first (it writes it)' })."
}
$server = (Get-Content $settingsPath -Raw | ConvertFrom-Json).server
Write-Host "The ledger: $server" -ForegroundColor Cyan
$env:ATLAS_LEDGER_CLIENT_SETTINGS = $settingsPath

$programs = @{
    TestData = @{ Project = 'AtlasLedger.TestData'; Assembly = 'test-data' }
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

# The test person's user row in a tenant: its key derived from who they are, so making them a user twice is one row.
function Get-UserId {
    param([Parameter(Mandatory)][string]$Issuer, [Parameter(Mandatory)][string]$Subject)
    $hash = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes("tangent-atlas/user/$Issuer/$Subject"))
    $hash[6] = ($hash[6] -band 0x0F) -bor 0x80; $hash[8] = ($hash[8] -band 0x3F) -bor 0x80
    $hex = -join ($hash[0..15] | ForEach-Object { $_.ToString('x2') })
    [guid]"$($hex.Substring(0, 8))-$($hex.Substring(8, 4))-$($hex.Substring(12, 4))-$($hex.Substring(16, 4))-$($hex.Substring(20, 12))"
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

    $both = -not $Account -and -not $Holdings
    if ($Account -or $both) {
        Write-Host "`n== Tenant $TenantId" -ForegroundColor Cyan
        # A new tenant answers 404 here, which is what we want: its message isn't shown.
        $existing = (& dotnet $programs.Operator.Path events "$TenantId" --json --last 1 2>$null) -join ''
        if ($LASTEXITCODE -eq 0 -and $existing.Trim()) {
            throw "Tenant $TenantId already has events: it is loaded.  Empty it first: .\tools\Reset-Tenant.ps1 -TenantId $TenantId$(if ($Production) { ' -Production' })"
        }
        Write-Host "`n== The test person signs in; they become a user of the tenant" -ForegroundColor Cyan
        if ($SignOut) { Invoke-Program $programs.AccessTest @('sign-out') }
        $identity = (& dotnet $programs.AccessTest.Path whoami) -join '' | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $identity.subject) { throw 'access-test whoami failed.' }
        Write-Host "   Signed in: $($identity.subject) ($($identity.issuer))"
        $member = Join-Path $work 'member.json'
        @{ operations = @(@{ action = 'Insert'; table = 'User'; row = @{ userId = (Get-UserId $identity.issuer $identity.subject); identityIssuer = $identity.issuer; identitySubject = $identity.subject } }) } |
            ConvertTo-Json -Depth 5 | Set-Content -Path $member -Encoding utf8
        Invoke-Program $programs.Loader @("$TenantId", $member)
        Write-Host "`n== The account at the simulated broker" -ForegroundColor Cyan
        Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'joe10-reference.json'))

        # The first portfolio added is starred by default: the Joe 10 is a favorite of the test person.
        $reference = Get-Content (Join-Path $work 'joe10-reference.json') -Raw | ConvertFrom-Json
        $portfolioId = ($reference.operations | Where-Object { $_.table -eq 'Portfolio' } | Select-Object -First 1).row.portfolioId
        $favorite = Join-Path $work 'favorite.json'
        @{ operations = @(@{ action = 'Insert'; table = 'Favorite'; row = @{ userId = (Get-UserId $identity.issuer $identity.subject); portfolioId = $portfolioId; position = 0 } }) } |
            ConvertTo-Json -Depth 5 | Set-Content -Path $favorite -Encoding utf8
        Invoke-Program $programs.Loader @("$TenantId", $favorite)
        Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'joe10-jobs.json'))
        Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'joe10-weights.json'))
        Write-Host "`nThe Joe 10's account is in tenant $TenantId, empty, and $($identity.subject) is its user." -ForegroundColor Green
    }

    if ($Holdings -or $both) {
        if ($Holdings) {
            # The holdings go into the account already created: read as the operator.
            $export = Join-Path $work 'tenant.json'
            & dotnet $programs.Operator.Path export "$TenantId" $export 2>$null | Out-Null
            $accounts = if ($LASTEXITCODE -eq 0) { @((Get-Content $export -Raw | ConvertFrom-Json).operations | Where-Object { $_.table -eq 'Account' }) } else { @() }
            if ($accounts.Count -eq 0) {
                throw "Tenant $TenantId has no account yet: create it first with .\tools\Set-Joe10.ps1$(if ($Production) { ' -Production' }) -Account"
            }
        } else {
            Start-Sleep -Seconds $PauseSeconds
        }

        Write-Host "`n== The holdings: the deposit, then each purchase" -ForegroundColor Cyan
        Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'joe10-history.json'), '--pause', $PauseSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
        Write-Host "`nThe Joe 10's holdings are loaded into tenant $TenantId." -ForegroundColor Green
    }
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
