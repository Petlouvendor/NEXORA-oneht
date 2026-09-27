using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Sync;
using Nexora.Contracts.Mobile;
using Nexora.Contracts.Sync;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Sync;

/// <summary>Registre des appareils autorisés à synchroniser (Phase 10 §12, §31).</summary>
public sealed class SqlDeviceRegistryService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : IDeviceRegistryService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private static readonly string[] Types = ["pos", "mobile", "desktop", "server", "other"];

    private sealed record DeviceRow(
        Guid Id, string DeviceIdentifier, string? Name, string DeviceType, string Status,
        DateTimeOffset? LastSyncAt, DateTimeOffset? LastSeenAt, int Pending);

    public async Task<Result<Guid>> RegisterAsync(RegisterDeviceRequest request, CancellationToken ct = default)
    {
        var type = request.DeviceType.Trim().ToLowerInvariant();
        if (!Types.Contains(type)) return Error.Validation("sync.device_type", $"Type d'appareil « {type} » invalide.");
        if (string.IsNullOrWhiteSpace(request.DeviceIdentifier))
            return Error.Validation("sync.device_identifier", "Identifiant d'appareil requis.");

        if (request.PosTerminalId is { } tid &&
            !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.pos_terminal WHERE id = {tid} AND company_id = {Company}) AS "Value"
                """).FirstAsync(ct))
            return Error.NotFound("sync.pos_terminal", "Terminal POS introuvable.");

        // Un appareil « mobile » est personnel (§32) — lié à l'utilisateur qui l'enregistre ;
        // un poste partagé (pos/desktop/server/other) reste sans utilisateur (§18).
        var userId = type == "mobile" ? tenant.UserId : null;

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.sync_device (id, company_id, device_identifier, device_type, name, pos_terminal_id, public_key, user_id, status, last_seen_at)
            VALUES ({id}, {Company}, {request.DeviceIdentifier.Trim()}, {type}, {request.Name?.Trim()},
                    {request.PosTerminalId}, {request.PublicKey}, {userId}, 'active', now())
            ON CONFLICT (company_id, device_identifier) DO UPDATE
            SET device_type = EXCLUDED.device_type, name = EXCLUDED.name, pos_terminal_id = EXCLUDED.pos_terminal_id,
                public_key = EXCLUDED.public_key, user_id = EXCLUDED.user_id, status = 'active', last_seen_at = now(), updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);

        var actual = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.sync_device WHERE company_id = {Company} AND device_identifier = {request.DeviceIdentifier.Trim()}
            """).FirstAsync(ct);
        return Result.Success(actual);
    }

    public async Task<IReadOnlyList<SyncDeviceDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<DeviceRow>($"""
            SELECT d.id AS "Id", d.device_identifier AS "DeviceIdentifier", d.name AS "Name",
                   d.device_type AS "DeviceType", d.status AS "Status", d.last_sync_at AS "LastSyncAt",
                   d.last_seen_at AS "LastSeenAt",
                   COALESCE((SELECT COUNT(*) FROM nexora.sync_operation o
                             WHERE o.device_id = d.id AND o.status IN ('PENDING','SYNCING','FAILED','CONFLICT')), 0)::int AS "Pending"
            FROM nexora.sync_device d WHERE d.company_id = {Company}
            ORDER BY d.name NULLS LAST, d.device_identifier
            """).ToListAsync(ct);
        return rows.Select(r => new SyncDeviceDto(r.Id, r.DeviceIdentifier, r.Name ?? r.DeviceIdentifier,
            r.DeviceType, r.Status, r.LastSyncAt, r.LastSeenAt, r.Pending)).ToList();
    }

    public async Task<Result> RevokeAsync(Guid deviceId, RevokeDeviceRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Error.Validation("sync.revoke_reason", "Un motif de révocation est obligatoire (§31).");
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.sync_device SET status = 'revoked', updated_at = now()
            WHERE id = {deviceId} AND company_id = {Company} AND status <> 'revoked'
            """, ct);
        return n == 0 ? Error.NotFound("sync.device", "Appareil introuvable ou déjà révoqué.") : Result.Success();
    }

    public async Task<Result> HeartbeatAsync(Guid deviceId, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.sync_device SET last_seen_at = now(), updated_at = now()
            WHERE id = {deviceId} AND company_id = {Company} AND status = 'active'
            """, ct);
        return n == 0 ? Error.Forbidden("sync.device_inactive", "Appareil inconnu ou non actif.") : Result.Success();
    }

    public async Task<Result> RegisterPushTokenAsync(Guid deviceId, RegisterPushTokenRequest request, CancellationToken ct = default)
    {
        var provider = request.PushProvider.Trim().ToLowerInvariant();
        if (provider is not ("fcm" or "apns" or "webpush"))
            return Error.Validation("sync.push_provider", "Fournisseur invalide (fcm/apns/webpush).");
        if (string.IsNullOrWhiteSpace(request.PushToken))
            return Error.Validation("sync.push_token", "Jeton requis.");

        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.sync_device SET push_token = {request.PushToken.Trim()}, push_provider = {provider}, updated_at = now()
            WHERE id = {deviceId} AND company_id = {Company} AND status = 'active'
            """, ct);
        if (n == 0) return Error.NotFound("sync.device", "Appareil introuvable ou non actif.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>Politique de fonctionnement hors connexion (Phase 10 §10, §17).</summary>
public sealed class SqlOfflinePolicyService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : IOfflinePolicyService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record PolicyRow(
        bool AllowOfflineSales, bool AllowOfflineCredit, decimal OfflineCreditLimit, bool AllowOfflineDiscount,
        bool AllowOfflineRefund, int MaxOfflineMinutes, int PendingAlertThreshold, string AllowedPaymentMethods, bool AutoSync,
        bool AllowOfflineCustomerPayment);

    public async Task<OfflinePolicyDto> GetAsync(CancellationToken ct = default)
    {
        var p = await db.Database.SqlQuery<PolicyRow>($"""
            SELECT allow_offline_sales AS "AllowOfflineSales", allow_offline_credit AS "AllowOfflineCredit",
                   offline_credit_limit AS "OfflineCreditLimit", allow_offline_discount AS "AllowOfflineDiscount",
                   allow_offline_refund AS "AllowOfflineRefund", max_offline_minutes AS "MaxOfflineMinutes",
                   pending_alert_threshold AS "PendingAlertThreshold", allowed_payment_methods::text AS "AllowedPaymentMethods",
                   auto_sync AS "AutoSync", allow_offline_customer_payment AS "AllowOfflineCustomerPayment"
            FROM nexora.offline_policy WHERE company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (p is null)
        {
            await db.Database.ExecuteSqlAsync($"INSERT INTO nexora.offline_policy (company_id) VALUES ({Company}) ON CONFLICT DO NOTHING", ct);
            return new OfflinePolicyDto(Company, true, false, 0m, true, false, 720, 50, ["cash"], true);
        }
        return new OfflinePolicyDto(Company, p.AllowOfflineSales, p.AllowOfflineCredit, p.OfflineCreditLimit,
            p.AllowOfflineDiscount, p.AllowOfflineRefund, p.MaxOfflineMinutes, p.PendingAlertThreshold,
            JsonSerializer.Deserialize<List<string>>(p.AllowedPaymentMethods) ?? ["cash"], p.AutoSync, p.AllowOfflineCustomerPayment);
    }

    public async Task<Result> SetAsync(SetOfflinePolicyRequest r, CancellationToken ct = default)
    {
        if (r.OfflineCreditLimit is < 0m) return Error.Validation("offline.credit_limit", "Plafond négatif.");
        if (r.MaxOfflineMinutes is < 0) return Error.Validation("offline.minutes", "Durée négative.");
        if (r.PendingAlertThreshold is < 0) return Error.Validation("offline.threshold", "Seuil négatif.");
        var methodsJson = r.AllowedPaymentMethods is null ? null : JsonSerializer.Serialize(r.AllowedPaymentMethods);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.offline_policy (company_id) VALUES ({Company}) ON CONFLICT (company_id) DO NOTHING
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.offline_policy SET
                allow_offline_sales     = COALESCE({r.AllowOfflineSales}, allow_offline_sales),
                allow_offline_credit    = COALESCE({r.AllowOfflineCredit}, allow_offline_credit),
                offline_credit_limit    = COALESCE({r.OfflineCreditLimit}, offline_credit_limit),
                allow_offline_discount  = COALESCE({r.AllowOfflineDiscount}, allow_offline_discount),
                allow_offline_refund    = COALESCE({r.AllowOfflineRefund}, allow_offline_refund),
                max_offline_minutes     = COALESCE({r.MaxOfflineMinutes}, max_offline_minutes),
                pending_alert_threshold = COALESCE({r.PendingAlertThreshold}, pending_alert_threshold),
                allowed_payment_methods = COALESCE({methodsJson}::jsonb, allowed_payment_methods),
                auto_sync               = COALESCE({r.AutoSync}, auto_sync),
                allow_offline_customer_payment = COALESCE({r.AllowOfflineCustomerPayment}, allow_offline_customer_payment),
                updated_at = now()
            WHERE company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
