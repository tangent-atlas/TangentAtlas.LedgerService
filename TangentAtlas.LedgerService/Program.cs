// <copyright file="Program.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
using System.Threading.RateLimiting;
using Asp.Versioning;
using AtlasLedger.Model;
using AtlasLedger.Model.Master;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using TangentAtlas.LedgerService.Middleware;
using TangentAtlas.LedgerService.Security;

// Application Builder
var webApplicationBuilder = WebApplication.CreateBuilder(args);

// Authentication
webApplicationBuilder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(webApplicationBuilder.Configuration.GetSection("AzureAdB2C"));

// Controllers - AddLedgerControllers() registers the generated (and
// AccountsController's hand-written) controllers from the AtlasLedger.Model
// package.
webApplicationBuilder.Services
    .AddControllers().
    AddLedgerControllers();

// API Configuration
webApplicationBuilder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
    })
    .AddApiExplorer(options =>
    {
        // 'v'VVV => group name "v1", which yields the document at /openapi/v1.json.
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    })
    .AddMvc()
    .AddOpenApi(options =>
    {
        // Configure the documents that describe the API.
        options.Document.AddDocumentTransformer((document, context, cancellationToken) =>
        {
            const string schemeId = "Bearer";
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[schemeId] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Name = "Authorization",
                Description = "Enter your Azure AD B2C bearer token.",
            };

            document.Security ??= [];
            document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(schemeId, document)] = [] });
            return Task.CompletedTask;
        });
    });

// Rate Limiting
webApplicationBuilder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("fixed", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 10;
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// CORS
webApplicationBuilder.Services.AddCors(options =>
{
    options.AddPolicy("AtlasLedgerPortal", policy =>
    {
        policy
            .WithOrigins(webApplicationBuilder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
                ?? Array.Empty<string>())
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// The Ledger Engine.
webApplicationBuilder.Services.AddSingleton<Ledger>();

// Add Entity Framework DbContext. Connection string name "Ledger" is set by
// Deploy-AtlasLedgerApi.ps1 as an Azure App Service connection string, using
// "Authentication=Active Directory Managed Identity" - no password anywhere.
// Locally (Development), appsettings.Development.json uses
// "Authentication=Active Directory Default" instead (your own signed-in
// Entra identity via DefaultAzureCredential).
webApplicationBuilder.Services
    .AddDbContext<LedgerContext>(options => options.UseSqlServer(webApplicationBuilder.Configuration.GetConnectionString("Ledger")));

// Tenant Authorization - ITenantContext/ITenantMembership are defined in the
// public AtlasLedger.Model package; TenantContext (the concrete
// implementation that reads live HTTP claims) is deliberately private here,
// not in the public package, since it's specific to how THIS deployment's
// auth is wired.
webApplicationBuilder.Services
    .AddHttpContextAccessor()
    .AddSingleton<ITenantMembership, TenantMembership>()
    .AddScoped<ITenantContext, TenantContext>();

// Health checks - for Azure App Service / load balancer probes, separate
// from the ledger's own API surface below.
webApplicationBuilder.Services.AddHealthChecks();

// Build the application.
var webApplication = webApplicationBuilder.Build();

// Load the data model from the database at startup.
using (var serviceScope = webApplication.Services.CreateScope())
{
    var ledger = serviceScope.ServiceProvider.GetRequiredService<Ledger>();
    var ledgerContext = serviceScope.ServiceProvider.GetRequiredService<LedgerContext>();
    await ledgerContext.Database.EnsureCreatedAsync();
    await ledgerContext.LoadAsync(ledger);
}

// Prime the tenant membership map
_ = webApplication.Services.GetRequiredService<ITenantMembership>();

// Pipeline
// Error handling is registered FIRST so it wraps everything downstream — including
// auth, rate limiting, and CORS — not just endpoint execution.
webApplication.UseMiddleware<ErrorHandlingMiddleware>();

if (webApplication.Environment.IsDevelopment())
{
    // Native OpenAPI: one document per discovered API version, e.g. /openapi/v1.json.
    webApplication.MapOpenApi().WithDocumentPerVersion();

    // Optional dev-only UI (requires the Scalar.AspNetCore package). Visit /scalar.
    webApplication.MapScalarApiReference(options =>
    {
        var descriptions = webApplication.DescribeApiVersions();
        for (var i = 0; i < descriptions.Count; i++)
        {
            var description = descriptions[i];
            options.AddDocument(
                description.GroupName,
                description.GroupName.ToUpperInvariant(),
                isDefault: i == descriptions.Count - 1);
        }
    });
}

webApplication.UseHttpsRedirection();
webApplication.UseCors("AtlasLedgerPortal");
webApplication.UseRateLimiter();
webApplication.UseAuthentication();
webApplication.UseAuthorization();

// Health endpoint - unauthenticated, unversioned, for infrastructure probes only.
webApplication.MapHealthChecks("/health");

// Apply the "fixed" window policy to the API surface.
webApplication.MapControllers().RequireRateLimiting("fixed");

// Run the application.
webApplication.Run();
