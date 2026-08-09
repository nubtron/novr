#!/usr/bin/env python3
"""Run a RenderDoc replay analysis against a capture, from WSL.

    tools/rd_run.py hud_blend <capture.rdc> [script args...]
    tools/rd_run.py --list

Analyses live in tools/rd/ and are ordinary Python that `import renderdoc`.
They run on the *Windows* side, for two reasons that are not going away:

  * D3D11 replay is Windows-only. A Linux renderdoc module cannot open these
    captures at all, so there is nothing to be gained by running locally.
  * The RenderDoc release build embeds Python inside qrenderdoc.exe and ships
    no importable renderdoc.pyd, and renderdoccmd has no `python` subcommand.
    Replaying from a script therefore needs a source build; point
    tools.renderdoc.replay_python / replay_pymodules at it in ~/.vr-harness.toml.

Scripts are staged into the harness work_dir rather than run in place: the repo
lives on the WSL filesystem, and Windows processes reading it over \\\\wsl$ is
exactly the arrangement the work_dir rules exist to avoid.
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from vr_harness import HarnessError, load_project, to_win  # noqa: E402

PROJECT = "novr"
SCRIPT_DIR = Path(__file__).resolve().parent / "rd"


def available() -> list[str]:
    return sorted(p.stem for p in SCRIPT_DIR.glob("*.py") if not p.name.startswith("_"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("script", nargs="?", help=f"analysis to run: {', '.join(available())}")
    parser.add_argument("capture", nargs="?", help="path to the .rdc (WSL or Windows form)")
    parser.add_argument("args", nargs="*", help="extra arguments for the analysis")
    parser.add_argument("--list", action="store_true", help="list available analyses")
    parser.add_argument("--config", default=None, help="override the harness config path")
    args = parser.parse_args()

    if args.list or not args.script:
        for name in available():
            doc = (SCRIPT_DIR / f"{name}.py").read_text(encoding="utf-8").splitlines()
            summary = doc[0].strip('"') if doc else ""
            print(f"  {name:<16} {summary}")
        return 0

    source = SCRIPT_DIR / f"{args.script}.py"
    if not source.is_file():
        raise HarnessError(f"no analysis named {args.script!r} (have: {', '.join(available())})")
    if not args.capture:
        raise HarnessError("a capture path is required")

    capture = Path(args.capture).expanduser()
    if not capture.is_file():
        raise HarnessError(f"capture not found: {capture}")

    project = load_project(PROJECT, args.config)
    project.require_replay()

    staged_dir = project.work_dir_wsl / "rd"
    staged_dir.mkdir(parents=True, exist_ok=True)
    staged = staged_dir / source.name
    shutil.copy2(source, staged)

    command = [
        "powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
        f"$env:PYTHONPATH='{project.replay_pymodules}'; "
        f"& '{project.replay_python}' -u '{to_win(staged)}' '{to_win(capture)}' "
        + " ".join(f"'{a}'" for a in args.args),
    ]
    # Streamed, not captured: a full-frame replay takes minutes and prints
    # progress as it goes. Swallowing that until the end makes a working run
    # indistinguishable from a hung one.
    return subprocess.run(command, cwd="/").returncode


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except HarnessError as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(1)
