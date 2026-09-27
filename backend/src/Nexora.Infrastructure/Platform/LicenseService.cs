using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexora.Application.Abstractions;
using Nexora.Application.Platform;
using Nexora.Contracts.Platform;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Platform;

// =============================================================================
// Licences (Phase 38 §27-29) + jeton offline signé (§28).
//   token = base64url(payload).base64url(HMAC-SHA256(base64url(payload)))
//   • durée de validité limitée + grâce (§28)
//   • vérification locale, protection contre modification (signature)
//   • pas de dépendance permanente à Internet pour les opérations autorisées
// La signature symétrique (HMAC) suffit pour « vérification locale + anti-altération » ; une
// signature asymétrique (le vérifieur n'a que la clé publique) est l'évolution production.
// =============================================================================
public sealed class LicenseService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    IDateTimeProvider clock,
    IOptions<JwtOptions> jwt) : ILicenseService
{
    private static readonly string[] Scopes = ["organization", "company", "user", "terminal", "module"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record LicenseRow(Guid Id, Guid CustomerId, Guid? SubscriptionId, string Scope, string? SubjectRef,
        string Status, int GraceDays, DateTimeOffset ActivatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastIssuedAt,
        DateTimeOffset? RevokedAt, string? RevokeReason);

    private const string LicenseSelect = """
        SELECT id AS "Id", customer_id AS "CustomerId", subscription_id AS "SubscriptionId", scope AS "Scope",
               subject_ref AS "SubjectRef", status AS "Status", grace_days AS "GraceDays", activated_at AS "ActivatedAt",
               expires_at AS "ExpiresAt", last_issued_at AS "LastIssuedAt", revoked_at AS "RevokedAt", revoke_reason AS "RevokeReason"
        FROM nexora.platform_license
        """;

    public async Task<IReadOnlyList<LicenseDto>> ListForCustomerAsync(Guid customerId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<LicenseRow>(LicenseSelect + " WHERE customer_id = {0} ORDER BY activated_at DESC", customerId).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<LicenseDto>> IssueAsync(IssueLicenseRequest r, CancellationToken ct = default)
    {
        if (!Scopes.Contains(r.Scope)) return Error.Validation("platform.license_scope", "Portée de licence invalide.");
        if (r.ValidityDays is < 1 or > 3650) return Error.Validation("platform.license_validity", "Validité hors bornes (1–3650 j).");
        if (r.GraceDays is < 0 or > 90) return Error.Validation("platform.license_grace", "Grâce hors bornes (0–90 j).");

        var customerExists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.platform_customer WHERE id = {r.CustomerId}) AS "Value"
            """).FirstAsync(ct);
        if (!customerExists) return Error.NotFound("platform.customer", "Client introuvable.");

        var subId = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.subscription
            WHERE customer_id = {r.CustomerId} AND status IN ('trialing','active','past_due','grace','suspended')
            ORDER BY created_at DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);

        var now = clock.UtcNow;
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.platform_license
                (id, customer_id, subscription_id, scope, subject_ref, status, grace_days, activated_at, expires_at)
            VALUES ({id}, {r.CustomerId}, {subId}, {r.Scope}, {r.SubjectRef}, 'active', {r.GraceDays}, {now}, {now.AddDays(r.ValidityDays)})
            """, ct);
        await PlatformInvoices.LogEventAsync(db, r.CustomerId, subId, PlatformEvents.LicenseActivated, new { id, r.Scope, r.SubjectRef }, tenant.UserId, now, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(ToDto((await LoadAsync(id, ct))!));
    }

    public async Task<Result<LicenseDto>> RevokeAsync(Guid id, RevokeLicenseRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("platform.license_reason", "Motif de révocation requis.");
        var lic = await LoadAsync(id, ct);
        if (lic is null) return Error.NotFound("platform.license", "Licence introuvable.");
        if (lic.Status == "revoked") return Error.Conflict("platform.license_revoked", "Licence déjà révoquée.");

        var now = clock.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.platform_license SET status = 'revoked', revoked_at = {now}, revoked_by = {tenant.UserId},
                revoke_reason = {r.Reason}, updated_at = now()
            WHERE id = {id}
            """, ct);
        await PlatformInvoices.LogEventAsync(db, lic.CustomerId, lic.SubscriptionId, PlatformEvents.LicenseRevoked, new { id, reason = r.Reason }, tenant.UserId, now, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(ToDto((await LoadAsync(id, ct))!));
    }

    public async Task<Result<LicenseTokenDto>> IssueOfflineTokenAsync(Guid id, Guid? restrictToCustomer, CancellationToken ct = default)
    {
        var lic = await LoadAsync(id, ct);
        if (lic is null) return Error.NotFound("platform.license", "Licence introuvable.");
        if (restrictToCustomer is { } rc && rc != lic.CustomerId) return Error.Forbidden("platform.not_your_license", "Licence hors de votre périmètre.");
        if (lic.Status == "revoked") return Error.Conflict("platform.license_revoked", "Licence révoquée — aucun jeton ne peut être émis.");

        var now = clock.UtcNow;
        var hardExp = lic.ExpiresAt.AddDays(lic.GraceDays);
        var payload = new
        {
            v = 1,
            lid = lic.Id,
            cid = lic.CustomerId,
            scope = lic.Scope,
            sub = lic.SubjectRef,
            iat = now.ToUnixTimeSeconds(),
            exp = lic.ExpiresAt.ToUnixTimeSeconds(),
            hardExp = hardExp.ToUnixTimeSeconds(),
        };
        var token = Sign(JsonSerializer.Serialize(payload, Json));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.platform_license SET last_issued_at = {now}, token_hash = {hash}, updated_at = now() WHERE id = {id}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(new LicenseTokenDto(token, now, lic.ExpiresAt, hardExp));
    }

    public async Task<LicenseVerifyResult> VerifyOfflineTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || !TryVerify(token, out var payloadJson))
            return new LicenseVerifyResult(false, "Signature invalide ou jeton altéré.", null, null, null, null, false);

        LicenseTokenPayload? p;
        try { p = JsonSerializer.Deserialize<LicenseTokenPayload>(payloadJson, Json); }
        catch { return new LicenseVerifyResult(false, "Contenu de jeton illisible.", null, null, null, null, false); }
        if (p is null) return new LicenseVerifyResult(false, "Contenu de jeton vide.", null, null, null, null, false);

        var now = clock.UtcNow.ToUnixTimeSeconds();
        var exp = DateTimeOffset.FromUnixTimeSeconds(p.exp);
        var hard = DateTimeOffset.FromUnixTimeSeconds(p.hardExp);
        if (now > p.hardExp)
            return new LicenseVerifyResult(false, "Jeton expiré (au-delà de la période de grâce).", p.lid, p.cid, p.scope, exp, false);

        // Révocation : vérifiable seulement quand la plateforme est joignable (§28 tolère l'offline).
        var revoked = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.platform_license WHERE id = {p.lid} AND status = 'revoked') AS "Value"
            """).FirstOrDefaultAsync(ct);
        if (revoked)
            return new LicenseVerifyResult(false, "Licence révoquée.", p.lid, p.cid, p.scope, exp, false);

        var inGrace = now > p.exp;
        return new LicenseVerifyResult(true, inGrace ? "Valide — période de grâce, renouvelez bientôt." : "Valide.",
            p.lid, p.cid, p.scope, exp, inGrace);
    }

    // ------------------------------------------------------------- Signature

    private byte[] Key()
    {
        var k = jwt.Value.SigningKey;
        if (string.IsNullOrWhiteSpace(k)) k = "dev-only-insecure-license-key-change-me";
        return Encoding.UTF8.GetBytes("license:" + k);   // domaine séparé de la signature des JWT d'accès
    }

    private string Sign(string payloadJson)
    {
        var body = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        using var h = new HMACSHA256(Key());
        var sig = Base64UrlEncode(h.ComputeHash(Encoding.ASCII.GetBytes(body)));
        return $"{body}.{sig}";
    }

    private bool TryVerify(string token, out string payloadJson)
    {
        payloadJson = "";
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        using var h = new HMACSHA256(Key());
        var expected = h.ComputeHash(Encoding.ASCII.GetBytes(parts[0]));
        byte[] provided;
        try { provided = Base64UrlDecode(parts[1]); }
        catch { return false; }
        if (!CryptographicOperations.FixedTimeEquals(expected, provided)) return false;
        try { payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[0])); return true; }
        catch { return false; }
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        t = (t.Length % 4) switch { 2 => t + "==", 3 => t + "=", _ => t };
        return Convert.FromBase64String(t);
    }

    private async Task<LicenseRow?> LoadAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQueryRaw<LicenseRow>(LicenseSelect + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);

    private static LicenseDto ToDto(LicenseRow r) => new(r.Id, r.CustomerId, r.SubscriptionId, r.Scope, r.SubjectRef, r.Status,
        r.GraceDays, r.ActivatedAt, r.ExpiresAt, r.LastIssuedAt, r.RevokedAt, r.RevokeReason);

    private sealed record LicenseTokenPayload(int v, Guid lid, Guid cid, string scope, string? sub, long iat, long exp, long hardExp);
}
