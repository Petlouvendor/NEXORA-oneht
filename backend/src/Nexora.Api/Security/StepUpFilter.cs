using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nexora.Application.Security;

namespace Nexora.Api.Security;

/// <summary>Catégories d'actions critiques (plan §37) — chacune exige sa propre confirmation forte.</summary>
public static class StepUpPurposes
{
    public const string Payment = "payment";
    public const string Cancellation = "cancellation";
    public const string AccountingValidation = "accounting_validation";
    public const string PermissionChange = "permission_change";
    public const string Deletion = "deletion";
    public const string SensitiveData = "sensitive_data";

    public static string Label(string purpose) => purpose switch
    {
        Payment => "ce paiement",
        Cancellation => "cette annulation",
        AccountingValidation => "cette validation comptable",
        PermissionChange => "ce changement de droits",
        Deletion => "cette suppression",
        SensitiveData => "l'accès à ces données sensibles",
        _ => "cette action",
    };
}

/// <summary>
/// Action critique (finalisation phase 17) : exige une réauthentification forte récente pour la catégorie —
/// mot de passe, plus le code de double authentification si elle est active. Le jeton obtenu par
/// <c>POST /security/step-up</c> est présenté dans l'en-tête <c>X-Step-Up-Token</c> ; il vaut pour sa
/// catégorie et sa session pendant la durée fixée par la politique (10 min par défaut). Sans lui : 403
/// <c>security.step_up_required</c>, avec la catégorie, pour que l'interface demande la confirmation.
/// La permission de l'action est vérifiée avant (filtre d'autorisation).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireStepUpAttribute(string purpose) : Attribute, IFilterFactory
{
    public const string HeaderName = "X-Step-Up-Token";
    public string Purpose => purpose;
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new StepUpFilter(purpose, serviceProvider.GetRequiredService<IStepUpAuthService>());

    private sealed class StepUpFilter(string purpose, IStepUpAuthService stepUp) : IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var http = context.HttpContext;
            var token = http.Request.Headers[HeaderName].ToString();
            Guid? sid = Guid.TryParse(http.User.FindFirst("sid")?.Value, out var s) ? s : null;
            if (!string.IsNullOrWhiteSpace(token) && await stepUp.ValidateAsync(token, purpose, sid, http.RequestAborted))
            {
                await next();
                return;
            }

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "security.step_up_required",
                Detail = $"Confirmez votre identité pour {StepUpPurposes.Label(purpose)}.",
                Type = "https://nexora/errors/security.step_up_required",
            };
            problem.Extensions["purpose"] = purpose;
            problem.Extensions["correlationId"] = http.TraceIdentifier;
            context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status403Forbidden };
        }
    }
}
