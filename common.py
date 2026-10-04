"""Shared settings for the two extraction drivers: where the game is, which
mapping file to use, where TyrExtract was built, and where output goes.

Each of the four is resolved the same way, most specific first:

    1. the command-line flag  --paks / --usmap / --exe / --out
    2. the environment variable  TYR_PAKS / TYR_USMAP / TYR_EXTRACT / TYR_OUT
    3. a default worked out from this machine

The defaults find the game through Steam's own bookkeeping -- the registry
says where Steam is, `libraryfolders.vdf` says which drives hold libraries,
and the game sits under `steamapps/common/Tyr` in one of them (or the old
`Tyr Playtest` folder, if that is what you have). That
covers a normal install on any drive; a copy of the game somewhere Steam does
not know about needs --paks.

When a value cannot be worked out, the error names the flag to pass. Getting
a stack trace out of subprocess three functions later, because a path was
silently empty, is the failure mode this exists to avoid.
"""
import os
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

# Steam's own layout under a library root. Both halves are fixed by Steam, not
# by the game: "steamapps/common/<install dir>" is where every game lands.
# Early Access installs as "Tyr"; the playtest used "Tyr Playtest". The first
# one present wins.
GAME_INSTALL_DIRS = ("Tyr", "Tyr Playtest")
PAKS_SUBPATH = Path("Tyr") / "Content" / "Paks"
# UE4SS writes its .usmap dump beside itself, and this is where UE4SS goes.
USMAP_SUBPATH = Path("Tyr") / "Binaries" / "Win64" / "ue4ss"

EXE_SUBPATH = Path("TyrExtract") / "bin" / "Release" / "net10.0" / "TyrExtract.exe"


def _steam_roots():
    """Every Steam library root on this machine, best effort.

    The registry key is the only reliable pointer to the Steam install itself;
    everything after that is read out of Steam's `libraryfolders.vdf`, which is
    how Steam records libraries on other drives. Parsed with a regex rather
    than a VDF library because one key is wanted and a dependency is not.
    """
    roots, seen = [], set()

    def add(p):
        try:
            p = Path(p).resolve()
        except OSError:
            return
        if p not in seen and p.is_dir():
            seen.add(p)
            roots.append(p)

    steam = None
    try:
        import winreg
        for hive, key in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam"),
                          (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam")):
            try:
                with winreg.OpenKey(hive, key) as k:
                    steam = winreg.QueryValueEx(k, "SteamPath" if hive == winreg.HKEY_CURRENT_USER
                                                else "InstallPath")[0]
                break
            except OSError:
                continue
    except ImportError:
        pass  # not Windows; the fallbacks below still get a chance

    for cand in (steam, r"C:\Program Files (x86)\Steam", r"C:\Program Files\Steam"):
        if cand:
            add(cand)

    for root in list(roots):
        vdf = root / "steamapps" / "libraryfolders.vdf"
        try:
            text = vdf.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for m in re.finditer(r'"path"\s*"([^"]+)"', text):
            add(m.group(1).replace("\\\\", "\\"))
    return roots


def find_game_dir():
    """The game's install directory, or None."""
    for name in GAME_INSTALL_DIRS:
        for root in _steam_roots():
            cand = root / "steamapps" / "common" / name
            if (cand / PAKS_SUBPATH).is_dir():
                return cand
    return None


def _newest_usmap(directory):
    try:
        found = sorted(Path(directory).glob("*.usmap"), key=lambda p: p.stat().st_mtime)
    except OSError:
        return None
    return found[-1] if found else None


def add_common_args(ap):
    """The four flags both drivers share. Defaults are resolved in resolve(),
    not here, so `--help` does not pay for a registry read."""
    ap.add_argument("--paks", help="the game's Content/Paks folder "
                                   "(default: found via the Steam install; TYR_PAKS)")
    ap.add_argument("--usmap", help="mapping file for the game build "
                                    "(default: the newest .usmap beside this script, then the "
                                    "game's ue4ss folder; TYR_USMAP)")
    ap.add_argument("--exe", help="path to the built TyrExtract.exe "
                                  "(default: %s; TYR_EXTRACT)" % EXE_SUBPATH.as_posix())
    ap.add_argument("--out", help="output directory (default: ./out; TYR_OUT)")


class Config:
    def __init__(self, paks, usmap, exe, out):
        self.paks, self.usmap, self.exe, self.out = paks, usmap, exe, out

    def base_cmd(self):
        return [str(self.exe), "--paks", str(self.paks), "--usmap", str(self.usmap)]


def resolve(args):
    """Turn the flags plus the environment plus this machine into four real
    paths, or exit with a message naming the flag that would fix it."""
    game = None

    paks = args.paks or os.environ.get("TYR_PAKS")
    if not paks:
        game = find_game_dir()
        if game:
            paks = game / PAKS_SUBPATH
    if not paks:
        raise SystemExit(
            "cannot find the game's paks. Pass --paks, e.g.\n"
            r'  --paks "C:\Program Files (x86)\Steam\steamapps\common\Tyr\Tyr\Content\Paks"')
    paks = Path(paks)
    if not paks.is_dir():
        raise SystemExit("--paks is not a directory: %s" % paks)

    usmap = args.usmap or os.environ.get("TYR_USMAP")
    if not usmap:
        usmap = _newest_usmap(HERE)
    if not usmap:
        game = game or find_game_dir()
        if game:
            usmap = _newest_usmap(game / USMAP_SUBPATH)
    if not usmap:
        raise SystemExit(
            "no .usmap mapping file found. One ships in this repo; if you moved it, or you "
            "dumped a newer one, pass --usmap <file>. See the README for how to dump one.")
    usmap = Path(usmap)
    if not usmap.is_file():
        raise SystemExit("--usmap is not a file: %s" % usmap)

    exe = Path(args.exe or os.environ.get("TYR_EXTRACT") or (HERE / EXE_SUBPATH))
    if not exe.is_file():
        raise SystemExit(
            "TyrExtract is not built: %s\nBuild it first:  dotnet build -c Release TyrExtract\n"
            "Or pass --exe <path>." % exe)

    out = Path(args.out or os.environ.get("TYR_OUT") or (HERE / "out"))
    out.mkdir(parents=True, exist_ok=True)

    return Config(paks, usmap, exe, out)


def scratch_dir(name):
    """A working directory for the intermediate JSON the extractor writes per
    map, which nothing downstream reads. Kept out of --out so the output
    directory holds only results."""
    base = os.environ.get("TEMP") or os.environ.get("TMPDIR") or "."
    d = Path(base) / name
    d.mkdir(parents=True, exist_ok=True)
    return d


def print_config(cfg):
    print("paks:  %s" % cfg.paks, file=sys.stderr)
    print("usmap: %s" % cfg.usmap, file=sys.stderr)
    print("out:   %s" % cfg.out, file=sys.stderr)
