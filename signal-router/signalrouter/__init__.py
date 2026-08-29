"""Read-only signal normalisation for the zerodaytrade bots.

This package contains no order-placement code, no ATAS client, no named-pipe
handle and no network writer. It reads lines that bots have already written and
turns them into one shape. That is the whole of it, and it is deliberate: the
guarantee that observation cannot place a trade is enforced by the absence of the
capability, not by a mode flag that a future edit could get wrong.
"""

from .model import DEFAULT_TICK_SIZE, Direction, ROW_FIELDS, Signal, Status, StrategyId

__all__ = ["Signal", "Direction", "Status", "StrategyId", "ROW_FIELDS", "DEFAULT_TICK_SIZE"]
__version__ = "0.1.0"
