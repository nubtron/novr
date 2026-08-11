"""Headless stereo via the OpenXR mock runtime.

The mod's XR stack is OpenXR, and the Unity OpenXR package we already vendor
ships a full mock runtime — NOVR.XR.OpenXR/MockRuntime/windows/x64/. It is
version-matched to the game's Unity/OpenXR build by construction, needs no
download, and despite the XR_UNITY_null_gfx extension name it implements real
D3D11/D3D12/Vulkan swapchains (MockD3D11_xrCreateSwapchainHook,
xrEnumerateSwapchainImages, CreateCommittedResource), so the game renders actual
GPU frames that RenderDoc can capture.

Selecting it: the OpenXR loader picks a runtime from XR_RUNTIME_JSON first, then
the registry. Only the env var is usable here — a per-user HKCU ActiveRuntime
override is ignored by this loader (measured: it stayed on VirtualDesktopXR),
and HKLM would need admin and would change the runtime machine-wide for every
app. The env var is process-scoped, needs no privileges, and cannot leak into
the user's normal VR session.

Verified with no headset attached:
    Runtime Name: Unity Mock Runtime   Runtime Version: 0.0.2
    OpenXRSession: UNKNOWN -> IDLE -> READY -> SYNCHRONIZED -> VISIBLE -> FOCUSED
"""

from __future__ import annotations

import shutil
from pathlib import Path, PureWindowsPath

from . import HarnessError, Project

#: Where the runtime lives in the repo, relative to the repo root.
REPO_RUNTIME_DIR = Path("NOVR.XR.OpenXR/MockRuntime")

MANIFEST_NAME = "unity-mock-runtime.json"
_STAGE_DIR = "mock-runtime"


def repo_root() -> Path:
    """The novr checkout this script is running from."""
    return Path(__file__).resolve().parents[2]


def stage(project: Project, force: bool = False) -> str:
    """Copy the vendored runtime somewhere Windows can load it.

    The repo lives on the WSL filesystem, which Windows processes cannot read
    reliably, so the runtime is staged into the harness work_dir. The manifest's
    library_path is relative ('.\\windows\\x64\\mock_runtime.dll'), so the
    directory layout has to be preserved rather than flattened.

    Returns the Windows path of the staged manifest, for XR_RUNTIME_JSON.
    """
    source = repo_root() / REPO_RUNTIME_DIR
    manifest = source / MANIFEST_NAME
    payload = source / "windows" / "x64"
    if not manifest.is_file() or not payload.is_dir():
        raise HarnessError(
            f"vendored OpenXR mock runtime not found under {source} "
            "(expected unity-mock-runtime.json and windows/x64/)"
        )

    target = project.work_dir_wsl / _STAGE_DIR
    target_payload = target / "windows" / "x64"

    if force and target.exists():
        shutil.rmtree(target)

    target_payload.mkdir(parents=True, exist_ok=True)
    shutil.copy2(manifest, target / MANIFEST_NAME)
    for dll in sorted(payload.glob("*.dll")):
        shutil.copy2(dll, target_payload / dll.name)

    return str(PureWindowsPath(project.work_dir) / _STAGE_DIR / MANIFEST_NAME)


def env(project: Project, force: bool = False) -> dict[str, str]:
    """Environment the launcher .cmd must set for headless stereo.

    Only the runtime manifest. The staged directory also holds mock_api.dll,
    the runtime's test API, and putting it on PATH is enough to make its
    DllImports resolve — but not enough to make them do anything: mock_api
    learns where the runtime lives from the hook the MockRuntime *feature*
    installs at instance creation, and NOVR loads the runtime straight from
    XR_RUNTIME_JSON without that feature. MockRuntime_SetView then succeeds and
    moves nothing. Measured, not assumed: a five-angle yaw sweep through it
    produced five identical frames. The harness turns the head through the mod
    instead (NOVR/HarnessViewPose.cs).
    """
    return {"XR_RUNTIME_JSON": stage(project, force=force)}
