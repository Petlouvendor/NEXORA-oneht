namespace Nexora.Domain.Common;

/// <summary>Devise — table nexora.currency (référentiel global).</summary>
public sealed class Currency : Entity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public short DecimalPlaces { get; set; } = 2;
    public string Status { get; set; } = "active";
}
