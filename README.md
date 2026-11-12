# Crypto Order Book Dashboard

Real-time order book and trade tape dashboard for **Binance Spot** and **USD-M Futures**, built with **Blazor Server on .NET 10**.

It streams live market data over WebSockets, keeps fully synchronized local order books, and lets you filter both the book and the tape by order/trade size in USD, so the large orders and prints that move a market are easy to spot.

## Features

- **Live order books**: full diff-stream sync per symbol and market, with depth bars, spread, mid price and a feed status indicator (Connecting / Syncing / Synced / Reconnecting).
- **Size filter**: show only resting orders in a chosen notional range, from under $10k up to $100M+. Rows are colour-coded by size tier.
- **Trade tape**: live aggregated trades with buy/sell coloring, plus buy volume, sell volume and delta over the last 60 seconds.
- **Historical large-trade search**: when no recent trade matches the selected size range, search the last 7 or 30 days. Hourly volume is used to skip hours that can't contain a qualifying trade. The search reports progress, can be cancelled, and states clearly when its results are incomplete (rate limits, time cap, very busy hours). A server-wide limit caps concurrent searches so they can't exhaust the Binance rate limit the live feeds rely on.
- **Spot and Futures side by side** for every tracked symbol.
- Public market data only. **No API keys required.**

## Tech stack

- .NET 10 / ASP.NET Core Blazor Web App (Interactive Server render mode over SignalR)
- [Binance.Net](https://github.com/JKorf/Binance.Net) for REST, WebSocket and order book synchronization
- Bootstrap 5

## Getting started

Prerequisite: the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/bojanrendulic/CryptoOrderBookDashboard.git
cd CryptoOrderBookDashboard
dotnet run --project CryptoOrderBookDashboard
```

Then open http://localhost:5117. The order books need a few seconds to sync after startup.

For hot reload during development:

```bash
dotnet watch --project CryptoOrderBookDashboard
```

## Configuration

Tracked symbols and refresh settings live in `CryptoOrderBookDashboard/appsettings.json`:

```json
"OrderBookDashboard": {
  "DepthLevels": 20,
  "UpdateThrottleMilliseconds": 250,
  "MaxConcurrentHistoricalSearches": 2,
  "Symbols": [
    { "Symbol": "BTCUSDT", "Markets": [ "Spot", "UsdFutures" ] },
    { "Symbol": "ETHUSDT", "Markets": [ "Spot", "UsdFutures" ] }
  ]
}
```

| Setting | Meaning |
|---|---|
| `DepthLevels` | Price levels shown per side when no size filter is active |
| `UpdateThrottleMilliseconds` | How often the UI checks for fresh data (minimum 50 ms) |
| `MaxConcurrentHistoricalSearches` | Server-wide limit on historical searches running at once (default 2). Further searches wait in a queue. |
| `Symbols[].Symbol` | Binance symbol, e.g. `BTCUSDT` |
| `Symbols[].DisplayName` | Optional dropdown label. Defaults to the symbol without its quote asset (`BTCUSDT` → `BTC`). |
| `Symbols[].Markets` | `Spot` and/or `UsdFutures`. An empty list means both. |

The symbol dropdowns are built from this list, so adding a symbol here is all it takes.

## How it works

```
Binance WebSocket / REST
   ├─ OrderBookHostedService ─► synchronized order books ─► OrderBookStore ─┐
   └─ TradeFeedHostedService ─► aggTrade stream + backfill ─► TradeStore ───┤
                                                                            │
   RenderPulseService (every UpdateThrottleMilliseconds) ─ Changed ─────────┤
                                                                            ▼
                              OrderBookPanel / TradeTapePanel (per browser circuit)
```

Hosted background services own all exchange connections and write into singleton stores. UI components don't re-render on every exchange message. On each pulse they check a per-feed version number and re-render only if their data changed, which keeps SignalR traffic bounded no matter how busy the market is. The order book store copies each book at most once per change and shares that copy between all viewers.

## Disclaimer

This is a personal project for market observation. It is not affiliated with Binance and is not financial advice.

## License

[MIT](LICENSE)
