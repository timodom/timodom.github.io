"""Scalper -> Signal.

Source: ``scalper/logs/trades.csv``, written by ``scalper/logging/trade_log.py``.

Two structural differences from the other two sources, both of which change how
you read the data rather than just how you parse it:

1. **This is a closed-trade log, not a signal log.** A row appears only when a
   trade *finishes*, and already carries its outcome. So scalper rows arrive
   late and terminal — you cannot use them for a live "it is signalling now"
   feed, but they are the best evidence source of the three, because the outcome
   is measured rather than reconstructed.

2. **Stops and targets are in ticks, not prices**, and there is no symbol column
   at all (the scalper is ES-only). Both are converted here using the same
   ``tick_size`` default the writer uses (0.25).

``realized_pnl`` is left in metadata rather than mapped to ``outcome_r``: R is
computed from prices so it means the same thing across all three strategies,
independent of contract size and dollar value.
"""

from __future__ import annotations

import csv
import io
import json
from datetime import datetime, timezone

from ..model import DEFAULT_TICK_SIZE, Direction, Signal, Status, StrategyId
from .base import ParseResult, Reject

_FIELDS = [
    "trade_id", "session", "direction", "entry_signal_reason", "entry_conditions_json",
    "intended_entry_price", "actual_entry_price", "slippage_ticks", "stop_ticks",
    "target_t1_ticks", "target_t2_ticks", "runner_mode", "quantity",
    "entry_time", "exit_time", "exit_reason", "exit_price", "realized_pnl",
]

# trade_log.ExitReason -> our terminal status. runner_absorption / manual /
# session_end are managed exits that are neither a stop nor a target, so they get
# CLOSED and keep the precise reason in metadata rather than being forced into a
# bucket that would skew the win-rate.
_EXIT = {
    "stop": Status.STOPPED,
    "target": Status.TARGET,
    "time_invalidation": Status.EXPIRED,
    "runner_absorption": Status.CLOSED,
    "manual": Status.CLOSED,
    "session_end": Status.CLOSED,
}


class ScalperAdapter:
    name = "scalper"

    def __init__(self, symbol: str = "ES", tick_size: float = DEFAULT_TICK_SIZE) -> None:
        self.symbol = symbol
        self.tick_size = tick_size

    def parse_line(self, line: str, source_file: str = "") -> ParseResult:
        raw = line.strip()
        if not raw:
            return None
        if raw.startswith("trade_id"):
            return None  # header

        try:
            row = next(csv.reader(io.StringIO(raw)))
        except csv.Error as exc:
            return Reject(f"unparseable csv ({exc})", raw, source_file)

        if len(row) < len(_FIELDS):
            return Reject(f"expected {len(_FIELDS)} columns, got {len(row)}", raw, source_file)

        f = dict(zip(_FIELDS, row))

        d = f["direction"].strip().upper()
        if d not in ("LONG", "SHORT"):
            return Reject(f"unknown direction {f['direction']!r}", raw, source_file)
        direction = Direction[d]
        sign = 1 if direction is Direction.LONG else -1

        try:
            ts = _parse_ts(f["entry_time"])
            entry = float(f["actual_entry_price"] or f["intended_entry_price"])
            stop_ticks = float(f["stop_ticks"])
            t1_ticks = _opt_float(f["target_t1_ticks"])
            t2_ticks = _opt_float(f["target_t2_ticks"])
            exit_price = _opt_float(f["exit_price"])
            qty = int(float(f["quantity"])) if f["quantity"].strip() else None
        except ValueError as exc:
            return Reject(f"bad numeric/timestamp ({exc})", raw, source_file)

        if stop_ticks <= 0:
            return Reject(f"non-positive stop_ticks {stop_ticks}", raw, source_file)

        ts_size = self.tick_size
        stop = entry - sign * stop_ticks * ts_size
        target_1 = entry + sign * t1_ticks * ts_size if t1_ticks else None
        target_2 = entry + sign * t2_ticks * ts_size if t2_ticks else None

        exit_reason = f["exit_reason"].strip().lower()
        status = _EXIT.get(exit_reason, Status.CLOSED)

        # R from prices, so it is comparable across strategies and contract sizes.
        outcome_r = None
        if exit_price is not None:
            risk = abs(entry - stop)
            if risk > 0:
                outcome_r = round(sign * (exit_price - entry) / risk, 4)

        try:
            conditions = json.loads(f["entry_conditions_json"] or "{}")
        except json.JSONDecodeError:
            conditions = {"_unparsed": f["entry_conditions_json"]}

        return Signal(
            signal_id=f["trade_id"].strip() or f"SCALP-{int(ts.timestamp() * 1000)}",
            strategy_id=StrategyId.RAIN_SCALP,
            ts_utc=ts,
            symbol=self.symbol,
            direction=direction,
            entry=entry,
            stop=stop,
            target_1=target_1,
            target_2=target_2,
            quantity=qty,
            timeframe=f["session"].strip() or None,
            status=status,
            confidence=None,   # the scalper emits no score; absent beats invented
            reason=f["entry_signal_reason"].strip(),
            outcome_r=outcome_r,
            source_file=source_file,
            metadata={
                "session": f["session"].strip(),
                "exit_reason": exit_reason,
                "exit_price": exit_price,
                "exit_time": f["exit_time"].strip(),
                "realized_pnl": _opt_float(f["realized_pnl"]),
                "slippage_ticks": _opt_float(f["slippage_ticks"]),
                "stop_ticks": stop_ticks,
                "runner_mode": f["runner_mode"].strip().lower() in ("true", "1", "yes"),
                "entry_conditions": conditions,
            },
        )


def _parse_ts(value: str) -> datetime:
    v = value.strip()
    if not v:
        raise ValueError("empty entry_time")
    dt = datetime.fromisoformat(v.replace("Z", "+00:00"))
    # main.py logs local exchange time without an offset; assume UTC and record
    # it, so a later correction is a one-line config change, not a re-parse.
    return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)


def _opt_float(value: str) -> float | None:
    v = value.strip()
    return float(v) if v else None
