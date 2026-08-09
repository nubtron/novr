# VR harness tools

Unattended verification for NOVR: launch the game with no headset, fly a
mission, capture both the mod's own state and the GPU's view of a frame, and
shut down. The point is to replace "wear the headset, fly, squint at the HUD,
form an opinion" with output you can diff.

Everything machine-specific lives in `~/.vr-harness.toml`, which is **not**
committed. Copy `vr-harness.example.toml` there and edit the paths.

## Setup

```bash
cp tools/vr-harness.example.toml ~/.vr-harness.toml
$EDITOR ~/.vr-harness.toml          # game_dir, renderdoc dir, work_dir, player_log
```

Requirements: WSL with Windows interop (checked at startup, with a clear error
if absent), Python 3.11+ for `tomllib`, and RenderDoc installed on the Windows
side. The OpenXR mock runtime needs no install — it is vendored in the repo at
`NOVR.XR.OpenXR/MockRuntime/` and staged automatically.

## Running

```bash
tools/capture.py                    # full run: 3 dumps from a Free Flight mission
tools/capture.py --mission "Free"   # pick a mission by name
tools/capture.py --dumps 5 --delay 12
tools/capture.py --no-renderdoc     # buffer dumps only
tools/capture.py --keep-running     # leave the game up to poke at
```

Output:

- **Buffer dumps** — `BepInEx/plugins/NOVR/dumps/<time>/`: per-eye grabs,
  mirror, per-camera render textures, and `meta.json` with stereo matrices,
  canvas inventory and active config. Render them with
  `tools/dump-viewer/build.py` in the novr-research repo.
- **GPU captures** — `<work_dir>/captures/*.rdc`, plus `.thumb.png` extracted
  headlessly via `renderdoccmd thumb` as a quick "is this frame black" check.
  Inspect with the `rdc` CLI or qrenderdoc.

A successful run takes about 35 seconds with one dump, ~50 s with the default
three. `--max-runtime` (default 300 s) is a hard ceiling on the whole script,
not just the individual waits, so it cannot sit there forever holding a game
process open.

The run restores your `[Debug]` config afterwards, so a normal launch does not
suddenly start a mission by itself. The game is always closed on the way out
unless you pass `--keep-running`.

`--mission` defaults to a **built-in** mission (`01. Convoy Attack` and friends),
which spawns you into a cockpit by itself. Free Flight looks like the obvious
choice but offers no airbase hangar spawn, so a run against it starts the
mission and then waits at aircraft selection forever.

When a run produces nothing, the driver prints the mod's own `[NOVR-HARNESS]`
lines and a likely fix — read that before opening Player.log.

## Why the launch path looks strange

Two constraints, both established by experiment rather than documentation. Full
detail is in the module docstrings of `vr_harness/game.py` and
`vr_harness/mockxr.py`; the summary:

**The game must not be created directly by a WSL-spawned process.** BepInEx is
loaded by Doorstop 4.5, which proxies `WINHTTP.dll` out of the game directory.
When `powershell.exe` launched from WSL creates the process, `WINHTTP.dll`
resolves from System32 instead, Doorstop never loads, and the mod is silently
absent — no error anywhere, the game just looks fine and has no VR. Routing
through `explorer.exe` fixes it. Steam launch also works; Steam is not special,
it just isn't WSL.

| Launcher | game-dir `WINHTTP.dll` | BepInEx |
|---|---|---|
| Steam | yes | yes |
| `explorer.exe <exe>` | yes | yes |
| `explorer.exe <launcher.cmd>` | yes | yes |
| WSL → powershell `Start-Process <exe>` | no | **no** |
| explorer → cmd → `renderdoccmd capture <exe>` | yes | **no** |

**RenderDoc must be injected, not used as the launcher.** That last row is why:
`renderdoccmd capture` creates the process itself and hits the same failure. So
the harness launches first and injects after. The usual objection to late
injection — that the D3D device already exists — does not apply here: the
process appears ~1.4s after launch and has no `d3d11.dll`/`dxgi.dll` loaded for
a good while after, so a 100 ms poll wins comfortably. The driver checks this
rather than assuming it, and warns if graphics modules were already loaded.

Because the harness owns an intermediate `.cmd`, it can set environment
variables for the game — which is how the mock OpenXR runtime is selected.
A per-user `HKCU` registry override does **not** work (measured: the loader
ignored it and stayed on VirtualDesktopXR), and `HKLM` would need admin and
would repoint every VR app on the machine. `XR_RUNTIME_JSON` is process-scoped
and cannot leak into your normal VR session.

## Headless stereo

The mod's XR stack is OpenXR and the vendored Unity OpenXR package already ships
a matching mock runtime, so there is nothing to download and no version skew.
Despite the `XR_UNITY_null_gfx` extension name it implements real D3D11/D3D12/
Vulkan swapchains, so the game renders actual GPU frames that RenderDoc can
capture. Verified with no headset attached:

```
Runtime Name: Unity Mock Runtime   Runtime Version: 0.0.2
OpenXRSession: UNKNOWN -> IDLE -> READY -> SYNCHRONIZED -> VISIBLE -> FOCUSED
[NOVR] Native VR UI root created.
```

## Config reference

`[Debug]` entries in the mod config, all off or inert by default:

| Key | Purpose |
|---|---|
| `RenderDoc Capture On Dump` | Fire a GPU capture whenever a buffer dump fires. No-op without RenderDoc injected. |
| `Auto Start Mission` | Start a mission and dump unattended. Off for normal play. |
| `Auto Start Mission Name` | Mission to match by name; empty picks a Free Flight mission. |
| `Auto Dump Count` / `Auto Dump Delay` | How many dumps and how far apart. |

`capture.py` sets these for the duration of a run and restores them afterwards,
so you rarely need to touch them by hand.

## Layout

```
tools/
  capture.py                 the driver
  vr-harness.example.toml    copy to ~/.vr-harness.toml
  vr_harness/
    __init__.py              config loading, WSL check, path conversion
    game.py                  launch / inject / verify hooks / kill
    mockxr.py                stage + select the OpenXR mock runtime
    bepinex_cfg.py           surgical .cfg edits with restore
```
