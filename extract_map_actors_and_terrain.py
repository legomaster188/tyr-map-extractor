"""Extract placed-actor transforms, terrain height grids and level bounds for
every map, straight from the cooked game files.

It drives three TyrExtract modes -- --actors, --landscape, --worldbounds --
and writes three files into --out (default ./out):

    map_actors.json       PlayerStart / CaptureZone / spawn-wall / etc.
    map_heights.json      coarse world-space terrain height grid
    map_worldbounds.json  ALevelBounds box + a World-Partition check

Every map here turned out to be ONE PersistentLevel with no
__ExternalActors__ and no WorldPartition exports -- see the isWorldPartition
field this script surfaces per map. That means every placed actor's
RootComponent RelativeLocation is already a world-space transform with no
per-cell frame offset to correct for. (The frame offset that does bite is on
LevelInstance-packed geometry, which is a different thing and does not apply
to PlayerStart/CaptureZone/etc -- they are placed directly.)

    python extract_map_actors_and_terrain.py
    python extract_map_actors_and_terrain.py --gridres 1024

--gridres is the resolution of the height grid, and it costs output size
rather than run time: 256 is a fast look, 1024 is what you want if anything
is going to stand on the terrain. 2048 is the ceiling the landscape textures
themselves impose.
"""
import argparse
import json
import subprocess
import sys

import common

# slug -> (plugin folder, map asset name) -- same table as
# extract_map_geometry.py.
MAPS = {
    "divide": ("TyrMapDivide", "Map_Divide"),
    "fields": ("TyrMapFields", "Map_Fields"),
    "ravine": ("TyrMapRavine", "Map_Ravine"),
    "scorch": ("TyrMapScorch", "Map_Scorch"),
    "wind-valley": ("TyrMapWindValley", "Map_WindValley"),
    "expanse": ("TyrMapExpanse", "Map_Expanse"),
}

# Real class names, read off the distinct export types in Map_Divide.
# PlayerStart is the engine class (no BP_ prefix); every zone/wall type is a
# Blueprint subclass and always ends "_C" in the cooked build.
CLASS_FILTER = ",".join([
    "PlayerStart", "BP_CaptureZone", "BP_AmmunitionZone", "BP_HealZone",
    "BP_AbilityResourceZone", "BP_NoGoZone", "BP_BasicSpawnWall",
])


def pkg_path(slug, plugin, name):
    sub = "" if slug == "expanse" else "Maps/"
    return "Tyr/Plugins/GameFeatures/Maps/%s/Content/%s%s" % (plugin, sub, name)


# Fields and Wind Valley each ship a second, streamed sublevel alongside the
# persistent one -- Map_<Name>_Gameplay -- and Wind Valley's PlayerStart/
# CaptureZone/etc actors live ONLY there; the persistent Map_WindValley has
# none. Found by dumping Map_WindValley with no --classfilter, seeing zero
# PlayerStart/zone actors, then listing the plugin's Maps/ folder and finding
# this second .umap. Fields has one too but it turned out empty of these
# classes -- scan it anyway rather than assume. Terrain and LevelBounds were
# checked directly and only ever live in the persistent map.
def gameplay_pkg_path(slug, plugin, name):
    if slug == "expanse":
        return None
    return "Tyr/Plugins/GameFeatures/Maps/%s/Content/Maps/%s_Gameplay" % (plugin, name)


def run(cfg, mode_args, out, timeout=600):
    subprocess.run(cfg.base_cmd() + mode_args + ["--dumpout", str(out)],
                   check=True, capture_output=True, timeout=timeout)
    return json.loads(out.read_text(encoding="utf-8", errors="replace"))


def main(argv):
    ap = argparse.ArgumentParser(
        description="Extract spawn/zone actors, terrain heights and level bounds for every map.")
    common.add_common_args(ap)
    ap.add_argument("--gridres", type=int, default=256,
                    help="height-grid resolution per side (default %(default)s, ceiling 2048)")
    a = ap.parse_args(argv)

    cfg = common.resolve(a)
    common.print_config(cfg)
    scratch = common.scratch_dir("tyr_mapterrain")

    actors_out, heights_out, bounds_out = {}, {}, {}
    report = []

    for slug, (plugin, name) in MAPS.items():
        pkg = pkg_path(slug, plugin, name)
        row = {"slug": slug, "pkg": pkg}

        try:
            actors = run(cfg, ["--actors", pkg, "--classfilter", CLASS_FILTER],
                         scratch / (slug + "_actors.json"))
            gp_pkg = gameplay_pkg_path(slug, plugin, name)
            if gp_pkg:
                actors += run(cfg, ["--actors", gp_pkg, "--classfilter", CLASS_FILTER],
                              scratch / (slug + "_gameplay_actors.json"))
            actors_out[slug] = actors
            counts = {}
            for act in actors:
                counts[act["class"]] = counts.get(act["class"], 0) + 1
            off_frame = sum(1 for act in actors if not act["worldFrame"])
            row["actors"] = counts
            row["actors_off_world_frame"] = off_frame
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired, ValueError, OSError) as e:
            row["actors_error"] = str(e)[:200]

        try:
            heights = run(cfg, ["--landscape", pkg, "--gridres", str(a.gridres)],
                          scratch / (slug + "_heights.json"), timeout=1200)
            heights_out[slug] = heights
            g = heights.get("grid", {})
            row["landscape_components"] = heights.get("componentsDecoded", 0)
            row["landscape_components_failed"] = heights.get("componentsFailed", 0)
            row["grid_res"] = g.get("resolution")
            if g.get("resolution"):
                row["grid_world_extent"] = (
                    round(g["worldMaxX"] - g["worldMinX"]),
                    round(g["worldMaxY"] - g["worldMinY"]),
                )
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired, ValueError, OSError) as e:
            row["landscape_error"] = str(e)[:200]

        try:
            bounds = run(cfg, ["--worldbounds", pkg], scratch / (slug + "_worldbounds.json"))
            bounds_out[slug] = bounds
            row["level_bounds_found"] = bounds.get("levelBoundsFound")
            row["is_world_partition"] = bounds.get("isWorldPartition")
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired, ValueError, OSError) as e:
            row["worldbounds_error"] = str(e)[:200]

        report.append(row)
        print("%-13s actors=%-4s landscape_ok=%-4s wp=%s"
              % (slug, sum(row.get("actors", {}).values()) if "actors" in row else "FAIL",
                 row.get("landscape_components", "FAIL"),
                 row.get("is_world_partition", "?")))

    (cfg.out / "map_actors.json").write_text(
        json.dumps(actors_out, indent=1, ensure_ascii=False), encoding="utf-8")
    (cfg.out / "map_heights.json").write_text(
        json.dumps(heights_out, ensure_ascii=False), encoding="utf-8")
    (cfg.out / "map_worldbounds.json").write_text(
        json.dumps(bounds_out, indent=1, ensure_ascii=False), encoding="utf-8")

    print("\nwrote %s/{map_actors,map_heights,map_worldbounds}.json" % cfg.out)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
