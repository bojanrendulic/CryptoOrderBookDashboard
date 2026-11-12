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
- **`OrderBooks/OrderBookStore`** (singleton) holds the live library books. Each book has a version counter that is bumped on `OnOrderBookUpdate` and `OnStatusChange`. The full-book copy (`book.Book`, which reads both sides atomically) is cached per version and shared by all readers, so it is rebuilt only after a change, never once per viewer. `GetSnapshot` projects that cache into an immutable `OrderBookSnapshot`: either the top `DepthLevels` levels, or up to 100 levels in a USD notional range.
- **`Trades/TradeFeedHostedService`** subscribes to the aggTrade stream first, then backfills the last 1000 trades over REST, and backfills again on `ConnectionRestored`. `TradeStore` (singleton, newest-first, capacity 1000 per key) de-duplicates by Binance aggregate trade id (`TradeEntry.Id`), so the overlap between backfill and stream is safe. It also keeps a version counter per key.
- **`Common/RenderPulseService`** raises both stores' `Changed` event every `UpdateThrottleMilliseconds`. Components never re-render per exchange message, only on this pulse, and only when `Store.GetVersion(key)` differs from the version they last rendered. `TradeTapePanel` also refreshes at least once a second, because its 60s delta decays even when no trades arrive.
- **`Trades/HistoricalTradeSearchService`** (**singleton**, so that its `SemaphoreSlim` caps concurrent searches server-wide via `MaxConcurrentHistoricalSearches`; queued searches report `IsQueued` progress) finds trades in a notional range over the last 7 or 30 days. It fetches 1h klines, skips hours whose quote volume is below the minimum (that is a provable lower bound), then pages aggTrades for the candidate hours, 4 hours concurrently. Results carry an explicit status: `Completed`, `Incomplete` (page cap per hour hit), `TimedOut`, `ApiError`, `MatchLimitReached` or `Cancelled`. Never report a partial scan as `Completed`.
- **Keys**: `OrderBookKey(MarketType, Symbol)` identifies a feed in both stores.
- **`Common/`** holds the shared helpers: `NotionalRange` (size-filter presets) and `Notional` (`InRange`, `SizeTier`), and `DisplayFormat` (number/USD/market label formatting). Use these instead of re-implementing them in components or services.

### UI

- `Components/Pages/OrderBooks.razor` (`/`, `/orderbooks`) and `Trades.razor` (`/trades`) use `Components/Shared/MarketFilterBar` (symbol + size-range dropdowns, `@bind-Symbol` / `@bind-Range`) and render one panel per market from `TrackedSymbolOptions.GetMarkets()`. The symbol dropdown is built from configuration.
- `Components/OrderBook/OrderBookPanel` and `Components/Trades/TradeTapePanel` subscribe to store `Changed` in `OnInitialized` and unsubscribe on dispose. Their `OnStoreChanged` handlers are `async void` and must catch all exceptions, because anything escaping would crash the process. Panel instances are **reused** when the symbol or range changes (no `@key`), so `OnParametersSet` has to fully reset per-symbol state. `TradeTapePanel` detaches any running historical search there.
- `OrderBookPanel.razor.js` centers the order book on the spread by scrolling only the panel's `.order-book-columns` container, not the page.
- `Components/Layout/MainLayout.razor` has the top nav. Link new pages there.

### Hosting

- `Program.cs` registers options (`OrderBookDashboard` section → `OrderBookDashboardOptions`), `AddBinance()`, the stores, the hosted services and the search service, plus the standard Blazor Web App pipeline.
- Interactive Server means all component logic runs server-side over a SignalR circuit. `ReconnectModal` handles circuit drops.
- Static assets are referenced through `@Assets[...]`. Bootstrap is vendored under `wwwroot/lib/bootstrap/`.

## Configuration

`appsettings.json` → `OrderBookDashboard`: `DepthLevels`, `UpdateThrottleMilliseconds` (minimum 50), `MaxConcurrentHistoricalSearches` (default 2), and `Symbols[]` with `Symbol`, optional `DisplayName`, and `Markets` (`Spot` / `UsdFutures`; an empty list means both).

## Known limitations / ideas

- No authentication. Anyone who can reach the app can start historical searches, which are limited only by the server-wide concurrency cap.
- No health checks or metrics for feed status, beyond the status change logs.
- Filtered order book snapshots are still computed per viewer, although each one only enumerates the shared cached copy.
