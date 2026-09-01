namespace CabinetNC.Desktop;

using CabinetNC.Domain.Parts;

/// <summary>Stock-stage list row: identical parts from several cnjobs collapse to one line.</summary>
public sealed class StockPartRow
{
    public required Panel Representative { get; init; }
    public required IReadOnlyList<Panel> Members { get; init; }

    public string DisplayPartName => Representative.DisplayPartName;
    public required string MaterialGroupLabel { get; init; }
    public string DisplayDetail
    {
        get
        {
            var qty = Members.Sum(p => Math.Max(1, p.Quantity));
            var pkgs = Members
                .Select(p => p.DisplayPackage)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var baseDetail = Representative.WithQuantity(qty).DisplayDetail;
            return pkgs > 1 ? $"{baseDetail} · {pkgs} 单" : baseDetail;
        }
    }
}
