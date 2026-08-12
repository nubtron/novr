"""Minimal read/write for BepInEx .cfg files.

The harness needs to flip a handful of [Debug] keys before a run and put them
back afterwards, without disturbing anything else in the user's config. BepInEx
regenerates these files with comments and defaults, so a naive rewrite would
throw all of that away — hence surgical line edits rather than configparser
round-tripping (configparser also mangles the '## comment' style BepInEx uses,
and lowercases nothing but reorders plenty).
"""

from __future__ import annotations

import re
from contextlib import contextmanager
from pathlib import Path

_SECTION_RE = re.compile(r"^\[(?P<name>.+)\]\s*$")
_ENTRY_RE = re.compile(r"^(?P<key>[^#=\s][^=]*?)\s*=\s*(?P<value>.*?)\s*$")


def read(path: Path) -> dict[tuple[str, str], str]:
    """Return {(section, key): value}."""
    values: dict[tuple[str, str], str] = {}
    section = ""
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        match = _SECTION_RE.match(stripped)
        if match:
            section = match.group("name")
            continue
        entry = _ENTRY_RE.match(stripped)
        if entry:
            values[(section, entry.group("key").strip())] = entry.group("value")
    return values


def write(path: Path, updates: dict[tuple[str, str], str]) -> dict[tuple[str, str], str]:
    """Apply {(section, key): value} in place. Returns the previous values.

    Keys that are not already present are ignored rather than appended: BepInEx
    only honours entries the plugin has bound, so inventing a key silently does
    nothing and would just mislead the next reader of the file.
    """
    if not updates:
        return {}

    previous: dict[tuple[str, str], str] = {}
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    section = ""

    for index, line in enumerate(lines):
        stripped = line.strip()
        match = _SECTION_RE.match(stripped)
        if match:
            section = match.group("name")
            continue
        entry = _ENTRY_RE.match(stripped)
        if not entry:
            continue
        key = (section, entry.group("key").strip())
        if key in updates:
            previous[key] = entry.group("value")
            lines[index] = f"{entry.group('key').strip()} = {updates[key]}"

    missing = set(updates) - set(previous)
    if missing:
        names = ", ".join(f"[{s}] {k}" for s, k in sorted(missing))
        raise KeyError(
            f"{path.name} has no entry for {names} — run the game once with the "
            "current build so BepInEx writes the defaults, then retry"
        )

    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return previous


#: Keys that must never survive a run, whatever the file said on the way in,
#: mapped to the value a normal play session expects. Restoring these to the
#: snapshot is not safe: a run that dies before its finally block leaves them
#: on, and then the *next* run snapshots "on" and faithfully restores it. One
#: crash silently arms auto-start for every launch after it, which is exactly
#: how a headset session once found itself launching a mission by itself.
UNSAFE_KEYS = {
    ("Debug", "Auto Start Mission"): "false",
    ("Debug", "Enable Frame Dumps"): "false",
    ("Debug", "RenderDoc Capture On Dump"): "false",
    # A yaw sweep left armed would move the head of someone wearing a real
    # headset, who has one of their own and did not ask for ours.
    ("Debug", "Auto Dump Yaws"): "",
    # A mute left armed would silence the next real play session.
    ("Debug", "Harness Mute"): "false",
}


@contextmanager
def temporarily(path: Path, updates: dict[tuple[str, str], str]):
    """Apply config changes for the duration of a run, then restore them.

    A harness run turns on auto-start and dumping; leaving those on would mean
    the user's next normal launch silently starts a mission by itself. Keys in
    UNSAFE_KEYS are restored to their known-safe value rather than to whatever
    was observed, so a previously-contaminated file gets cleaned instead of
    preserved.
    """
    previous = write(path, updates)
    try:
        yield
    finally:
        restore = dict(previous)
        for key, safe in UNSAFE_KEYS.items():
            if key in restore and restore[key] != safe:
                print(f"config: [{key[0]}] {key[1]} was {restore[key]} before this "
                      f"run — restoring to {safe} (a normal launch must not have it on)")
                restore[key] = safe
        if restore:
            write(path, restore)
