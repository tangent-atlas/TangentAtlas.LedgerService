<#
.SYNOPSIS
    Loads the Joe 10 paper account into a tenant of the ledger (the one on this computer, or with -Production the one in
    Azure) and makes the test person a user of it, so the website shows it live when they sign in.

.DESCRIPTION
    Against the server Start-LocalLedger.ps1 runs (its settings, %APPDATA%\TangentAtlas\ledger-client.local.json), or
    with -Production the server in Azure (%APPDATA%\TangentAtlas\ledger-client.json, which TangentAtlas.Platform's
    Deploy-EntraApplications.ps1 writes; the tools sign in with their certificate, from this computer's address):
      1. test-data joe10 writes the household's scripts (from -StatePath, the forward test's state, or a generic Joe 10).
         The forward test's clone beside this repository is updated first (git pull), and the state must be as of the
         latest close EODHD has: a state older than that stops the script, since it would load last week's holdings.
         With the state, it also reads SPY's last closes from EODHD (the token as Start-LocalLedger.ps1 keeps it: the
         EODHD_API_TOKEN environment variable, or %APPDATA%\TangentAtlas\eodhd-token.xml, encrypted for you), which set
         the SPY Benchmark's share in SPY by the Joe 10's thermostat.  The licensed closes stay in the temporary folder.
      2. access-test whoami signs the test person in (a browser opens the first time: sign in as the test person,
         ledger-tester@tangentatlas.com); script-loader makes them a user of the tenant, first, so a page they have open
         shows the account as it loads (within the website's tenant refresh, ten seconds).
      3. The accounts (-Account): script-loader loads the reference (the household; the Joe 10 portfolio with its model
         and its Play Money account at the simulated broker, Play Money Brokerage; the assets), then the rest of the
         household (in the Joe 10 portfolio a Bank of America checking account and a Mastercard; beside it the SPY
         Benchmark portfolio with its model and its own Play Money account), stars the Joe 10 and the SPY Benchmark for
         the test person, the jobs (orders at 3:45 pm New York on market days) and both
         models' weights.  The accounts appear at $0.
      4. The holdings (-Holdings): script-loader loads the history (the deposit, then the purchases at the state's
         close), then the household's (the checking and card balances, the benchmark's deposit and its SPY), one
         transaction every -PauseSeconds, so a page open on the account shows each arrive.
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
    repository (repos\tangent-atlas\TangentAtlas.ForwardTest), brought up to date with git pull before it is read.
    Whichever state is used, it must be as of the latest close EODHD has.  The scripts are written to a temporary
    folder and deleted afterwards.

.PARAMETER Generic
    Load the generic Joe 10 instead (bought at $100 a share: real closes value it as a large gain or loss).

.PARAMETER Account
    Only create the household, the Joe 10 portfolio and its account (with the model, assets, jobs and weights), empty.

.PARAMETER Holdings
    Only load the holdings (the deposit and the purchases) into the account already created.

.PARAMETER Favorites
    Only star the Joe 10 and the SPY Benchmark for the test person, in a tenant already loaded (starring twice changes
    nothing).

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
    [switch]$Favorites,
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
try {
    Invoke-WebRequest -Uri ($server.TrimEnd('/') + '/health') -TimeoutSec 10 -UseBasicParsing | Out-Null
} catch {
    throw "The ledger at $server doesn't answer: start it first ($(if ($Production) { 'check the App Service' } else { 'F5 on AtlasLedger.Server in Visual Studio, or .\tools\Start-LocalLedger.ps1' }))."
}
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

# The test person's favorites: the Joe 10 first, then the SPY Benchmark (the portfolios in the two references, in order).
function Add-Favorites {
    param([Parameter(Mandatory)]$Identity)
    $userId = Get-UserId $Identity.issuer $Identity.subject
    $portfolioIds = foreach ($name in 'joe10-reference.json', 'household-reference.json') {
        ((Get-Content (Join-Path $work $name) -Raw | ConvertFrom-Json).operations | Where-Object { $_.table -eq 'Portfolio' }).row.portfolioId
    }
    $position = 0
    $operations = @(foreach ($portfolioId in $portfolioIds) { @{ action = 'Insert'; table = 'Favorite'; row = @{ userId = $userId; portfolioId = $portfolioId; position = $position++ } } })
    $favorite = Join-Path $work 'favorite.json'
    @{ operations = $operations } | ConvertTo-Json -Depth 5 | Set-Content -Path $favorite -Encoding utf8
    Invoke-Program $programs.Loader @("$TenantId", $favorite)
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
        # The forward test commits its state after each close; a clone that isn't pulled holds an old one.
        $forwardTest = Split-Path (Split-Path (Resolve-Path $StatePath).Path -Parent) -Parent
        if (-not $PSBoundParameters.ContainsKey('StatePath') -and (Test-Path (Join-Path $forwardTest '.git'))) {
            Write-Host "   Updating the forward test's clone: $forwardTest"
            & git -C $forwardTest pull --ff-only --quiet
            if ($LASTEXITCODE -ne 0) { Write-Warning "git pull in $forwardTest failed; checking the state's date against EODHD instead." }
        }
        $testDataArguments += @('--state', (Resolve-Path $StatePath).Path)

        # SPY's closes for the benchmark's thermostat: licensed, so fetched here, kept in the temporary folder only.
        $tokenPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TangentAtlas\eodhd-token.xml'
        if ($env:EODHD_API_TOKEN) {
            $token = $env:EODHD_API_TOKEN
        } else {
            if (-not (Test-Path $tokenPath)) {
                New-Item -ItemType Directory -Force -Path (Split-Path $tokenPath) | Out-Null
                Read-Host -AsSecureString 'EODHD API token (typed hidden; kept encrypted for you on this computer)' | Export-Clixml -Path $tokenPath
            }
            $token = [Net.NetworkCredential]::new('', (Import-Clixml -Path $tokenPath)).Password
        }
        if (-not $token) { throw "The EODHD token is empty: delete $tokenPath and run again, or use -Generic." }
        $stateDate = (Get-Content $StatePath -Raw | ConvertFrom-Json).last_date
        $lastDate = [datetime]::ParseExact($stateDate, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        $from = $lastDate.AddDays(-50).ToString('yyyy-MM-dd')
        $today = (Get-Date).ToString('yyyy-MM-dd')
        $spyCloses = Join-Path $work 'spy-closes.json'
        try {
            # Up to today, not to the state's date, so a newer close shows the state is stale (test-data reads only the
            # closes up to the state's date).
            Invoke-WebRequest -Uri "https://eodhd.com/api/eod/SPY.US?from=$from&to=$today&period=d&fmt=json&api_token=$token" -OutFile $spyCloses -UseBasicParsing | Out-Null
        } catch {
            throw "Couldn't read SPY's closes from EODHD ($($_.Exception.Message)).  Check the token (delete $tokenPath to enter it again), or use -Generic."
        }
        $latestClose = @(Get-Content $spyCloses -Raw | ConvertFrom-Json) | ForEach-Object { $_.date } | Sort-Object | Select-Object -Last 1
        if (-not $latestClose) { throw "EODHD returned no closes for SPY from $from to $today." }
        if ($latestClose -gt $stateDate) {
            throw "The Joe 10's state ($StatePath) is as of the close of $stateDate, but EODHD has closes to ${latestClose}: loading it would put that day's holdings and prices in the ledger.  Pull TangentAtlas.ForwardTest (git pull), or wait for the forward test's daily run (it commits the morning after each close), then run this again."
        }
        Write-Host "   The state is as of the close of $stateDate, the latest EODHD has."
        $testDataArguments += @('--spy', $spyCloses)
    }
    Invoke-Program $programs.TestData $testDataArguments

    if ($Favorites) {
        Write-Host "`n== The test person's favorites: the Joe 10 and the SPY Benchmark" -ForegroundColor Cyan
        $identity = (& dotnet $programs.AccessTest.Path whoami) -join '' | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $identity.subject) { throw 'access-test whoami failed.' }
        Add-Favorites $identity
        Write-Host "`nStarred for $($identity.subject)." -ForegroundColor Green
        return
    }

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
        Write-Host "`n== The rest of the household: checking and a Mastercard in the Joe 10; the SPY Benchmark beside it" -ForegroundColor Cyan
        Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'household-reference.json'))

        Add-Favorites $identity
        Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'joe10-jobs.json'))
        Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'joe10-weights.json'))
        Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'household-weights.json'))
        Write-Host "`nThe household's accounts are in tenant $TenantId, empty, and $($identity.subject) is their user." -ForegroundColor Green
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
        Write-Host "`n== The household: checking and card balances, then the SPY Benchmark's deposit and its SPY" -ForegroundColor Cyan
        Invoke-Program $programs.Loader @("$TenantId", (Join-Path $work 'household-history.json'), '--pause', $PauseSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
        Write-Host "`nThe household's holdings are loaded into tenant $TenantId." -ForegroundColor Green
    }
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
