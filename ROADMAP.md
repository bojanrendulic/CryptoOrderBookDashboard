# Roadmap

Ideas for taking the dashboard from "live order book viewer" to a full market-structure tool. Items are grouped by theme and roughly ordered by value for effort within each group. Status reflects the `main` branch.

**Legend:** ✅ done · 🟡 partly done · ⬜ planned

## Overview

| # | Item | Theme | Status |
|---|---|---|---|
| 1 | Funding rates | Market data | ✅ |
| 2 | Long/short ratio | Market data | ✅ |
| 3 | Bid/ask depth imbalance | Market data | ✅ |
| 4 | Open interest vs price | Market data | 🟡 |
| 5 | Liquidations feed | Market data | ⬜ |
| 6 | Feed health and stale-data indicator | Reliability | 🟡 |
| 7 | Depth chart | Visualization | ✅ |
| 8 | Order book heatmap over time | Visualization | ⬜ |
| 9 | Multi-symbol watchlist | Visualization | ⬜ |
| 10 | Alerts | Product | ⬜ |
| 11 | API weight / rate-limit monitor | Operations | ⬜ |
| 12 | History and data retention | Architecture | ⬜ |
| 13 | Shared state across instances (Redis) | Architecture | ⬜ |

## Market data

Extra data that complements the order book and trade tape.

### 1. Funding rates ✅

Shows whether longs or shorts are paying, which is a strong sentiment signal on perpetuals.

- Done: the `/funding` page covers all USDT-M perpetuals. It shows live estimated rates from the `!markPrice@arr` stream, normalized to 8h, and 30 days of settled history from `/fapi/v1/fundingRate`. Rates are compared against the real neutral rate of +0.01%/8h, and extremes are highlighted.

### 2. Long/short ratio ✅

- Done: the funding detail panel shows the global long/short account ratio and the top traders' long/short position ratio.
- Possible follow-up: show history (the endpoints return up to 30 days at 5m–1d periods) instead of only the latest value.

### 3. Bid/ask depth imbalance ✅

A quick indicator of short-term pressure in the book.

- Done: the order book shows bid vs ask USD liquidity within ±0.5/1/2/5% of mid as an imbalance bar.
  - We use a price band instead of "top N levels", because N levels covers very different price distances depending on the symbol's tick size.
- Possible follow-up: a small sparkline of how the imbalance changes over the last few minutes. A single reading is noisy, and orders can be pulled at any moment.

### 4. Open interest vs price 🟡

Open interest (OI) is the total size of open positions. Read together with price, it shows whether a trend is "healthy":

| Price | OI | Usual reading |
|---|---|---|
| ↑ | ↑ | New money entering: trend supported |
| ↑ | ↓ | Shorts closing (short squeeze): weaker move |
| ↓ | ↑ | New shorts opening: bearish conviction |
| ↓ | ↓ | Longs closing or being liquidated: capitulation |

- Done: current OI and its 24h change are shown in the funding detail panel.
- Planned:
  - A price + OI chart over 24h–30d, using `/futures/data/openInterestHist`.
  - A label for the four quadrants above.

### 5. Liquidations feed ⬜

A live list of forced liquidations, in the style of Coinglass. Popular and visually striking.

- **Source:** the `!forceOrder@arr` WebSocket stream.
- **Caveat:** Binance sends at most **one liquidation per symbol per second** (the latest one), so the feed is a sample, not a complete record. The UI must say so, and totals must not be presented as exact.
- **UI:**
  - A tape (time, symbol, side, size in USD, price) with a size filter.
  - Rolling 1h/24h long vs short liquidation totals.
  - Optionally, markers on the trade tape.

## Visualization and UX

### 6. Feed health and stale-data indicator 🟡

Users have to know whether they can trust the numbers they are looking at.

- Done:
  - Each order book shows a status badge (Connecting / Syncing / Synced / Reconnecting).
  - Binance.Net reconnects and resyncs automatically.
  - The trade tape backfills after a reconnect.
- Planned:
  - A "stale" warning when no update has arrived for N seconds while the status still says Synced. A silent socket is the dangerous case.
  - "Last update" age on each panel.
  - A small global status indicator in the nav.
  - A `/health` endpoint (ASP.NET Core health checks) reporting the state of each feed.

### 7. Depth chart ✅

The classic cumulative bid/ask "V" chart next to the order book. Total liquidity is easier to read from it than from a table of numbers.

- Done:
  - `DepthChart` sits above the order book rows and uses the band from the Depth selector.
  - Cumulative USD is sampled at 120 price steps per side from the cached book copy in `OrderBookStore`.
  - It is drawn as inline SVG and refreshed at most once a second.
  - It is shown by default and can be collapsed.
- Possible follow-ups:
  - a hover crosshair with the exact price and cumulative size
  - a log scale option
  - hiding the chart by default and opening it with a button

### 8. Order book heatmap over time ⬜

Time on the X axis, price on the Y axis, colour for the resting size at each price. This is the strongest visual element of tools like this, and it shows walls appearing, moving and being pulled.

- **Capture:** a background service samples the grouped book (for example 0.05% buckets within ±5% of mid) every 1–2 seconds into an in-memory ring buffer per symbol.
- **Memory:** about 200 buckets × 1 sample/s × 1h ≈ 720k cells per book, so the window must be bounded.
- **Rendering:** a `<canvas>` drawn with a small JS module. SVG and DOM elements are too heavy at this size. Only new columns are sent over SignalR, not the whole matrix.
- Builds on the existing grouping code (`PriceGrouping`, `OrderBookStore.Group`).

### 9. Multi-symbol watchlist ⬜

A grid showing all tracked pairs at once. Each tile has:

- price and 24h change
- spread
- a mini depth imbalance bar
- funding rate
- an indicator for the largest wall within ±1%

Clicking a tile opens the full order book for that pair. All the data already exists in the stores; this is mostly UI work.

## Product

### 10. Alerts ⬜

The user sets a condition and gets notified, for example "a wall of $1M+ appears on BTC within 1% of price". This is the feature that keeps users coming back.

- **Possible conditions:**
  - a wall above size X within Y%
  - a trade above size X
  - funding rate above or below a threshold
  - a liquidation above size X
  - depth imbalance above a threshold
- **Evaluation:** server-side, on the existing render pulse, with cooldowns so one wall doesn't trigger 10 times.
- **Delivery:** an in-app toast first. Browser notifications (Notification API) and Telegram/Discord webhooks later.
- **Depends on:** persistent storage (#12) to keep alert definitions, and some notion of a user. The app has no authentication today.

## Architecture and operations

### 11. API weight / rate-limit monitor ⬜

Log how much Binance API weight the app uses. This is useful when debugging Binance problems, especially for historical trade searches and the funding history backfill.

- **Source:** Binance returns `X-MBX-USED-WEIGHT-1M` on every REST response.
- **Output:** record usage per endpoint and show it on a small internal page or in logs and metrics.
- **Warnings:** warn before reaching the limit, and make background jobs back off.

### 12. History and data retention ⬜

Store snapshots in a database (for example every 5 minutes) so trends can be shown over time, not just live:

- depth/imbalance per band
- funding
- open interest
- the largest walls

**Options:**
- SQLite for a single node (no extra infrastructure).
- PostgreSQL/TimescaleDB if the history grows.

**Retention policy:** downsample old data (5m → 1h after 7 days).

This is also the foundation for alerts (#10) and longer heatmap windows (#8).

### 13. Shared state across instances (Redis) ⬜

Only needed if the app ever runs on more than one server:

- background services write the books to Redis
- every instance reads from Redis
- Redis pub/sub replaces the in-process `Changed` event

**Not needed while there is one node.** Running more than one instance today would also open duplicate Binance connections per instance. Keep the store interfaces narrow so this can be added later without touching components.
