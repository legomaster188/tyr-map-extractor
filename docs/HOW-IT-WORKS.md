# How it works

Notes on the parts of a Tyr map that are not obvious from the asset names,
and what each extractor mode does about them.

## The maps are Packed Level Actors, and that is the whole difficulty

A Tyr map package is mostly empty. Walk `Map_Divide.umap` looking for static
mesh components and you get 22 meshes: some pebbles, some grass, seven tarps
and three cliffs. The map actually has 169 meshes across 81 placements.

The missing 147 are in **Packed Level Actors** — UE5's `BP_LI_*` blueprints,
filed under `/Game/World/<Map>/PLA/`. A packed level actor bakes a group of
props into one blueprint with its own ISM/HISM components, and the level then
places instances of that blueprint. The catch is where the geometry lives:
the ISM component with its `StaticMesh` and its `PerInstanceSMData` is in the
**blueprint class package**, not in the map. The map's own copy of the
component carries neither, because neither differs from the archetype, and a
cooked package only serialises what differs.

So `--meshscene` is class-driven rather than package-driven:

1. Walk the map's exports for actors whose class is a `BP_LI_*` blueprint.
   Read each actor's own world transform from its `RootComponent`.
2. Load that blueprint's class package and collect its component templates —
   the ISM/HISM components with the real `StaticMesh` and the real
   `PerInstanceSMData` array.
3. For each per-instance transform in the template, compose
   `actorTransform ∘ componentRelative ∘ instanceRelative` and emit one world
   instance.
4. Do the same for loose `StaticMeshActor`s and for the level's own
   `InstancedFoliageActor` scatter, which are placed directly and need no
   class lookup.

Two rules that come out of getting this wrong:

- **A component that is its actor's `RootComponent` must not be composed with
  its own actor.** The actor transform was *read from* that component, so
  composing them applies it twice. It puts a barrier at double its position,
  and puts every actor with yaw 180 at exactly the world origin, because
  `R(180)·T` cancels `T`.
- **Still walk the archetype chain for a root component.** A cooked component
  only serialises what differs from its template, so reading the level's copy
  alone silently returns scale 1.0 for something scaled on the blueprint.

`--meshscene` reports its counters (`bpActors`, `bpComponents`,
`ismComponents`, `unresolvedComponents`, `classPkgFailures`, …) so a scan that
quietly found nothing looks different from one that found nothing to find.

## Destructibles carry no static mesh at all

A destructible prop — barriers, walls, debris piles, crashed ship parts — is a
Chaos `UGeometryCollectionComponent`. It has no `StaticMesh` property anywhere
in its chain, so anything that asks for `StaticMesh` gets null and skips it.
That was 37 placements missing near Wind Valley's centre and 198 on Fields,
all of it in-bounds gameplay cover.

Two things made it quiet rather than loud. CUE4Parse does not model
`UGeometryCollectionComponent` as a `UStaticMeshComponent` subclass, so it was
never in the template set at all. And every one of these blueprints inherits
from `BP_Destructible_Prop_Base`, which contributes one **empty**
`UStaticMeshComponent` — enough to keep the component count non-zero and
produce one "unresolved component" per destructible.

The collection names its own un-fractured mesh. `--meshscene` reads
`RestCollection` and then, in priority order:

1. **`RootProxyData.ProxyMeshes`** — UE's own "what this looks like before it
   breaks", with a `MeshTransforms` entry per proxy mesh. Fields' 12
   collections all resolve here.
2. **`AutoInstanceMeshes`, and only when it holds exactly one entry.** Several
   entries mean per-fragment instancing, and nothing in the asset says which
   is the intact silhouette, so picking one would be a guess wearing a
   lookup's clothes. Wind Valley's 5 collections all resolve here.
3. **`GC_X` → `SM_X` in the collection's own folder**, last, and so far never
   used. It is the one route that can be quietly wrong:
   `GC_RidgeWall_01_Pile_Debris_01` is filed under `Props/Interactive_Field_01`
   while its source mesh `SM_RidgeWall_01_Pile_Debris_01` lives under
   `Structures/RidgeWall_01`, so a folder-local guess misses — and on a map
   where some other `SM_` happens to sit in that folder, it hits the wrong
   thing instead of missing.

Anything that resolves by none of the three is counted in `gcUnresolved` and
draws nothing. A destructible rendered as the wrong mesh is worse than one
rendered as nothing.

`unresolvedComponents` deliberately does **not** drop when a collection
resolves. It counts static mesh components with no static mesh, and the empty
placeholder is still exactly that. `gcResolved` is the counter for the
geometry collections. One number, one meaning.

## `--landscape`: the height grid

UE packs each `LandscapeComponent`'s height samples into a shared heightmap
`Texture2D`: R and G are the high and low byte of a 16-bit height, B and A are
a packed normal. The component is located inside that texture by
`HeightmapScaleBias.{Z,W}` (a 0..1 UV bias → pixel origin) and is
`NumSubsections × (SubsectionSizeQuads + 1)` pixels a side.

A sample's local position, before the Landscape actor's own transform, is

    (SectionBaseX + quadX, SectionBaseY + quadY, ((R << 8 | G) - 32768) / 128)

and the Landscape actor's `RootComponent` location/rotation/scale then carries
that into world space, composed like any other scene component.

The output is those samples resampled onto a square grid:

```
{
  "package": "...",
  "components": [ { "component": "...", "sectionBaseX": .., "decoded": true }, ... ],
  "componentsDecoded": 64, "componentsFailed": 0, "samplePoints": 1058841,
  "grid": {
    "resolution": 1024,
    "worldMinX": .., "worldMaxX": .., "worldMinY": .., "worldMaxY": ..,
    "heights": [ [ z, z, null, ... ], ... ]   // row[y][x], world Z in cm
  }
}
```

`null` means no sample landed in that cell. `--gridres` sets the resolution;
2048 is the ceiling, because that is the native texel resolution of these
landscapes and asking for more invents detail. The cost is output size, not
run time.

**The quad frame is not the world frame, and every one of these landscapes
carries yaw −90.** Skip that and the terrain lands rotated. The height grid
above is already resampled onto world axes, so it does not need the yaw — but
anything in *quad* space does, and both the weightmaps and the terrain bake
below are in quad space:

    worldX = originX + quadX·scaleX·cos(yaw) + quadY·scaleY·(−sin(yaw))
    worldY = originY + quadX·scaleX·sin(yaw) + quadY·scaleY·cos(yaw)

Divide's landscape sits at (−500, −200), scale 175, yaw −90, 1008 quads:
1008 × 175 = 176,400 cm, which reproduces the extracted world box
(X −500..175900, Y −176600..−200) exactly, and only with the yaw applied. At
yaw 0 the Y range comes out mirrored. The map's own
`ARuntimeVirtualTextureVolume` sits at the same corner with the same −90 yaw.

## Terrain colour: the material is stylised, so bake it instead

The obvious plan — pull each painted layer's albedo texture and blend them by
weight — does not work here, because those textures do not exist.

Every map's Landscape points at `MI_Landscape_<Map>_01` over
`M_Landscape_<Map>_01`. Read the flattened parameters (`--dump` the material
instance) and each painted layer — Cliff, Grass, Sand, Mud, Path, FloorCity —
contributes only a `_GRH` map (packed gloss/roughness/height, greyscale) and a
`_NM` normal. There is no colour texture. The colour comes from
`<Layer> Color Variation 1..4` **vector** parameters plus a
`<Layer>/Color Curve` scalar, computed in the shader rather than sampled.
Divide's four Cliff variations, for instance, are `#6C553C`, `#516249`,
`#BCB5A0`, `#956F5B`.

What there is instead is better: **the RVT bake**. The material instance names
`RVT Baked BaseColor Map`, `RVT Baked Normal Map` and `RVT Baked Roughness
Map`, pointing at `/Game/World/<Map>/Landscape/Textures/T_Landscape_C` /
`_NM` / `_R`. That is the game's own material output for the whole landscape,
baked to one 2048² texture, and the game itself switches to it past
`RVT Blend Distance Start`. It is a real terrain albedo with none of a
minimap's furniture — no contour lines, no painted drop shadows, no icons.

Five of the six maps have one. Where the material instance does not override
the parameter, the reference lives in the parent material's expression graph,
which cooking strips — but the asset is at the same path under the same name,
so fetch it there by convention. Expanse has no `Landscape/Textures` folder at
all and has no bake; it degrades to whatever shading a renderer does from the
heights alone. **Do not let a missing texture fall back to another map's.**
Every map has a file called `T_Landscape_C`; guessing between them is worse
than failing.

`_NM` fails on three maps with `Detex decompression failed: not initialized`.
Those normal maps are BC5, which CUE4Parse routes through a native `Detex.dll`
that is not in the nuget package. Base colour and roughness are unaffected.

Getting these out:

    --texture <package>  --texout <file.png>     one texture at native size
    --texturelist <file> --texout <dir>          many, one package path per line
    --landscapemat <map package> [--texout <dir>]

`--landscapemat` reports, for one map: the material assigned to each Landscape
actor (the thing to then `--dump` for its parameters), the landscape's
location/rotation/scale (that is where the yaw comes from), and every
`LandscapeComponent`'s **weightmap allocation** — which painted layer lives in
which channel of which weightmap texture, plus the component's `SectionBase`
so the tiles can be stitched into one map-wide image. With `--texout` it also
decodes every distinct weightmap texture to PNG.

The weightmaps are the part that exists nowhere else. Per-layer painting is
stored as one 128² tile per component, and a tile on its own is unusable:
nothing in it says where on the map it sits or which channel is which layer.
That is the allocation table's job.

**Channel order is a trap.** Weightmaps decode as `PF_B8G8R8A8` and CUE4Parse
does not swizzle, so the PNG is BGRA laid out in RGBA slots: the game's
channel 0 is the PNG's byte 2. Getting it wrong mislabels every layer
silently instead of failing.

## Mesh export, and LODs

`--meshexport <package>` writes one mesh as binary glTF 2.0 via
CUE4Parse-Conversion's `MeshExporter`. `--meshexportlist <file>` does the same
for a list of package paths in one process, which matters because mounting the
paks and the mapping file costs about two seconds and a map has hundreds of
distinct meshes.

Materials and textures are off (`ExportMaterials = false`). A scene of
hundreds of instanced rocks does not need a PBR material per instance, and
skipping material resolution also sidesteps whatever a cooked build dislikes
about a mapping file dumped from an older build.

`--meshlod first` (the default) writes `<Mesh>.glb` with LOD0 only.
`--meshlod all` writes `<Mesh>_LOD<N>.glb`, one file per level, **beside** the
plain file rather than replacing it — so both naming schemes can coexist and a
renderer can keep structural geometry on LOD0 while reading a cheaper level
for foliage. Every `.glb` records its own vertex and triangle counts, and the
scene JSON records `lodTriangles` for every level, so the choice can be made
without opening the files.

The reason to care: at LOD0 one gameplay tree is 18,507 triangles. The same
tree is 1,157 triangles at LOD4 and occupies the same bounding box to within
2 cm. An LOD changes how finely a bush is tessellated and nothing about where
it stands.

`extract_map_geometry.py --lods` drives this, reading the scene JSON already
on disk rather than re-scanning the maps.

## Skeletal meshes do not come out

Vehicle skins ship as `SK_*` skeletal meshes. Every one of them loads and none
of it populates — `LODModels` null, no materials, no reference skeleton — in
all four export formats, base meshes included. That is a CUE4Parse limitation
against UE 5.6 in this title, not a skin problem. Static meshes are
unaffected, and maps are entirely static meshes.

## Output shapes

### `<out>/<slug>_scene.json`

```
{
  "slug": "scorch",
  "meshes": [
    { "path": "/Game/World/Divide/Structure_Kit/Addons/SM_Tarp_01_Merge_06",
      "name": "SM_Tarp_01_Merge_06",
      "originX": .., "originY": .., "originZ": ..,    // local bbox centre, mesh space
      "extentX": .., "extentY": .., "extentZ": ..,    // local bbox HALF-extent
      "lod0Triangles": 58854,
      "lodTriangles": [58854, 41120, ...] },
    ...
  ],
  "instances": [
    { "mesh": 0,                  // index into meshes[]
      "source": "ism" | "actor",  // ISM/HISM instance vs. a placed StaticMeshActor
      "x": .., "y": .., "z": ..,               // world position, UE units (cm), Z up
      "qx": .., "qy": .., "qz": .., "qw": ..,  // world rotation, UE quaternion
      "sx": .., "sy": .., "sz": ..             // world scale (usually 1,1,1, not always)
    },
    ...
  ],
  "actors": [ ... ]               // the BP_LI_* actors the instances came from
}
```

`x/y/z` are in the **same world frame as `map_heights.json` and
`map_actors.json`** — no recalibration between them. That holds by
construction: all three walk the identical persistent-level and
gameplay-sublevel packages under the identical "a component sits at identity
in an already-composed level" assumption.

### `<out>/meshes/<mesh package path>.glb`

Binary glTF 2.0, no materials or textures. The path on disk mirrors the UE
package path with the leading `/Game` swapped for whatever content-plugin path
CUE4Parse resolved it to. The glTF exporter owns that naming, not the driver
script — so **read the actual filenames under `meshes/` rather than
recomputing the mapping**.

**Not every mesh in a `_scene.json` has a `.glb`.** Backdrop and excluded
meshes are never exported, `--top N` cuts it further, and anything over
150,000 triangles is skipped and reported. Skip an instance whose mesh has no
file on disk; do not treat the miss as an error.

### `<out>/map_actors.json`

Per map, a list of placed actors matching the class filter — `PlayerStart`,
`BP_CaptureZone_C`, `BP_AmmunitionZone_C`, `BP_HealZone_C`,
`BP_AbilityResourceZone_C`, `BP_NoGoZone_C`, `BP_BasicSpawnWall_C` — each with
its class, name, label, world location and rotation, a `worldFrame` flag, and
any scalar properties the actor carries.

`worldFrame` is false when the actor's `RootComponent` transform is *not*
directly world-space — an actor nested inside a blueprint or level instance,
whose transform still needs composing. On these maps it is true for everything
these classes cover.

### `<out>/map_worldbounds.json`

Per map, the `ALevelBounds` box, plus the World-Partition check. One field is
worth reading carefully: `boxExtentTagPresent`. If it is false, `BoxExtent`
was not serialised, which means it equals the class default (commonly 1,1,1
for a runtime-fitted level-bounds box) — and then `scale` **is** the world
half-extent rather than a multiplier on it.
