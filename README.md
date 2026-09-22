# cTrader Scanner Suite

Alert-only cTrader Automate (cAlgo) scanners for daily bars. Both scanners evaluate the **last completed daily bar** (no repaint), report the setup only, and place no trades. Entry = next open. No SL/PT is computed. `AccessRights.None`.

## Projects

| Folder | Algo | Purpose |
|---|---|---|
| `ReversalScanner/` | `ReversalScanner` cBot | Fade extended moves that roll over (blow-off / capitulation) |
| `ContinuationScanner/` | `ContinuationScanner` cBot | Trade trend resumptions after a pullback to EMA21 |
| `TradeManager/` | `TradeManager` cBot | Daily account-wide pending-order cancellation and TSI-based position exits |
| `TrueStrengthIndex/` | `TrueStrengthIndex` indicator | Blau TSI oscillator (25/13/13) with optional signal line and zero-line regime | 
| `SupportResistance/` | `SupportResistance` indicator | Support/resistance levels (separate, unchanged) |

Each scanner has a pure C# engine (`ReversalEngine.cs` / `ContinuationEngine.cs`) with no cAlgo dependencies, so the signal logic is unit-testable and deterministic. `ContinuationScanner.csproj` links `ReversalEngine.cs` from the ReversalScanner folder for shared indicator math, scheduling, and enum types.

`TradeManager` is separate from both scanners. It checks once per daily close and can apply two opt-in macro gates to pending orders (both default OFF, matching the scanner defaults): the SPY band for US-equity orders and the BTC/SMA band for configured crypto orders. Its always-on work is per-symbol and direction-aware: it cancels unfilled orders on a symbol-specific TSI 25/13/13 zero-line cross (against the order direction) or already-reached order TP, and closes positions only on the same TSI zero-line cross against the position direction. Zero-line crosses are tracked from the order/position creation time, so a cBot restart never misses an earlier cross: only the most recent cross counts, a reversal entry below the zero line is closed only after TSI first crossed above it and then crossed back against the position, and a pending exit that has not executed is disarmed when the regime recovers first. It never modifies orders or position SL/TP. TSI exits wait for spread <= 0.05 ATR or force a market close after the configured delay.

## Signal logic (both scanners, daily EOD bars)

Indicator stack: **EMA21, EMA50 trend alignment (continuations), SMA200 trend filter, ATR(14) Wilder, CLV** plus momentum: **TSI(25,13,13) — zero-line regime for continuations, divergence trigger for reversals**. No RSI. All conditions are evaluated on the close of the last completed daily bar.

### Reversal (ReversalScanner)

Two-step trigger. Step 1 — divergence detection (no pivot-confirmation lag): a bar in the last `TriggerWindow` bars made a fresh lookback extreme whose TSI diverged from the reference extreme. Newer, more extreme prints do NOT kill the setup: the divergence re-anchors to the newest extreme as long as the TSI keeps stepping in the divergent direction vs the previous extreme (divergence chain — price higher + TSI lower for shorts, mirrored for longs, including against the original strong reference when intermediate extremes are too weak to anchor). The setup only dies when momentum recovers at a newer extreme. Step 2 — the trigger: the signal bar's close location. The divergence bar and trigger bar may be the same bar. The optional next-bar confirmation (default OFF) must close beyond the signal bar high/low.

| | Long Reversal | Short Reversal |
|---|---|---|
| Divergence (step 1) | Low < lowest Low of the prior `DivergenceLookback` bars (reference >= `DivergenceMinGap` (3) bars back), reference TSI < -10, TSI >= reference TSI + 0.1, no lower Low since | High > highest High of the prior `DivergenceLookback` bars (reference >= `DivergenceMinGap` (3) bars back), reference TSI > +10, TSI <= reference TSI - 0.1, no higher High since |
| Trigger (step 2) | CLV >= +0.35 (strong close) | CLV <= -0.35 (weak close) |
| Trend filter | Close > SMA200 | Close < SMA200 |
| Confirmation (optional, default off) | Next close > signal-bar High | Next close < signal-bar Low |

### Continuation (ContinuationScanner)

| | Long Continuation | Short Continuation |
|---|---|---|
| EMA21 touch | Latest bar Low <= EMA21 | Latest bar High >= EMA21 |
| Trend alignment | EMA21 > EMA50 | EMA21 < EMA50 |
| Trigger | Close > EMA21 (reclaim) | Close < EMA21 (breakdown) |
| Trend filter | Close > SMA200 | Close < SMA200 |
| Close location | CLV >= +0.35 | CLV <= -0.35 |
| Momentum regime | TSI > 0 | TSI < 0 |
| Divergence suppression | No active bearish price/TSI divergence with the exact ReversalScanner rule (fresh lookback High with TSI at least `DivergenceMinTsiDrop` (0.1) below the reference extreme TSI, reference > +`DivergenceTsiExtremeLevel` (10)); parameters mirror the reversal thresholds | Mirrored for lows (fresh Low, TSI >= reference + 0.1, reference < -10) |

The continuation momentum gate is regime-only: the TSI zero line decides, and the TSI signal line (EMA 13 of TSI) is computed for display but is not part of the trigger. On top of the regime, the divergence suppression uses the **exact** ReversalScanner divergence detection (`ReversalEngine.HasActiveBearish/BullishTsiDivergence`): a fresh lookback extreme whose TSI diverges from the reference extreme kills the setup in that direction (bearish divergence suppresses longs, bullish divergence suppresses shorts), even when the symbol does not qualify for a reversal signal (e.g. price above SMA200). The TSI extreme gate (reference > +10 / < -10) keeps healthy trends from being suppressed by harmless lower-high lookbacks; `DivergenceTriggerWindow` 0 turns the suppression off. The `TrueStrengthIndex` indicator plots both lines so the regime can be checked visually.

## Market-wide gates (once per scan pass)

| Gate | Scope | Rule | On missing data |
|---|---|---|---|
| SPY benchmark (group 4) | US equities (`.US`) only | `BenchmarkBufferAtr` defaults to 0.5: longs are blocked only below SPY SMA50 - 0.5x SPY ATR; shorts only above SMA50 + 0.5x ATR. Inside the band both sides are allowed. Both scanners default it OFF: reversals are contrarian (the symbol's own SMA200 + TSI divergence supply the regime); continuations carry their own trend regime via EMA21/EMA50 alignment and the symbol SMA200, so a market-trend proxy only blocked strong leaders pulling back through a shallow market dip | Bypassed (both sides allowed) |
| VIX long block (group 4b) | US equities only | Last completed VIX close > 25 blocks longs; shorts never blocked. Default ON on ContinuationScanner only; default OFF on ReversalScanner (capitulation longs coincide with high VIX — the symbol's own SMA200 + TSI divergence carry the reversal regime) | Bypassed |
| Crypto benchmark (group 4c) | Configured crypto list only | Uses the same `BenchmarkBufferAtr` band: live BTC below BTC SMA50 - buffer blocks longs; above SMA50 + buffer blocks shorts; SMA/ATR use completed BTC daily bars. Default OFF on both scanners: the symbol's own SMA200/EMA/TSI regime carries the setup, so a BTC proxy only blocked alts showing relative strength/weakness independent of BTC | Bypassed |

Crypto / FX / metals / commodities are exempt from the SPY and VIX gates by design. The crypto universe is a comma-separated parameter; spacing is ignored (`BTC EUR` matches `BTCEUR`).

## Pass-level re-verification

A trigger uses the latest completed daily bar by default (`Max Setup Age = 0`). Every scan pass re-checks it on the last completed bar; the live BTC quote remains the deliberate exception for the crypto regime gate:

- **Momentum intact**: a reversal long whose TSI no longer sits at least the minimum drop below its reference TSI (or a short above) is stale and is not reported; a continuation long whose TSI fell back below zero (or a short above zero) is stale and is not reported.
- **Gates**: the completed VIX (long-block) regime is default ON on ContinuationScanner only; ReversalScanner defaults it OFF. The SPY-SMA50 and BTC-SMA gates are default OFF on both scanners (still available as parameters); when enabled, the crypto gate compares live BTC with an SMA of completed BTC bars.

Both scanners evaluate only the latest completed daily bar. Continuations require that same bar to touch EMA21 and close with the required reclaim/breakdown conditions. Reversal confirmation, when enabled, uses the immediately following completed bar after the signal bar.

### Which bar is "the last completed bar"?

US equities (`.US` with `US Session Bar Logic` on): the regular cash session closes at 16:00 ET, so a daily bar counts as completed once the 16:00 ET boundary of the session that bar carries has passed. That boundary is derived from the **bar's own open time** (`ReversalEngine.HasUsCashSessionEnded`), not from the calendar date of the bar stamp. Brokers stamp US-equity daily bars in different ways (00:00 UTC, 00:00 ET, cTrader's own 17:00 ET aggregation boundary, or the session open), but every daily bar window contains exactly one regular session, so this test works for all of them: it never evaluates a bar whose session is still running (no repaint) and never reports a signal from an older bar than the closed data allows.

Consequences worth knowing:

- The scan picks the newest bar whose session has closed, so a bar the broker pre-created for the next session (still untouched, or still trading after the close) can never hide the session that just finished.
- Outside market hours (after the close, pre-market, weekends, holidays) the whole series is closed and the newest closed session bar is scanned — the latest closed bar, not the one before it.
- During the regular session the newest bar is the session in progress, so the last closed session bar is scanned; the `DailyAfterClose` pass (16:00 ET + 2s) therefore reports the session that closed that day.
- A bar whose stamp is a day earlier than the session it carries is normal for cTrader's 17:00 ET aggregation; the alert prints both the scan timestamp and the signal bar's open date for this reason.

For every other instrument (FX, metals, commodities, indices, crypto) the bar selection stays market-state based: while the market is open the newest bar is treated as forming, and a closed market means every bar in the series is closed. Unknown market state is treated as open, and a bar stamped in the future or an untouched (no ticks, no range) bar is never evaluated.

Whether the live price is still tradeable is not checked by the scanner; the trader checks it manually before entering.

Alerts fire at most once per completed signal bar (identity = bar open time), so repeated passes never re-notify an unchanged setup. Every alert carries the scan timestamp and the signal bar's open date, so a setup that arrives late (e.g. the symbol's data feed lagged behind the chart) is immediately recognizable as such — the scanner never suppresses a valid signal, but it is up to the trader to check whether a late signal is still tradeable.

## Scan architecture

- Watchlist-driven (default `Screener`), scanned in batches (default 20 symbols per timer tick) to keep the UI responsive.
- Scheduling: `DailyAfterClose` (default, 16:00 ET + 2s), `Hourly`, `Every15Minutes`, `CustomInterval`, `ManualOnly` plus an on-chart SCAN NOW button.
- No-repaint bar selection: every signal and VIX/SPY/BTC SMA uses a completed daily bar. US equities derive it from each bar's own open time and the 16:00 ET session close (see *Which bar is "the last completed bar"?*); everything else is market-state based, and unknown market state is handled conservatively.
- On-chart HUD: gate states, progress, active setups with live ATR P&L, recent alerts.

## Build

Requires the .NET SDK and the `cTrader.Automate` NuGet package (restored automatically):

```
dotnet build ReversalScanner/ReversalScanner.csproj
dotnet build ContinuationScanner/ContinuationScanner.csproj
```

The engine files (`ReversalEngine.cs`, `ContinuationEngine.cs`) are plain C# and compile in any .NET 6+ project without the cTrader package, which makes the signal logic easy to unit-test outside the platform.
