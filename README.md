# tyr-map-extractor

Pull the maps out of [Tyr](https://store.steampowered.com/app/2862420/):
terrain, props, textures and spawn points, from your own game install. Nothing
extracted is redistributed here; this is the tooling, you run it against the
files you already have.

Two halves. A C# extractor built on
[CUE4Parse](https://github.com/FabianFG/CUE4Parse) that reads the cooked
packages, and two Python scripts that drive it map by map and merge the
results.

```
python extract_map_geometry.py --export --only scorch,fields
```

## What you get

| file | what's in it |
|---|---|
| `<slug>_scene.json` | every static mesh on the map and every instance's world position, rotation and scale |
| `meshes/*.glb` | the geometry of those meshes, binary glTF 2.0 |
| `map_heights.json` | terrain height grid in world coordinates |
| `map_actors.json` | player starts, capture zones, ammo/heal/resource zones, no-go zones, spawn walls |
| `map_worldbounds.json` | the level bounds box |
| PNGs | the RVT terrain bake, layer detail maps, minimaps, weightmaps |

Positions are Unreal world units, centimetres with Z up, and everything above is
in the same frame, so the props stand on the terrain without any fitting.

Six maps: Divide, Fields, Ravine, Scorch, Wind Valley, Expanse.

## Setting it up

**1. Install the .NET 10 SDK.**
<https://dotnet.microsoft.com/download/dotnet/10.0>, the SDK, not just the
runtime. Check it took:

```
dotnet --version
```

**2. Build the extractor.** From this folder:

```
dotnet build -c Release TyrExtract
```

That writes `TyrExtract\bin\Release\net10.0\TyrExtract.exe`. The scripts look
there by default.

**3. Find your game files.** The extractor needs the `Paks` folder:

```
<Steam>\steamapps\common\Tyr Playtest\Tyr\Content\Paks
```

You do not usually have to type it. The scripts read Steam's registry key and
its `libraryfolders.vdf`, so a normal install on any drive is found on its own.
If yours isn't, pass `--paks "<that path>"`.

**4. The mapping file.** UE 5.6 uses unversioned property serialisation, which
means the packages carry field *values* with no field *names*. A `.usmap`
mapping file supplies the names. Without one, nothing readable comes out.

One ships in this repo:
`Tyr-5.6.0-30304+++Tyr+release-dd6777a8.usmap`, dumped from game build
CL-30304. It still parses the current build, since the property layouts for maps
and meshes have not moved, and the scripts pick it up automatically.

If a patch eventually breaks it, dump a fresh one with
[UE4SS](https://github.com/UE4SS-RE/RE-UE4SS):

1. Get the **experimental-latest** build of UE4SS (`v3.0.1` does not work on
   this game) and unzip it into `Tyr\Binaries\Win64`.
2. Launch Tyr and get as far as the main menu. You don't need to enter a
   match; the object tree is populated by then.
3. Open the UE4SS console window (the hotkey is in `UE4SS-settings.ini` under
   `[Debug]`).
4. Go to the **Dumpers** tab and hit **Generate .usmap file**.
5. It lands in the `ue4ss\` folder, named for the build,
   `Tyr-5.6.0-<CL>+++Tyr+release-<hash>.usmap`. Pass it with `--usmap`, or drop
   it in this folder and it becomes the default (newest wins).

**5. Oodle.** The paks are Oodle-compressed and `oo2core_9_win64.dll` is not in
the game install. It's proprietary, so it isn't in this repo either. The
extractor downloads it next to the exe on first run and says so. Nothing to do.

## Getting Scorch and Fields

Map packages are addressed by their path inside the paks:

```
Tyr/Plugins/GameFeatures/Maps/TyrMapScorch/Content/Maps/Map_Scorch
Tyr/Plugins/GameFeatures/Maps/TyrMapFields/Content/Maps/Map_Fields
Tyr/Plugins/GameFeatures/Maps/TyrMapFields/Content/Maps/Map_Fields_Gameplay
```

Fields and Wind Valley keep some of their actors in that second `_Gameplay`
sublevel. The scripts scan both and merge; if you call the extractor by hand,
scan both yourself.

**Look at one map without extracting anything.** `--meshscene` is the
inventory pass: every mesh, every instance, no geometry:

```
TyrExtract\bin\Release\net10.0\TyrExtract.exe ^
  --paks "<Steam>\steamapps\common\Tyr Playtest\Tyr\Content\Paks" ^
  --usmap "Tyr-5.6.0-30304+++Tyr+release-dd6777a8.usmap" ^
  --meshscene Tyr/Plugins/GameFeatures/Maps/TyrMapScorch/Content/Maps/Map_Scorch ^
  --dumpout scorch_scene.json
```

Scorch comes back as 156 meshes and 107,105 instances.

**The props, as glTF.** This is the one you want:

```
python extract_map_geometry.py --export --only scorch,fields
```

Scene JSON plus a `.glb` per mesh, into `out\`. Drop `--export` for the
inventory only, which is much faster. Drop `--only` for all six maps. Add
`--lods` afterwards to re-export foliage at every LOD, if 18,000-triangle
trees are too expensive for whatever you're drawing in.

**The terrain, the spawns and the zones:**

```
python extract_map_actors_and_terrain.py --gridres 1024
```

Writes `map_heights.json`, `map_actors.json` and `map_worldbounds.json` for
every map. `--gridres` is the height grid's resolution per side; 256 is a fast
look, 1024 is what you want if anything is going to stand on the ground, 2048
is the ceiling.

**Textures.** One at native resolution:

```
TyrExtract.exe --paks ... --usmap ... ^
  --texture /Game/World/Scorch/Landscape/Textures/T_Landscape_C ^
  --texout scorch_terrain.png
```

`--texturelist <file>` does a list of package paths in one go.
`--landscapemat <map package>` reports which material the terrain uses and
which painted layer sits in which weightmap channel, and with `--texout` dumps
the weightmap tiles too.

There is no per-layer terrain albedo to fetch, the material computes its
colours in the shader from vector parameters. `T_Landscape_C` is the game's own
bake of the finished result, 2048², and it is what you want on the ground.
[`docs/HOW-IT-WORKS.md`](docs/HOW-IT-WORKS.md) explains that, and the quad-space
transform you need to lay it down the right way round.

## What comes out

`<slug>_scene.json` is a mesh table and an instance table:

```
{
  "slug": "scorch",
  "meshes": [
    { "path": "/Game/World/Scorch/...", "name": "SM_...",
      "originX": .., "originY": .., "originZ": ..,   // local bbox centre
      "extentX": .., "extentY": .., "extentZ": ..,   // local bbox half-extent
      "lod0Triangles": 58854, "lodTriangles": [..] }
  ],
  "instances": [
    { "mesh": 0, "source": "ism",
      "x": .., "y": .., "z": ..,                     // world position, cm, Z up
      "qx": .., "qy": .., "qz": .., "qw": ..,        // world rotation
      "sx": .., "sy": .., "sz": .. }                 // world scale
  ]
}
```

`meshes[i].path` maps to a file under `out\meshes\`, but the glTF exporter
picks the on-disk name, so read the directory rather than recomputing it. Not
every mesh has one, horizon backdrop and collision proxies are never
exported. Skip an instance whose mesh has no file; it isn't an error.

`map_heights.json` is `grid.heights[y][x]`, world Z in centimetres, `null`
where no sample landed, with the world box in `worldMinX`/`worldMaxX`/etc.

`map_actors.json` is a list per map: class, name, label, world location and
rotation.

Shapes in full, and the awkward parts of each:
[`docs/HOW-IT-WORKS.md`](docs/HOW-IT-WORKS.md).

## Known limits

**Expanse is no longer in the paks.** It was in an earlier build. Today
`Map_Expanse` doesn't resolve to a file, so it comes back as zero actors and a
failed landscape while the other five extract normally. That's the game, not
the tool. It's left in the map table because it may come back.

**The game patches roughly monthly.** Nothing here reads a version number, so a
patch usually changes only the numbers you get out. If a patch does move the
property layouts, packages start deserialising as empty rather than erroring.
That's the signal to dump a fresh `.usmap`, per the steps above.

**Windows only as written.** The paths, the registry lookup for the Steam
install and the Oodle DLL all assume it. The C# is not Windows-specific beyond
that.

**Skeletal meshes don't come out.** Vehicles ship as `SK_*` and this CUE4Parse
build reads none of them from this title, the object loads and nothing
populates. Maps are entirely static meshes, so it doesn't affect anything here.

**`_NM` normal maps fail on three maps** with `Detex decompression failed`.
They're BC5, which CUE4Parse hands to a native `Detex.dll` that isn't in the
nuget package. Base colour and roughness are fine.

## Licence

MIT, see [LICENSE](LICENSE).

The map contents are Stoke Games' and are not redistributed here. This repo is
tooling only, no extracted geometry, no textures, no game assets.
