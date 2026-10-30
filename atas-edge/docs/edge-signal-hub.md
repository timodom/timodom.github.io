# Edge Signal Hub

Posts the Opening Range model's events to a Discord channel for ZeroDayTrades members. It runs its own copy of the
Opening Range Bot's engine on closed 15-minute bars, with the market edition's locked setup (immediate entry plus the
instrument profile). It never reads the bot, your account or your orders, and it places nothing.

## What it posts
- Range set when the 09:30 bar closes (or "skipped today" when the range is outside the profile's limits)
- Entry, stop moved (protect), exit
- Every post ends with: *Model signal, simulated, not a live trade. Hypothetical results; not financial advice.*

Posted prices are the model's; live fills can differ by slippage. On an ES 5-minute chart the market edition of the bot
decides on 5-minute closes, so its ES trades can differ from these posts. NQ decides on 15-minute closes everywhere.

## Setup
1. Create a Discord webhook for the channel (channel settings → Integrations → Webhooks → New Webhook → Copy Webhook URL).
2. Add **Edge Signal Hub** (category Edge) to the **ES 15m** chart and the **NQ 15m** chart. A 5-minute chart is
   refused: the posts must come from the validated 15-minute model.
3. Paste the URL into **Discord webhook** and click OK. The field then shows `saved (…last 4)`. The URL itself is kept
   in `C:\ATAS\secrets\discord-signals.txt`, never in the workspace or a chart template. One webhook serves both charts.
4. Tick **Send test post**. The test line should appear in the channel.
5. Tick **Post to Discord**.

The hub is in the in-house `Edge.Atas.Research.dll` only. Never ship that DLL to customers.

Check the chart's "Chart bar times are UTC" setting the same way as the bot's: the range must be built from the
09:30 ET bar.

## Rules
- Only bars that close while the chart is live are posted. Reloading a chart or restarting ATAS never replays history.
  An exit seen after a restart says the entry was before the restart.
- ES/MES share one hub, as do NQ/MNQ. A second hub on the same family alerts and stays silent.
- Other instruments and timeframes raise a "Not running" alert and post nothing.
- Log: `C:\ATAS\signals\edge-signal-hub-ES.log` / `-NQ.log`. A running hub writes `Started on … 15m` when it loads.
  Delivery failures are logged and alerted once per cause. A hub that stops at a check (wrong chart, second hub) writes
  no log; read its alert instead.

## Tests
```
dotnet run --project atas-edge/tests/SignalHub.Tests -c Release
dotnet run --project atas-edge/tests/SignalHub.Tests -c Release -- --replay <Edge Bar Export csv> --symbol ES
```
The replay's trade count and net must equal `tests/RangeResearch`'s "ORB-15 immediate entry" row on the same file.
On 2026-10-25: `ES-67289.csv` 509 trades, +$57,314; `NQ-67287.csv` 585 trades, +$158,945, both equal to the harness.

## Building
`dotnet build atas-edge/Edge.Research -c Release`, with ATAS closed. This needs the private `zerodaytrade-bots` checkout
for `shared/DiscordPacer.cs` (default `%USERPROFILE%\Documents\zerodaytrade-bots`; override with `-p:ZDT_BOTS=...`).
