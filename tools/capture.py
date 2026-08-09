#!/usr/bin/env python3
"""Unattended capture run: launch headless, fly a mission, dump, tear down.

    tools/capture.py                     # full run, default settings
    tools/capture.py --mission "Free"    # pick a mission by name
    tools/capture.py --dumps 5
    tools/capture.py --keep-running      # leave the game up for poking at
    tools/capture.py --no-renderdoc      # buffer dumps only

What it does, and why each step exists, is documented in vr_harness/game.py and
vr_harness/mockxr.py. The short version: the OpenXR mock runtime supplies stereo
with no headset, explorer.exe launches the game so Doorstop actually loads,
RenderDoc is injected before the D3D device exists, and NOVR's AutoStartMission
flies a mission and fires the dumps. Output lands in the mod's dumps/ folder and
the harness work_dir.

Everything machine-specific comes from ~/.vr-harness.toml — see
tools/vr-harness.example.toml.
"""

from __future__ import annotations

import argparse
import sys
import time
from datetime import datetime
from pathlib import Path, PureWindowsPath

sys.path.insert(0, str(Path(__file__).resolve().parent))

from vr_harness import HarnessError, load_project, powershell, to_win  # noqa: E402
from vr_harness import bepinex_cfg, game, mockxr  # noqa: E402

PROJECT = "novr"
DONE_MARKER = "harness.done"
DUMPS_DIR = "dumps"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--mission", default="",
                        help="mission name to match (default: a built-in mission — Free Flight has no airbase spawn)")
    parser.add_argument("--dumps", type=int, default=3, help="how many dumps to fire (default 3)")
    parser.add_argument("--delay", type=float, default=8.0, help="seconds before the first dump and between dumps")
    parser.add_argument("--timeout", type=int, default=300, help="seconds to wait for the run to finish")
    # Measured: a successful run is ~35s wall clock with one dump (launch ~1s,
    # mission load and spawn ~20s, dump, capture settle), so roughly 50s with
    # the default three. 300s is deliberate slack for a cold start after a game
    # update rather than a tight bound — the point is that the script cannot sit
    # there indefinitely with a game process running. Each wait below is
    # individually capped too, but those caps stack; this one is absolute.
    parser.add_argument("--max-runtime", type=int, default=300,
                        help="hard ceiling in seconds for the whole run (default 300)")
    # A/B-ing a config value is the whole point of an unattended harness: run
    # once with the feature on, once off, diff the output. Restricting that to
    # the [Debug] section would mean hand-editing the .cfg around every run,
    # which is exactly the manual step this replaces. Values are restored
    # afterwards like any other harness edit.
    parser.add_argument("--set", action="append", default=[], metavar="SECTION:KEY=VALUE",
                        help="override any config entry for this run, e.g. --set 'General:HUD Opacity=0'")
    parser.add_argument("--no-renderdoc", action="store_true", help="skip the GPU capture, buffer dumps only")
    parser.add_argument("--keep-running", action="store_true", help="do not close the game at the end")
    parser.add_argument("--config", default=None, help="override the harness config path")
    return parser.parse_args()


def wait_for_done(marker: Path, project, deadline: float, pid: int) -> bool:
    """Wait for the mod's completion marker, up to an absolute deadline.

    Polling for output files instead would mean a crashed or mod-less run always
    costs the full timeout; the marker plus a liveness check turns most failures
    into a fast, specific error.

    The liveness check watches the pid we launched, not just the process name.
    A run that says "the game exited" while a game is visibly on screen is
    baffling, and it happens: the process we set up — mock runtime env, Doorstop,
    RenderDoc injected — can die and be replaced by one we did not configure.
    Whatever is on screen then is not the thing under test.
    """
    while time.monotonic() < deadline:
        if marker.exists():
            return True
        if not game.is_running(project, pid):
            others = [p for p in game.running_pids(project) if p != pid]
            if others:
                raise HarnessError(
                    f"the game process we launched (pid {pid}) exited and a different "
                    f"one took its place (pid {', '.join(map(str, others))}).\n"
                    "That replacement has neither our mock-runtime environment nor "
                    "RenderDoc injected, so the run is void. Close every game "
                    "instance and retry."
                )
            raise HarnessError(
                f"the game (pid {pid}) exited before the run completed — check Player.log"
            )
        time.sleep(2)
    return False


def harness_log(project, limit: int = 8) -> list[str]:
    """The mod's own [NOVR-HARNESS] lines from Player.log, newest last.

    Every failure in this harness announces itself in the game log and nowhere
    else. Telling the user to "check Player.log" costs them a grep through
    ~1500 lines of Unity noise to find the one line that names the problem, so
    the driver reads it for them.
    """
    log = project.player_log_wsl if project.player_log else None
    if not log or not log.is_file():
        return []
    try:
        text = log.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return []
    lines = [
        l.strip()
        for l in text.splitlines()
        if "[NOVR-HARNESS]" in l
        # The mission inventory is one enormous line; useful when choosing a
        # mission, pure noise in a failure report.
        and "Available single-player missions" not in l
    ]
    return lines[-limit:]


def explain_failure(project) -> None:
    """Print what the mod said, and what it usually means."""
    lines = harness_log(project)
    if not lines:
        print(
            "\nThe mod logged nothing. Either BepInEx did not load (see the "
            "launch table in tools/README.md) or [Debug] Auto Start Mission "
            "never took effect.",
            file=sys.stderr,
        )
        return

    print("\nWhat the mod reported:", file=sys.stderr)
    for line in lines:
        print(f"  {line}", file=sys.stderr)

    # Map the *last* state the mod reached to a fix. Scanning the whole tail for
    # any match reads stale lines from earlier in the same run — a completed run
    # still contains "no local player yet" from before the mission loaded, and
    # matching that would blame a timeout for a run that actually succeeded.
    hints = [
        ("Run complete", None),
        ("Fired dump", None),
        ("local aircraft acquired",
         "the aircraft spawned but no output appeared — check the dump path and disk space"),
        ("no spawnable aircraft",
         "this mission has no airbase hangar spawn (Free Flight is like this) — "
         "use --mission with a built-in mission such as '01. Convoy Attack'"),
        ("no faction HQ",
         "faction join failed; check the mission actually has a joinable faction"),
        ("no local player yet",
         "the mission never finished loading — try a longer --timeout"),
        ("Started mission",
         "mission started but never reached a cockpit — see the spawn lines above"),
    ]
    for line in reversed(lines):
        for needle, hint in hints:
            if needle in line:
                if hint:
                    print(f"\nLikely fix: {hint}.", file=sys.stderr)
                return


def wait_for_captures_to_settle(captures_dir: Path, deadline: float) -> None:
    """Wait until RenderDoc has finished writing its .rdc files.

    RenderDoc serialises the capture asynchronously, well after the frame that
    triggered it. Killing the game the moment the mod says it is done truncates
    that write, and the result is a file of plausible size that fails to open
    with "File is corrupted: Unrecognised section type" — which looks like a
    RenderDoc bug rather than our teardown. Wait for the sizes to stop changing.
    """
    stable_rounds = 0
    previous: dict[Path, int] = {}

    while time.monotonic() < deadline:
        current = {p: p.stat().st_size for p in captures_dir.glob("*.rdc")}
        if current and current == previous:
            stable_rounds += 1
            # Three quiet rounds: one can happen mid-write between buffers.
            if stable_rounds >= 3:
                return
        else:
            stable_rounds = 0
        previous = current
        time.sleep(1.5)

    print("  warning: capture files still changing at timeout; may be truncated")


def newest_dump_dir(plugin_dir: Path) -> Path | None:
    root = plugin_dir / DUMPS_DIR
    if not root.is_dir():
        return None
    dirs = [d for d in root.iterdir() if d.is_dir()]
    return max(dirs, key=lambda d: d.stat().st_mtime) if dirs else None


def extract_thumbnails(project, captures: list[Path]) -> list[Path]:
    """renderdoccmd thumb — headless proof that a capture has real content.

    Cheap sanity check that does not need the Qt UI: a black or missing
    thumbnail means the capture is not worth opening.
    """
    thumbs = []
    for rdc in captures:
        png = rdc.with_suffix(".thumb.png")
        result = powershell(
            f"& '{project.renderdoccmd}' thumb '{to_win(rdc)}' --out '{to_win(png)}'"
        )
        if png.exists():
            thumbs.append(png)
        else:
            # renderdoccmd reports a corrupt capture on stdout and still exits
            # 0, so the return code cannot be trusted here — the missing file is
            # the real signal, and the message is the useful part.
            detail = (result.stdout + result.stderr).strip().replace("\n", " ")
            print(f"  warning: no thumbnail for {rdc.name}: {detail[:200]}")
    return thumbs


def main() -> int:
    args = parse_args()
    project = load_project(PROJECT, args.config)
    print(f"config: {project.source}")
    print(f"game:   {project.game_dir}")

    # One directory per run rather than a wiped shared one. Comparing a run
    # against an earlier run is the main thing this harness is for, and a run
    # that deletes its predecessor's evidence — including a --no-renderdoc run
    # that produces none of its own — makes that impossible.
    stamp = datetime.now().strftime("%H%M%S")
    captures_dir = project.work_dir_wsl / "captures" / stamp
    captures_dir.mkdir(parents=True, exist_ok=True)
    capture_prefix = str(PureWindowsPath(project.work_dir) / "captures" / stamp / PROJECT)

    marker = project.plugin_dir_wsl / DONE_MARKER
    marker.unlink(missing_ok=True)

    env = dict(mockxr.env(project))
    print(f"mock XR: {env['XR_RUNTIME_JSON']}")

    updates = {
        ("Debug", "Auto Start Mission"): "true",
        ("Debug", "Auto Start Mission Name"): args.mission,
        ("Debug", "Auto Dump Count"): str(args.dumps),
        ("Debug", "Auto Dump Delay"): str(args.delay),
        ("Debug", "RenderDoc Capture On Dump"): "false" if args.no_renderdoc else "true",
    }
    for override in args.set:
        section, _, rest = override.partition(":")
        key, sep, value = rest.partition("=")
        if not section or not sep:
            raise HarnessError(f"--set expects SECTION:KEY=VALUE, got {override!r}")
        updates[(section.strip(), key.strip())] = value.strip()
        print(f"config: [{section.strip()}] {key.strip()} = {value.strip()} (for this run)")

    # One ceiling for the whole run. Each wait below is individually bounded,
    # but those bounds stack; a single deadline is what actually guarantees the
    # harness cannot sit there indefinitely with a game process running.
    deadline = time.monotonic() + args.max_runtime

    game.kill(project)

    with bepinex_cfg.temporarily(project.config_file_wsl, updates):
        launch = game.launch_with_renderdoc(project, capture_prefix, env)
        print(
            f"launched pid={launch.pid} (appeared t+{launch.appeared_after_s:.2f}s, "
            f"injected t+{launch.injected_after_s:.2f}s)"
        )
        if launch.hooked_late:
            print(
                f"  WARNING: {launch.gfx_modules_at_inject} graphics module(s) were already "
                "loaded at injection — the capture may be incomplete"
            )

        try:
            # Give BepInEx a moment to come up before judging whether it did.
            time.sleep(8)
            status = game.verify_hook(project)
            print(f"hooks: {status.describe()}")
            if not status.doorstop:
                raise HarnessError(
                    "BepInEx/Doorstop did not load — the mod is not in the process.\n"
                    "This is the WSL-parent launch failure; see tools/vr_harness/game.py."
                )
            if not args.no_renderdoc and not status.renderdoc:
                print("  warning: renderdoc.dll not present; continuing with buffer dumps only")

            wait_deadline = min(deadline, time.monotonic() + args.timeout)
            print(f"waiting up to {int(wait_deadline - time.monotonic())}s for the run to finish...")
            finished = wait_for_done(marker, project, wait_deadline, launch.pid)
            if not finished:
                print("  timed out waiting for harness.done", file=sys.stderr)
        finally:
            # Must happen before the kill: RenderDoc writes the capture from
            # inside the game process.
            if not args.no_renderdoc:
                # Allow a little past the ceiling: aborting mid-write is
                # what produces a corrupt capture in the first place.
                wait_for_captures_to_settle(captures_dir, min(deadline + 60, time.monotonic() + 90))
            if not args.keep_running:
                game.kill(project)
                print("game closed")

    dump_dir = newest_dump_dir(project.plugin_dir_wsl)
    captures = sorted(captures_dir.glob("*.rdc"))
    thumbs = extract_thumbnails(project, captures) if captures else []

    print("\n== results ==")
    print(f"buffer dump:  {dump_dir if dump_dir else '(none)'}")
    print(f"gpu captures: {len(captures)} in {captures_dir}")
    for rdc in captures:
        print(f"  {rdc.name}")
    print(f"thumbnails:   {len(thumbs)}")

    if dump_dir is None and not captures:
        print("\nnothing was produced.", file=sys.stderr)
        explain_failure(project)
        return 1
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except HarnessError as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(1)
