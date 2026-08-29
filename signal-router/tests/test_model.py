"""The signal model's own invariants."""
from datetime import datetime, timezone

import pytest

from signalrouter.model import (ROW_FIELDS, Direction, Signal, Status,
                                StrategyId)


def make(**kw):
    base = dict(
        signal_id="X-1", strategy_id=StrategyId.RAIN_BREAK_RETEST,
        ts_utc=datetime(2026, 8, 29, 11, 43, 21, tzinfo=timezone.utc),
        symbol="ES", direction=Direction.LONG, entry=6502.0, stop=6497.0,
    )
    base.update(kw)
    return Signal(**base)


def test_naive_timestamp_refused():
    # A naive timestamp is how you silently mix ET and UTC in one table.
    with pytest.raises(ValueError, match="timezone-aware"):
        make(ts_utc=datetime(2026, 8, 29, 11, 43, 21))


def test_zero_risk_refused():
    with pytest.raises(ValueError, match="zero risk"):
        make(stop=6502.0)


def test_confidence_range_enforced():
    with pytest.raises(ValueError):
        make(confidence=1.4)
    with pytest.raises(ValueError):
        make(confidence=-0.1)
    assert make(confidence=0.0).confidence == 0.0


def test_risk_helpers():
    s = make(entry=6502.0, stop=6497.0)
    assert s.risk_points == 5.0
    assert s.risk_ticks() == 20.0
    assert s.risk_ticks(tick_size=0.5) == 10.0


def test_stop_coherence_is_reported_not_enforced():
    # An incoherent stop is a real finding about a strategy; rejecting the row
    # would hide it, so the model records it and flags it.
    bad = make(direction=Direction.LONG, entry=6502.0, stop=6510.0)
    assert bad.stop_is_coherent is False
    assert make(direction=Direction.SHORT, entry=6502.0, stop=6510.0).stop_is_coherent


def test_terminal_statuses():
    assert Status.STOPPED.is_terminal and Status.TARGET.is_terminal
    assert Status.CLOSED.is_terminal and Status.EXPIRED.is_terminal
    assert not Status.SIGNALED.is_terminal
    assert not Status.FILLED.is_terminal


def test_to_row_is_flat_and_covers_every_declared_field():
    row = make(metadata={"b": 2, "a": 1}).to_row()
    assert set(ROW_FIELDS) <= set(row), set(ROW_FIELDS) - set(row)
    assert isinstance(row["metadata"], str)
    assert row["metadata"] == '{"a":1,"b":2}'   # sorted => stable diffs
    assert row["ts_utc"] == "2026-08-29T11:43:21Z"
    assert row["strategy_name"] == "RAIN Break & Retest"
    assert row["direction"] == "LONG"


def test_signal_is_immutable():
    s = make()
    with pytest.raises(Exception):
        s.entry = 1.0
