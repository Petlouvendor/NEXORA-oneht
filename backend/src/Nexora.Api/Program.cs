using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Nexora.Api.HealthChecks;
using Nexora.Api.Middleware;
using Nexora.Api.Observability;
using Nexora.Api.Security;
using Nexora.Api.Setup;
using Nexora.Application;
using Nexora.Infrastructure;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Operations;
using Nexora.Infrastructure.Setup;

// --- Commandes d'exploitation de l'image (finalisation phase 18) : migrate, healthcheck ---
var command = CliCommands.Detect(args);
var builder = WebApplication.CreateBuilder(command is null ? args : []);

// --- Secrets en fichiers (finalisation phase 18) : un fichier par clé (« Jwt__SigningKey »…), monté par
// l'orchestrateur (secrets Docker / Kubernetes) — prioritaire sur les variables d'environnement.
var secretsDirectory = builder.Configuration["NEXORA_SECRETS_DIR"] is { Length: > 0 } dir ? dir : "/run/secrets";
if (Directory.Exists(secretsDirectory))
    builder.Configuration.AddKeyPerFile(secretsDirectory, optional: true);

// --- Installation initiale (résumé fonctionnel §20) : configuration écrite par l'assistant ---
InstallConfiguration.AddInstallFile(builder.Configuration, builder.Environment.ContentRootPath);

if (command is not null)
    return await CliCommands.RunAsync(command, args, builder.Configuration);

// --- Secrets de production (résumé fonctionnel §18, §21) : jamais de repli codé en dur hors dev ---
// Sans base configurée, l'API démarre en mode installation : seul l'assistant répond (503 ailleurs),
// les secrets manquants ne protègent alors aucune donnée ; l'assistant les génère.
if (!SetupState.IsSetupMode(builder.Configuration))
    StartupSecretsGuard.EnsureConfigured(builder.Configuration, builder.Environment);

// --- Journaux structurés, traces et métriques (Phase 3 §19, finalisation phase 18) ---
builder.AddNexoraObservability();

// --- Composition des couches ----------------------------------------------
builder.Services.AddSingleton(RuntimeEnvironment.From(builder.Configuration, builder.Environment.EnvironmentName));
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
    // 10 connexions par minute et par adresse ; réglable (Security:LoginRateLimitPerMinute) pour un environnement de
    // test qui ouvre beaucoup de sessions depuis une seule machine (finalisation phase 19) — jamais au-dessus de 1 000.
    var loginPerMinute = Math.Clamp(builder.Configuration.GetValue("Security:LoginRateLimitPerMinute", 10), 1, 1000);
    options.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // Rafraîchissement (finalisation phase 17) : plus fréquent que la connexion (bureau partagé derrière une même
    // adresse), mais borné — une rafale de rafraîchissements est un signal d'abus.
    options.AddPolicy("refresh", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
    // Points d'accès anonymes adressés par un code (partage GED, vérification de document ou de licence) — analyse
    // 2026-10 §6.4 : sans limite, un code ou un mot de passe de partage se devine par force brute.
    var publicPerMinute = Math.Clamp(builder.Configuration.GetValue("Security:PublicRateLimitPerMinute", 30), 1, 1000);
    options.AddPolicy("public", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = publicPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

// --- Proxy inverse (finalisation phase 18) : TLS terminé en amont. Sans reprise des en-têtes X-Forwarded-*,
// toutes les requêtes sembleraient venir du proxy (une seule limite de connexions pour tous les utilisateurs) et
// en HTTP (cookie de session non sécurisé). Seuls les proxys déclarés sont crus.
var behindProxy = ProductionConfiguration.BehindReverseProxy(builder.Configuration);
if (behindProxy)
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.ForwardLimit = 1;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (var network in builder.Configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        foreach (var proxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
            o.KnownProxies.Add(IPAddress.Parse(proxy));
        var hosts = builder.Configuration["AllowedHosts"];
        if (!string.IsNullOrWhiteSpace(hosts) && hosts.Trim() != "*")
            foreach (var host in hosts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                o.AllowedHosts.Add(host);
    });

// --- Health checks (Phase 3 §19, finalisation phase 18) ----------------
// Vivacité : le processus répond (aucune dépendance). Disponibilité : base joignable ET schéma conforme à la version.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<MigrationsHealthCheck>("migrations", tags: ["ready"]);

// --- Traitements asynchrones du monolithe modulaire (Phase 16 §22) ---
// Inerte tant que Scheduler:Enabled n'est pas positionné (tests, poste de dev).
builder.Services.AddHostedService<Nexora.Api.Scheduling.BackgroundScheduler>();

var app = builder.Build();

var setup = app.Services.GetRequiredService<SetupState>();
if (setup.Required)
    app.Logger.LogWarning(
        "Nexora One n'est pas installé : assistant d'installation ouvert sur /setup. Jeton d'installation : {SetupToken} " +
        "(configuration écrite dans {ConfigPath}).", setup.Token, setup.ConfigPath);

if (behindProxy) app.UseForwardedHeaders();   // avant tout ce qui lit l'adresse du client ou le protocole
// Finalisation phase 18 : corrélation puis journal des requêtes AVANT la gestion des erreurs — le journal voit le
// statut réellement renvoyé (422, 409…) au lieu d'un « 500 » avec pile d'appels, et l'identifiant de corrélation.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseNexoraRequestLogging();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<SetupModeMiddleware>();   // mode installation : assistant seul (§20)
app.UseMiddleware<SecurityHeadersMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
    // Derrière un proxy, c'est lui qui redirige vers HTTPS (et les sondes internes restent en HTTP).
    if (!behindProxy) app.UseHttpsRedirection();
}

app.UseCors(CorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();   // clé d'API → identité machine (Phase 35 §13)
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();
app.UseMiddleware<MaintenanceModeMiddleware>();   // API dégradée en écriture pendant une maintenance (Phase 41 §23-24)

app.MapControllers();

var liveness = new HealthCheckOptions { Predicate = _ => false };
var readiness = new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = HealthResponseWriter.WriteAsync };
app.MapHealthChecks("/health", liveness).AllowAnonymous();
app.MapHealthChecks("/health/live", liveness).AllowAnonymous();
app.MapHealthChecks("/health/ready", readiness).AllowAnonymous();

// Alias versionnés (Phase 37 §46, §56) — liveness / readiness sous /api/v1.
app.MapHealthChecks("/api/v1/health", liveness).AllowAnonymous();
app.MapHealthChecks("/api/v1/ready", readiness).AllowAnonymous();

app.Run();
return CliCommands.Ok;

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
