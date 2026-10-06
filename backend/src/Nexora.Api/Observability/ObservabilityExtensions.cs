using System.Security.Claims;
using Nexora.Api.Middleware;
using Nexora.Application.Observability;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Operations;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Nexora.Api.Observability;

/// <summary>
/// Observabilité de production (finalisation phase 18, §39 « logs structurés, OpenTelemetry, métriques, traces ») :
/// <list type="bullet">
/// <item>journaux Serilog en JSON hors développement (horodatage, niveau, message, corrélation, trace, span) ;</item>
/// <item>traces (requêtes HTTP entrantes, appels sortants, requêtes PostgreSQL, tâches planifiées) et métriques
/// (ASP.NET Core, Kestrel, runtime .NET, Npgsql, métriques métier « Nexora ») exportées en OTLP — seulement si un
/// collecteur est configuré (<c>Observability:OtlpEndpoint</c> ou <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>) : sans lui,
/// rien n'est instrumenté ;</item>
/// <item>journal des requêtes au niveau de leur statut réel, avec code d'erreur, utilisateur et entreprise ; sondes
/// de santé hors du journal.</item>
/// </list>
/// </summary>
public static class ObservabilityExtensions
{
    public static WebApplicationBuilder AddNexoraObservability(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        Console.OutputEncoding = System.Text.Encoding.UTF8;   // journaux lisibles aussi sur un hôte Windows
        var json = ProductionConfiguration.LogFormat(configuration, builder.Environment.IsDevelopment()) == "json";

        builder.Host.UseSerilog((ctx, cfg) =>
        {
            cfg.ReadFrom.Configuration(ctx.Configuration)
               .Enrich.FromLogContext()
               .Enrich.WithProperty("Application", "nexora-api");
            if (json) cfg.WriteTo.Console(new RenderedCompactJsonFormatter());
            else cfg.WriteTo.Console();
        });

        var endpoint = ProductionConfiguration.OtlpEndpoint(configuration);
        if (endpoint is null) return builder;

        var serviceName = configuration["Observability:ServiceName"] is { Length: > 0 } n ? n : "nexora-api";
        var runtime = RuntimeEnvironment.From(configuration, builder.Environment.EnvironmentName);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: runtime.Version, serviceInstanceId: Environment.MachineName)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(t => t
                .AddSource(NexoraMetrics.ActivitySourceName)
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = ctx => !IsProbe(ctx.Request.Path);
                    o.RecordException = true;
                })
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddOtlpExporter(o => o.Endpoint = new Uri(endpoint)))
            .WithMetrics(m => m
                .AddMeter(NexoraMetrics.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql")
                .AddOtlpExporter(o => o.Endpoint = new Uri(endpoint)));
        return builder;
    }

    /// <summary>Journal des requêtes : un événement par requête, au niveau de son statut réel.</summary>
    public static IApplicationBuilder UseNexoraRequestLogging(this IApplicationBuilder app)
        => app.UseSerilogRequestLogging(o =>
        {
            o.MessageTemplate = "HTTP {RequestMethod:l} {RequestPath:l} : {StatusCode} en {Elapsed:0} ms";
            o.GetLevel = (ctx, _, ex) =>
                IsProbe(ctx.Request.Path) ? LogEventLevel.Verbose
                : ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
                : ctx.Response.StatusCode == StatusCodes.Status429TooManyRequests ? LogEventLevel.Warning
                : LogEventLevel.Information;
            o.EnrichDiagnosticContext = (diagnostic, ctx) =>
            {
                if (ctx.Items.TryGetValue(ExceptionHandlingMiddleware.ProblemCodeItem, out var code) && code is string c)
                    diagnostic.Set("ProblemCode", c);
                if (ctx.User.FindFirstValue(NexoraClaims.UserId) is { } userId)
                    diagnostic.Set("UserId", userId);
                if (ctx.Request.Headers.TryGetValue(TenantResolutionMiddleware.CompanyHeader, out var company))
                    diagnostic.Set("CompanyId", company.ToString());
                if (ctx.Connection.RemoteIpAddress is { } ip)
                    diagnostic.Set("ClientIp", ip.ToString());
            };
        });

    public static bool IsProbe(PathString path)
        => path.StartsWithSegments("/health") || path.StartsWithSegments("/api/v1/health") || path.StartsWithSegments("/api/v1/ready");
}
