# cTrader Scanner Suite

Alert-only cTrader Automate (cAlgo) scanners for daily bars. Both scanners evaluate the **last completed daily bar** (no repaint), report the setup only, and place no trades. Entry = next open. No SL/PT is computed. `AccessRights.None`.

## Projects

| Folder | Algo | Purpose |
|---|---|---|
| `ReversalScanner/` | `ReversalScanner` cBot | Fade extended moves that roll over (blow-off / capitulation) |
| `ContinuationScanner/` | `ContinuationScanner` cBot | Trade trend resumptions after a pullback to EMA50 |
| `SupportResistance/` | `SupportResistance` indicator | Support/resistance levels (separate, unchanged) |
| `PpoReversalScanner/` | reference only | Legacy PPO-cross scanner. Does **not** build in this repo (links to projects that are not present). Kept as a reference; do not use |

Each scanner has a pure C# engine (`ReversalEngine.cs` / `ContinuationEngine.cs`) with no cAlgo dependencies, so the signal logic is unit-testable and deterministic. `ContinuationScanner.csproj` links `ReversalEngine.cs` from the ReversalScanner folder for shared indicator math, scheduling, and enum types.

## Signal logic (both scanners, daily EOD bars)

Indicator stack: **EMA50, SMA200 (continuations only), ATR(14) Wilder, PPO(16,32,9), CLV**. No RSI. All conditions are evaluated on the close of the last completed daily bar.

### Reversal (ReversalScanner)

Structure level = highest High / lowest Low of the trailing `Lookback` bars (default 5), **excluding** the trigger bar. No separate retest requirement.

| | Long Reversal | Short Reversal |
|---|---|---|
| Break | Close > lowest Low | Close < highest High |
| Close location | CLV >= +0.35 | CLV <= -0.35 |
| EMA extension | Close < EMA50 - 2.0*ATR | Close > EMA50 + 2.0*ATR |
| Momentum | PPO > PPOsig | PPO < PPOsig |
| Max distance | (Close - level)/ATR < 1.5 | (level - Close)/ATR < 1.5 |

### Continuation (ContinuationScanner)

| | Long Continuation | Short Continuation |
|---|---|---|
| Setup window | Low <= EMA50 somewhere in the trailing `Lookback` bars (default 5) | High >= EMA50 somewhere in the window |
| Trigger | Close > EMA50 (reclaim) | Close < EMA50 (breakdown) |
| Trend filter | Close > SMA200 | Close < SMA200 |
| Close location | CLV >= +0.35 | CLV <= -0.35 |
| Momentum | PPO > PPOsig | PPO < PPOsig |
| Max distance | (Close - swingLow)/ATR < 1.5 | (swingHigh - Close)/ATR < 1.5 |

`swingLow`/`swingHigh` = lowest Low / highest High of the lookback window excluding the trigger bar.

## Market-wide gates (once per scan pass)

| Gate | Scope | Rule | On missing data |
|---|---|---|---|
| SPY benchmark (group 4) | US equities (`.US`) only | Longs need SPY close > SPY SMA50, shorts need SPY close < SPY SMA50 (last completed SPY bar) | Bypassed (both sides allowed) |
| VIX long block (group 4b) | US equities only | Live VIX > 25 blocks longs; shorts never blocked | Bypassed |
| Crypto benchmark (group 4c) | Configured crypto list only | Longs need BTC close > BTC SMA50, shorts need BTC close < BTC SMA50 (last completed BTC daily bar, 24/7 logic) | Bypassed |

Crypto / FX / metals / commodities are exempt from the SPY and VIX gates by design. The crypto universe is a comma-separated parameter; spacing is ignored (`BTC EUR` matches `BTCEUR`).

## Pass-level re-verification

A trigger may be up to `Max Setup Age` (default 2) trading days old. Every scan pass re-checks it on the last completed bar and the live quote:

- **Live PPO intact**: a long whose PPO crossed back below its signal (or a short above) is stale and is not reported.
- **Live distance**: the live price (bid/ask mid, fallback last completed close) must still be within 1.5 ATR of the structural extreme. Entry happens at the next open with a live price, so the distance is measured against the live quote, not the old close.
- **Gates**: VIX / BTC / SPY regime must still allow the direction.

Alerts fire at most once per completed signal bar (identity = bar open time), so repeated passes never re-notify an unchanged setup.

## Scan architecture

- Watchlist-driven (default `Screener`), scanned in batches (default 20 symbols per timer tick) to keep the UI responsive.
- Scheduling: `DailyAfterClose` (default, 16:00 ET + 2s), `Hourly`, `Every15Minutes`, `CustomInterval`, `ManualOnly` plus an on-chart SCAN NOW button.
- No-repaint bar selection: US equities use session logic (last completed 09:30-16:00 ET session); 24/7 symbols use market-open state with phantom-rollover protection.
- On-chart HUD: gate states, progress, active setups with live ATR P&L, recent alerts.

## Build

Requires the .NET SDK and the `cTrader.Automate` NuGet package (restored automatically):

```
dotnet build ReversalScanner/ReversalScanner.csproj
dotnet build ContinuationScanner/ContinuationScanner.csproj
```

The engine files (`ReversalEngine.cs`, `ContinuationEngine.cs`) are plain C# and compile in any .NET 6+ project without the cTrader package, which makes the signal logic easy to unit-test outside the platform.
