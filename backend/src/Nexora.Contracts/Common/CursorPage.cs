namespace Nexora.Contracts.Common;

/// <summary>
/// Pagination par curseur (Phase 46 §11) — préférée à <see cref="PagedResult{T}"/> pour les listes
/// susceptibles de croître sans borne (ex. catalogue marketplace) : pas de <c>COUNT(*)</c>, tri
/// stable, curseur opaque. <see cref="PagedResult{T}"/> reste le motif établi (Phase 3 §8, §24)
/// pour les écrans qui ont besoin d'un numéro de page ; les deux coexistent délibérément.
/// </summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
