"""The app as a zip for testers: dist/TartarusSolver-<version>[-full].zip.

Publishes src/Tartarus.App (Release, win-x64) into a staging folder, leaves out debug files (.pdb, .lib), puts the
models the app reads at run time next to the exe (models/...; OCR's come with the build), adds a short how-to, and
zips it. --full also bundles .NET (self-contained): bigger, but nothing to install first.

    .venv/Scripts/python.exe tools/package.py [--full] [--out dist]
"""
import argparse, json, os, re, shutil, subprocess, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

# What the app reads from models/ besides OCR's (which the build copies): the networks, the solver's weights, the
# pictures it tells cards and picks apart by and draws hints with, and the game's texts.
MODELS = [
    "cells/cellnet.onnx", "cells/cellnet.json", "cells/arrownet.onnx", "cells/arrownet.json",
    "slots/*.onnx", "slots/*.json",
    "sandbox/template.bin",
    "solver/value.bin", "solver/policy.bin", "solver/weights.txt", "solver/levels.txt", "solver/chests.txt",
    "cards/*.png",
    "instruments/*.png", "instruments/pick/*.png", "instruments/pick/pick.json",
    "treasures/*.png",
    "chests/*.png",
    "text/tartarus_text.json",
]

HOWTO = """Tartarus Solver {version}

Hints for Tartarus Prison in Torchlight: Infinite. The app only looks at the game's window and reads its log file;
it never touches the game's memory and never presses anything in the game.

Start: run TartarusSolver.exe, then the game. The app finds the game's window and its log by itself.
{runtime}
Hotkeys (while the game or the app is in front):
  Ctrl+Shift+S  scan now            Ctrl+Shift+R  auto scan on/off
  Ctrl+Shift+H  overlay on/off      Ctrl+Shift+B  report a bug (screenshot, the app's log, this run's game log)

The hint: the best move framed bold green, as good ones thin; over it the chests the run is expected to end with.
If something looks wrong, press Ctrl+Shift+B right away and say what you expected.
Settings: %LOCALAPPDATA%\\TartarusSolver\\settings.json
"""
NEEDS_RUNTIME = "\nNeeds the .NET 9 Desktop Runtime (x64): https://dotnet.microsoft.com/download/dotnet/9.0\n"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--full", action="store_true", help="bundle .NET (self-contained)")
    ap.add_argument("--out", type=Path, default=ROOT / "dist")
    a = ap.parse_args()

    version = re.search(r"<Version>([^<]+)</Version>", (ROOT / "Directory.Build.props").read_text(encoding="utf-8")).group(1)
    name = f"TartarusSolver-{version}" + ("-full" if a.full else "")
    stage = a.out / name
    if stage.exists():
        shutil.rmtree(stage)
    app = stage / "TartarusSolver"

    # Bug reports' DSN: kept out of the source; from the environment, else this machine's own app settings.
    dsn = os.environ.get("TARTARUS_SENTRY_DSN", "")
    settings = Path(os.environ.get("LOCALAPPDATA", "")) / "TartarusSolver" / "settings.json"
    if not dsn and settings.exists():
        dsn = json.loads(settings.read_text(encoding="utf-8")).get("ReportDsn") or ""
    print(f"publishing {name}..." + ("" if dsn else " (no Sentry DSN: bug reports will only be saved locally)"))
    subprocess.run(["dotnet", "publish", str(ROOT / "src/Tartarus.App"), "-c", "Release", "-p:Platform=x64", "-r", "win-x64",
                    f"-p:SelfContained={'true' if a.full else 'false'}", f"-p:SentryDsn={dsn}", "-o", str(app), "-v", "q", "-nologo",
                    # Its own build folders: the running app's exe in bin/ stays as it is.
                    "--artifacts-path", str(a.out / ".artifacts")], check=True)
    for junk in list(app.rglob("*.pdb")) + list(app.rglob("*.lib")):
        junk.unlink()
    shutil.rmtree(app / "js", ignore_errors=True)

    missing = []
    for pattern in MODELS:
        found = sorted((ROOT / "models").glob(pattern))
        if not found:
            missing.append(pattern)
        for src in found:
            dst = app / "models" / src.relative_to(ROOT / "models")
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)
    if missing:
        raise SystemExit(f"models missing: {', '.join(missing)}")
    (app / "README.txt").write_text(HOWTO.format(version=version, runtime="" if a.full else NEEDS_RUNTIME), encoding="utf-8")

    zip_path = a.out / f"{name}.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for f in sorted(app.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(stage))
    raw = sum(f.stat().st_size for f in app.rglob("*") if f.is_file())
    files = sum(1 for f in app.rglob("*") if f.is_file())
    print(f"{zip_path}: {files} files, {raw / 2**20:.1f} MB -> {zip_path.stat().st_size / 2**20:.1f} MB")


if __name__ == "__main__":
    main()
