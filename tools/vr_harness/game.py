"""Launching the game headless with RenderDoc hooked, and tearing it down again.

Two non-obvious constraints shape this, both established by experiment:

1. **The game must not be created directly by a WSL-spawned process.** BepInEx
   is loaded by Doorstop 4.5, which proxies WINHTTP.dll out of the game
   directory (UnityPlayer.dll imports it for real). When powershell.exe launched
   from WSL creates the process, WINHTTP.dll resolves from System32 instead, so
   Doorstop never loads and the mod is simply absent — with no error anywhere.
   Routing the launch through explorer.exe (a normal Windows shell parent) makes
   the game-directory copy load and BepInEx come up in ~5s. Verified by
   comparing loaded modules; the precise loader reason is not pinned down, but
   it reproduces every time. Steam launch also works, for the same reason —
   Steam is not special, it just isn't WSL.

2. **RenderDoc must be injected, not used as the launcher.** `renderdoccmd
   capture <exe>` creates the process itself and re-breaks Doorstop exactly like
   case 1 (measured: RenderDoc hooks fine, BepInEx never appears). So we launch
   first and inject after. The usual objection — that the D3D device already
   exists by then — does not apply: the process appears ~1.4s after launch and
   d3d11.dll/dxgi.dll are not loaded for a good while after that, so a 100ms
   poll wins comfortably. `LaunchResult.hooked_late` asserts that rather than
   trusting it.

Because we own the intermediate .cmd, we can also set environment variables for
the game — which is how the OpenXR mock runtime gets selected (see mockxr.py).
A per-user registry override does not work: this loader ignores HKCU.
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


@dataclass
class LaunchResult:
    pid: int
    appeared_after_s: float
    injected_after_s: float
    #: d3d11/dxgi module count at injection time. Non-zero means we lost the
    #: race and the capture may be missing early resources.
    gfx_modules_at_inject: int
    capture_prefix: str
    launcher: str

    @property
    def hooked_late(self) -> bool:
        return self.gfx_modules_at_inject > 0


def kill(project: Project) -> None:
    """Close any running instance.

    Called on the way in (a stale instance holds file locks that break a deploy)
    and unconditionally on the way out — an abandoned game process pinning a GPU
    and a headset is the rudest thing a harness can leave behind.
    """
    powershell(
        f"Get-Process -Name '{project.process_name}' -ErrorAction SilentlyContinue "
        f"| Stop-Process -Force -ErrorAction SilentlyContinue"
    )
    # Give Windows a beat to release handles on the plugin DLLs, otherwise a
    # deploy immediately after this fails with a sharing violation.
    time.sleep(2)


def is_running(project: Project) -> bool:
    result = powershell(
        f"if (Get-Process -Name '{project.process_name}' -ErrorAction SilentlyContinue) "
        f"{{ 'yes' }} else {{ 'no' }}"
    )
    return result.stdout.strip().endswith("yes")


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


def launch_with_renderdoc(
    project: Project,
    capture_prefix: str,
    env: dict[str, str] | None = None,
) -> LaunchResult:
    """Launch headless via explorer.exe and inject RenderDoc before D3D init.

    `capture_prefix` is a Windows path prefix; RenderDoc appends
    `_frameNNNN.rdc`. `env` is set inside the launcher .cmd, so it reaches the
    game even though we cannot set it on the process ourselves.
    """
    launcher = _write_launcher(project, env or {})
    prefix_dir = PureWindowsPath(capture_prefix).parent

    # Launch, poll and inject inside one PowerShell invocation: one
    # powershell.exe round trip per poll would blow the injection window.
    script = f"""
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path '{prefix_dir}' | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()
Start-Process explorer.exe -ArgumentList '{launcher}'
$p = $null
while ($sw.Elapsed.TotalSeconds -lt {_APPEAR_TIMEOUT_S}) {{
    $p = Get-Process -Name '{project.process_name}' -ErrorAction SilentlyContinue
    if ($p) {{ break }}
    Start-Sleep -Milliseconds {_POLL_MS}
}}
if (-not $p) {{ Write-Output 'ERROR|process never appeared'; exit 1 }}
$appeared = $sw.Elapsed.TotalSeconds
$p.Refresh()
$gfx = ($p.Modules | Where-Object {{ $_.ModuleName -match 'd3d11|d3d12|dxgi|vulkan' }} | Measure-Object).Count
& '{project.renderdoccmd}' inject --PID $p.Id --capture-file '{capture_prefix}' | Out-Null
Write-Output ("OK|" + $p.Id + "|" + $appeared + "|" + $sw.Elapsed.TotalSeconds + "|" + $gfx)
"""
    result = powershell(script, timeout=_APPEAR_TIMEOUT_S + 60)
    line = next(
        (l for l in result.stdout.splitlines() if l.startswith(("OK|", "ERROR|"))),
        "",
    )
    if not line.startswith("OK|"):
        detail = line[len("ERROR|"):] if line else (result.stderr.strip() or "no output")
        raise HarnessError(f"launch failed: {detail}")

    _, pid, appeared, injected, gfx = line.split("|")
    return LaunchResult(
        pid=int(pid),
        appeared_after_s=float(appeared),
        injected_after_s=float(injected),
        gfx_modules_at_inject=int(gfx),
        capture_prefix=capture_prefix,
        launcher=launcher,
    )


def loaded_modules(project: Project, pattern: str = "renderdoc|d3d11|dxgi|winhttp") -> list[str]:
    result = powershell(
        f"$p = Get-Process -Name '{project.process_name}' -ErrorAction SilentlyContinue; "
        f"if (-not $p) {{ exit }}; $p.Refresh(); "
        f"$p.Modules | Where-Object {{ $_.ModuleName -match '{pattern}' }} "
        f"| ForEach-Object {{ $_.FileName }}"
    )
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


@dataclass
class HookStatus:
    renderdoc: bool
    doorstop: bool
    modules: list[str] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return self.renderdoc and self.doorstop

    def describe(self) -> str:
        parts = [
            f"doorstop/BepInEx: {'yes' if self.doorstop else 'NO'}",
            f"renderdoc: {'yes' if self.renderdoc else 'NO'}",
        ]
        return ", ".join(parts)


def verify_hook(project: Project) -> HookStatus:
    """Check that both hooks actually took, before waiting on any output.

    Doorstop counts as loaded only when WINHTTP.dll resolved out of the game
    directory rather than System32 alone — that is the exact symptom separating
    a working launch from a silently mod-less one, and it is invisible in every
    other signal until the run times out with no dumps.
    """
    modules = loaded_modules(project)
    lowered = [m.lower() for m in modules]
    game_dir = project.game_dir.lower()
    return HookStatus(
        renderdoc=any("renderdoc.dll" in m for m in lowered),
        doorstop=any("winhttp.dll" in m and m.startswith(game_dir) for m in lowered),
        modules=modules,
    )
