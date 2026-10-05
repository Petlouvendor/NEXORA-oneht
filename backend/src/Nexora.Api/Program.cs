using System.Text;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Nexora.Api.HealthChecks;
using Nexora.Api.Middleware;
using Nexora.Api.Security;
using Nexora.Api.Setup;
using Nexora.Application;
using Nexora.Infrastructure;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Setup;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- Installation initiale (résumé fonctionnel §20) : configuration écrite par l'assistant ---
InstallConfiguration.AddInstallFile(builder.Configuration, builder.Environment.ContentRootPath);

// --- Secrets de production (résumé fonctionnel §18, §21) : jamais de repli codé en dur hors dev ---
// Sans base configurée, l'API démarre en mode installation : seul l'assistant répond (503 ailleurs),
// les secrets manquants ne protègent alors aucune donnée ; l'assistant les génère.
if (!SetupState.IsSetupMode(builder.Configuration))
    StartupSecretsGuard.EnsureConfigured(builder.Configuration, builder.Environment);

// --- Journalisation structurée (Phase 3 §19) --------------------------------
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// --- Composition des couches ----------------------------------------------
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton(sp => SetupState.FromConfiguration(
    sp.GetRequiredService<IConfiguration>(), builder.Environment.ContentRootPath));
builder.Services.AddMemoryCache(o => o.SizeLimit = 10_000);   // limite anti-fuite mémoire (REQ-02)
builder.Services.AddProblemDetails();

// --- API : contrôleurs, versioning, OpenAPI ------------------------------
builder.Services.AddControllers();
builder.Services
    .AddApiVersioning(o =>
    {
        o.DefaultApiVersion = new ApiVersion(1, 0);
        o.AssumeDefaultVersionWhenUnspecified = true;
        o.ReportApiVersions = true;
    })
    .AddApiExplorer(o =>
    {
        o.GroupNameFormat = "'v'VVV";
        o.SubstituteApiVersionInUrl = true;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.CustomSchemaIds(OpenApiSchemaId));

// --- Authentification / autorisation (Phase 3 §9-10) --------------------
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(string.IsNullOrEmpty(jwt.SigningKey)
                    ? "dev-only-insecure-key-change-me-0123456789abcdef"
                    : jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder()   // deny-by-default (§10)
        .RequireAuthenticatedUser()
        .Build());

// --- CORS (Phase 3 §13) ------------------------------------------------
const string CorsPolicy = "nexora-web";
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders(CorrelationIdMiddleware.HeaderName)));

// --- Rate limiting des endpoints sensibles (Phase 3 §13) --------------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    // Rafraîchissement (finalisation phase 17) : plus fréquent que la connexion (bureau partagé derrière une même
    // adresse), mais borné — une rafale de rafraîchissements est un signal d'abus.
    options.AddPolicy("refresh", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

// --- Health checks (Phase 3 §19) -------------------------------------
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

// --- Traitements asynchrones du monolithe modulaire (Phase 16 §22) ---
// Inerte tant que Scheduler:Enabled n'est pas positionné (tests, poste de dev).
builder.Services.AddHostedService<Nexora.Api.Scheduling.BackgroundScheduler>();

var app = builder.Build();

var setup = app.Services.GetRequiredService<SetupState>();
if (setup.Required)
    app.Logger.LogWarning(
        "Nexora One n'est pas installé : assistant d'installation ouvert sur /setup. Jeton d'installation : {SetupToken} " +
        "(configuration écrite dans {ConfigPath}).", setup.Token, setup.ConfigPath);

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SetupModeMiddleware>();   // mode installation : assistant seul (§20)
app.UseSerilogRequestLogging();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors(CorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();   // clé d'API → identité machine (Phase 35 §13)
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();
app.UseMiddleware<MaintenanceModeMiddleware>();   // API dégradée en écriture pendant une maintenance (Phase 41 §23-24)

app.MapControllers();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

// Alias versionnés (Phase 37 §46, §56) — liveness / readiness sous /api/v1.
app.MapHealthChecks("/api/v1/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/api/v1/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

app.Run();

public partial class Program
{
    /// <summary>
    /// Identifiant de schéma OpenAPI unique : préfixe par le namespace (hors « Nexora. ») et par
    /// le type déclarant, afin d'éviter les collisions entre types homonymes déclarés dans des
    /// espaces différents (ex. <c>Restaurant</c>/<c>Sales.OrderTenderInput</c>,
    /// <c>Pos</c>/<c>Orders.ReasonBody</c>). Les génériques deviennent <c>…OfArg</c>.
    /// </summary>
    internal static string OpenApiSchemaId(Type t)
    {
        var ns = t.Namespace is { } n && n.StartsWith("Nexora.", StringComparison.Ordinal)
            ? n["Nexora.".Length..].Replace(".", "")
            : string.Empty;
        var outer = t.DeclaringType is { } d ? d.Name : string.Empty;
        var name = t.Name;
        var tick = name.IndexOf('`');
        return tick < 0
            ? ns + outer + name
            : ns + outer + name[..tick] + "Of" + string.Concat(t.GetGenericArguments().Select(OpenApiSchemaId));
    }
}
