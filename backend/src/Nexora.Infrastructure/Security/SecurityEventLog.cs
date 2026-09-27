using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Security;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Security;

/// <summary>
/// Écrit les évènements de sécurité dans <c>nexora.audit_log</c> (Phase 33 §33-34). Le journal
/// n'accepte que l'insertion (trigger d'immuabilité sur UPDATE/DELETE, V030). Exécuté en portée
/// système : la connexion et la MFA se produisent avant l'établissement du contexte tenant.
/// </summary>
public sealed class SqlSecurityEventLog(NexoraDbContext db, ITenantContext tenant) : ISecurityEventLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RecordAsync(
        string action, Guid? userId, Guid? companyId, string? detail = null,
        string? ipAddress = null, object? context = null, CancellationToken ct = default)
    {
        var ctx = JsonSerializer.Serialize(new
        {
            detail,
            source = "auth",
            data = context,
        }, Json);

        using var _ = tenant.BeginSystemScope();
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.audit_log (company_id, user_id, action, entity_type, entity_id, ip_address, context)
                VALUES ({companyId}, {userId}, {action}, 'security_event', {userId}, {ipAddress}::inet, {ctx}::jsonb)
                """, ct);
        }
        catch
        {
            // La journalisation ne doit jamais faire échouer l'opération de sécurité elle-même.
        }
    }
}
