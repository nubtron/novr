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
import shutil
import sys
import time
from pathlib import Path, PureWindowsPath

sys.path.insert(0, str(Path(__file__).resolve().parent))

from vr_harness import HarnessError, load_project, powershell  # noqa: E402
from vr_harness import bepinex_cfg, game, mockxr  # noqa: E402

PROJECT = "novr"
DONE_MARKER = "harness.done"
DUMPS_DIR = "dumps"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--mission", default="", help="mission name to match (default: a Free Flight mission)")
    parser.add_argument("--dumps", type=int, default=3, help="how many dumps to fire (default 3)")
    parser.add_argument("--delay", type=float, default=8.0, help="seconds before the first dump and between dumps")
    parser.add_argument("--timeout", type=int, default=300, help="seconds to wait for the run to finish")
    parser.add_argument("--no-renderdoc", action="store_true", help="skip the GPU capture, buffer dumps only")
    parser.add_argument("--keep-running", action="store_true", help="do not close the game at the end")
    parser.add_argument("--config", default=None, help="override the harness config path")
    return parser.parse_args()


def wait_for_done(marker: Path, project, deadline_s: int) -> bool:
    """Wait for the mod's completion marker.

    Polling for output files instead would mean a crashed or mod-less run always
    costs the full timeout; the marker plus a liveness check turns most failures
    into a fast, specific error.
    """
    deadline = time.monotonic() + deadline_s
    while time.monotonic() < deadline:
        if marker.exists():
            return True
        if not game.is_running(project):
            raise HarnessError("game exited before the run completed — check Player.log")
        time.sleep(2)
    return False


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
            f"& '{project.renderdoccmd}' thumb '{PureWindowsPath(project.work_dir)}\\captures\\{rdc.name}' "
            f"--out '{PureWindowsPath(project.work_dir)}\\captures\\{png.name}'"
        )
        if png.exists():
            thumbs.append(png)
        elif result.returncode != 0:
            print(f"  warning: thumb failed for {rdc.name}: {result.stderr.strip()[:200]}")
    return thumbs


def main() -> int:
    args = parse_args()
    project = load_project(PROJECT, args.config)
    print(f"config: {project.source}")
    print(f"game:   {project.game_dir}")

    captures_dir = project.work_dir_wsl / "captures"
    if captures_dir.exists():
        shutil.rmtree(captures_dir)
    captures_dir.mkdir(parents=True, exist_ok=True)
    capture_prefix = str(PureWindowsPath(project.work_dir) / "captures" / PROJECT)

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

            print(f"waiting up to {args.timeout}s for the run to finish...")
            finished = wait_for_done(marker, project, args.timeout)
            if not finished:
                print("  timed out waiting for harness.done", file=sys.stderr)
        finally:
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
        print("\nnothing was produced — check Player.log", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except HarnessError as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(1)
