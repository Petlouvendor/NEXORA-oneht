using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Mobile;
using Nexora.Application.Notifications;
using Nexora.Application.Sales;
using Nexora.Contracts.Mobile;
using Nexora.Contracts.Sales;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Customers;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Mobile;

/// <summary>
/// Tournées de livraison (Phase 44 §12, §52 ; finalisation phase 12) — assigné → en route → sur place → livré (preuve)
/// → synchronisé. <see cref="CompleteStopAsync"/> réutilise <see cref="OrderService.CompleteOrderAsync"/> pour l'arrêt lié
/// à une commande : même moteur que le solde d'une commande en caisse, statut de commande et encaissement compris.
/// <list type="bullet">
/// <item>Une livraison exige une preuve : signature ou photo, toutes deux de cette entreprise et du bon type.</item>
/// <item>Un encaissement n'existe que sur une commande, avec un compte de caisse : sinon aucune écriture ne le portait.</item>
/// <item>La position n'est enregistrée que si l'entreprise l'a prévue (<c>field_ops.capture_location</c>).</item>
/// </list>
/// </summary>
public sealed class SqlDeliveryService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, OrderService orders, INotificationService notifications,
    ISettingsService settings, IBusinessCalendar calendar) : IDeliveryService
{
    public const string LocationSetting = "field_ops.capture_location";
    private static readonly string[] Closed = ["delivered", "returned"];
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record RouteRow(Guid Id, Guid? DriverUserId, DateOnly RouteDate, string? Vehicle, string Status,
        DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? DriverName);
    private sealed record StopRow(Guid Id, Guid DeliveryRouteId, int SequenceNo, Guid? SalesOrderId, Guid? CustomerId, string Address,
        decimal? Latitude, decimal? Longitude, string? ContactPhone, string Status, Guid? SignatureAttachmentId, Guid? PhotoAttachmentId,
        decimal? AmountCollected, DateTimeOffset? DeliveredAt, string? FailureReason, string? CustomerName, string? OrderNumber,
        decimal? OrderTotal, decimal? OrderDeposit, string? Currency, DateTimeOffset? EnRouteAt, DateTimeOffset? ArrivedAt,
        string? CompletedByName, string? DriverName, DateOnly RouteDate, Guid? DriverUserId);

    private FormattableString Routes(Guid? id, DateOnly? date) => $"""
        SELECT r.id AS "Id", r.driver_user_id AS "DriverUserId", r.route_date AS "RouteDate", r.vehicle AS "Vehicle",
               r.status AS "Status", r.started_at AS "StartedAt", r.completed_at AS "CompletedAt", u.full_name AS "DriverName"
        FROM nexora.delivery_route r LEFT JOIN nexora.app_user u ON u.id = r.driver_user_id
        WHERE r.company_id = {Company} AND ({id}::uuid IS NULL OR r.id = {id}::uuid) AND ({date}::date IS NULL OR r.route_date = {date}::date)
        ORDER BY r.route_date DESC, r.created_at DESC
        LIMIT 100
        """;

    private FormattableString Stops(Guid? routeId, Guid? stopId, Guid? driverOpen) => $"""
        SELECT s.id AS "Id", s.delivery_route_id AS "DeliveryRouteId", s.sequence_no AS "SequenceNo", s.sales_order_id AS "SalesOrderId",
               s.customer_id AS "CustomerId", s.address AS "Address", s.latitude AS "Latitude", s.longitude AS "Longitude",
               s.contact_phone AS "ContactPhone", s.status AS "Status", s.signature_attachment_id AS "SignatureAttachmentId",
               s.photo_attachment_id AS "PhotoAttachmentId", s.amount_collected AS "AmountCollected",
               s.delivered_at AS "DeliveredAt", s.failure_reason AS "FailureReason",
               COALESCE(c.name, oc.name) AS "CustomerName", o.document_number AS "OrderNumber", o.total AS "OrderTotal",
               o.deposit_amount AS "OrderDeposit", cur.code AS "Currency", s.en_route_at AS "EnRouteAt", s.arrived_at AS "ArrivedAt",
               cb.full_name AS "CompletedByName", du.full_name AS "DriverName", r.route_date AS "RouteDate", r.driver_user_id AS "DriverUserId"
        FROM nexora.delivery_stop s
        JOIN nexora.delivery_route r ON r.id = s.delivery_route_id
        LEFT JOIN nexora.customer c ON c.id = s.customer_id
        LEFT JOIN nexora.sales_order o ON o.id = s.sales_order_id
        LEFT JOIN nexora.customer oc ON oc.id = o.customer_id
        LEFT JOIN nexora.currency cur ON cur.id = o.currency_id
        LEFT JOIN nexora.app_user cb ON cb.id = s.completed_by
        LEFT JOIN nexora.app_user du ON du.id = r.driver_user_id
        WHERE s.company_id = {Company}
          AND ({routeId}::uuid IS NULL OR s.delivery_route_id = {routeId}::uuid)
          AND ({stopId}::uuid IS NULL OR s.id = {stopId}::uuid)
          AND ({driverOpen}::uuid IS NULL OR (r.driver_user_id = {driverOpen}::uuid AND r.status <> 'cancelled'
               AND (s.status NOT IN ('delivered','failed','returned') OR s.updated_at >= current_date)))
        ORDER BY r.route_date, s.sequence_no
        """;

    // ------------------------------------------------------------------ options de saisie

    public async Task<FieldOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var agentIds = await CustomerAccountRules.HoldersAsync(db, tenant, Application.Security.Permissions.FieldOps.Manage, null, ct);
        var agents = agentIds.Count == 0 ? [] : await db.Database.SqlQuery<FieldUserOption>($"""
            SELECT id AS "Id", full_name AS "FullName" FROM nexora.app_user WHERE id = ANY({agentIds.ToArray()}) AND status = 'active' ORDER BY full_name
            """).ToListAsync(ct);
        var orderRows = await db.Database.SqlQuery<FieldOrderOption>($"""
            SELECT o.id AS "Id", o.document_number AS "DocumentNumber", o.customer_id AS "CustomerId", c.name AS "CustomerName",
                   o.delivery_address AS "DeliveryAddress", o.total AS "Total", o.deposit_amount AS "Deposit", cur.code AS "Currency",
                   o.status AS "Status"
            FROM nexora.sales_order o
            LEFT JOIN nexora.customer c ON c.id = o.customer_id
            JOIN nexora.currency cur ON cur.id = o.currency_id
            WHERE o.company_id = {Company} AND o.status IN ('confirmed', 'reserved', 'partially_fulfilled')
              AND NOT EXISTS (SELECT 1 FROM nexora.delivery_stop s WHERE s.sales_order_id = o.id AND s.status NOT IN ('delivered','failed','returned'))
            ORDER BY o.created_at DESC LIMIT 200
            """).ToListAsync(ct);
        var accounts = await db.Database.SqlQuery<FieldAccountOption>($"""
            SELECT f.id AS "Id", f.name AS "Name", cur.code AS "Currency" FROM nexora.financial_account f
            JOIN nexora.currency cur ON cur.id = f.currency_id
            WHERE f.company_id = {Company} AND f.status = 'active' AND f.account_type IN ('cash', 'mobile_money')
            ORDER BY f.name
            """).ToListAsync(ct);
        return new FieldOptionsDto(agents, orderRows, accounts, await CaptureLocationAsync(ct));
    }

    private async Task<bool> CaptureLocationAsync(CancellationToken ct)
    {
        var v = (await settings.ResolveAsync(LocationSetting, null, ct)).Value;
        return v.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    // ------------------------------------------------------------------ tournées

    public async Task<Result<DeliveryRouteDto>> CreateRouteAsync(CreateDeliveryRouteRequest request, CancellationToken ct = default)
    {
        if (request.Stops is null || request.Stops.Count == 0) return Error.Validation("delivery_route.stops", "Au moins un arrêt requis.");
        if (request.Stops.Count > 100) return Error.Validation("delivery_route.stops", "Cent arrêts au plus par tournée.");
        if (request.RouteDate < calendar.Today.AddDays(-1))
            return Error.Validation("delivery_route.date", "La date de tournée ne peut pas être passée.");
        if (request.Vehicle is { Length: > 80 }) return Error.Validation("delivery_route.vehicle", "Véhicule : 80 caractères au plus.");
        if (request.DriverUserId is { } driver
            && !(await CustomerAccountRules.HoldersAsync(db, tenant, Application.Security.Permissions.FieldOps.Manage, null, ct)).Contains(driver))
            return Error.Validation("delivery_route.driver", "Ce livreur n'a pas accès au terrain dans cette entreprise.");

        var orderIds = new HashSet<Guid>();
        foreach (var (s, i) in request.Stops.Select((s, i) => (s, i + 1)))
        {
            if (s.SalesOrderId is { } oid)
            {
                if (!orderIds.Add(oid)) return Error.Validation("delivery_route.order_duplicate", $"Arrêt {i} : commande déjà dans la tournée.");
                var status = await db.Database.SqlQuery<string?>($"""
                    SELECT status AS "Value" FROM nexora.sales_order WHERE id = {oid} AND company_id = {Company}
                    """).FirstOrDefaultAsync(ct);
                if (status is null) return Error.Validation("delivery_route.order", $"Arrêt {i} : commande introuvable.");
                if (status is not ("confirmed" or "reserved" or "partially_fulfilled"))
                    return Error.Validation("delivery_route.order", $"Arrêt {i} : la commande n'est pas livrable (statut « {status} »).");
                if (await db.Database.SqlQuery<bool>($"""
                        SELECT EXISTS(SELECT 1 FROM nexora.delivery_stop WHERE sales_order_id = {oid} AND status NOT IN ('delivered','failed','returned')) AS "Value"
                        """).FirstAsync(ct))
                    return Error.Conflict("delivery_route.order_planned", $"Arrêt {i} : cette commande est déjà prévue dans une autre tournée.");
            }
            else if (s.CustomerId is { } cid && !await db.Database.SqlQuery<bool>($"""
                    SELECT EXISTS(SELECT 1 FROM nexora.customer WHERE id = {cid} AND company_id = {Company}) AS "Value"
                    """).FirstAsync(ct))
                return Error.Validation("delivery_route.customer", $"Arrêt {i} : client introuvable.");
            if (string.IsNullOrWhiteSpace(s.Address) && s.CustomerId is null && s.SalesOrderId is null)
                return Error.Validation("delivery_route.stop_address", $"Arrêt {i} : adresse, client ou commande requis.");
            if (s.Address is { Length: > 300 }) return Error.Validation("delivery_route.stop_address", $"Arrêt {i} : adresse trop longue.");
            if (!ValidCoordinates(s.Latitude, s.Longitude))
                return Error.Validation("delivery_route.coordinates", $"Arrêt {i} : coordonnées invalides.");
        }

        var routeId = Guid.NewGuid();
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.delivery_route (id, company_id, driver_user_id, route_date, vehicle)
                VALUES ({routeId}, {Company}, {request.DriverUserId}, {request.RouteDate}, {request.Vehicle?.Trim()})
                """, token);
            var seq = 1;
            foreach (var s in request.Stops)
            {
                // Adresse de livraison de la commande quand l'arrêt n'en précise pas.
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.delivery_stop
                        (id, company_id, delivery_route_id, sequence_no, sales_order_id, customer_id, address, latitude, longitude, contact_phone)
                    SELECT {Guid.NewGuid()}, {Company}, {routeId}, {seq}, {s.SalesOrderId},
                           COALESCE({s.CustomerId}::uuid, (SELECT customer_id FROM nexora.sales_order WHERE id = {s.SalesOrderId}::uuid)),
                           COALESCE(NULLIF(btrim({s.Address ?? ""}), ''), (SELECT delivery_address FROM nexora.sales_order WHERE id = {s.SalesOrderId}::uuid), ''),
                           {s.Latitude}, {s.Longitude}, {s.ContactPhone?.Trim()}
                    """, token);
                seq++;
            }
            return 0;
        }, ct);

        // §20 : le livreur assigné est prévenu sur ses appareils (best-effort, jamais bloquant).
        if (request.DriverUserId is { } assigned)
            await notifications.SendPushAsync([assigned], "Nouvelle tournée",
                $"{request.Stops.Count} livraison(s) prévue(s) le {request.RouteDate:dd/MM}.", "/field-ops?tab=mine", ct);

        return await GetRouteAsync(routeId, ct);
    }

    private static bool ValidCoordinates(decimal? lat, decimal? lng)
        => (lat is null && lng is null) || (lat is >= -90m and <= 90m && lng is >= -180m and <= 180m);

    public async Task<IReadOnlyList<DeliveryRouteDto>> ListRoutesAsync(DateOnly? date, CancellationToken ct = default)
    {
        var result = new List<DeliveryRouteDto>();
        foreach (var r in await db.Database.SqlQuery<RouteRow>(Routes(null, date)).ToListAsync(ct)) result.Add(await ToRouteDto(r, ct));
        return result;
    }

    public async Task<Result<DeliveryRouteDto>> GetRouteAsync(Guid id, CancellationToken ct = default)
    {
        var route = await db.Database.SqlQuery<RouteRow>(Routes(id, null)).FirstOrDefaultAsync(ct);
        return route is null ? Error.NotFound("delivery_route.not_found", "Tournée introuvable.") : Result.Success(await ToRouteDto(route, ct));
    }

    /// <summary>Arrêts du livreur connecté : ceux qui restent à faire, et ceux conclus aujourd'hui (suivi de la journée).</summary>
    public async Task<IReadOnlyList<DeliveryStopDto>> MyStopsAsync(CancellationToken ct = default)
    {
        if (tenant.UserId is not { } me) return [];
        return (await db.Database.SqlQuery<StopRow>(Stops(null, null, me)).ToListAsync(ct)).Select(ToStopDto).ToList();
    }

    // ------------------------------------------------------------------ étapes

    public async Task<Result<DeliveryStopDto>> AdvanceStopAsync(Guid stopId, string status, CancellationToken ct = default)
    {
        var s = (status ?? "").Trim().ToLowerInvariant();
        if (s is not ("en_route" or "arrived")) return Error.Validation("delivery_stop.status", "Étape attendue : en route ou sur place.");
        var stop = await db.Database.SqlQuery<StopRow>(Stops(null, stopId, null)).FirstOrDefaultAsync(ct);
        if (stop is null) return Error.NotFound("delivery_stop.not_found", "Arrêt introuvable.");
        if (Closed.Contains(stop.Status)) return Error.Conflict("delivery_stop.already_closed", "Arrêt déjà livré.");
        if (NotTheDriver(stop) is { } notDriver) return notDriver;
        if (s == "en_route" && stop.Status == "arrived") return Error.Conflict("delivery_stop.order", "Déjà sur place.");
        if (s == stop.Status) return Result.Success(ToStopDto(stop));

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.delivery_stop
                   SET status = {s}, failure_reason = NULL,
                       en_route_at = CASE WHEN {s} = 'en_route' THEN now() ELSE COALESCE(en_route_at, now()) END,
                       arrived_at = CASE WHEN {s} = 'arrived' THEN now() ELSE arrived_at END,
                       updated_at = now()
                WHERE id = {stopId} AND company_id = {Company} AND status NOT IN ('delivered','returned')
                """, token);
            await StartRouteAsync(stop.DeliveryRouteId, token);
            return 0;
        }, ct);
        return await GetStopDtoAsync(stopId, ct);
    }

    /// <summary>
    /// Un arrêt d'une tournée attribuée ne se fait avancer ou clôturer que par son livreur (audit 2026-10 éd. 2 : tout
    /// détenteur de field_ops.manage clôturait la livraison d'un autre — preuve et encaissement compris). Une tournée
    /// sans livreur reste ouverte à l'équipe ; pour reprendre la tournée d'un autre, on la réattribue.
    /// </summary>
    private Error? NotTheDriver(StopRow stop)
        => stop.DriverUserId is { } driver && driver != tenant.UserId
            ? Error.Forbidden("delivery_stop.not_driver", "Seul le livreur de la tournée met à jour cet arrêt.")
            : null;

    /// <summary>La tournée passe « en cours » au premier mouvement d'un de ses arrêts (elle restait « planifiée »).</summary>
    private Task StartRouteAsync(Guid routeId, CancellationToken ct) => db.Database.ExecuteSqlAsync($"""
        UPDATE nexora.delivery_route SET status = 'in_progress', started_at = COALESCE(started_at, now()), updated_at = now()
        WHERE id = {routeId} AND status = 'planned'
        """, ct);

    public async Task<Result<DeliveryStopDto>> CompleteStopAsync(Guid stopId, CompleteDeliveryStopRequest request, CancellationToken ct = default)
    {
        var stop = await db.Database.SqlQuery<StopRow>(Stops(null, stopId, null)).FirstOrDefaultAsync(ct);
        if (stop is null) return Error.NotFound("delivery_stop.not_found", "Arrêt introuvable.");
        if (Closed.Contains(stop.Status)) return Error.Conflict("delivery_stop.already_closed", "Arrêt déjà livré.");
        if (NotTheDriver(stop) is { } notDriver) return notDriver;

        if (request.SignatureAttachmentId is null && request.PhotoAttachmentId is null)
            return Error.Validation("delivery_stop.proof", "Preuve de livraison requise : signature du client ou photo.");
        if (await CheckAttachmentAsync(request.SignatureAttachmentId, "signature", ct) is { } sigError) return sigError;
        if (await CheckAttachmentAsync(request.PhotoAttachmentId, "photo", ct) is { } photoError) return photoError;

        var amount = request.AmountCollected is > 0m ? request.AmountCollected : null;
        if (amount is not null && stop.SalesOrderId is null)
            return Error.Validation("delivery_stop.amount", "Un encaissement à la livraison n'est possible que pour une commande.");
        if (amount is not null && request.PaymentFinancialAccountId is null)
            return Error.Validation("delivery_stop.account", "Indiquez la caisse qui reçoit l'encaissement.");

        // Position : seulement si l'entreprise l'a prévue ; sinon elle n'est jamais enregistrée.
        decimal? lat = null, lng = null;
        if (await CaptureLocationAsync(ct))
        {
            if (!ValidCoordinates(request.Latitude, request.Longitude))
                return Error.Validation("delivery_stop.coordinates", "Coordonnées invalides.");
            (lat, lng) = (request.Latitude, request.Longitude);
        }

        if (stop.SalesOrderId is { } orderId)
        {
            var tenders = amount is { } paid
                ? new List<OrderTenderInput> { new(request.PaymentFinancialAccountId!.Value, null, paid, "Encaissé à la livraison") }
                : [];
            var complete = await orders.CompleteOrderAsync(orderId,
                new CompleteOrderRequest(request.OperationUuid, null, tenders.Count == 0, null, tenders, null), ct);
            if (!complete.IsSuccess) return complete.Error!;
        }

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.delivery_stop SET status = 'delivered', latitude = COALESCE({lat}, latitude), longitude = COALESCE({lng}, longitude),
                       signature_attachment_id = {request.SignatureAttachmentId}, photo_attachment_id = {request.PhotoAttachmentId},
                       amount_collected = {amount}, payment_financial_account_id = {(amount is null ? null : request.PaymentFinancialAccountId)},
                       failure_reason = NULL, arrived_at = COALESCE(arrived_at, now()), delivered_at = now(),
                       completed_by = {tenant.UserId}, updated_at = now()
                WHERE id = {stopId} AND company_id = {Company}
                """, token);
            await StartRouteAsync(stop.DeliveryRouteId, token);
            await MaybeCompleteRouteAsync(stop.DeliveryRouteId, token);
            return 0;
        }, ct);
        return await GetStopDtoAsync(stopId, ct);
    }

    private async Task<Error?> CheckAttachmentAsync(Guid? id, string kind, CancellationToken ct)
    {
        if (id is not { } attachmentId) return null;
        var found = await db.Database.SqlQuery<string?>($"""
            SELECT kind AS "Value" FROM nexora.attachment WHERE id = {attachmentId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (found is null) return Error.Validation("delivery_stop.attachment", "Pièce jointe de preuve introuvable.");
        return found == kind ? null
            : Error.Validation("delivery_stop.attachment", kind == "signature" ? "La signature fournie n'est pas une signature." : "La photo fournie n'est pas une photo.");
    }

    public async Task<Result<DeliveryStopDto>> FailStopAsync(Guid stopId, FailDeliveryStopRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 3)
            return Error.Validation("delivery_stop.reason", "Motif de l'échec requis.");
        if (request.Reason.Length > 300) return Error.Validation("delivery_stop.reason", "Motif limité à 300 caractères.");
        var stop = await db.Database.SqlQuery<StopRow>(Stops(null, stopId, null)).FirstOrDefaultAsync(ct);
        if (stop is null) return Error.NotFound("delivery_stop.not_found", "Arrêt introuvable.");
        if (Closed.Contains(stop.Status)) return Error.Conflict("delivery_stop.already_closed", "Arrêt déjà livré.");
        if (NotTheDriver(stop) is { } notDriver) return notDriver;

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.delivery_stop SET status = 'failed', failure_reason = {request.Reason.Trim()}, completed_by = {tenant.UserId}, updated_at = now()
                WHERE id = {stopId} AND company_id = {Company}
                """, token);
            await StartRouteAsync(stop.DeliveryRouteId, token);
            await MaybeCompleteRouteAsync(stop.DeliveryRouteId, token);
            return 0;
        }, ct);
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
    }

    private async Task<Result<DeliveryStopDto>> GetStopDtoAsync(Guid stopId, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<StopRow>(Stops(null, stopId, null)).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("delivery_stop.not_found", "Arrêt introuvable.") : Result.Success(ToStopDto(row));
    }

    private async Task<DeliveryRouteDto> ToRouteDto(RouteRow r, CancellationToken ct)
    {
        var stops = await db.Database.SqlQuery<StopRow>(Stops(r.Id, null, null)).ToListAsync(ct);
        return new DeliveryRouteDto(r.Id, r.DriverUserId, r.RouteDate, r.Vehicle, r.Status, r.StartedAt, r.CompletedAt,
            stops.Select(ToStopDto).ToList(), r.DriverName);
    }

    private static DeliveryStopDto ToStopDto(StopRow r) => new(r.Id, r.DeliveryRouteId, r.SequenceNo, r.SalesOrderId, r.CustomerId, r.Address,
        r.Latitude, r.Longitude, r.ContactPhone, r.Status, r.SignatureAttachmentId, r.PhotoAttachmentId, r.AmountCollected, r.DeliveredAt,
        r.FailureReason, r.CustomerName, r.OrderNumber, r.OrderTotal, r.OrderDeposit, r.Currency, r.EnRouteAt, r.ArrivedAt,
        r.CompletedByName, r.DriverName, r.RouteDate);
}
