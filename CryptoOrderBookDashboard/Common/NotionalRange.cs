namespace CryptoOrderBookDashboard.Common;

// A USD (quote currency) notional bucket, used by the size filter dropdowns.
// Min is inclusive, Max exclusive; null means unbounded on that side.
public sealed record NotionalRange(string Key, string Label, decimal? Min, decimal? Max)
{
    public static readonly IReadOnlyList<NotionalRange> Presets =
    [
        new("0-10k", "< $10k", 0m, 10_000m),
        new("10k-50k", "$10k – $50k", 10_000m, 50_000m),
        new("50k-250k", "$50k – $250k", 50_000m, 250_000m),
        new("250k-1m", "$250k – $1M", 250_000m, 1_000_000m),
        new("1m-5m", "$1M – $5M", 1_000_000m, 5_000_000m),
        new("5m-25m", "$5M – $25M", 5_000_000m, 25_000_000m),
        new("25m-50m", "$25M – $50M", 25_000_000m, 50_000_000m),
        new("50m-100m", "$50M – $100M", 50_000_000m, 100_000_000m),
        new("100m+", "$100M+", 100_000_000m, null),
    ];

    public static NotionalRange Default => Presets[0];

    public static NotionalRange FromKey(string? key) =>
        Presets.FirstOrDefault(r => r.Key == key) ?? Default;
}

public static class Notional
{
    public static bool InRange(decimal notional, decimal? min, decimal? max) =>
        (min is null || notional >= min.Value) && (max is null || notional < max.Value);

    // Visual weight of a row's size marker - shared by order book and trade tape so the
    // same notional always looks the same in both.
    public static int SizeTier(decimal notionalUsd) => notionalUsd switch
    {
        < 10_000m => 0,
        < 50_000m => 1,
        < 250_000m => 2,
        < 1_000_000m => 3,
        < 5_000_000m => 4,
        _ => 5,
    };
}
