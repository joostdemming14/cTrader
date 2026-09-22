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
| `PpoReversalScanner/` | reference only | Legacy PPO-cross scanner. Does **not** build in this repo (links to projects that are not present). Kept as a reference; do not use |

Each scanner has a pure C# engine (`ReversalEngine.cs` / `ContinuationEngine.cs`) with no cAlgo dependencies, so the signal logic is unit-testable and deterministic. `ContinuationScanner.csproj` links `ReversalEngine.cs` from the ReversalScanner folder for shared indicator math, scheduling, and enum types.

`TradeManager` is separate from both scanners. It checks once per daily close, applies the SPY/VIX macro gate only to US-equity pending orders, applies the BTC/SMA crypto macro gate only to configured crypto pending orders, cancels unfilled orders on a symbol-specific TSI 25/13/13 zero-line cross (against the order direction) or already-reached order TP, and closes positions only on the same TSI zero-line cross against the position direction. It never modifies orders or position SL/TP. TSI exits wait for spread <= 0.05 ATR or force a market close after the configured delay.

## Signal logic (both scanners, daily EOD bars)

Indicator stack: **EMA21, EMA50 trend alignment (continuations), SMA200 trend filter, ATR(14) Wilder, CLV** plus momentum: **TSI(25,13,13) — zero-line regime for continuations, divergence trigger for reversals**. No RSI. All conditions are evaluated on the close of the last completed daily bar.

### Reversal (ReversalScanner)

Two-step trigger. Step 1 — divergence detection (no pivot-confirmation lag): a bar in the last `TriggerWindow` bars made a fresh lookback extreme whose TSI diverged from the reference extreme; the setup dies when a more extreme print follows. Step 2 — the trigger: the signal bar's close location. The divergence bar and trigger bar may be the same bar. The optional next-bar confirmation (default OFF) must close beyond the signal bar high/low.

| | Long Reversal | Short Reversal |
|---|---|---|
| Divergence (step 1) | Low < lowest Low of the prior `DivergenceLookback` bars (reference >= `DivergenceMinGap` (3) bars back), reference TSI < -10, TSI >= reference TSI + 1.0, no lower Low since | High > highest High of the prior `DivergenceLookback` bars (reference >= `DivergenceMinGap` (3) bars back), reference TSI > +10, TSI <= reference TSI - 1.0, no higher High since |
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
| Divergence guard | No price/TSI divergence over the last `DivergenceGuardBars` (5) bars (long blocked when price is up but TSI is down) | Mirrored (short blocked when price is down but TSI is up) |

The continuation momentum gate is regime-only: the TSI zero line decides, and the TSI signal line (EMA 13 of TSI) is computed for display but is not part of the trigger. On top of the regime, a divergence guard (default 5 bars, 0 = off) rejects a setup when price moved net up over the guard window while TSI moved net down (shorts mirrored) — a pullback where price and TSI move together is unaffected. The `TrueStrengthIndex` indicator plots both lines so the regime can be checked visually. The `TrueStrengthIndex` indicator plots both lines so the regime can be checked visually.

## Market-wide gates (once per scan pass)

| Gate | Scope | Rule | On missing data |
|---|---|---|---|
| SPY benchmark (group 4) | US equities (`.US`) only | `BenchmarkBufferAtr` defaults to 0.5: longs are blocked only below SPY SMA50 - 0.5x SPY ATR; shorts only above SMA50 + 0.5x ATR. Inside the band both sides are allowed. Both scanners default it OFF: reversals are contrarian (the symbol's own SMA200 + TSI divergence supply the regime); continuations carry their own trend regime via EMA21/EMA50 alignment and the symbol SMA200, so a market-trend proxy only blocked strong leaders pulling back through a shallow market dip | Bypassed (both sides allowed) |
| VIX long block (group 4b) | US equities only | Last completed VIX close > 25 blocks longs; shorts never blocked | Bypassed |
| Crypto benchmark (group 4c) | Configured crypto list only | Uses the same `BenchmarkBufferAtr` band: live BTC below BTC SMA50 - buffer blocks longs; above SMA50 + buffer blocks shorts; SMA/ATR use completed BTC daily bars. Default OFF on both scanners: the symbol's own SMA200/EMA/TSI regime carries the setup, so a BTC proxy only blocked alts showing relative strength/weakness independent of BTC | Bypassed |

Crypto / FX / metals / commodities are exempt from the SPY and VIX gates by design. The crypto universe is a comma-separated parameter; spacing is ignored (`BTC EUR` matches `BTCEUR`).

## Pass-level re-verification

A trigger uses the latest completed daily bar by default (`Max Setup Age = 0`). Every scan pass re-checks it on the last completed bar; the live BTC quote remains the deliberate exception for the crypto regime gate:

- **Momentum intact**: a reversal long whose TSI no longer sits at least the minimum drop below its reference TSI (or a short above) is stale and is not reported; a continuation long whose TSI fell back below zero (or a short above zero) is stale and is not reported.
- **Gates**: the completed VIX (long-block) regime is the only market gate left ON by default. The SPY-SMA50 and BTC-SMA gates are default OFF on both scanners (still available as parameters); when enabled, the crypto gate compares live BTC with an SMA of completed BTC bars.

Both scanners evaluate only the latest completed daily bar. Continuations require that same bar to touch EMA21 and close with the required reclaim/breakdown conditions. Reversal confirmation, when enabled, uses the immediately following completed bar after the signal bar.

Whether the live price is still tradeable is not checked by the scanner; the trader checks it manually before entering.

Alerts fire at most once per completed signal bar (identity = bar open time), so repeated passes never re-notify an unchanged setup.

## Scan architecture

- Watchlist-driven (default `Screener`), scanned in batches (default 20 symbols per timer tick) to keep the UI responsive.
- Scheduling: `DailyAfterClose` (default, 16:00 ET + 2s), `Hourly`, `Every15Minutes`, `CustomInterval`, `ManualOnly` plus an on-chart SCAN NOW button.
- No-repaint bar selection: every signal and VIX/SPY/BTC SMA uses a completed daily bar; US equities use session logic and unknown market state is handled conservatively.
- On-chart HUD: gate states, progress, active setups with live ATR P&L, recent alerts.

## Build

Requires the .NET SDK and the `cTrader.Automate` NuGet package (restored automatically):

```
dotnet build ReversalScanner/ReversalScanner.csproj
dotnet build ContinuationScanner/ContinuationScanner.csproj
```

The engine files (`ReversalEngine.cs`, `ContinuationEngine.cs`) are plain C# and compile in any .NET 6+ project without the cTrader package, which makes the signal logic easy to unit-test outside the platform.
