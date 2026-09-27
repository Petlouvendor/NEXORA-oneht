namespace Nexora.Domain.Common;

/// <summary>
/// Porte une <see cref="Error"/> métier jusqu'à la couche de présentation, qui la
/// traduit en réponse HTTP structurée (RFC 7807 — Phase 3 §8). À réserver aux cas
/// où le flux <see cref="Result"/> n'est pas praticable (ex. service invoqué hors
/// contrôleur).
/// </summary>
public sealed class DomainException(Error error) : Exception(error.Message)
{
    public Error Error { get; } = error;
}
