# Crypto Order Book Dashboard

Real-time order book and trade tape dashboard for **Binance Spot** and **USD-M Futures**, built with **Blazor Server on .NET 10**.

It streams live market data over WebSockets, keeps fully synchronized local order books, and lets you filter both the book and the tape by order/trade size in USD, so the large orders and prints that move a market are easy to spot.

## Features

- **Live order books**: full diff-stream sync per symbol and market, with depth bars, spread, mid price and a feed status indicator (Connecting / Syncing / Synced / Reconnecting).
- **Size filter, grouping and depth**: show only resting orders above a minimum size (≥ $10k up to ≥ $50M), group nearby prices into buckets (0.01%–0.5% of price) so walls split across many price levels show up as one row, and see the total bid vs ask liquidity within ±0.5%, 1%, 2% or 5% of mid as an imbalance bar and a cumulative depth chart (the classic "V") that shows the price and cumulative USD under the mouse. With Depth set to "Rows", the bar and chart follow exactly the filtered rows in the table. Each row shows its approximate USD value, and rows are colour-coded by size tier. Binance only snapshots the nearest 5000 (spot) or 1000 (futures) price levels when a book syncs, so deeper levels are filled in from live updates over time; the dashboard marks that part of the book (dimmed rows, a faint chart segment, "≥" depth figures) and shows how far the snapshot reaches. On the Trades page the size filter uses ranges (under $10k up to $100M+).
- **Trade tape**: live aggregated trades with buy/sell coloring, plus buy volume, sell volume and delta over the last 60 seconds.
- **Historical large-trade search**: when no recent trade matches the selected size range, search the last 7 or 30 days. Hourly volume is used to skip hours that can't contain a qualifying trade. The search reports progress, can be cancelled, and states clearly when its results are incomplete (rate limits, time cap, very busy hours). A server-wide limit caps concurrent searches so they can't exhaust the Binance rate limit the live feeds rely on.
- **Funding rates** (`/funding`) for all ~900 USDT-M perpetuals. Shows the live estimated rate (normalized to 8h, since many symbols settle every 4h or 1h), annualized rate, mark vs index premium, a countdown to settlement and 30 days of settled history with daily bars. Each row carries a sentiment label measured against the real neutral rate of +0.01%/8h, and extremes (crowded longs or shorts) are highlighted. A market-wide summary shows the median funding and how many perpetuals are crowded on each side. Selecting a symbol adds open interest with its 24h change, the long/short account ratio and the top traders' position ratio. Direct link: `/funding?symbol=BTCUSDT`.
- **Liquidations** (`/liquidations`) for all USDT-M perpetuals from the `!forceOrder@arr` stream: a filterable live tape, rolling 1h/24h long vs short totals, the top symbols over 24h and a "sampling pressure" list. Binance publishes at most one liquidation per symbol per second, so everything is clearly marked as **sampled** and totals are shown as lower bounds (≥). There is no public liquidation history, so the page also shows since when data has been collected and any gaps.
- **SQLite persistence**: liquidations survive restarts. Per-minute totals of every liquidation and the individual large ones (≥ $200k by default) are stored in a local SQLite file (`data/dashboard.db`, WAL mode, batched transactions from a single writer, one-year retention by default).
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

Persistence and liquidations:

```json
"Persistence": {
  "DatabasePath": "data/dashboard.db",
  "RetentionDays": 365
},
"Liquidations": {
  "MinPersistUsd": 200000
}
```

| Setting | Meaning |
|---|---|
| `Persistence:DatabasePath` | SQLite file, relative to the app folder. Created on first start. |
| `Persistence:RetentionDays` | Rows older than this are deleted daily. `0` keeps everything. |
| `Liquidations:MinPersistUsd` | Individual liquidations at or above this USD size are stored. Per-minute totals are always stored. |

## Tests

```bash
dotnet test
```

## How it works

```
Binance WebSocket / REST
   ├─ OrderBookHostedService ─► synchronized order books ─► OrderBookStore ─┐
   ├─ TradeFeedHostedService ─► aggTrade stream + backfill ─► TradeStore ───┤
   ├─ FundingRateHostedService ─► !markPrice@arr + funding history ─┐       │
   ├─ PositioningService ─► open interest, long/short ratios ───────┴► FundingStore
   └─ LiquidationHostedService ─► !forceOrder@arr ─► LiquidationStore ◄─► SQLite (PersistenceWriter)
                                                                            │
   RenderPulseService (every UpdateThrottleMilliseconds) ─ Changed ─────────┤
                                                                            ▼
                              OrderBookPanel / TradeTapePanel (per browser circuit)
```

Hosted background services own all exchange connections and write into singleton stores. UI components don't re-render on every exchange message. On each pulse they check a per-feed version number and re-render only if their data changed, which keeps SignalR traffic bounded no matter how busy the market is. The order book store copies each book at most once per change and shares that copy between all viewers.

## Roadmap

Planned features (order book heatmap, alerts, watchlist, a database of large trades and more) are tracked in [ROADMAP.md](ROADMAP.md).

## Disclaimer

This is a personal project for market observation. It is not affiliated with Binance and is not financial advice.

## License

[MIT](LICENSE)
