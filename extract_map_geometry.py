"""Extract static-mesh geometry (rocks, cliffs, structures, foliage) for
every map, straight from the cooked game files.

extract_map_actors_and_terrain.py gives a smooth terrain grid and a handful of
gameplay markers; neither says anything about what is actually STANDING on the
terrain, which is what makes a map look like Scorch instead of a coloured hill.
This script pulls that in two passes per map, using TyrExtract's --meshscene /
--meshexport modes:

    1. --meshscene walks the map package and returns EVERY distinct static
       mesh referenced (with its local bounding box) plus EVERY instance's
       world transform (position/quaternion/scale). This is the full
       inventory -- one JSON file per map, kept whole because it answers
       "what's actually here" precisely.

       The load-bearing part of that walk is class-driven: these maps are
       assembled from UE5 **Packed Level Actors** (`BP_LI_*` under
       `/Game/World/<Map>/PLA/`), and a packed level actor's ISM/HISM
       components live in the BLUEPRINT CLASS package, not in the map. The
       map's own copy of each component carries no StaticMesh and no
       PerInstanceSMData -- neither differs from the archetype, so neither
       is serialised. Walking only the map package therefore finds the
       InstancedFoliageActor's scatter and the loose StaticMeshActors and
       nothing else, which is how Divide came back as 22 meshes (pebbles,
       grass, seven tarps, three cliffs) when it actually has 169 across
       81 packed level actors. See Program.cs's --meshscene comment.
    2. Every mesh that survives EXCLUDE_PATH_SUBSTR gets its geometry
       exported to glTF via --meshexport. `--top N` cuts that to the N
       highest-ranking by instances x footprint; the default is 0 = all.
       An early version exported the top 20 and a renderer built on it drew
       7,361 pebbles, some grass and three cliffs, because the ranking
       (instances x footprint) is dominated by whatever is scattered most,
       and the shape-defining cliffs are placed 1-12 times each. Ranking is
       for the report only.

Output, under --out (default ./out) -- easily 50-200 MB a map, so it is
gitignored here:

    <out>/<slug>_scene.json          full instance inventory (persistent +
                                     gameplay sublevel, merged, mesh indices
                                     renumbered)
    <out>/meshes/<mesh path>.glb     exported geometry for every non-excluded
                                     mesh, deduplicated by package path ACROSS
                                     maps (many rock/foliage meshes are shared)
    <out>/report.json                per-map + per-mesh stats

    python extract_map_geometry.py                          # scene + report only
    python extract_map_geometry.py --export                 # also export glTF
    python extract_map_geometry.py --export --only scorch,fields
    python extract_map_geometry.py --top 20
"""
import argparse
import json
import subprocess
import sys

import common

# slug -> (plugin folder, map asset name). The asset name is not the map name
# on every map, so read the second column when looking one up in the paks.
MAPS = {
    "divide": ("TyrMapDivide", "Map_Divide"),
    "fields": ("TyrMapFields", "Map_Fields"),
    "ravine": ("TyrMapRavine", "Map_Ravine"),
    "scorch": ("TyrMapScorch", "Map_Scorch"),
    "wind-valley": ("TyrMapWindValley", "Map_WindValley"),
    "expanse": ("TyrMapExpanse", "Map_Expanse"),
    # Core, added by the 2026-09-24 patch. Map_Core_V2 is the persistent level
    # (the plugin's PluginMap asset, and what a replay header names).
    # Map_Core_LD is an unloaded greybox and Map_Core_Lighting_V2 is lighting
    # only; neither is scanned.
    "core": ("TyrMapCore", "Map_Core_V2"),
}


def pkg_path(slug, plugin, name):
    sub = "" if slug == "expanse" else "Maps/"
    return "Tyr/Plugins/GameFeatures/Maps/%s/Content/%s%s" % (plugin, sub, name)


def gameplay_pkg_path(slug, plugin, name):
    if slug == "expanse":
        return None
    return "Tyr/Plugins/GameFeatures/Maps/%s/Content/Maps/%s_Gameplay" % (plugin, name)


def run_meshscene(cfg, pkg, out):
    subprocess.run(cfg.base_cmd() + ["--meshscene", pkg, "--dumpout", str(out)],
                   check=True, capture_output=True, timeout=1200)
    return json.loads(out.read_text(encoding="utf-8", errors="replace"))


def run_meshexport_batch(cfg, paths, out_dir, scratch, lod=None):
    """Exports many meshes in ONE extractor process. Mounting the paks and
    the usmap costs ~2 s, which dominated when this was one process per
    mesh and the mesh count went from 20/map to all of them.

    `lod="all"` asks for every LOD the mesh carries rather than LOD0 alone.
    The exporter then writes `<Mesh>_LOD<N>.glb` per level INSTEAD of the
    plain `<Mesh>.glb`, so the two runs land side by side in the same tree
    and neither overwrites the other -- which is what lets a renderer keep
    structural meshes on the LOD0 files it already has while reading a lower
    level for foliage."""
    if not paths:
        return [], []
    listfile = scratch / "meshexport_list.txt"
    listfile.write_text(chr(10).join(paths), encoding="utf-8")
    resfile = scratch / "meshexport_result.json"
    cmd = cfg.base_cmd() + ["--meshexportlist", str(listfile),
                            "--meshout", str(out_dir), "--dumpout", str(resfile)]
    if lod:
        cmd += ["--meshlod", lod]
    subprocess.run(cmd, capture_output=True, timeout=3600)
    res = json.loads(resfile.read_text(encoding="utf-8"))
    failed_paths = {f["path"] for f in res["failed"]}
    return [p for p in paths if p not in failed_paths], res["failed"]


def _count_by(items, key):
    out = {}
    for it in items:
        v = it.get(key)
        out[v] = out.get(v, 0) + 1
    return out


def merge_scenes(parts):
    """Merge N --meshscene results (persistent + optional gameplay sublevel)
    into one, renumbering instances' mesh indices against a single
    deduplicated meshes[] list keyed by package path."""
    meshes, index_by_path, instances, actors = [], {}, [], []
    for part in parts:
        remap = {}
        for i, m in enumerate(part["meshes"]):
            path = m["path"]
            if path not in index_by_path:
                index_by_path[path] = len(meshes)
                meshes.append(m)
            remap[i] = index_by_path[path]
        actor_base = len(actors)
        actors.extend(part.get("bpActorList") or [])
        for inst in part["instances"]:
            inst = dict(inst)
            inst["mesh"] = remap[inst["mesh"]]
            if inst.get("a", -1) >= 0:
                inst["a"] = inst["a"] + actor_base
            instances.append(inst)
    return meshes, instances, actors


def footprint(mesh):
    ex, ey = mesh.get("extentX") or 0, mesh.get("extentY") or 0
    return (ex * 2) * (ey * 2)


# Naive instances-x-footprint ranking puts distant skybox dressing ahead of
# actual gameplay geometry: Fields' vista mountain cards are 600,000+ units
# across (the whole map is ~900,000 units), so one background instance
# outscores every rock and structure combined. Same problem the other way
# for WindValley's SM_Collision_Proxy_* (invisible physics-only shapes, not
# art) and the RVT_Tank_Tracks / PCG_Spline_Terrain decal meshes (a single
# ~1.5M-triangle plane draping the whole map, functionally a second terrain
# drape that duplicates the height grid). None of these are "a rock, a cliff,
# a building" -- exclude them from ranking outright rather than let them win a
# top-N slot.
# "/engine/" catches the placeholder shapes CUE4Parse resolves for collision
# primitives -- every BP_Bush actor contributes two /Engine/EngineMeshes/Sphere
# "instances" that are its SphereComponent trigger volumes, not art (1,616 of
# them on Divide alone). Sky spheres and BasicShapes/Plane come out the same
# way. None of it is geometry anybody wants drawn.
EXCLUDE_PATH_SUBSTR = ("cloudring", "collision_proxy", "pcg_spline_terrain",
                       "_generated", "basicshapes", "/engine/", "lighting_studio")
# A single mesh above this triangle count is either the vista/track cases
# above under a name that didn't match, or something else worth a human
# look before spending an export on it -- skip and report, don't guess.
MAX_EXPORT_TRIANGLES = 150_000

# Horizon filler: the far-distance cards and merged aggregates that stand in
# for scenery beyond the playable area. They must not be drawn, and telling
# them from real geometry is not a substring test on "vista" -- that deleted
# all 692 instances of Fields' VistaBuilding_01 kit, which is a real building
# standing on real terrain just outside the playable square.
#
# The discriminator is the game's own filing, and the two halves fall cleanly
# apart when every "vista" mesh in all six maps is listed:
#
#   - The real backdrop lives in a folder literally called `Vistas`
#     (/Game/World/Fields/Vistas/Meshes/SM_Vista_MountainCard_01 and friends,
#     reused as horizon filler by Divide, Ravine and Scorch too), or is a
#     merged aggregate of it (SM_MERGED_Vista_RidgeWall_01,
#     SM_MERGED_Fields_VistaBuildings_01). Note the artists' own split: the
#     DISTANT copies of that same building were merged into `Vistas/_MERGED`,
#     while the near one stayed loose kit parts under `Structures/`. That is
#     the map telling you which is which.
#   - SM_Vista_Mountain_01 is the one backdrop mesh filed outside a `Vistas`
#     folder (/Game/World/Structures/Mountain_01/), so the name test carries
#     it. All 19 of its instances also sit 1.0-11.6 map spans out and would
#     be far-field culled by any renderer anyway; the explicit rule is here so
#     that a backdrop's removal never DEPENDS on a distance filter.
#
# `SM_Vista_` and `SM_MERGED_Vista` both end where the kit name does not:
# the kept meshes are `SM_VistaBuilding_01_...`, i.e. "Vista" followed by
# "Building", never by the separator.
BACKDROP_FOLDER = 'vistas'
BACKDROP_NAME_PREFIXES = ('sm_vista_', 'sm_merged_vista')


def is_backdrop(path, name):
    """True for horizon filler that must never be drawn, by the game's own
    filing rather than by a substring of the mesh name. See BACKDROP_FOLDER."""
    if BACKDROP_FOLDER in [seg.lower() for seg in path.split('/')]:
        return True
    return name.lower().startswith(BACKDROP_NAME_PREFIXES)


def is_excluded(mesh):
    p = mesh["path"].lower()
    if any(s in p for s in EXCLUDE_PATH_SUBSTR):
        return True
    return is_backdrop(mesh["path"], mesh.get("name") or mesh["path"].rsplit("/", 1)[-1])


# At LOD0 a single gameplay tree costs 18,507 triangles, which is what makes a
# triangle budget buy THREE of Wind Valley's 211 large trees while a
# 21-triangle wheat blade gets 640 copies. The answer is not a bigger budget,
# it is cheaper geometry: the same tree is 1,157 triangles at LOD4 and occupies
# the same bounding box to within 2 cm. See docs/HOW-IT-WORKS.md.
#
# Default filter is the game's own /Foliages/ folder, which across all six maps
# is exactly the set that reads as foliage: 101 meshes, nothing under Foliages/
# that is anything else, nothing outside it that is foliage. Widening it is a
# flag, not an edit.
LOD_FILTER_DEFAULT = "/foliages/"


def export_lods(cfg, filter_substr, only, scratch):
    """Exports every LOD of the meshes matching `filter_substr`, reading the
    scene JSON ALREADY on disk rather than re-scanning the maps.

    Re-scanning is 2 minutes a map and rewrites <slug>_scene.json; this mode
    needs neither. The mesh list is whatever the last scan found."""
    wanted = set(only.split(",")) if only else set(MAPS)
    mesh_dir = cfg.out / "meshes"
    mesh_dir.mkdir(parents=True, exist_ok=True)
    paths, missing = {}, []
    for slug in MAPS:
        if slug not in wanted:
            continue
        scene_path = cfg.out / (slug + "_scene.json")
        if not scene_path.exists():
            missing.append(slug)
            continue
        scene = json.loads(scene_path.read_text(encoding="utf-8"))
        for m in scene["meshes"]:
            if is_excluded(m):
                continue
            if filter_substr and filter_substr.lower() not in m["path"].lower():
                continue
            if (m.get("lod0Triangles") or 0) > MAX_EXPORT_TRIANGLES:
                continue
            paths.setdefault(m["path"], slug)
    if missing:
        print("no scene JSON, skipped: %s" % ",".join(missing))
    todo = sorted(paths)
    print("exporting all LODs for %d meshes matching %r" % (len(todo), filter_substr))
    ok, failed = run_meshexport_batch(cfg, todo, mesh_dir, scratch, lod="all")
    print("  %d exported, %d failed" % (len(ok), len(failed)))
    for f in failed[:20]:
        print("  FAIL %s: %s" % (f.get("path"), f.get("error")))
    return 0 if not failed else 1


def main(argv):
    ap = argparse.ArgumentParser(
        description="Extract every static mesh placed on a Tyr map, and optionally its geometry.")
    common.add_common_args(ap)
    ap.add_argument("--top", type=int, default=0,
                    help="meshes exported per map, ranked by instances x footprint; 0 = every "
                         "non-excluded mesh (the default -- see module docstring)")
    ap.add_argument("--export", action="store_true", help="also run --meshexport for the top-N meshes")
    ap.add_argument("--only", help="comma-separated map slugs, default all. Known slugs: "
                                   + ", ".join(MAPS))
    ap.add_argument("--lods", action="store_true",
                    help="export EVERY LOD of the meshes matching --lodfilter, reading the "
                         "scene JSON already on disk (no re-scan). Writes <Mesh>_LOD<N>.glb "
                         "alongside the existing <Mesh>.glb; see export_lods()")
    ap.add_argument("--lodfilter", default=LOD_FILTER_DEFAULT,
                    help="path substring selecting which meshes --lods covers "
                         "(default %(default)r)")
    a = ap.parse_args(argv)

    cfg = common.resolve(a)
    common.print_config(cfg)
    mesh_dir = cfg.out / "meshes"
    mesh_dir.mkdir(parents=True, exist_ok=True)
    scratch = common.scratch_dir("tyr_mapgeometry")

    if a.lods:
        return export_lods(cfg, a.lodfilter, a.only, scratch)

    if a.only:
        wanted = [s.strip() for s in a.only.split(",") if s.strip()]
        unknown = [s for s in wanted if s not in MAPS]
        if unknown:
            raise SystemExit("unknown slug(s): %s" % ",".join(unknown))
        wanted = set(wanted)
    else:
        wanted = set(MAPS)

    # Start from the report already on disk so `--only` UPDATES it instead of
    # truncating it to the maps of this run. The scene JSON and the .glb files
    # of the maps not named are still sitting in the output directory
    # untouched; a report that no longer mentions them describes a directory
    # that does not exist.
    report = {}
    report_path = cfg.out / "report.json"
    if report_path.exists():
        try:
            report = json.loads(report_path.read_text(encoding="utf-8"))
        except ValueError:
            report = {}
    exported_paths = set()  # dedup exports across maps by package path

    for slug, (plugin, name) in MAPS.items():
        if slug not in wanted:
            continue
        pkg = pkg_path(slug, plugin, name)
        gp_pkg = gameplay_pkg_path(slug, plugin, name)
        row = {"slug": slug, "pkg": pkg, "gameplay_pkg": gp_pkg}

        parts = []
        try:
            parts.append(run_meshscene(cfg, pkg, scratch / (slug + "_scene.json")))
            if gp_pkg:
                try:
                    parts.append(run_meshscene(cfg, gp_pkg, scratch / (slug + "_gameplay_scene.json")))
                except subprocess.CalledProcessError as e:
                    row["gameplay_error"] = (e.stderr or b"")[:300].decode("utf-8", "replace")
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as e:
            row["error"] = str(e)[:300]
            report[slug] = row
            print("%-13s FAILED: %s" % (slug, row["error"]))
            continue

        meshes, instances, actors = merge_scenes(parts)
        scene = {"slug": slug, "meshes": meshes, "instances": instances, "actors": actors}
        scene_path = cfg.out / (slug + "_scene.json")
        scene_path.write_text(json.dumps(scene, separators=(",", ":")), encoding="utf-8")

        counts = {}
        for inst in instances:
            counts[inst["mesh"]] = counts.get(inst["mesh"], 0) + 1
        candidates = [i for i in range(len(meshes)) if not is_excluded(meshes[i])]
        ranked = sorted(candidates, key=lambda i: -(counts.get(i, 0) * footprint(meshes[i])))
        top = ranked[:a.top] if a.top > 0 else ranked
        excluded = [i for i in range(len(meshes)) if is_excluded(meshes[i])]

        row["distinct_meshes"] = len(meshes)
        row["total_instances"] = len(instances)
        row["instances_by_source"] = _count_by(instances, "source")
        row["scan_counters"] = {k: sum(p.get(k) or 0 for p in parts) for k in
                                ("bpActors", "bpComponents", "ismComponents", "actorMeshes",
                                 "splineSkipped", "unresolvedComponents", "classPkgFailures",
                                 "gcComponents", "gcResolved", "gcUnresolved", "gcInstances",
                                 "gcRootComponents", "smcRootComponents")}
        # Which intact mesh each Chaos geometry collection resolved to, and by
        # which route. `unresolvedComponents` deliberately does NOT drop when
        # these resolve -- it counts static mesh components with no mesh, and a
        # destructible's empty placeholder StaticMeshComponent (inherited from
        # BP_Destructible_Prop_Base) is still exactly that. The geometry now
        # comes from the geometry collection beside it, which is what
        # gcResolved counts. Merging the two would make one number mean two
        # things.
        row["geometry_collections"] = {k: v for p in parts
                                       for k, v in (p.get("gcResolutions") or {}).items()}
        row["scene_bytes"] = scene_path.stat().st_size
        row["top_meshes"] = [
            {"path": meshes[i]["path"], "instances": counts.get(i, 0),
             "extent": [meshes[i].get("extentX"), meshes[i].get("extentY"), meshes[i].get("extentZ")],
             "lod0Triangles": meshes[i].get("lod0Triangles")}
            for i in top
        ]
        row["excluded_from_ranking"] = [
            {"path": meshes[i]["path"], "instances": counts.get(i, 0),
             "lod0Triangles": meshes[i].get("lod0Triangles")}
            for i in excluded
        ]

        if a.export:
            exported, skipped_heavy, todo = [], [], []
            for i in top:
                path = meshes[i]["path"]
                if path in exported_paths:
                    exported.append(path)
                    continue
                tris = meshes[i].get("lod0Triangles") or 0
                if tris > MAX_EXPORT_TRIANGLES:
                    skipped_heavy.append({"path": path, "lod0Triangles": tris})
                    continue
                todo.append(path)
            ok_paths, failed = run_meshexport_batch(cfg, todo, mesh_dir, scratch)
            exported.extend(ok_paths)
            exported_paths.update(ok_paths)
            row["exported"] = exported
            row["export_failed"] = failed
            row["export_skipped_heavy"] = skipped_heavy

        report[slug] = row
        print("%-13s meshes=%-4d instances=%-7d scene=%.1fMB top-exported=%d"
              % (slug, row["distinct_meshes"], row["total_instances"],
                 row["scene_bytes"] / 1e6, len(row.get("exported", []))))

    report = {slug: report[slug] for slug in MAPS if slug in report}
    report_path.write_text(json.dumps(report, indent=1, ensure_ascii=False), encoding="utf-8")
    print("\nwrote %s/*_scene.json, %s/report.json" % (cfg.out, cfg.out)
          + (", %s/meshes/*.glb" % cfg.out if a.export else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
