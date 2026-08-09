"""Shared plumbing for the VR harness tools.

Everything machine-specific lives in ~/.vr-harness.toml (see
tools/vr-harness.example.toml); this module is the only place that knows how to
find and interpret it, so the individual scripts stay path-free and commitable.

The harness drives Windows processes from WSL, so most values are Windows paths
that occasionally need a WSL form to be read directly. `Project` keeps both and
names them explicitly (`game_dir` vs `game_dir_wsl`) rather than guessing at the
call site.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tomllib
from dataclasses import dataclass
from pathlib import Path, PurePosixPath, PureWindowsPath

__all__ = [
    "HarnessError",
    "Project",
    "load_config",
    "load_project",
    "require_wsl",
    "to_wsl",
    "to_win",
    "powershell",
]

CONFIG_ENV = "VR_HARNESS_CONFIG"
CONFIG_NAME = ".vr-harness.toml"


class HarnessError(RuntimeError):
    """Anything the user is expected to fix (missing config, missing tool)."""


def require_wsl() -> None:
    """The harness assumes WSL + Windows interop.

    Only WSL is supported today: every script shells out to powershell.exe to
    launch Steam and drive renderdoccmd. A native-Linux port would need a
    different launcher entirely, so fail loudly rather than half-work.
    """
    if os.environ.get("WSL_DISTRO_NAME"):
        return
    try:
        release = Path("/proc/version").read_text(encoding="utf-8", errors="replace")
    except OSError:
        release = ""
    if "microsoft" in release.lower():
        return
    raise HarnessError(
        "these tools currently require WSL with Windows interop "
        "(they launch the game through Steam and drive renderdoccmd.exe)"
    )


def _wslpath(mode: str, value: str) -> str:
    try:
        out = subprocess.run(
            ["wslpath", mode, value],
            check=True,
            capture_output=True,
            text=True,
        )
    except FileNotFoundError as exc:  # pragma: no cover - require_wsl covers this
        raise HarnessError("wslpath not found — not running under WSL?") from exc
    except subprocess.CalledProcessError as exc:
        raise HarnessError(f"wslpath {mode} {value!r} failed: {exc.stderr.strip()}") from exc
    return out.stdout.strip()


def to_wsl(windows_path: str) -> Path:
    """'C:\\foo\\bar' -> PosixPath('/mnt/c/foo/bar')."""
    return Path(_wslpath("-u", str(windows_path)))


def to_win(wsl_path: str | os.PathLike[str]) -> str:
    """'/mnt/c/foo' -> 'C:\\foo'. Also works for paths that do not exist yet."""
    return _wslpath("-w", str(wsl_path))


def _config_candidates() -> list[Path]:
    explicit = os.environ.get(CONFIG_ENV)
    if explicit:
        return [Path(explicit).expanduser()]

    candidates = [Path.home() / CONFIG_NAME]

    # Windows-side fallback, so a single file can serve both a WSL session and
    # anything run natively on Windows. USERPROFILE is not exported into WSL by
    # default, so derive it from the Windows shell when it is missing.
    userprofile = os.environ.get("USERPROFILE")
    if not userprofile:
        try:
            userprofile = subprocess.run(
                ["cmd.exe", "/c", "echo %USERPROFILE%"],
                capture_output=True,
                text=True,
                timeout=15,
                cwd="/",
            ).stdout.strip()
        except (OSError, subprocess.SubprocessError):
            userprofile = ""
    if userprofile and not userprofile.startswith("%"):
        try:
            candidates.append(to_wsl(userprofile) / CONFIG_NAME)
        except HarnessError:
            pass

    return candidates


def load_config(path: str | os.PathLike[str] | None = None) -> dict:
    """Read the harness TOML. Raises HarnessError with the paths tried."""
    candidates = [Path(path).expanduser()] if path else _config_candidates()
    for candidate in candidates:
        if candidate.is_file():
            with candidate.open("rb") as handle:
                config = tomllib.load(handle)
            config["_source"] = str(candidate)
            return config

    tried = "\n  ".join(str(c) for c in candidates)
    raise HarnessError(
        "no harness config found. Copy tools/vr-harness.example.toml to "
        f"~/{CONFIG_NAME} and edit it for this machine.\nTried:\n  {tried}"
    )


@dataclass(frozen=True)
class Project:
    """One game's paths, resolved and validated."""

    name: str
    game_dir: str  # Windows form
    exe: str  # relative to game_dir
    process_name: str
    plugin_dir: str  # Windows form
    config_file: str  # Windows form
    player_log: str  # Windows form
    renderdoc_dir: str  # Windows form
    work_dir: str  # Windows form
    source: str  # which config file this came from
    #: Windows python.exe able to `import renderdoc`, and the directory holding
    #: renderdoc.pyd. Both empty unless the machine has a replay-capable build:
    #: the shipped RenderDoc release embeds Python inside qrenderdoc and does
    #: not install a standalone module, so replay analysis needs a source build.
    replay_python: str = ""
    replay_pymodules: str = ""

    @property
    def game_dir_wsl(self) -> Path:
        return to_wsl(self.game_dir)

    @property
    def plugin_dir_wsl(self) -> Path:
        return to_wsl(self.plugin_dir)

    @property
    def config_file_wsl(self) -> Path:
        return to_wsl(self.config_file)

    @property
    def player_log_wsl(self) -> Path:
        return to_wsl(self.player_log)

    @property
    def work_dir_wsl(self) -> Path:
        return to_wsl(self.work_dir)

    @property
    def renderdoccmd(self) -> str:
        return str(PureWindowsPath(self.renderdoc_dir) / "renderdoccmd.exe")

    @property
    def can_replay(self) -> bool:
        return bool(self.replay_python and self.replay_pymodules)

    def require_replay(self) -> None:
        """Fail with the reason, not just 'not configured'."""
        if self.can_replay:
            return
        raise HarnessError(
            "no replay-capable RenderDoc Python module configured.\n"
            "The RenderDoc release build embeds Python inside qrenderdoc and ships no\n"
            "importable renderdoc.pyd, so replaying a capture from a script needs a\n"
            "source build (see REFERENCES.md). Once built, set in "
            f"{self.source}:\n"
            "  [tools.renderdoc]\n"
            "  replay_python    = 'C:\\...\\python.exe'\n"
            "  replay_pymodules = 'C:\\...\\renderdoc\\x64\\Release\\pymodules'"
        )

    def check(self) -> None:
        """Validate the paths that every command needs, with fixable errors."""
        if not self.game_dir_wsl.is_dir():
            raise HarnessError(
                f"game_dir does not exist: {self.game_dir}\n"
                f"(fix projects.{self.name}.game_dir in {self.source})"
            )
        if not (self.game_dir_wsl / self.exe).is_file():
            raise HarnessError(
                f"exe not found: {self.exe} under {self.game_dir}\n"
                f"(fix projects.{self.name}.exe in {self.source})"
            )
        if not to_wsl(self.renderdoccmd).is_file():
            raise HarnessError(
                f"renderdoccmd.exe not found at {self.renderdoccmd}\n"
                f"(fix tools.renderdoc.dir in {self.source})"
            )
        work = self.work_dir_wsl
        if str(work).startswith("//wsl") or str(work).startswith("\\\\wsl"):
            raise HarnessError(
                f"work_dir must be a real Windows path, not a WSL share: {self.work_dir}"
            )
        work.mkdir(parents=True, exist_ok=True)


def _require(table: dict, key: str, where: str, source: str):
    if key not in table:
        raise HarnessError(f"missing '{key}' in [{where}] of {source}")
    return table[key]


def load_project(name: str, path: str | os.PathLike[str] | None = None) -> Project:
    """Resolve one project from the harness config, validating as we go."""
    require_wsl()
    config = load_config(path)
    source = config["_source"]

    projects = config.get("projects", {})
    if name not in projects:
        known = ", ".join(sorted(projects)) or "(none)"
        raise HarnessError(
            f"no [projects.{name}] table in {source} (found: {known})"
        )
    entry = projects[name]
    tools = config.get("tools", {})

    game_dir = _require(entry, "game_dir", f"projects.{name}", source)

    def under_game(value: str) -> str:
        """Project-relative paths are written POSIX-style for readability."""
        if PureWindowsPath(value).is_absolute():
            return value
        return str(PureWindowsPath(game_dir) / PurePosixPath(value))

    project = Project(
        name=name,
        game_dir=game_dir,
        exe=_require(entry, "exe", f"projects.{name}", source),
        process_name=_require(entry, "process_name", f"projects.{name}", source),
        plugin_dir=under_game(entry.get("plugin_dir", "BepInEx/plugins")),
        config_file=under_game(entry.get("config_file", "")) if entry.get("config_file") else "",
        player_log=entry.get("player_log", ""),
        renderdoc_dir=_require(
            tools.get("renderdoc", {}), "dir", "tools.renderdoc", source
        ),
        work_dir=_require(tools.get("harness", {}), "work_dir", "tools.harness", source),
        source=source,
        replay_python=tools.get("renderdoc", {}).get("replay_python", ""),
        replay_pymodules=tools.get("renderdoc", {}).get("replay_pymodules", ""),
    )
    project.check()
    return project


def powershell(script: str, timeout: int = 120) -> subprocess.CompletedProcess:
    """Run a PowerShell snippet on the Windows side.

    cwd is forced to / because a WSL cwd makes powershell.exe emit a UNC warning
    and fall back to C:\\Windows, which has bitten path-relative commands before.
    """
    exe = shutil.which("powershell.exe") or "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
    return subprocess.run(
        [exe, "-NoProfile", "-NonInteractive", "-Command", script],
        capture_output=True,
        text=True,
        timeout=timeout,
        cwd="/",
    )


def die(message: str) -> "typing.NoReturn":  # noqa: F821 - annotation only
    print(f"error: {message}", file=sys.stderr)
    raise SystemExit(1)
