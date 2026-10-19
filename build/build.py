#!/usr/bin/env python3
"""Build installable CS2 Bot Improver packages (Python 3.12+, .NET 10 SDK)."""

import argparse
import json
from pathlib import Path
import platform
import re
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request
import zipfile


ROOT = Path(__file__).resolve().parent.parent
TEMPLATES = ROOT / "build/templates"
LOCK = ROOT / "build/dependencies.lock.json"
PLUGINS = (
    "BotAI", "BotAimImprover", "BotBuy", "BotRandomizer", "BotState",
    "NadeSystem", "RoundDamageRecap",
)
COMPONENTS = ("BotController", "BotHider", "BotVision")


def run(*args):
    print("+", " ".join(map(str, args)), flush=True)
    subprocess.run(list(map(str, args)), check=True)


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)


def copy_tree(source, target):
    shutil.copytree(source, target, dirs_exist_ok=True)


def text(path):
    # Normalize Git's checkout-dependent line endings before patching/packing.
    return path.read_text(encoding="utf-8-sig")


def write_text(path, content, newline="\n"):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content.replace("\r\n", "\n").replace("\r", "\n")
                     .replace("\n", newline).encode("utf-8"))


class Dependencies:
    def __init__(self, cache):
        self.cache = cache
        self.lock = json.loads(LOCK.read_text(encoding="utf-8"))

    def extract(self, name, target_platform):
        dependency = self.lock[name]
        asset = dependency["assets"][target_platform]
        archive = self.cache / "downloads" / asset
        if not archive.exists():
            archive.parent.mkdir(parents=True, exist_ok=True)
            url = (f"https://github.com/{dependency['repository']}/releases/download/"
                   f"{dependency['tag']}/{asset}")
            print(f"Downloading {url}", flush=True)
            partial = archive.with_suffix(archive.suffix + ".part")
            try:
                with urllib.request.urlopen(url, timeout=120) as response, partial.open("wb") as out:
                    shutil.copyfileobj(response, out)
                partial.replace(archive)
            finally:
                partial.unlink(missing_ok=True)
        target = self.cache / "extracted" / name / dependency["tag"] / target_platform
        marker = target / ".complete"
        if not marker.exists():
            target.mkdir(parents=True, exist_ok=True)
            if asset.endswith(".zip"):
                with zipfile.ZipFile(archive) as package:
                    package.extractall(target)
            else:
                with tarfile.open(archive) as package:
                    package.extractall(target, filter="data")
            marker.touch()
        return target

    def vpkedit(self):
        host = {"Windows": "windows", "Linux": "linux"}.get(platform.system())
        if host is None or platform.machine().lower() not in {"amd64", "x86_64"}:
            raise RuntimeError("VPKEdit requires an x64 Windows or Linux build host")
        name = "vpkeditcli.exe" if host == "windows" else "vpkeditcli"
        executable = next(self.extract("vpkedit", host).rglob(name))
        if host == "linux":
            executable.chmod(0o755)
        return executable


def add_runtime(destination, target_platform, dependencies):
    mm = dependencies.extract("metamod", target_platform) / "addons"
    copy_tree(mm, destination / "addons")
    css = dependencies.extract("counterstrikesharp", target_platform) / "addons"
    copy_tree(css, destination / "addons")
    for name in COMPONENTS:
        root = dependencies.extract(name, target_platform) / "addons"
        # Only native components: managed implementations are built from source.
        copy_tree(root / name, destination / "addons" / name)
        copy(root / "metamod" / f"{name}.vdf",
             destination / "addons/metamod" / f"{name}.vdf")
    native_dir = "win64" if target_platform == "windows" else "linuxsteamrt64"
    suffix = ".dll" if target_platform == "windows" else ".so"
    if not (destination / f"addons/metamod/bin/{native_dir}/metamod.2.cs2{suffix}").is_file():
        raise RuntimeError("The selected Metamod release does not support CS2")


def build_managed(destination, work, configuration):
    def build(project):
        output = work / project.stem
        run("dotnet", "build", project, "-c", configuration, "--nologo", "-o", output)
        return output

    def assembly(project, category, sidecars=True, output=None):
        if output is None:
            output = build(project)
        target = destination / "addons/counterstrikesharp" / category / project.stem
        copy(output / f"{project.stem}.dll", target / f"{project.stem}.dll")
        if sidecars:
            for suffix in (".deps.json", ".pdb"):
                file = output / f"{project.stem}{suffix}"
                if file.exists():
                    copy(file, target / file.name)
        if (output / "shared").exists():
            copy_tree(output / "shared", destination / "addons/counterstrikesharp/shared")
        return output

    for name in ("BotController", "BotHider"):
        base = ROOT / "addons" / name / "csharp"
        # Building Impl also builds its API project reference into this output.
        output = assembly(base / f"{name}Impl/{name}Impl.csproj", "plugins", name != "BotController")
        assembly(base / f"{name}Api/{name}Api.csproj", "shared", name != "BotController", output)

    for name in PLUGINS:
        source = ROOT / "addons/counterstrikesharp/plugins" / name
        project = source / f"{name}.csproj"
        if not project.is_file():
            raise RuntimeError(f"Missing {project}; run git submodule update --init --recursive")
        # BotState's project has a binary HintPath. Supply the API just built from
        # the pinned submodule, restoring the checkout's original file afterwards.
        reference = source / "libs/BotControllerApi.dll"
        original = reference.read_bytes() if reference.is_file() else None
        try:
            if name == "BotState":
                copy(destination / "addons/counterstrikesharp/shared/BotControllerApi/BotControllerApi.dll",
                     reference)
            assembly(project, "plugins")
        finally:
            if name == "BotState":
                if original is None:
                    reference.unlink(missing_ok=True)
                else:
                    reference.write_bytes(original)
        target = destination / "addons/counterstrikesharp/plugins" / name
        for file in sorted(source.rglob("*")):
            relative = file.relative_to(source)
            if any(part in {".git", ".github", "bin", "obj", "libs", "tests", "tools", "disabled"}
                   for part in relative.parts):
                continue
            if (file.is_file() and file.suffix in {".json", ".kv3", ".vdata", ".vdata_c"}
                    and file.name != "packages.lock.json"):
                copy(file, target / relative)
        data = ROOT / "addons/counterstrikesharp/data" / name
        if data.is_dir():
            copy_tree(data, target)


def add_configs(destination, target_platform, keep_rules):
    newline = "\r\n" if target_platform == "windows" else "\n"
    for source in sorted((ROOT / "cfg").glob("*.cfg")):
        if source.stem.endswith("_rules_unchanged"):
            continue
        alternate = source.with_name(source.stem + "_rules_unchanged.cfg")
        selected = alternate if keep_rules and alternate.exists() else source
        write_text(destination / "cfg" / source.name, text(selected), newline)

    gameinfo = text(TEMPLATES / target_platform / "gameinfo.gi")
    write_text(destination / "gameinfo.gi", gameinfo, newline)
    write_text(destination / "backup/WithBots/gameinfo.gi", gameinfo, newline)
    # Line-based matching works with CRLF/LF and does not depend on blank lines.
    online = re.sub(r"(?m)^[ \t]*Game[ \t]+csgo/(?:overrides/botprofile\.vpk|addons/metamod)[ \t]*\n?",
                    "", gameinfo)
    write_text(destination / "backup/Online/gameinfo.gi", online, newline)
    copy(TEMPLATES / "core.json", destination / "addons/counterstrikesharp/configs/core.json")
    copy(TEMPLATES / "map_whitelist.json", destination / "addons/BotHider/map_whitelist.json")


def build_vpks(destination, work, executable):
    for difficulty in ("High", "Low", "Medium"):
        source = work / difficulty
        files = {
            "botprofile.db": ROOT / f"overrides/{difficulty}/botprofile.db",
            "scripts/ai/rush/bt_config.kv3": ROOT / f"overrides/scripts/{difficulty}/bt_config.kv3",
            "scripts/ai/rush/bt_default.kv3": ROOT / "overrides/scripts/bt_default.kv3",
        }
        for relative, path in files.items():
            write_text(source / relative, text(path))
        output = destination / difficulty / "botprofile.vpk"
        output.parent.mkdir(parents=True, exist_ok=True)
        run(executable, source, "--output", output, "--type", "vpk", "--version", "2",
            "--single-file", "--no-progress")
    copy(destination / "Medium/botprofile.vpk", destination / "botprofile.vpk")


def make_zip(source, target, target_platform):
    # Stable order, timestamps and permissions; include executable bits for Linux
    # even when creating a Linux package on a Windows development machine.
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file in sorted(source.rglob("*")):
            if not file.is_file():
                continue
            relative = file.relative_to(source).as_posix()
            info = zipfile.ZipInfo(relative, date_time=(2020, 1, 1, 0, 0, 0))
            info.create_system = 3
            executable = target_platform == "linux" and (
                file.suffix == ".so" or file.name in {"dotnet", "createdump"})
            info.external_attr = (0o100755 if executable else 0o100644) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, file.read_bytes())
    print(f"Created {target}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", choices=("windows", "linux", "all"), default="all")
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts")
    parser.add_argument("--cache", type=Path, default=ROOT / ".build-dependencies")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    dependencies = Dependencies(args.cache.resolve())
    executable = dependencies.vpkedit()
    with tempfile.TemporaryDirectory(prefix="cs2-build-") as temporary:
        work = Path(temporary)
        common = work / "common"
        build_managed(common, work / "managed", args.configuration)
        build_vpks(common / "overrides", work / "vpk", executable)
        targets = ("windows", "linux") if args.platform == "all" else (args.platform,)
        for target_platform in targets:
            base = work / target_platform
            add_runtime(base, target_platform, dependencies)
            copy_tree(common, base)
            variants = (("CS2BotImprover", False), ("CS2BotImprover_rules_unchanged", True)) \
                if target_platform == "windows" else (("CS2BotImprover_for_Linux", True),)
            for name, keep_rules in variants:
                add_configs(base, target_platform, keep_rules)
                make_zip(base, output / f"{name}.zip", target_platform)


if __name__ == "__main__":
    main()
