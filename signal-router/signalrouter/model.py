"""The one signal shape every strategy is normalised into.

Built on the schema RAIN already writes to ``rain_signals.csv``
(``signal_id,timestamp,symbol,side,quantity,entry,sl,tp1,tp2,status,score``)
rather than a new invention, so the RAIN adapter is close to a pass-through and
the vocabulary stays one you already read every day.

Deliberately absent: any notion of consensus, agreement, or a combined score.
Each strategy stays independently measurable; agreement is something you compute
from a table of these later, not something the pipeline bakes in.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field, asdict
from datetime import datetime, timezone
from enum import Enum
from typing import Any, Optional

# ES. Every price-vs-ticks conversion in the adapters goes through this.
DEFAULT_TICK_SIZE = 0.25


class Direction(str, Enum):
    LONG = "LONG"
    SHORT = "SHORT"


class Status(str, Enum):
    """Lifecycle. Sources start at different points and that is expected:
    RAIN and MBO emit at SIGNALED; the scalper's trade log only exists once a
    trade has closed, so it arrives already terminal."""

    SIGNALED = "SIGNALED"        # setup fired, nothing acted on
    CONFIRMED = "CONFIRMED"      # passed a later confirmation gate
    FILLED = "FILLED"            # hypothetical or real entry taken
    STOPPED = "STOPPED"          # terminal: stop hit
    TARGET = "TARGET"            # terminal: target hit
    EXPIRED = "EXPIRED"          # terminal: timed out before resolving
    INVALIDATED = "INVALIDATED"  # terminal: setup broke before entry
    CLOSED = "CLOSED"            # terminal: any other managed exit

    @property
    def is_terminal(self) -> bool:
        return self in _TERMINAL


_TERMINAL = frozenset(
    {Status.STOPPED, Status.TARGET, Status.EXPIRED, Status.INVALIDATED, Status.CLOSED}
)


class StrategyId(str, Enum):
    """Stable keys. These become the Discord channel per strategy and the
    GROUP BY column in every question you will ask of this data."""

    RAIN_BREAK_RETEST = "RAIN_BREAK_RETEST"
    RAIN_SCALP = "RAIN_SCALP"
    MBO_ABSORPTION = "MBO_ABSORPTION"


STRATEGY_NAMES = {
    StrategyId.RAIN_BREAK_RETEST: "RAIN Break & Retest",
    StrategyId.RAIN_SCALP: "RAIN Scalp",
    StrategyId.MBO_ABSORPTION: "MBO Absorption Fade",
}


@dataclass(frozen=True, slots=True)
class Signal:
    """One observation from one strategy. Immutable: the router appends new rows
    rather than mutating, so the history of what a bot believed *at the time*
    survives — which is the whole point of collecting this."""

    signal_id: str
    strategy_id: StrategyId
    ts_utc: datetime
    symbol: str
    direction: Direction
    entry: float
    stop: float
    status: Status = Status.SIGNALED

    target_1: Optional[float] = None
    target_2: Optional[float] = None
    timeframe: Optional[str] = None
    quantity: Optional[int] = None
    confidence: Optional[float] = None   # normalised 0..1 across all strategies
    reason: str = ""

    # Filled in by the outcome tracker (Phase 3), never by a bot.
    mfe: Optional[float] = None
    mae: Optional[float] = None
    outcome_r: Optional[float] = None

    source_file: str = ""
    metadata: dict[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        if self.ts_utc.tzinfo is None:
            raise ValueError(f"ts_utc must be timezone-aware: {self.ts_utc!r}")
        if self.confidence is not None and not 0.0 <= self.confidence <= 1.0:
            raise ValueError(f"confidence must be 0..1, got {self.confidence}")
        if self.entry == self.stop:
            raise ValueError("entry == stop gives zero risk; refusing to record it")

    @property
    def strategy_name(self) -> str:
        return STRATEGY_NAMES.get(self.strategy_id, self.strategy_id.value)

    @property
    def risk_points(self) -> float:
        """Absolute distance to the stop. The denominator of every R figure."""
        return abs(self.entry - self.stop)

    def risk_ticks(self, tick_size: float = DEFAULT_TICK_SIZE) -> float:
        return self.risk_points / tick_size

    @property
    def stop_is_coherent(self) -> bool:
        """A LONG stop must sit below entry and a SHORT stop above it.

        Not enforced in __post_init__ on purpose: a source emitting an incoherent
        stop is a real finding about that strategy, and silently rejecting the
        row would hide it. Recorded, then flagged."""
        return (
            self.stop < self.entry
            if self.direction is Direction.LONG
            else self.stop > self.entry
        )

    def to_row(self) -> dict[str, Any]:
        """Flat dict for CSV/SQLite. Metadata collapses to a JSON string so the
        row stays one column wide per field."""
        d = asdict(self)
        d["strategy_id"] = self.strategy_id.value
        d["strategy_name"] = self.strategy_name
        d["direction"] = self.direction.value
        d["status"] = self.status.value
        d["ts_utc"] = self.ts_utc.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
        d["metadata"] = json.dumps(self.metadata, separators=(",", ":"), sort_keys=True)
        return d


ROW_FIELDS = [
    "signal_id", "strategy_id", "strategy_name", "ts_utc", "symbol", "timeframe",
    "direction", "entry", "stop", "target_1", "target_2", "quantity",
    "status", "confidence", "reason", "mfe", "mae", "outcome_r",
    "source_file", "metadata",
]
