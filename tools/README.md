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
tools/capture.py                    # full run: 3 dumps from a built-in mission
tools/capture.py --mission "05. Furball"   # pick a mission by name
tools/capture.py --dumps 5 --delay 12
tools/capture.py --renderdoc        # also inject RenderDoc (off by default)
tools/capture.py --keep-running     # leave the game up to poke at
tools/capture.py --set 'General:HUD Opacity=0'   # A/B any config key
```

`--set SECTION:KEY=VALUE` overrides any entry in the mod config for the duration
of the run and restores it afterwards. Running once with a feature on and once
off, then diffing the output, is the point of the harness; needing to hand-edit
the `.cfg` between runs would put the manual step straight back.

Output:

- **Buffer dumps** — `BepInEx/plugins/NOVR/dumps/<time>/`: per-eye grabs,
  mirror, per-camera render textures, and `meta.json` with stereo matrices,
  canvas inventory and active config. Render them with
  `tools/dump-viewer/build.py` in the novr-research repo.
- **GPU captures** — `<work_dir>/captures/<time>/*.rdc`, plus `.thumb.png`
  extracted headlessly via `renderdoccmd thumb` as a quick "is this frame black"
  check. One directory per run, never overwritten: comparing a run against an
  earlier one is the main use, so runs must not delete each other's evidence.

A successful run takes about 35 seconds with one dump, ~50 s with the default
three. `--max-runtime` (default 300 s) is a hard ceiling on the whole script,
not just the individual waits, so it cannot sit there forever holding a game
process open.

The run restores your `[Debug]` config afterwards, so a normal launch does not
suddenly start a mission by itself. `Auto Start Mission`, `Enable Frame Dumps`
and `RenderDoc Capture On Dump` are restored to **off**, not to whatever the
file happened to say on the way in — a run that dies before its cleanup leaves
them on, and a snapshot-based restore would then preserve that state forever.
One crash used to arm auto-start for every launch after it. The game is always
closed on the way out unless you pass `--keep-running`.

`--mission` defaults to a **built-in** mission (`01. Convoy Attack` and friends),
which spawns you into a cockpit by itself. Free Flight looks like the obvious
choice but offers no airbase hangar spawn, so a run against it starts the
mission and then waits at aircraft selection forever.

When a run produces nothing, the driver prints the mod's own `[NOVR-HARNESS]`
lines and a likely fix — read that before opening Player.log.

## Why the launch path looks strange

All of this is experiment, not documentation. Full detail is in the module
docstrings of `vr_harness/game.py` and `vr_harness/mockxr.py`; the summary:

**Whether the mod loads is proved, never assumed.** Doorstop's `WINHTTP.dll`
proxy sits in the module list whether or not Doorstop does anything — measured
by setting `enabled = false` in `doorstop_config.ini`, which yields a module
list identical to a good run. The old check looked only at that module, so
every mod-less run on 2026-08-14 was reported as `hooks: doorstop/BepInEx: yes`
and the harness went on to measure an unmodded game. The verdict now comes from
`BepInEx/LogOutput.log` growing after launch and naming the plugin it loaded.

**No launcher has stayed reliable, so the driver tries both.** On 2026-08-09 a
WSL-spawned `powershell.exe Start-Process` resolved `WINHTTP.dll` from System32
and never loaded Doorstop, while `explorer.exe` worked every time. On 08-14 the
opposite, then both worked, with nothing on the machine visibly changing.
`capture.py` takes the first launcher that demonstrably loaded the mod.

| Launcher | game-dir `WINHTTP.dll` | BepInEx |
|---|---|---|
| Steam | yes | yes |
| `explorer.exe <exe>` | yes | yes |
| `explorer.exe <launcher.cmd>` | yes | yes (08-09: yes, 08-14: both seen) |
| WSL → powershell `Start-Process <exe>` | yes | 08-09: **no**, 08-14: yes |
| either, with RenderDoc injected at t+1.4s | yes | **no** (0 of 8) |
| explorer → cmd → `renderdoccmd capture <exe>` | yes | **no** |

**RenderDoc and the mod currently cannot both be in the process**, which is why
`--renderdoc` is opt-in and off by default. Injecting into a fresh process — the
only point early enough to beat Unity's D3D device — stops Doorstop dead: 0 of 8
launches loaded BepInEx with RenderDoc injected at ~t+1.4 s, 6 of 6 loaded it
with none. The plain reading is a hook collision, since RenderDoc re-patches
import tables for `LoadLibrary`/`GetProcAddress` and Doorstop's hook on those is
how it catches Unity loading Mono, at ~t+3.3 s. Injecting later, when Doorstop
is done, leaves the mod alone but arrives after the device exists: RenderDoc
registers its hooks, the mod triggers a capture, and no `.rdc` is ever written
(confirmed in RenderDoc's own log). The measured timeline leaves no window —
`d3d11.dll` at t+2.5 s, preloader at t+3.3 s, chainloader done at t+5.3 s. This
worked on 08-09; what changed since is unknown. Until it is, GPU captures need a
launch with no harness, and buffer dumps carry the load.

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

## Replaying a capture from a script

A thumbnail says the frame is not black. Answering "is this draw additive" or
"does this texture really have usable alpha" needs replay:

```bash
tools/rd_run.py --list
tools/rd_run.py hud_blend <work_dir>/captures/<time>/novr_frame1234.rdc
```

Analyses live in `tools/rd/` and are plain Python that `import renderdoc`. They
run on the Windows side, which is not a preference:

- **D3D11 replay is Windows-only.** A Linux `renderdoc` module cannot open
  these captures at all, so `rdc` on the WSL side is limited to the metadata it
  can read without replaying.
- **The RenderDoc release ships no importable module.** Python is embedded
  inside `qrenderdoc.exe`; there is no `renderdoc.pyd` in the install and
  `renderdoccmd` has no `python` subcommand. Scripted replay needs a source
  build of the same version as the installed one, with
  `tools.renderdoc.replay_python` / `replay_pymodules` pointing at it.

Replay cost is worth knowing before you start: roughly 30 draws/second, so a
~16k-draw VR frame takes about nine minutes to walk exhaustively. Scope to the
draws you need when you can.

## Config reference

`[Debug]` entries in the mod config, all off or inert by default:

| Key | Purpose |
|---|---|
| `Enable Frame Dumps` | Let F1 / `dump.trigger` write a buffer dump. Off for normal play — a dump stalls the frame and writes megabytes, which is not what F1 should do to someone who only wanted to fly. |
| `RenderDoc Capture On Dump` | Fire a GPU capture whenever a buffer dump fires. No-op without RenderDoc injected. |
| `Auto Start Mission` | Start a mission and dump unattended. Off for normal play. |
| `Auto Start Mission Name` | Mission to match by name; empty picks a built-in mission. |
| `Auto Dump Count` / `Auto Dump Delay` | How many dumps and how far apart. |

`capture.py` sets these for the duration of a run and restores them afterwards,
so you rarely need to touch them by hand.

## Layout

```
tools/
  capture.py                 the driver
  rd_run.py                  run a replay analysis against a capture
  rd/                        the analyses themselves (Windows-side python)
    hud_blend.py             per-draw blend state + bound textures
  vr-harness.example.toml    copy to ~/.vr-harness.toml
  vr_harness/
    __init__.py              config loading, WSL check, path conversion
    game.py                  launch / inject / verify hooks / kill
    mockxr.py                stage + select the OpenXR mock runtime
    bepinex_cfg.py           surgical .cfg edits with restore
```
