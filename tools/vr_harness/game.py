"""Launching the game headless with RenderDoc hooked, and tearing it down again.

Three things shape this, all established by experiment:

1. **How the process is created decides whether BepInEx loads — and no launch
   method has been reliable.** BepInEx is loaded by Doorstop 4.5, which proxies
   WINHTTP.dll out of the game directory (UnityPlayer.dll imports it for real).
   On 2026-08-09 a powershell.exe launched from WSL resolved WINHTTP.dll from
   System32 instead, so Doorstop never loaded and the mod was simply absent;
   routing through explorer.exe fixed it, every time. That does not reproduce
   now — on 08-14 both launchers loaded the mod, repeatedly, and both failed
   under a bad RenderDoc injection (case 3), which is what the "explorer.exe
   stopped working" panic that morning actually was. Since a launcher has
   silently stopped working once, none is trusted: `launch()` takes one,
   `verify_hook()` proves whether it worked, and capture.py retries with the
   other. Steam launch works too; Steam is not special, it just isn't WSL.

2. **A loaded Doorstop proxy is not a loaded mod.** WINHTTP.dll from the game
   directory appears in the module list whether or not Doorstop runs the
   preloader — measured directly, by setting `enabled = false` in
   doorstop_config.ini: the module list is identical to a good run, and
   BepInEx/LogOutput.log is never written. That is exactly the signature of the
   08-14 failures, and it is why the only accepted proof that the mod is in the
   process is BepInEx's own log growing after we launched. See `verify_hook`.

3. **RenderDoc must be injected, not used as the launcher — and it must be an
   official build.** `renderdoccmd capture <exe>` creates the process itself and
   breaks Doorstop the way case 1 does (measured: RenderDoc hooks fine, BepInEx
   never appears). So we launch first and inject after, as early as possible,
   because Unity has d3d11.dll up by t+2.5s and RenderDoc that arrives after the
   device exists hooks nothing — it registers, the mod triggers a capture, and
   no .rdc is written.

   Which build is doing the injecting matters as much as when. A locally source-
   built renderdoc.dll injected at t+1.4s stopped Doorstop dead — 0 of 8 launches
   loaded the mod, on both launchers, with the game-directory WINHTTP.dll present
   every time — while the official 1.45 release injected at the same instant gave
   3 of 3 and a 536 MB capture. That was the whole of the 08-14 "the harness
   stopped loading the mod" mystery: the release install had been removed on
   08-09 and `tools.renderdoc.dir` repointed at a source build made for replay.
   Every harness capture since had been silently empty, and every run mod-less.

Either launcher can set environment variables for the game — which is how the
OpenXR mock runtime gets selected (see mockxr.py). A per-user registry override
does not work: this loader ignores HKCU.
"""

from __future__ import annotations

import time
from dataclasses import dataclass, field
from pathlib import PureWindowsPath

from . import HarnessError, Project, powershell

# Poll interval for spotting the freshly launched process. 100ms leaves roughly
# an order of magnitude of headroom against the measured d3d11 load delay.
_POLL_MS = 100
_APPEAR_TIMEOUT_S = 90

_LAUNCHER_NAME = "vr-harness-launch.cmd"

#: How the game process gets created. Neither has stayed reliable (see the
#: module docstring), so both are kept and capture.py falls back to the other.
LAUNCHERS = ("explorer", "start-process")

#: How long BepInEx gets to finish loading plugins before we call the launch
#: mod-less. Measured: the preloader's banner lands a few seconds after the
#: process appears and the chainloader finishes a few seconds after that. The
#: ceiling is slack for a cold start, not a typical wait — a good launch is
#: detected as soon as it is done.
_BEPINEX_TIMEOUT_S = 60

#: BepInEx 5's "every plugin that is going to load has loaded" line.
_CHAINLOADER_DONE = "Chainloader startup complete"


@dataclass
class LaunchResult:
    pid: int
    appeared_after_s: float
    launcher: str
    #: Seconds from the injection call to RenderDoc being in, or None if
    #: RenderDoc was never injected.
    injected_after_s: float | None = None
    #: d3d11/dxgi module count at injection time. Non-zero means the graphics
    #: modules were already up and the capture may be missing early resources —
    #: which is now the normal case, see `inject_renderdoc`.
    gfx_modules_at_inject: int = 0
    capture_prefix: str = ""
    #: (mtime, size) of BepInEx/LogOutput.log immediately before launch, or None
    #: if it did not exist. The baseline `verify_hook` compares against — a
    #: before/after comparison rather than a timestamp so it cannot be fooled by
    #: clock skew between WSL and the Windows filesystem.
    log_before: tuple[float, int] | None = None

    @property
    def hooked_late(self) -> bool:
        return self.gfx_modules_at_inject > 0


def kill(project: Project) -> None:
    """Close any running instance, and confirm it actually died.

    Called on the way in (a stale instance holds file locks that break a deploy)
    and unconditionally on the way out — an abandoned game process pinning a GPU
    and a headset is the rudest thing a harness can leave behind.

    The confirmation is not paranoia: a run once printed "game closed" while a
    game process kept running for another three minutes, because Stop-Process
    was issued during the window where one instance had exited and its
    replacement had not yet appeared. Reporting a clean teardown that did not
    happen is worse than reporting a messy one.
    """
    for _ in range(5):
        powershell(
            f"Get-Process -Name '{project.process_name}' -ErrorAction SilentlyContinue "
            f"| Stop-Process -Force -ErrorAction SilentlyContinue"
        )
        # Give Windows a beat to release handles on the plugin DLLs, otherwise a
        # deploy immediately after this fails with a sharing violation.
        time.sleep(2)
        if not running_pids(project):
            return

    raise HarnessError(
        f"could not close every '{project.process_name}' process — "
        f"still running: {running_pids(project)}. Close it by hand before the next run."
    )


def running_pids(project: Project) -> list[int]:
    """PIDs of every process with the game's name, newest last."""
    result = powershell(
        f"Get-Process -Name '{project.process_name}' -ErrorAction SilentlyContinue "
        f"| ForEach-Object {{ $_.Id }}"
    )
    return [int(line) for line in result.stdout.split() if line.strip().isdigit()]


def is_running(project: Project, pid: int | None = None) -> bool:
    """Is the game running — and, when a pid is given, is it still *that* one?

    Name-only liveness cannot tell "our instance is fine" from "our instance
    died and something else with the same name is up". Those need different
    responses: the second means the launch environment (mock runtime, Doorstop)
    belongs to a process we no longer control, so the run is invalid even though
    a game is plainly on screen.
    """
    pids = running_pids(project)
    if pid is None:
        return bool(pids)
    return pid in pids


def _write_launcher(project: Project, env: dict[str, str]) -> str:
    """Drop the .cmd that explorer.exe will run. Returns its Windows path."""
    exe = str(PureWindowsPath(project.game_dir) / project.exe)
    lines = ["@echo off"]
    lines += [f"set {name}={value}" for name, value in env.items()]
    lines.append(f'cd /d "{project.game_dir}"')
    # `start ""` so the .cmd returns immediately instead of pinning a console
    # for the life of the game.
    lines.append(f'start "" "{exe}"')

    path = project.work_dir_wsl / _LAUNCHER_NAME
    path.write_text("\r\n".join(lines) + "\r\n", encoding="ascii")
    return str(PureWindowsPath(project.work_dir) / _LAUNCHER_NAME)


def _create_process_ps(project: Project, launcher: str, env: dict[str, str]) -> tuple[str, str]:
    """PowerShell that creates the game process. Returns (snippet, description).

    The two differ only in who becomes the parent — which is the whole variable
    under suspicion, so they are kept side by side rather than one being "the"
    launcher with the other as a comment about history.
    """
    if launcher == "explorer":
        cmd = _write_launcher(project, env)
        return f"Start-Process explorer.exe -ArgumentList '{cmd}'", cmd
    if launcher == "start-process":
        exe = str(PureWindowsPath(project.game_dir) / project.exe)
        # Set on this PowerShell process; Start-Process passes its environment
        # to the child, which is how the mock runtime reaches the game without
        # an intermediate .cmd.
        sets = "".join(f"$env:{name} = '{value}'\n" for name, value in env.items())
        return (
            f"{sets}Start-Process -FilePath '{exe}' -WorkingDirectory '{project.game_dir}'",
            exe,
        )
    raise HarnessError(f"unknown launcher {launcher!r} — expected one of {LAUNCHERS}")


def launch(
    project: Project,
    env: dict[str, str] | None = None,
    launcher: str = LAUNCHERS[0],
) -> LaunchResult:
    """Launch headless and return as soon as the process exists.

    `env` reaches the game either through the launcher .cmd or through this
    PowerShell process, depending on `launcher`. Nothing is injected here, and
    whether the mod loaded is not decided here — see `verify_hook`.
    """
    create, described = _create_process_ps(project, launcher, env or {})
    log_before = bepinex_log_state(project)

    # Launch and poll inside one PowerShell invocation: a powershell.exe round
    # trip per poll would cost more than the thing being measured.
    script = f"""
$ErrorActionPreference = 'Stop'
$sw = [Diagnostics.Stopwatch]::StartNew()
{create}
$p = $null
while ($sw.Elapsed.TotalSeconds -lt {_APPEAR_TIMEOUT_S}) {{
    $p = Get-Process -Name '{project.process_name}' -ErrorAction SilentlyContinue
    if ($p) {{ break }}
    Start-Sleep -Milliseconds {_POLL_MS}
}}
if (-not $p) {{ Write-Output 'ERROR|process never appeared'; exit 1 }}
Write-Output ("OK|" + $p.Id + "|" + $sw.Elapsed.TotalSeconds)
"""
    result = powershell(script, timeout=_APPEAR_TIMEOUT_S + 60)
    line = next(
        (l for l in result.stdout.splitlines() if l.startswith(("OK|", "ERROR|"))),
        "",
    )
    if not line.startswith("OK|"):
        detail = line[len("ERROR|"):] if line else (result.stderr.strip() or "no output")
        raise HarnessError(f"launch failed: {detail}")

    _, pid, appeared = line.split("|")
    return LaunchResult(
        pid=int(pid),
        appeared_after_s=float(appeared),
        launcher=described,
        log_before=log_before,
    )


def inject_renderdoc(project: Project, launch: LaunchResult, capture_prefix: str) -> None:
    """Inject RenderDoc into a running game, and record what it cost.

    Callers choose the moment, and the two choices are not equivalent:

    - **Early**, straight after the process appears (~t+0.5s). The only point
      that beats Unity's D3D device, so the only one that captures anything.
      Also the point where a broken renderdoc.dll costs you the mod — see the
      module docstring — which is why the driver proves BepInEx loaded after.
    - **At the preloader** (~t+3.3s), once Doorstop has handed over. Cannot
      disturb the mod, and cannot capture: measured with both an official and a
      source-built RenderDoc, the hooks register, the mod triggers a capture,
      and no .rdc is ever written.

    `LaunchResult.hooked_late` reports which side of the device the hook landed
    on rather than leaving it to be assumed.
    """
    prefix_dir = PureWindowsPath(capture_prefix).parent
    script = f"""
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path '{prefix_dir}' | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Get-Process -Id {launch.pid} -ErrorAction SilentlyContinue
if (-not $p) {{ Write-Output 'ERROR|process is gone'; exit 1 }}
$p.Refresh()
$gfx = ($p.Modules | Where-Object {{ $_.ModuleName -match 'd3d11|d3d12|dxgi|vulkan' }} | Measure-Object).Count
& '{project.renderdoccmd}' inject --PID {launch.pid} --capture-file '{capture_prefix}' | Out-Null
Write-Output ("OK|" + $sw.Elapsed.TotalSeconds + "|" + $gfx)
"""
    result = powershell(script, timeout=120)
    line = next(
        (l for l in result.stdout.splitlines() if l.startswith(("OK|", "ERROR|"))),
        "",
    )
    if not line.startswith("OK|"):
        detail = line[len("ERROR|"):] if line else (result.stderr.strip() or "no output")
        raise HarnessError(f"renderdoc injection failed: {detail}")

    _, took, gfx = line.split("|")
    launch.injected_after_s = float(took)
    launch.gfx_modules_at_inject = int(gfx)
    launch.capture_prefix = capture_prefix


def loaded_modules(project: Project, pattern: str = "renderdoc|d3d11|dxgi|winhttp") -> list[str]:
    result = powershell(
        f"$p = Get-Process -Name '{project.process_name}' -ErrorAction SilentlyContinue; "
        f"if (-not $p) {{ exit }}; $p.Refresh(); "
        f"$p.Modules | Where-Object {{ $_.ModuleName -match '{pattern}' }} "
        f"| ForEach-Object {{ $_.FileName }}"
    )
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def bepinex_log_state(project: Project) -> tuple[float, int] | None:
    """(mtime, size) of BepInEx/LogOutput.log, or None if it is not there."""
    try:
        stat = project.bepinex_log_wsl.stat()
    except OSError:
        return None
    return (stat.st_mtime, stat.st_size)


def read_bepinex_log(project: Project) -> str:
    try:
        return project.bepinex_log_wsl.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""


def wait_for_preloader(project: Project, launch: LaunchResult,
                       timeout_s: float = _BEPINEX_TIMEOUT_S) -> bool:
    """Wait for BepInEx's first byte of log — i.e. Doorstop having done its job.

    This is the moment RenderDoc becomes safe to inject: Doorstop has already
    caught Unity loading Mono and handed control to the preloader, so nothing is
    left for a second hook engine to trample. It is also the earliest such
    moment, which matters in the other direction — see `inject_renderdoc`.
    """
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        if bepinex_log_state(project) != launch.log_before:
            return True
        if not is_running(project, launch.pid):
            return False
        time.sleep(0.05)
    return False


def wait_for_bepinex(project: Project, launch: LaunchResult,
                     timeout_s: float = _BEPINEX_TIMEOUT_S) -> bool:
    """Wait until BepInEx has finished loading plugins, and say whether it did.

    Waiting for the log to merely *grow* is not enough, and the difference is
    not academic: the preloader's banner lands seconds before the chainloader
    loads anything, so a check on first growth sees a BepInEx with no plugins
    and reads a healthy launch as a failure (measured — it killed a good game
    and relaunched it). The startup marker is the point where "which plugins
    loaded" becomes a settled answer.

    Replaces a fixed sleep: a good launch is confirmed as soon as it is done
    instead of always costing the worst case, and a mod-less one is not handed a
    green light because the sleep happened to be long enough.
    """
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        if bepinex_log_state(project) != launch.log_before:
            if _CHAINLOADER_DONE in read_bepinex_log(project):
                return True
        if not is_running(project, launch.pid):
            return False
        time.sleep(0.5)
    return False


@dataclass
class HookStatus:
    renderdoc: bool
    doorstop: bool
    #: BepInEx's own log grew after we launched — the only proof that the
    #: preloader ran in *this* process rather than some earlier one.
    preloader: bool
    #: Plugin names BepInEx reported loading this run, e.g. ["NOVR 0.4.3"].
    plugins: list[str] = field(default_factory=list)
    modules: list[str] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return self.preloader and bool(self.plugins)

    def describe(self, renderdoc: bool = True) -> str:
        """One line for the console. `renderdoc=False` when nobody asked for it —
        a run that never wanted a GPU capture should not be told it lacks one."""
        if self.preloader:
            loaded = ", ".join(self.plugins) if self.plugins else "no plugins!"
            bepinex = f"yes ({loaded})"
        elif self.doorstop:
            bepinex = "NO — doorstop proxy loaded but the preloader never ran"
        else:
            bepinex = "NO — doorstop proxy not even loaded"
        line = f"BepInEx: {bepinex}"
        return f"{line}, renderdoc: {'yes' if self.renderdoc else 'NO'}" if renderdoc else line


def verify_hook(project: Project, launch: LaunchResult) -> HookStatus:
    """Did the mod actually load into the process we launched?

    The module list alone cannot answer that. Doorstop's WINHTTP.dll proxy is
    loaded out of the game directory whether or not Doorstop does anything —
    measured by disabling Doorstop in its own .ini, which produces a module list
    identical to a good run and a mod that is simply not there. Every mod-less
    run on 2026-08-14 passed the old module-only check, and the harness went on
    to report measurements of an unmodded game as if they meant something.

    So the module list is kept as *diagnosis* — it separates "the proxy never
    loaded" from "it loaded and stood down" — while the verdict comes from
    BepInEx's own log growing since `launch`, and from the plugin lines in it.
    """
    modules = loaded_modules(project)
    lowered = [m.lower() for m in modules]
    game_dir = project.game_dir.lower()
    grew = bepinex_log_state(project) != launch.log_before
    return HookStatus(
        renderdoc=any("renderdoc.dll" in m for m in lowered),
        doorstop=any("winhttp.dll" in m and m.startswith(game_dir) for m in lowered),
        preloader=grew,
        plugins=loaded_plugins(project) if grew else [],
        modules=modules,
    )


def loaded_plugins(project: Project) -> list[str]:
    """Plugin names from BepInEx's `Loading [NOVR 0.4.3]` lines.

    Names the mod as well as the loader: a BepInEx that came up and then failed
    to load our plugin (a bad DLL, a missing dependency) is a different failure
    from one that never ran, and it reads identically in every other signal.
    """
    text = read_bepinex_log(project)
    names = []
    for line in text.splitlines():
        marker = "] Loading ["
        if marker in line and line.rstrip().endswith("]"):
            names.append(line.rstrip()[line.index(marker) + len(marker):-1])
    return names
