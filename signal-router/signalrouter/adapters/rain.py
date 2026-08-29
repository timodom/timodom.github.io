"""RAIN Scanner -> Signal.

Source: ``C:\\ATAS\\signals\\rain_signals.csv``, written by
``rain-scanner/lib/AlertSender.ps1::Write-SignalCsv``.

    signal_id,timestamp,symbol,side,quantity,entry,sl,tp1,tp2,status,score
    RAIN-20260829-114321-123,2026-08-29T11:43:21Z,ES,buy,2,6502,6497,6512,6517,pending,4

Verified against the writer, not the docs. Three things the writer makes true
that a reader would otherwise get wrong:

* ``side`` is lowercase ``buy``/``sell``, not LONG/SHORT.
* numbers are InvariantCulture (period decimal) because a C# indicator parses
  this file — so a locale-sensitive parse here would silently corrupt prices.
* ``score`` is an integer 0..4 from the 4-check confirmation gate, which becomes
  ``confidence`` on a 0..1 scale so it is comparable with MBO's float.

**Known gap:** ``Write-SignalCsv`` returns early unless ``EnableExecCsv``, which
is only true in ``Mode=LIVE``. In ``DRY_RUN`` this file is never written, so RAIN
produces nothing for this adapter to read. Observing RAIN while it is *not*
trading needs a different source — see ``README.md``.
"""

from __future__ import annotations

import csv
import io
from datetime import datetime, timezone

from ..model import Direction, Signal, Status, StrategyId
from .base import ParseResult, Reject

HEADER = "signal_id,timestamp,symbol,side,quantity,entry,sl,tp1,tp2,status,score"
_FIELDS = HEADER.split(",")

_SIDE = {"buy": Direction.LONG, "sell": Direction.SHORT}

# RAIN's own status vocabulary in this file, mapped onto ours.
_STATUS = {
    "pending": Status.SIGNALED,
    "filled": Status.FILLED,
    "cancelled": Status.INVALIDATED,
    "canceled": Status.INVALIDATED,
    "stopped": Status.STOPPED,
    "target": Status.TARGET,
}

MAX_SCORE = 4  # the 4-check gate: delta, close, wick, DOM


class RainAdapter:
    name = "rain"

    def __init__(self, strategy_id: StrategyId = StrategyId.RAIN_BREAK_RETEST,
                 timeframe: str = "M3") -> None:
        self.strategy_id = strategy_id
        self.timeframe = timeframe

    def parse_line(self, line: str, source_file: str = "") -> ParseResult:
        raw = line.strip()
        if not raw:
            return None
        if raw.startswith("signal_id"):
            return None  # header

        try:
            row = next(csv.reader(io.StringIO(raw)))
        except csv.Error as exc:
            return Reject(f"unparseable csv ({exc})", raw, source_file)

        if len(row) < len(_FIELDS):
            return Reject(f"expected {len(_FIELDS)} columns, got {len(row)}", raw, source_file)

        f = dict(zip(_FIELDS, row))

        direction = _SIDE.get(f["side"].strip().lower())
        if direction is None:
            return Reject(f"unknown side {f['side']!r}", raw, source_file)

        try:
            ts = _parse_ts(f["timestamp"])
            entry = float(f["entry"])
            stop = float(f["sl"])
            tp1 = _opt_float(f["tp1"])
            tp2 = _opt_float(f["tp2"])
            qty = int(float(f["quantity"])) if f["quantity"].strip() else None
            score = int(float(f["score"])) if f["score"].strip() else 0
        except ValueError as exc:
            return Reject(f"bad numeric/timestamp ({exc})", raw, source_file)

        if entry == stop:
            return Reject("entry == stop (zero risk)", raw, source_file)

        status = _STATUS.get(f["status"].strip().lower(), Status.SIGNALED)

        return Signal(
            signal_id=f["signal_id"].strip(),
            strategy_id=self.strategy_id,
            ts_utc=ts,
            symbol=f["symbol"].strip() or "ES",
            direction=direction,
            entry=entry,
            stop=stop,
            target_1=tp1,
            target_2=tp2,
            quantity=qty,
            timeframe=self.timeframe,
            status=status,
            confidence=min(max(score / MAX_SCORE, 0.0), 1.0),
            reason=f"RAIN retest confirmation {score}/{MAX_SCORE}",
            source_file=source_file,
            metadata={"score": score, "raw_status": f["status"].strip(), "raw_side": f["side"].strip()},
        )


def _parse_ts(value: str) -> datetime:
    """Writer emits ``yyyy-MM-ddTHH:mm:ssZ``. Accept an offset form too, since a
    config or PowerShell version change could plausibly alter it, and treat a
    naive timestamp as UTC because the writer explicitly calls ToUniversalTime."""
    v = value.strip()
    if not v:
        raise ValueError("empty timestamp")
    dt = datetime.fromisoformat(v.replace("Z", "+00:00"))
    return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)


def _opt_float(value: str) -> float | None:
    v = value.strip()
    if not v:
        return None
    f = float(v)
    # The writer coerces a missing TP to 0 via [double]$null, so 0 means absent.
    return None if f == 0.0 else f
