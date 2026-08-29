"""Adapter contract.

Every adapter is a **pure function of one line plus its filename**. No file
handles, no sockets, no ATAS, no clock. That makes each one trivially testable
against real captured lines, and it makes the "cannot place an order" claim
structural rather than a promise: there is nothing in this package that could.

The other rule that matters: **a bad line never raises.** A single malformed row
in a file a live bot is appending to must not take the router down, so parsers
return a Reject instead of throwing. Rejects are counted and surfaced, not
swallowed — a rising reject rate is how you find out a bot changed its format.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional, Protocol, Union

from ..model import Signal


@dataclass(frozen=True, slots=True)
class Reject:
    """A line that could not become a Signal, and why."""

    reason: str
    line: str
    source_file: str = ""

    def __str__(self) -> str:
        snippet = self.line if len(self.line) <= 120 else self.line[:117] + "..."
        return f"{self.reason}: {snippet!r}"


ParseResult = Union[Signal, Reject, None]
"""None means 'nothing here, and that is fine' — a header row, a blank line.
Reject means 'this should have parsed and did not', which is worth counting."""


class Adapter(Protocol):
    """What Phase 2's file tailer will call. Implementations are stateless."""

    name: str

    def parse_line(self, line: str, source_file: str = "") -> ParseResult: ...
