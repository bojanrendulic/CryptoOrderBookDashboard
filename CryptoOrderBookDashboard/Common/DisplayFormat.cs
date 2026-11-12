using System.Globalization;
using CryptoOrderBookDashboard.OrderBooks;

namespace CryptoOrderBookDashboard.Common;

public static class DisplayFormat
{
    // Up to 8 decimals so small quantities (e.g. 0.00012 BTC) aren't rounded away;
    // trailing zeros are dropped.
    public static string Number(decimal value) => value.ToString("#,0.########", CultureInfo.InvariantCulture);

    public static string Usd(decimal value) => value.ToString("#,##0", CultureInfo.InvariantCulture);

    public static string Percent(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string MarketLabel(MarketType market) => market switch
    {
        MarketType.Spot => "Spot",
        MarketType.UsdFutures => "USD-M Futures",
        _ => market.ToString(),
    };
}
