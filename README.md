# TangentAtlas.LedgerService

The private, proprietary REST API host that exposes the open-source
`AtlasLedger.Model` ledger engine to the public internet. This repo owns:

- Deployment (this is what `Deploy-AtlasLedgerApi.ps1` provisions in Azure -
  `website-tangentatlas-ledger-staging` / `-production`)
- Auth wiring specific to this deployment (`AzureAdB2C` config, JWT bearer
  setup) and `TenantContext` - the concrete implementation of the public
  `ITenantContext` interface that reads live HTTP claims. The interface
  itself, and the tenant-membership map it depends on, live in the public
  package; only the "how do we resolve a specific request's identity"
  piece is private.
- CORS, rate limiting, error-handling middleware, and everything else in
  `Program.cs`'s hosting pipeline
- Anything touching real customer data, brokerage credentials, or billing

It deliberately owns **none** of the ledger engine itself, the data model,
or the generated controllers - those live in the public `atlas-ledger` repo
(MIT), as the `AtlasLedger.Model` package.

## Wiring up AtlasLedger.Model

`TangentAtlas.LedgerService.csproj` currently references `AtlasLedger.Model`
via a **temporary** relative `ProjectReference`, guarded by
`Condition="Exists(...)"` so this builds standalone even before that path
exists locally:

```xml
<ProjectReference Include="..\..\atlas-ledger\AtlasLedger.Model\AtlasLedger.Model.csproj"
                   Condition="Exists('..\..\atlas-ledger\AtlasLedger.Model\AtlasLedger.Model.csproj')" />
```

Once `AtlasLedger.Model` has shipped its first NuGet release (the
`atlas-ledger` repo's `publish-nuget.yml` fires on a published GitHub
Release), replace that with a normal, version-pinned package reference:

```xml
<PackageReference Include="AtlasLedger.Model" Version="0.1.0" />
```

Pin the version deliberately and bump it on purpose after testing - don't
float `latest`, even though it's your own package.

## Connection string

`ConnectionStrings:Ledger` has no password anywhere, in any environment
(name matches what `Program.cs` actually reads - confirmed against the real
codebase, not a placeholder):

- **Staging/Production**: set by `Deploy-AtlasLedgerApi.ps1` as an Azure App
  Service connection string, using `Authentication=Active Directory Managed
  Identity` - the Web App authenticates as its own system-assigned identity.
- **Development** (`appsettings.Development.json`): uses
  `Authentication=Active Directory Default` instead, which authenticates as
  *your* signed-in Entra identity (`az login` / Visual Studio) via the
  `DefaultAzureCredential` chain - the same server-admin access
  `Deploy-SqlServer.ps1` already granted you. No separate grant needed.

## Known issue carried over from the original codebase

`Security/TenantContext.cs`'s `ResolveUserId` currently always returns
`Guid.Empty`, regardless of the actual signed-in principal - every request
resolves to "unknown user." This fails closed (nobody sees any tenant's
data - not a leak) but also means no authenticated user can see their own
data yet. `SubjectClaimTypes` is already defined in that file but never
consulted. Flagged, not silently fixed - this is security-relevant logic
worth reviewing deliberately before wiring up the real claim lookup.

## CI/CD - one thing to fix before either workflow can run

Both `.github/workflows/deploy-*.yml` files log in via OIDC using a
managed identity that `Deploy-ManagedIdentities.ps1` already created and
now correctly points at this repo (`TangentAtlas.LedgerService`, not
`atlas-ledger`) -
`identity-tangentatlas-ledgerdeploy-staging` / `-production`.

Set these as **Variables** (not secrets) under this repo's Settings ->
Environments -> staging / production, using the values
`Deploy-ManagedIdentities.ps1` printed:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

Also add a **required reviewer** on the `production` environment
(Settings -> Environments -> production) - that's the actual approval gate
for `deploy-production.yml`, not anything in the workflow file itself.

## Local development

```powershell
dotnet run --project TangentAtlas.LedgerService
```

Then hit `https://localhost:5081/health` (infra probe, no auth) or
`https://localhost:5081/scalar` (interactive API docs, requires a B2C
bearer token for anything under `/api/v1`).
