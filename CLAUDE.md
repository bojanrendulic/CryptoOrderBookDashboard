# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Real-time Binance order book and trade tape dashboard (Spot + USD-M Futures), built as a .NET 10 Blazor Web App with Interactive Server render mode. Market data comes from public Binance endpoints via the `Binance.Net` package (CryptoExchange.Net). No API keys are used or needed.

## Commands

Run all commands from the repo root (`CryptoOrderBookDashboard.slnx`) or from the `CryptoOrderBookDashboard/` project directory.

```powershell
dotnet build                                   # build
dotnet run --project CryptoOrderBookDashboard  # run (Development launch profile: http://localhost:5117, https://localhost:7289)
dotnet watch --project CryptoOrderBookDashboard # run with hot reload
```

There are no test projects in the solution yet. If tests are added, add the test project to `CryptoOrderBookDashboard.slnx` and update this file with the corresponding `dotnet test` invocation.

## Architecture

Data flows one way: hosted services → singleton stores → UI components.

- **`OrderBooks/OrderBookHostedService`** creates one `BinanceSpotSymbolOrderBook` / `BinanceFuturesUsdtSymbolOrderBook` per configured symbol × market. It registers each one in `OrderBookStore` and starts it, retrying with backoff if the first start fails. `Limit` is intentionally left unset, so books do a full diff-stream sync and the size filter can find large orders deeper than the top N levels.
- **`OrderBooks/OrderBookStore`** (singleton) holds the live library books. `GetSnapshot` reads both sides atomically through `book.Book` and projects them into an immutable `OrderBookSnapshot`: either the top `DepthLevels` levels, or up to 100 levels in a USD notional range.
- **`Trades/TradeFeedHostedService`** subscribes to the aggTrade stream first, then backfills the last 1000 trades over REST, and backfills again on `ConnectionRestored`. `TradeStore` (singleton, newest-first, capacity 1000 per key) de-duplicates by Binance aggregate trade id (`TradeEntry.Id`), so the overlap between backfill and stream is safe.
- **Render pulse services** (`OrderBookRenderPulseService`, `TradeRenderPulseService`) raise the stores' `Changed` event every `UpdateThrottleMilliseconds`. Components never re-render per exchange message, only on this pulse.
- **`Trades/HistoricalTradeSearchService`** (scoped) finds trades in a notional range over the last 7 or 30 days. It fetches 1h klines, skips hours whose quote volume is below the minimum (that is a provable lower bound), then pages aggTrades for the candidate hours, 4 hours concurrently. Results carry an explicit status: `Completed`, `Incomplete` (page cap per hour hit), `TimedOut`, `ApiError`, `MatchLimitReached` or `Cancelled`. Never report a partial scan as `Completed`.
- **Keys**: `OrderBookKey(MarketType, Symbol)` identifies a feed in both stores.

### UI

- `Components/Pages/OrderBooks.razor` (`/`, `/orderbooks`) and `Trades.razor` (`/trades`) have a symbol dropdown and a size-range dropdown, and render one panel per market. The symbol list in the dropdowns is hard-coded and must be kept in sync with `appsettings.json`.
- `Components/OrderBook/OrderBookPanel` and `Components/Trades/TradeTapePanel` subscribe to store `Changed` in `OnInitialized` and unsubscribe on dispose. Their `OnStoreChanged` handlers are `async void` and must catch all exceptions, because anything escaping would crash the process. Panel instances are **reused** when the symbol or range changes (no `@key`), so `OnParametersSet` has to fully reset per-symbol state. `TradeTapePanel` detaches any running historical search there.
- `OrderBookPanel.razor.js` centers the order book on the spread via JS interop.
- `Components/Layout/MainLayout.razor` has the top nav. Link new pages there.

### Hosting

- `Program.cs` registers options (`OrderBookDashboard` section → `OrderBookDashboardOptions`), `AddBinance()`, the stores, the hosted services and the search service, plus the standard Blazor Web App pipeline.
- Interactive Server means all component logic runs server-side over a SignalR circuit. `ReconnectModal` handles circuit drops.
- Static assets are referenced through `@Assets[...]`. Bootstrap is vendored under `wwwroot/lib/bootstrap/`.

## Configuration

`appsettings.json` → `OrderBookDashboard`: `DepthLevels`, `UpdateThrottleMilliseconds` (minimum 50), and `Symbols[]` with `Symbol` and `Markets` (`Spot` / `UsdFutures`; an empty list means both).

## Known limitations / ideas

- Every circuit copies the full order book on each pulse (`book.Book` returns copies). Caching one snapshot per key per pulse would scale better with many viewers.
- Pulses fire even when nothing changed.
- Historical search has no global concurrency limit, and all users share the server's Binance IP rate limit with the live feeds.
- Helpers are duplicated (`MatchesRange`, `SizeTier`, `FormatNumber`, and the range dropdown with its parsing).
