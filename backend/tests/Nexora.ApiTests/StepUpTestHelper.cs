using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Nexora.ApiTests;

/// <summary>Réauthentification forte pour les actions critiques (finalisation phase 17) : obtient le jeton de la
/// catégorie et l'ajoute en en-tête <c>X-Step-Up-Token</c>.</summary>
internal static class StepUpTestHelper
{
    public static async Task AddStepUpAsync(HttpClient client, string password, string purpose)
    {
        var r = await client.PostAsJsonAsync("/api/v1/security/step-up", new { password, purpose });
        r.StatusCode.Should().Be(HttpStatusCode.OK, $"réauthentification « {purpose} » : {await r.Content.ReadAsStringAsync()}");
        var token = (await r.Content.ReadFromJsonAsync<StepUpShape>())!.Token;
        client.DefaultRequestHeaders.Remove("X-Step-Up-Token");
        client.DefaultRequestHeaders.Add("X-Step-Up-Token", token);
    }

    public static async Task ShouldRequireStepUpAsync(HttpResponseMessage response, string purpose)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("security.step_up_required").And.Contain($"\"purpose\":\"{purpose}\"");
    }

    private sealed record StepUpShape(string Token);
}
