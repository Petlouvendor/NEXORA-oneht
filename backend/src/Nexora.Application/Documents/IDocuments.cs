using Nexora.Contracts.Documents;
using Nexora.Domain.Common;

namespace Nexora.Application.Documents;

/// <summary>
/// Moteur documentaire commercial (Phase 30 §2, §8-13, §19, §31-33) : émission d'un document
/// à partir d'une source métier (snapshot figé + hash + numéro définitif + code de vérification),
/// annulation contrôlée, rendu, envoi. Ne modifie jamais la donnée source ni l'écriture comptable (§45).
/// </summary>
public interface IDocumentService
{
    Task<Result<IssuedDocumentDto>> IssueAsync(IssueDocumentRequest r, CancellationToken ct = default);
    Task<Result> CancelAsync(Guid documentId, CancelDocumentRequest r, CancellationToken ct = default);
    Task<Result<RenderedDocument>> RenderAsync(Guid documentId, string? format, CancellationToken ct = default);
    Task<Result<Guid>> SendAsync(Guid documentId, SendDocumentRequest r, CancellationToken ct = default);

    /// <summary>Vérification publique par code opaque (§27-28) — données minimales, sans authentification.</summary>
    Task<DocumentVerificationDto> VerifyAsync(string verificationCode, CancellationToken ct = default);
}

/// <summary>Lectures documentaires : recherche, détail, historique d'envoi, séquences (§34, §19).</summary>
public interface IDocumentQueries
{
    Task<IReadOnlyList<IssuedDocumentDto>> SearchAsync(DocumentSearchRequest r, CancellationToken ct = default);
    Task<Result<IssuedDocumentDetailDto>> GetAsync(Guid documentId, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentSendDto>> SendsAsync(Guid documentId, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentSequenceDto>> SequencesAsync(CancellationToken ct = default);
}
