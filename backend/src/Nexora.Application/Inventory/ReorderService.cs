namespace Nexora.Application.Inventory;

public interface IReorderService
{
    /// <summary>Produits sous le point de commande (Phase 5 §19, §35).</summary>
    Task<IReadOnlyList<ReorderSuggestion>> SuggestionsAsync(CancellationToken ct = default);
}
