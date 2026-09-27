using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Mobile;
using Nexora.Application.Notifications;
using Nexora.Application.Sales;
using Nexora.Contracts.Mobile;
using Nexora.Contracts.Sales;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Mobile;

/// <summary>
/// Tournées de livraison (Phase 44 §12, §52). <see cref="CompleteStopAsync"/> réutilise
/// <see cref="OrderService.CompleteOrderAsync"/> (Phase 25) pour l'étape liée à une commande —
/// même moteur que le solde d'une commande en caisse, statut de commande inclus (§52).
/// </summary>
public sealed class SqlDeliveryService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, OrderService orders, INotificationService notifications) : IDeliveryService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record RouteRow(Guid Id, Guid? DriverUserId, DateOnly RouteDate, string? Vehicle, string Status, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);
    private sealed record StopRow(Guid Id, Guid DeliveryRouteId, int SequenceNo, Guid? SalesOrderId, Guid? CustomerId, string Address,
        decimal? Latitude, decimal? Longitude, string? ContactPhone, string Status, Guid? SignatureAttachmentId, Guid? PhotoAttachmentId,
        decimal? AmountCollected, DateTimeOffset? DeliveredAt, string? FailureReason);

    private const string RouteSelect = """
        SELECT id AS "Id", driver_user_id AS "DriverUserId", route_date AS "RouteDate", vehicle AS "Vehicle",
               status AS "Status", started_at AS "StartedAt", completed_at AS "CompletedAt"
        FROM nexora.delivery_route
        """;
    private const string StopSelect = """
        SELECT id AS "Id", delivery_route_id AS "DeliveryRouteId", sequence_no AS "SequenceNo", sales_order_id AS "SalesOrderId",
               customer_id AS "CustomerId", address AS "Address", latitude AS "Latitude", longitude AS "Longitude",
               contact_phone AS "ContactPhone", status AS "Status", signature_attachment_id AS "SignatureAttachmentId",
               photo_attachment_id AS "PhotoAttachmentId", amount_collected AS "AmountCollected",
               delivered_at AS "DeliveredAt", failure_reason AS "FailureReason"
        FROM nexora.delivery_stop
        """;

    public async Task<Result<DeliveryRouteDto>> CreateRouteAsync(CreateDeliveryRouteRequest request, CancellationToken ct = default)
    {
        if (request.Stops.Count == 0) return Error.Validation("delivery_route.stops", "Au moins une étape requise.");
        foreach (var s in request.Stops)
            if (string.IsNullOrWhiteSpace(s.Address) && s.CustomerId is null)
                return Error.Validation("delivery_route.stop_address", "Chaque étape nécessite une adresse ou un client.");

        var routeId = Guid.NewGuid();
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.delivery_route (id, company_id, driver_user_id, route_date, vehicle)
                VALUES ({routeId}, {Company}, {request.DriverUserId}, {request.RouteDate}, {request.Vehicle})
                """, token);

            var seq = 1;
            foreach (var s in request.Stops)
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.delivery_stop
                        (id, company_id, delivery_route_id, sequence_no, sales_order_id, customer_id, address, latitude, longitude, contact_phone)
                    VALUES ({Guid.NewGuid()}, {Company}, {routeId}, {seq}, {s.SalesOrderId}, {s.CustomerId}, {s.Address}, {s.Latitude}, {s.Longitude}, {s.ContactPhone})
                    """, token);
                seq++;
            }
            return 0;
        }, ct);

        // §20 : notifie le livreur assigné sur ses appareils mobiles (push best-effort, jamais bloquant).
        if (request.DriverUserId is { } driver)
            await notifications.SendPushAsync([driver], "Nouvelle tournée",
                $"{request.Stops.Count} livraison(s) prévue(s) le {request.RouteDate:dd/MM}.", "/field-ops", ct);

        return await GetRouteAsync(routeId, ct);
    }

    public async Task<IReadOnlyList<DeliveryRouteDto>> ListRoutesAsync(DateOnly? date, CancellationToken ct = default)
    {
        var routes = date is null
            ? await db.Database.SqlQueryRaw<RouteRow>(RouteSelect + " WHERE company_id = {0} ORDER BY route_date DESC LIMIT 100", Company).ToListAsync(ct)
            : await db.Database.SqlQueryRaw<RouteRow>(RouteSelect + " WHERE company_id = {0} AND route_date = {1} ORDER BY created_at", Company, date.Value).ToListAsync(ct);
        var result = new List<DeliveryRouteDto>();
        foreach (var r in routes) result.Add(await ToRouteDto(r, ct));
        return result;
    }

    public async Task<Result<DeliveryRouteDto>> GetRouteAsync(Guid id, CancellationToken ct = default)
    {
        var route = await db.Database.SqlQueryRaw<RouteRow>(RouteSelect + " WHERE id = {0} AND company_id = {1}", id, Company).FirstOrDefaultAsync(ct);
        return route is null ? Error.NotFound("delivery_route.not_found", "Tournée introuvable.") : Result.Success(await ToRouteDto(route, ct));
    }

    public async Task<IReadOnlyList<DeliveryStopDto>> MyStopsAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<StopRow>(StopSelect + """
             WHERE company_id = {0} AND status NOT IN ('delivered','failed','returned')
               AND delivery_route_id IN (SELECT id FROM nexora.delivery_route WHERE driver_user_id = {1})
             ORDER BY sequence_no
            """, Company, (object?)tenant.UserId ?? DBNull.Value).ToListAsync(ct);
        return rows.Select(ToStopDto).ToList();
    }

    public async Task<Result<DeliveryStopDto>> AdvanceStopAsync(Guid stopId, string status, CancellationToken ct = default)
    {
        var s = status.Trim().ToLowerInvariant();
        if (s is not ("en_route" or "arrived")) return Error.Validation("delivery_stop.status", "Transition attendue : en_route ou arrived.");

        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.delivery_stop SET status = {s}, updated_at = now()
            WHERE id = {stopId} AND company_id = {Company} AND status NOT IN ('delivered','failed','returned')
            """, ct);
        if (rows == 0) return Error.NotFound("delivery_stop.not_found", "Étape introuvable ou déjà close.");
        await uow.SaveChangesAsync(ct);
        return await GetStopDtoAsync(stopId, ct);
    }

    public async Task<Result<DeliveryStopDto>> CompleteStopAsync(Guid stopId, CompleteDeliveryStopRequest request, CancellationToken ct = default)
    {
        var stop = await db.Database.SqlQueryRaw<StopRow>(StopSelect + " WHERE id = {0} AND company_id = {1}", stopId, Company).FirstOrDefaultAsync(ct);
        if (stop is null) return Error.NotFound("delivery_stop.not_found", "Étape introuvable.");
        if (stop.Status is "delivered" or "returned") return Error.Conflict("delivery_stop.already_closed", "Étape déjà livrée.");

        if (stop.SalesOrderId is { } orderId)
        {
            var tenders = request.AmountCollected is > 0m && request.PaymentFinancialAccountId is { } account
                ? new List<OrderTenderInput> { new(account, null, request.AmountCollected.Value, "Encaissé à la livraison (§12)") }
                : [];
            var complete = await orders.CompleteOrderAsync(orderId,
                new CompleteOrderRequest(request.OperationUuid, null, tenders.Count == 0, null, tenders, null), ct);
            if (!complete.IsSuccess) return complete.Error!;
        }

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.delivery_stop SET status = 'delivered', latitude = {request.Latitude}, longitude = {request.Longitude},
                   signature_attachment_id = {request.SignatureAttachmentId}, photo_attachment_id = {request.PhotoAttachmentId},
                   amount_collected = {request.AmountCollected}, payment_financial_account_id = {request.PaymentFinancialAccountId},
                   delivered_at = now(), updated_at = now()
            WHERE id = {stopId} AND company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        await MaybeCompleteRouteAsync(stop.DeliveryRouteId, ct);
        return await GetStopDtoAsync(stopId, ct);
    }

    public async Task<Result<DeliveryStopDto>> FailStopAsync(Guid stopId, FailDeliveryStopRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) return Error.Validation("delivery_stop.reason", "Motif requis.");

        var stop = await db.Database.SqlQueryRaw<StopRow>(StopSelect + " WHERE id = {0} AND company_id = {1}", stopId, Company).FirstOrDefaultAsync(ct);
        if (stop is null) return Error.NotFound("delivery_stop.not_found", "Étape introuvable.");
        if (stop.Status is "delivered" or "returned") return Error.Conflict("delivery_stop.already_closed", "Étape déjà close.");

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.delivery_stop SET status = 'failed', failure_reason = {request.Reason}, updated_at = now()
            WHERE id = {stopId} AND company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        await MaybeCompleteRouteAsync(stop.DeliveryRouteId, ct);
        return await GetStopDtoAsync(stopId, ct);
    }

    private async Task MaybeCompleteRouteAsync(Guid routeId, CancellationToken ct)
    {
        var allClosed = await db.Database.SqlQuery<bool>($"""
            SELECT NOT EXISTS (
                SELECT 1 FROM nexora.delivery_stop WHERE delivery_route_id = {routeId} AND status NOT IN ('delivered','failed','returned')
            ) AS "Value"
            """).FirstAsync(ct);
        if (!allClosed) return;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.delivery_route SET status = 'completed', completed_at = now(), updated_at = now()
            WHERE id = {routeId} AND status <> 'completed'
            """, ct);
        await uow.SaveChangesAsync(ct);
    }

    private async Task<Result<DeliveryStopDto>> GetStopDtoAsync(Guid stopId, CancellationToken ct)
    {
        var row = await db.Database.SqlQueryRaw<StopRow>(StopSelect + " WHERE id = {0} AND company_id = {1}", stopId, Company).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("delivery_stop.not_found", "Étape introuvable.") : Result.Success(ToStopDto(row));
    }

    private async Task<DeliveryRouteDto> ToRouteDto(RouteRow r, CancellationToken ct)
    {
        var stops = await db.Database.SqlQueryRaw<StopRow>(StopSelect + " WHERE delivery_route_id = {0} ORDER BY sequence_no", r.Id).ToListAsync(ct);
        return new DeliveryRouteDto(r.Id, r.DriverUserId, r.RouteDate, r.Vehicle, r.Status, r.StartedAt, r.CompletedAt, stops.Select(ToStopDto).ToList());
    }

    private static DeliveryStopDto ToStopDto(StopRow r) => new(r.Id, r.DeliveryRouteId, r.SequenceNo, r.SalesOrderId, r.CustomerId, r.Address,
        r.Latitude, r.Longitude, r.ContactPhone, r.Status, r.SignatureAttachmentId, r.PhotoAttachmentId, r.AmountCollected, r.DeliveredAt, r.FailureReason);
}
