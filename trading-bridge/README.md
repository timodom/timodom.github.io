# Trading Bridge

Run N trading bots on **one** machine, one ATAS instance, one broker account —
instead of one machine per bot.

## The idea

Today each bot is a monolith: data, logic, and execution all welded to an ATAS
chart. That is why scaling looks like scaling *machines*. This splits it:

```
Broker feed
    │
    ▼
ATAS (ONE instance)
  └─ BridgeStrategy ......... a dumb pipe: data out, orders in
    │  ▲
    │  │  TCP loopback, pipe-delimited lines
    ▼  │
StrategyHost (ONE process)
  ├─ NettingEngine .......... N virtual positions over 1 real account
  ├─ RiskGate ............... account limits + kill switch
  ├─ bot1 bot2 bot3 bot4 bot5 ... plain C# classes, zero ATAS types
  └─ SignalSink ............. Discord webhook + CSV journal
```

Two properties do the work:

1. **Only one file knows ATAS exists.** When ATAS ships a breaking update you fix
   `BridgeStrategy.cs` once, not five bots.
2. **Bots declare target positions, not orders.** `SetTarget("ES", +2)` is
   idempotent, so a disconnect, restart, or dropped message self-heals on the next
   evaluation instead of leaving a phantom position.

## Netting: how five bots share one account

Without netting, bot A long 2 and bot B short 1 leaves the broker showing +1 and
both bots' stop logic reading a position that isn't theirs.

`NettingEngine` keeps a **virtual position per bot**, crosses opposing intent
internally, and sends only the residual to the broker:

| | bot A | bot B | broker |
|---|---|---|---|
| A goes long 2 | +2 | 0 | **buy 2** |
| A exits, B enters 2 (same bar) | 0 | +2 | **nothing — crossed internally** |
| B exits | 0 | 0 | **sell 2** |

That middle row is the point: the position changes hands inside your process. No
commission, no slippage, no broker round-trip. On the 500-bar sample run, three
bots crossed **530 contracts** that never reached the broker.

Guarantees, all under test:

- `sum(bot positions) == broker position`, always — verified after every mutation
  across a 500-iteration randomised run
- Partial fills split pro-rata by largest-remainder, so integer contracts never
  get lost or invented
- Manual trades you place by hand land on a **house leg** and are never
  mis-attributed to a bot
- A rejected order releases its working quantity and retries; **repeated** rejects
  trip a circuit breaker instead of an infinite order storm
- Average price survives a flip through zero, so per-bot P&L stays honest

## Monday: test it without ATAS, without a broker

The replay harness implements the same `IMarketLink` as the live socket, so bots
cannot tell the difference. This is the real trading code, not a simulation of it.

```bash
dotnet test                                                    # 22 tests, ~1s
dotnet run --project src/Bridge.Host -- replay sample-data/ES-1m.csv ES
```

You get per-signal output, a `signals.csv` journal, a count of internally crossed
contracts, and the final net position.

Fill model: market orders fill at the **next** bar's open plus slippage, never at
the close of the bar that triggered them — filling at the signal bar's close is
the most common way a backtest lies to you.

### Then, in this order

1. **Replay with your own data.** Export bars to
   `timeMs,open,high,low,close,volume,delta` and run the command above.
2. **Port one real bot.** Take your simplest strategy, strip the ATAS types out,
   implement `OnBar`. If that feels hard, the difficulty *is* the diagnosis — that
   coupling is what has been costing you four machines.
3. **Build the ATAS side.** See `src/Bridge.Atas/README-BUILD.md`. Check the
   `[ATAS-API]` marked lines against your ATAS version first.
4. **Run live on a sim account** with `RiskGate(defaultMaxNet: 1)`. Watch the
   position reconcile. Kill ATAS mid-session on purpose and confirm the host
   survives, halts new orders, and resumes on reconnect.
5. **Only then** raise the limit and add the rest of the bots.

## Safety posture

- Link loss **halts new orders, it does not flatten.** A dropped socket is not a
  reason to market out of a good position — auto-flatten on a data glitch is
  itself a way to lose money. Flattening stays an explicit human decision.
- `RiskGate` clamps the **aggregate** net position, so a bug in one bot cannot
  size past the account limit even if it asks to.
- One bot throwing does not take the other four down; the host logs and continues.
- Everything runs on a single thread drained from one queue, which is why the
  netting engine needs no locks. The cost: a slow bot adds latency for all of
  them, so bots must not do I/O. `StrategyHost.QueueDepth` exposes it if one does.

## Known gaps

Honest list of what is **not** built yet:

- `BridgeStrategy.cs` is the one file that could not be compiled or tested here —
  it needs ATAS assemblies. The shape is right; the `[ATAS-API]` lines need
  checking against your installed version.
- Limit orders are carried on the wire but the engine only ever emits market
  orders. Passive execution needs a working-order model.
- No persistence: restart the host and bots begin flat. This is deliberate and
  safe, because targets are idempotent — but it means a mid-session restart hands
  any open position to the house leg until a bot re-declares.
- Single symbol per replay file. The engine is already multi-symbol; only the
  replay reader is not.
- Discord delivery is a stub — `ConsoleSignalSink` writes stdout and CSV. The
  webhook post is a few lines on top of `ISignalSink`.

## Layout

| Project | Depends on ATAS? | What it is |
|---|---|---|
| `Bridge.Contracts` | no | events, interfaces — the shared vocabulary |
| `Bridge.Netting` | no | **the netting engine + risk gate** |
| `Bridge.Wire` | no | line codec + TCP link |
| `Bridge.Replay` | no | CSV replay, signal journal |
| `Bridge.Host` | no | event loop, strategy fan-out, entry point |
| `Bridge.Atas` | **yes** | the thin pipe, excluded from the solution build |
| `Bridge.Tests` | no | 22 tests |
