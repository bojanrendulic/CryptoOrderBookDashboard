using System.Globalization;
using CryptoOrderBookDashboard.OrderBooks;

namespace CryptoOrderBookDashboard.Common;

public static class DisplayFormat
{
    // Up to 8 decimals so small quantities (e.g. 0.00012 BTC) aren't rounded away;
    // trailing zeros are dropped.
    public static string Number(decimal value) => value.ToString("#,0.########", CultureInfo.InvariantCulture);

    // Fixed 3 decimals so order book quantity columns line up.
    public static string Quantity(decimal value) => value.ToString("#,0.000", CultureInfo.InvariantCulture);

    public static string Usd(decimal value) => value.ToString("#,##0", CultureInfo.InvariantCulture);

    public static string Percent(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // Signed percentage of a fraction, e.g. 0.0001 -> "+0.0100%".
    public static string SignedRate(decimal fraction, int decimals = 4) =>
        (fraction * 100m).ToString($"+0.{new string('0', decimals)};-0.{new string('0', decimals)};0.{new string('0', decimals)}", CultureInfo.InvariantCulture) + "%";

    // Signed value that is already a percentage, e.g. 12.3 -> "+12.30%".
    public static string SignedPercent(decimal percent, int decimals = 2) =>
        percent.ToString($"+0.{new string('0', decimals)};-0.{new string('0', decimals)};0.{new string('0', decimals)}", CultureInfo.InvariantCulture) + "%";

    public static string CompactUsd(decimal value) => Math.Abs(value) switch
    {
        >= 1_000_000_000m => "$" + (value / 1_000_000_000m).ToString("0.00", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000m => "$" + (value / 1_000_000m).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 1_000m => "$" + (value / 1_000m).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => "$" + value.ToString("0", CultureInfo.InvariantCulture),
    };

    public static string Countdown(TimeSpan remaining) =>
        remaining <= TimeSpan.Zero
            ? "settling…"
            : $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";

    public static string MarketLabel(MarketType market) => market switch
    {
        MarketType.Spot => "Spot",
        MarketType.UsdFutures => "USD-M Futures",
        _ => market.ToString(),
    };
}
