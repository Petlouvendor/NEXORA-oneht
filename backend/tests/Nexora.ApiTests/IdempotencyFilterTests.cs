using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Api.Security;

namespace Nexora.ApiTests;

/// <summary>Filtre d'idempotence de l'API (Phase 18 §12, §16, §46).</summary>
public sealed class IdempotencyFilterTests
{
    private const string Key = "test-key-12345678";

    private static IdempotencyFilter Filter(MemoryCache cache) => new(cache, NullLogger<IdempotencyFilter>.Instance, Options.Create(new JsonOptions()));

    private static ActionExecutingContext Executing(string? key, IDictionary<string, object?> args)
    {
        var http = new DefaultHttpContext();
        if (key is not null) http.Request.Headers["Idempotency-Key"] = key;
        var actionContext = new ActionContext(http, new RouteData(), new ControllerActionDescriptor());
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), args, controller: new object());
    }

    private static ActionExecutionDelegate NextReturning(Func<IActionResult> result, Action onCall)
        => () =>
        {
            onCall();
            var ctx = Executing(Key, new Dictionary<string, object?>());
            var executed = new ActionExecutedContext(ctx, ctx.Filters, ctx.Controller!) { Result = result() };
            return Task.FromResult(executed);
        };

    [Fact]
    public async Task Missing_key_is_rejected_with_400()
    {
        var ctx = Executing(key: null, new Dictionary<string, object?>());
        await Filter(new MemoryCache(new MemoryCacheOptions()))
            .OnActionExecutionAsync(ctx, NextReturning(() => new OkResult(), () => throw new Exception("ne doit pas s'exécuter")));

        ctx.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Replay_returns_the_same_body_without_reprocessing()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = Filter(cache);
        var args = new Dictionary<string, object?> { ["r"] = new { amount = 100, currency = "HTG" } };
        var calls = 0;
        var next = NextReturning(() => new ObjectResult(new { id = "abc-123", total = 100 }) { StatusCode = 201 }, () => calls++);

        var first = Executing(Key, args);
        await filter.OnActionExecutionAsync(first, next);
        calls.Should().Be(1);

        var second = Executing(Key, args);
        await filter.OnActionExecutionAsync(second, next);

        calls.Should().Be(1);   // pas de re-traitement
        var replayed = second.Result.Should().BeOfType<ContentResult>().Which;
        replayed.StatusCode.Should().Be(201);
        replayed.Content.Should().Contain("abc-123").And.Contain("100");
        second.HttpContext.Response.Headers["Idempotency-Replay"].ToString().Should().Be("true");
    }

    private sealed record InvoiceResult(Guid InvoiceId, bool WasReplay);

    [Fact]
    public async Task Replayed_body_uses_the_same_json_casing_as_the_original_response()
    {
        // Défaut phase 3 : le rejeu renvoyait « InvoiceId » (PascalCase) au lieu de « invoiceId » — un client
        // qui réessayait après une coupure réseau lisait un identifiant indéfini.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = Filter(cache);
        var args = new Dictionary<string, object?> { ["r"] = new { amount = 1 } };
        var id = Guid.NewGuid();
        var next = NextReturning(() => new ObjectResult(new InvoiceResult(id, false)) { StatusCode = 200 }, () => { });

        await filter.OnActionExecutionAsync(Executing(Key, args), next);
        var second = Executing(Key, args);
        await filter.OnActionExecutionAsync(second, next);

        var content = second.Result.Should().BeOfType<ContentResult>().Which.Content!;
        content.Should().Contain("\"invoiceId\"").And.Contain("\"wasReplay\"").And.NotContain("InvoiceId");
    }

    [Fact]
    public async Task Same_key_with_a_different_payload_is_rejected_with_422()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = Filter(cache);
        var next = NextReturning(() => new ObjectResult(new { ok = true }) { StatusCode = 200 }, () => { });

        await filter.OnActionExecutionAsync(
            Executing(Key, new Dictionary<string, object?> { ["r"] = new { amount = 100 } }), next);

        var conflicting = Executing(Key, new Dictionary<string, object?> { ["r"] = new { amount = 999 } });
        await filter.OnActionExecutionAsync(conflicting, next);

        conflicting.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
    }

    // La liaison MVC injecte le jeton d'annulation dans les arguments : il ne doit ni
    // faire échouer le hachage ni participer à la clé d'idempotence (Phase 24).
    [Fact]
    public async Task Cancellation_token_argument_does_not_break_hashing()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = Filter(cache);
        var next = NextReturning(() => new ObjectResult(new { ok = true }) { StatusCode = 200 }, () => { });

        using var cts = new CancellationTokenSource();
        var withToken = new Dictionary<string, object?> { ["r"] = new { amount = 5 }, ["ct"] = cts.Token };
        var noToken = new Dictionary<string, object?> { ["r"] = new { amount = 5 } };

        var first = Executing(Key, withToken);
        await filter.OnActionExecutionAsync(first, next);
        first.Result.Should().BeNull();   // laissé passer, aucune exception

        var replay = Executing(Key, noToken);
        await filter.OnActionExecutionAsync(replay, next);
        replay.Result.Should().BeOfType<ContentResult>();   // même hash → rejoué
    }

    [Fact]
    public async Task Failed_operations_are_not_cached()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = Filter(cache);
        var args = new Dictionary<string, object?> { ["r"] = new { amount = 1 } };
        var calls = 0;
        var failing = NextReturning(() => new ObjectResult(new { error = "boom" }) { StatusCode = 500 }, () => calls++);

        await filter.OnActionExecutionAsync(Executing(Key, args), failing);
        await filter.OnActionExecutionAsync(Executing(Key, args), failing);

        calls.Should().Be(2);   // un échec n'est jamais mémorisé — la requête est rejouable
    }
}
