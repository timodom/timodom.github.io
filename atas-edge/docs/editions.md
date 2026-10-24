# Edge bot editions: market and in-house

One source file, `Edge.Strategies/EdgeRangeBreakoutBot.cs`, builds two DLLs. The trading engine,
chart drawing and live order handling are identical; only the settings and safeguards differ.

| | In-house | Market |
|---|---|---|
| Project | `Edge.Strategies` | `Edge.Market` (defines `MARKET`) |
| DLL | `Edge.Atas.Strategies.dll` (also carries Edge Trail Manager) | `Edge.Atas.Market.dll` (bot only) |
| Name in ATAS | Edge Range Breakout Bot | Edge Bot |
| Entry | Setting (code default Retest) | **Locked to Immediate** (the validated setup) |
| Range times, cutoffs, range limits, entry options, filters, stop/target types, breakeven, Protect, trade/loser limits | All settings | Hidden, locked at defaults |
| Tick value, commission | Settings | Set from the instrument |
| Max daily loss | Setting ($, 0 = off) | Setting ($, **0 = instrument default**: ES $500, MES $50) |
| Contracts, Live Trading, Flatten on stop, UTC checkbox, dashboard, chart colors | Settings | Settings |
| Supported charts | Any | **ES or MES, 5- or 15-minute only**; anything else shows "Not running" and does nothing |

Both editions show the chart (symbol and bar size) on the dashboard.

## Validated setup the market edition locks

ES continuous, 15-minute bars, 2023-12-18 to 2026-10-23 (709 sessions): 509 trades, 50% win
rate, profit factor 1.30, +$57,314 on 1 ES, worst drawdown $10,974, t ≈ 2.6. Details in
`range-breakout-log.md`. NQ and MNQ are added to the market edition only after they pass the same
test.

## Before selling the market edition

- Move the code out of the public `timodom.github.io` repo into a private one.
- Obfuscate the DLL and add licensing (ATAS Marketplace or own keys).
- Pass the Replay audit of live order handling (see `range-breakout-log.md`).
- Ship with the hypothetical-performance disclaimer.
- Open: replace the "Chart bar times are UTC" checkbox with automatic detection.

## Building

```
dotnet build atas-edge\Edge.Strategies -c Release
dotnet build atas-edge\Edge.Market -c Release
```

Both copy their DLL into `%APPDATA%\ATAS\Indicators`. Reload indicators in ATAS to pick them up.
