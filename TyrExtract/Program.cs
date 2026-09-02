using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using SkiaSharp;
using Newtonsoft.Json;

// Minimal geometry/property dumper for Tyr's cooked map packages.
//
// Every mode needs --paks (the game's Content\Paks folder) and --usmap (a
// mapping file dumped from the same game build); everything else selects
// what to read and where to write it.
//
// Usage: TyrExtract --paks <dir> --usmap <path> [--list <substr>] [--dump <pkg> ...] [--dumpout <file>]
//   --actors <pkg> [--classfilter <comma-substrs>] [--dumpout <file>]
//   --landscape <pkg> [--gridres <n>] [--dumpout <file>]
//   --worldbounds <pkg> [--dumpout <file>]
//   --meshscene <pkg> [--dumpout <file>]                 (static-mesh placement inventory + instances)
//   --meshexport <meshPkg> [--meshout <dir>] [--meshformat Gltf2|OBJ] [--meshlod first|all]

string? paksDir = null;
var appDir = AppContext.BaseDirectory;
string? usmapPath = null, dumpOut = null, listSub = null, instPkg = null, meshFilter = null, instGlob = null;
string? texPkg = null, texOut = null, rowsPkg = null;
string? actorsPkg = null, classFilter = null, landscapePkg = null, worldBoundsPkg = null;
string? meshScenePkg = null, meshExportPkg = null, meshOutDir = null, meshFormat = null;
string? exportTypesPkg = null, ismProbePkg = null, meshExportList = null;
string? meshLod = null;
string? landscapeMatPkg = null, texListFile = null;
int gridRes = 256;
var dumpPkgs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--usmap" && i + 1 < args.Length) usmapPath = args[++i];
    else if (args[i] == "--dump" && i + 1 < args.Length) dumpPkgs.Add(args[++i]);
    else if (args[i] == "--dumpout" && i + 1 < args.Length) dumpOut = args[++i];
    else if (args[i] == "--list" && i + 1 < args.Length) listSub = args[++i];
    else if (args[i] == "--instances" && i + 1 < args.Length) instPkg = args[++i];
    else if (args[i] == "--instancesGlob" && i + 1 < args.Length) instGlob = args[++i];
    else if (args[i] == "--meshfilter" && i + 1 < args.Length) meshFilter = args[++i];
    else if (args[i] == "--rows" && i + 1 < args.Length) rowsPkg = args[++i];
    else if (args[i] == "--texture" && i + 1 < args.Length) texPkg = args[++i];
    else if (args[i] == "--texout" && i + 1 < args.Length) texOut = args[++i];
    else if (args[i] == "--paks" && i + 1 < args.Length) paksDir = args[++i];
    else if (args[i] == "--actors" && i + 1 < args.Length) actorsPkg = args[++i];
    else if (args[i] == "--classfilter" && i + 1 < args.Length) classFilter = args[++i];
    else if (args[i] == "--landscape" && i + 1 < args.Length) landscapePkg = args[++i];
    else if (args[i] == "--gridres" && i + 1 < args.Length) gridRes = int.Parse(args[++i]);
    else if (args[i] == "--worldbounds" && i + 1 < args.Length) worldBoundsPkg = args[++i];
    else if (args[i] == "--exporttypes" && i + 1 < args.Length) exportTypesPkg = args[++i];
    else if (args[i] == "--ismprobe" && i + 1 < args.Length) ismProbePkg = args[++i];
    else if (args[i] == "--landscapemat" && i + 1 < args.Length) landscapeMatPkg = args[++i];
    else if (args[i] == "--texturelist" && i + 1 < args.Length) texListFile = args[++i];
    else if (args[i] == "--meshscene" && i + 1 < args.Length) meshScenePkg = args[++i];
    else if (args[i] == "--meshexport" && i + 1 < args.Length) meshExportPkg = args[++i];
    else if (args[i] == "--meshexportlist" && i + 1 < args.Length) meshExportList = args[++i];
    else if (args[i] == "--meshout" && i + 1 < args.Length) meshOutDir = args[++i];
    else if (args[i] == "--meshformat" && i + 1 < args.Length) meshFormat = args[++i];
    else if (args[i] == "--meshlod" && i + 1 < args.Length) meshLod = args[++i];
}

if (paksDir == null)
{
    Console.Error.WriteLine("no paks directory: pass --paks <dir>, e.g.");
    Console.Error.WriteLine(@"  --paks ""C:\Program Files (x86)\Steam\steamapps\common\Tyr Playtest\Tyr\Content\Paks""");
    return 2;
}
if (!Directory.Exists(paksDir))
{
    Console.Error.WriteLine($"--paks directory does not exist: {paksDir}");
    return 2;
}

// The paks are Oodle-compressed, so nothing reads without oo2core_9_win64.dll.
// It is proprietary and is NOT shipped with the game, so it cannot live in
// this repo either. CUE4Parse knows where to get a copy; fetch it once, beside
// the exe, and say so rather than failing with "not initialized" later on.
var oodlePath = Path.Combine(appDir, "oo2core_9_win64.dll");
if (!File.Exists(oodlePath))
{
    Console.Error.WriteLine($"[oodle] {Path.GetFileName(oodlePath)} not found, downloading it to {appDir}");
    try
    {
        // takes the path by ref and may rewrite it, so read it back afterwards
        // rather than assuming the file landed where it was asked to.
        var dest = oodlePath;
        OodleHelper.DownloadOodleDll(ref dest);
        oodlePath = dest ?? oodlePath;
        Console.Error.WriteLine($"[oodle] downloaded {new FileInfo(oodlePath).Length:N0} B to {oodlePath}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[oodle] download FAILED: {ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine($"[oodle] put oo2core_9_win64.dll in {appDir} by hand and re-run");
        return 2;
    }
}
OodleHelper.Initialize(oodlePath);
Console.Error.WriteLine("[x] oodle ok");

var version = new VersionContainer(EGame.GAME_UE5_6);
var provider = new DefaultFileProvider(paksDir, SearchOption.TopDirectoryOnly, version);
if (usmapPath != null)
    provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(new byte[32]));
provider.PostMount();
Console.Error.WriteLine($"[x] files: {provider.Files.Count}, mappings: {(provider.MappingsContainer != null)}");

// DataTable rows. The generic export path serialises a DataTable as an empty
// "Rows": {} because the rows live in RowMap rather than in tagged
// properties, so the map metadata this game keeps in DT_MapInfo (which is
// where the minimap capture bounds should be) was invisible.
if (rowsPkg != null)
{
    var rpkg = provider.LoadPackage(rowsPkg);
    foreach (var exp in rpkg.GetExports())
    {
        if (exp is not UDataTable dt) continue;
        Console.Error.WriteLine($"[rows] {dt.Name}: {dt.RowMap.Count} row(s)");
        foreach (var (key, row) in dt.RowMap)
            Console.WriteLine($"{key}	{JsonConvert.SerializeObject(row)}");
    }
    return 0;
}

// Export a UTexture2D at its native resolution -- minimaps, the RVT terrain
// bakes, layer detail maps, anything else that is a texture asset. The game
// asset is the only source that is not a resample of a resample.
if (texPkg != null)
{
    var tpkg = provider.LoadPackage(texPkg);
    var tex = tpkg.GetExports().OfType<UTexture2D>().FirstOrDefault();
    if (tex == null) { Console.Error.WriteLine("[tex] no UTexture2D in package"); return 1; }
    // Decode() yields a CTexture in this CUE4Parse version, not an SKBitmap.
    var ct = tex.Decode();
    if (ct == null) { Console.Error.WriteLine("[tex] decode returned null"); return 1; }
    var bmp = ct.ToSkBitmap();
    if (bmp == null) { Console.Error.WriteLine("[tex] ToSkBitmap returned null"); return 1; }
    Console.Error.WriteLine($"[tex] {tex.Name} {bmp.Width}x{bmp.Height} fmt={tex.Format}");
    var outPath = texOut ?? (tex.Name + ".png");
    using (var image = SKImage.FromBitmap(bmp))
    using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
    using (var fs = File.OpenWrite(outPath))
        data.SaveTo(fs);
    Console.Error.WriteLine($"[tex] wrote {outPath}");
    return 0;
}

if (listSub != null)
{
    foreach (var k in provider.Files.Keys.Where(k => k.Contains(listSub, StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine(k);
    return 0;
}

// Emit instanceWorldX,instanceWorldY,instanceWorldZ,meshName rows for every
// ISM/HISM instance in `pkgPath` whose mesh name contains meshFilter
// (null/empty = all). The per-instance TransformData translation is the
// world position when the component itself sits at identity in a
// persistent (already-composed) level, which is the case for the merged
// HISMs in Map_<Name> AND for per-LevelInstance packages composed into the
// persistent level at a fixed transform (Scorch's BP_LI_Scorch_* actors --
// confirmed by the original 2026-07-19 calibration analysis, which used
// exactly these packages this way).
static int ScanPackageInstances(CUE4Parse.FileProvider.DefaultFileProvider provider, string pkgPath, string? meshFilter, StreamWriter sw)
{
    var comps = 0;
    CUE4Parse.UE4.Assets.IPackage pkg;
    try { pkg = provider.LoadPackage(pkgPath); }
    catch (Exception ex) { Console.Error.WriteLine($"[inst] SKIP {pkgPath}: {ex.GetType().Name}: {ex.Message}"); return 0; }
    foreach (var exp in pkg.GetExports())
    {
        if (exp is not UInstancedStaticMeshComponent ism) continue;
        var mesh = ism.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("StaticMesh");
        var mn = mesh?.Name ?? "";
        if (!string.IsNullOrEmpty(meshFilter) && !mn.Contains(meshFilter, StringComparison.OrdinalIgnoreCase)) continue;
        var data = ism.PerInstanceSMData;
        if (data == null) continue;
        comps++;
        foreach (var inst in data)
        {
            var tr = inst.TransformData.Translation;
            sw.WriteLine($"{tr.X},{tr.Y},{tr.Z},{mn}");
        }
    }
    return comps;
}

if (instPkg != null)
{
    var outp = dumpOut ?? "instances.csv";
    using var sw = new StreamWriter(outp);
    sw.WriteLine("x,y,z,mesh");
    var comps = ScanPackageInstances(provider, instPkg, meshFilter, sw);
    Console.Error.WriteLine($"[inst] {comps} components (filter '{meshFilter}') -> {outp}");
    return 0;
}

if (instGlob != null)
{
    // Same as --instances but scans every package whose path CONTAINS
    // instGlob, in one provider session (one ~20K-file mount instead of
    // one per package) -- Scorch's calibration needs ~75 BP_LI_Scorch_*
    // LevelInstance packages aggregated, and re-mounting per package would
    // be minutes slower for no benefit.
    var outp = dumpOut ?? "instances.csv";
    using var sw = new StreamWriter(outp);
    sw.WriteLine("x,y,z,mesh");
    var pkgPaths = provider.Files.Keys
        .Where(k => k.Contains(instGlob, StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        .ToList();
    int totalComps = 0;
    foreach (var p in pkgPaths)
        totalComps += ScanPackageInstances(provider, p, meshFilter, sw);
    Console.Error.WriteLine($"[inst] {pkgPaths.Count} package(s), {totalComps} components (filter '{meshFilter}') -> {outp}");
    return 0;
}

// LoadPackage takes the on-disk provider path ("Tyr/Content/..."), and also
// resolves "/Game/..." virtual paths -- but NOT a game-feature plugin's
// "/TyrMapExpanse/..." form, which is what a Landscape actor's
// LandscapeMaterial reference actually looks like when the material lives in
// a map plugin. Fall back to finding the file by its leaf name, preferring a
// path that also contains the plugin segment.
static CUE4Parse.UE4.Assets.IPackage LoadPkg(CUE4Parse.FileProvider.DefaultFileProvider provider, string path)
{
    try { return provider.LoadPackage(path); }
    catch
    {
        if (!path.StartsWith("/")) throw;
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var root = segs.Length > 1 ? segs[0] : null;
        // ONLY for plugin mounts. "/Game/..." and "/Engine/..." already
        // resolve, so a failure there means the asset genuinely is not
        // present -- and falling back to a leaf-name search would happily
        // return a DIFFERENT map's file of the same name. Every map has a
        // T_Landscape_C; guessing between them is worse than failing.
        if (root == null || root.Equals("Game", StringComparison.OrdinalIgnoreCase)
                         || root.Equals("Engine", StringComparison.OrdinalIgnoreCase)) throw;
        var leaf = segs[^1];
        var hits = provider.Files.Keys
            .Where(k => (k.EndsWith("/" + leaf + ".uasset", StringComparison.OrdinalIgnoreCase)
                      || k.EndsWith("/" + leaf + ".umap", StringComparison.OrdinalIgnoreCase))
                     && k.Contains("/" + root + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (hits.Count != 1) throw;   // ambiguous is the same as not found
        return provider.LoadPackage(hits[0][..hits[0].LastIndexOf('.')]);
    }
}

// Rotate a local-space vector by a UE FRotator (degrees), matching
// FRotationMatrix's row-vector convention (v' = v * M). Needed because
// placed actors (and the Landscape actor itself) can carry a non-zero yaw,
// and a landscape's height samples are generated in the actor's local
// (SectionBase-quad) space before that rotation + DrawScale is applied.
static FVector RotateByRotator(FRotator rot, double lx, double ly, double lz)
{
    double p = rot.Pitch * Math.PI / 180.0, y = rot.Yaw * Math.PI / 180.0, r = rot.Roll * Math.PI / 180.0;
    double cp = Math.Cos(p), sp = Math.Sin(p), cy = Math.Cos(y), sy = Math.Sin(y), cr = Math.Cos(r), sr = Math.Sin(r);
    double m00 = cp * cy, m01 = cp * sy, m02 = sp;
    double m10 = sr * sp * cy - cr * sy, m11 = sr * sp * sy + cr * cy, m12 = -sr * cp;
    double m20 = -(cr * sp * cy + sr * sy), m21 = cr * sp * sy - sr * cy, m22 = cr * cp;
    return new FVector(
        (float)(lx * m00 + ly * m10 + lz * m20),
        (float)(lx * m01 + ly * m11 + lz * m21),
        (float)(lx * m02 + ly * m12 + lz * m22));
}

// A top-level placed actor is one whose Outer is the Level itself (as
// opposed to a component, which is Outer'd to its owning actor, or an actor
// nested inside a Blueprint/LevelInstance sub-package, which is Outer'd to
// something other than a Level). Only the first case's RelativeLocation is
// a world-space transform with no further composition needed. On a
// World-Partition map that would not hold: each streamed cell carries its own
// frame offset, which is why --worldbounds reports isWorldPartition.
static bool IsWorldFrameActor(UObject exp) => exp.Outer?.Class?.Name.ToString() == "Level";

// Dump every placed AActor in `pkgPath` whose ExportType matches one of the
// comma-separated substrings in `classFilter` (null/empty = every actor).
// One JSON object per actor: class, name, label, whether its RootComponent
// transform is directly world-space (see IsWorldFrameActor), the resolved
// world location/rotation, and any non-reference (int/float/bool/enum/name)
// property whose name looks like team/index/id metadata.
static List<Dictionary<string, object?>> ScanActors(CUE4Parse.FileProvider.DefaultFileProvider provider, string pkgPath, string? classFilter)
{
    var results = new List<Dictionary<string, object?>>();
    CUE4Parse.UE4.Assets.IPackage pkg;
    try { pkg = provider.LoadPackage(pkgPath); }
    catch (Exception ex) { Console.Error.WriteLine($"[actors] SKIP {pkgPath}: {ex.GetType().Name}: {ex.Message}"); return results; }
    var filters = string.IsNullOrEmpty(classFilter)
        ? null
        : classFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var exp in pkg.GetExports())
    {
        if (exp is not AActor actor) continue;
        if (filters != null && !filters.Any(f => actor.ExportType.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;

        UObject? rc = null;
        try { rc = actor.GetOrDefault<FPackageIndex>("RootComponent")?.Load(); } catch { }
        var loc = rc?.GetOrDefault<FVector>("RelativeLocation");
        var rot = rc?.GetOrDefault<FRotator>("RelativeRotation");

        var extra = new Dictionary<string, string>();
        foreach (var tag in actor.Properties)
        {
            if (tag.Tag == null) continue;
            var kind = tag.Tag.GetType().Name;
            if (kind is "ObjectProperty" or "ArrayProperty" or "StructProperty" or "MapProperty" or "SetProperty") continue;
            var n = tag.Name.ToString();
            if (n.Contains("team", StringComparison.OrdinalIgnoreCase) || n.Contains("index", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Id", StringComparison.Ordinal) || n.Contains("zone", StringComparison.OrdinalIgnoreCase)
                || n.Contains("tag", StringComparison.OrdinalIgnoreCase))
                extra[n] = tag.Tag.ToString() ?? "";
        }

        results.Add(new Dictionary<string, object?>
        {
            ["package"] = pkgPath,
            ["class"] = actor.ExportType,
            ["name"] = actor.Name,
            ["actorLabel"] = actor.ActorLabel,
            ["worldFrame"] = IsWorldFrameActor(actor),
            ["location"] = loc == null ? null : new { x = loc.Value.X, y = loc.Value.Y, z = loc.Value.Z },
            ["rotation"] = rot == null ? null : new { pitch = rot.Value.Pitch, yaw = rot.Value.Yaw, roll = rot.Value.Roll },
            ["properties"] = extra,
        });
    }
    return results;
}

if (actorsPkg != null)
{
    var results = ScanActors(provider, actorsPkg, classFilter);
    var outp = dumpOut ?? "actors.json";
    File.WriteAllText(outp, JsonConvert.SerializeObject(results, Formatting.Indented));
    var offFrame = results.Count(r => r["worldFrame"] is false);
    Console.Error.WriteLine($"[actors] {results.Count} actor(s) (filter '{classFilter}'), {offFrame} not in world frame -> {outp}");
    return 0;
}

// Terrain height extraction. UE packs each LandscapeComponent's height
// samples into a shared heightmap Texture2D (R,G = high/low byte of a
// 16-bit height, B,A = packed normal), located within that texture via
// HeightmapScaleBias.{Z,W} (a 0..1 UV bias -> pixel origin) and sized
// NumSubsections*(SubsectionSizeQuads+1) pixels/side. A sample's LOCAL
// position (before the Landscape actor's own transform) is
// (SectionBaseX+quadX, SectionBaseY+quadY, ((R<<8|G)-32768)/128); the
// Landscape actor's RootComponent RelativeLocation/RelativeRotation/
// RelativeScale3D (its "DrawScale") then carries that into world space --
// same composition as any other SceneComponent transform.
if (landscapePkg != null)
{
    CUE4Parse.UE4.Assets.IPackage pkg;
    try { pkg = provider.LoadPackage(landscapePkg); }
    catch (Exception ex) { Console.Error.WriteLine($"[landscape] FAIL {landscapePkg}: {ex.GetType().Name}: {ex.Message}"); return 1; }
    var exports = pkg.GetExports().ToList();
    var landscapeActors = exports.Where(e => e.ExportType is "Landscape" or "LandscapeStreamingProxy")
        .Cast<AActor>().ToDictionary(a => a.Name, a => a);
    var components = exports.Where(e => e.ExportType == "LandscapeComponent").ToList();
    Console.Error.WriteLine($"[landscape] {landscapeActors.Count} landscape actor(s), {components.Count} component(s)");

    var texCache = new Dictionary<string, SKBitmap?>();
    var points = new List<(double x, double y, double z)>();
    var compMeta = new List<object>();
    int decodedOk = 0, decodedFail = 0;

    foreach (var comp in components)
    {
        var ownerName = comp.Outer?.Name.ToString() ?? "";
        if (!landscapeActors.TryGetValue(ownerName, out var owner)) { Console.Error.WriteLine($"[landscape] component {comp.Name} has no owning actor '{ownerName}'"); continue; }
        var arc = owner.GetOrDefault<FPackageIndex>("RootComponent")?.Load();
        var aLoc = arc?.GetOrDefault<FVector>("RelativeLocation") ?? new FVector(0, 0, 0);
        var aRot = arc?.GetOrDefault<FRotator>("RelativeRotation") ?? new FRotator(0, 0, 0);
        var aScale = arc?.GetOrDefault<FVector>("RelativeScale3D") ?? new FVector(1, 1, 1);

        int sbx = comp.GetOrDefault<int>("SectionBaseX");
        int sby = comp.GetOrDefault<int>("SectionBaseY");
        int compQuads = comp.GetOrDefault<int>("ComponentSizeQuads");
        int subQuads = comp.GetOrDefault<int>("SubsectionSizeQuads");
        int numSubs = comp.GetOrDefault<int>("NumSubsections");
        var bias = comp.GetOrDefault<FVector4>("HeightmapScaleBias");
        var texIdx = comp.GetOrDefault<FPackageIndex>("HeightmapTexture");
        var meta = new Dictionary<string, object?>
        {
            ["component"] = comp.Name, ["owner"] = ownerName,
            ["sectionBaseX"] = sbx, ["sectionBaseY"] = sby, ["componentSizeQuads"] = compQuads,
            ["subsectionSizeQuads"] = subQuads, ["numSubsections"] = numSubs,
            ["heightmapTexture"] = texIdx?.Name, ["heightmapScaleBias"] = new { x = bias.X, y = bias.Y, z = bias.Z, w = bias.W },
            ["decoded"] = false,
        };
        compMeta.Add(meta);
        if (texIdx == null || subQuads <= 0 || numSubs <= 0) { decodedFail++; continue; }

        var texKey = texIdx.Name;
        if (!texCache.TryGetValue(texKey, out var bmp))
        {
            try
            {
                var tex = texIdx.Load() as UTexture2D;
                bmp = tex?.Decode()?.ToSkBitmap();
            }
            catch (Exception ex) { Console.Error.WriteLine($"[landscape] texture decode FAIL {texKey}: {ex.GetType().Name}: {ex.Message}"); bmp = null; }
            texCache[texKey] = bmp;
        }
        if (bmp == null) { decodedFail++; continue; }

        int block = numSubs * (subQuads + 1);
        int baseX = (int)Math.Round(bias.Z * bmp.Width);
        int baseY = (int)Math.Round(bias.W * bmp.Height);
        if (baseX < 0 || baseY < 0 || baseX + block > bmp.Width || baseY + block > bmp.Height)
        { Console.Error.WriteLine($"[landscape] {comp.Name}: pixel rect out of bounds ({baseX},{baseY},{block}) in {bmp.Width}x{bmp.Height}"); decodedFail++; continue; }

        for (int j = 0; j < block; j++)
        {
            int subY = j / (subQuads + 1), localJ = j % (subQuads + 1);
            int quadY = subY * subQuads + localJ;
            for (int i = 0; i < block; i++)
            {
                int subX = i / (subQuads + 1), localI = i % (subQuads + 1);
                int quadX = subX * subQuads + localI;
                var px = bmp.GetPixel(baseX + i, baseY + j);
                int raw = (px.Red << 8 | px.Green) - 32768;
                double lz = raw / 128.0;
                double lx = sbx + quadX, ly = sby + quadY;
                var scaled = new FVector((float)(lx * aScale.X), (float)(ly * aScale.Y), (float)(lz * aScale.Z));
                var w = RotateByRotator(aRot, scaled.X, scaled.Y, scaled.Z);
                points.Add((aLoc.X + w.X, aLoc.Y + w.Y, aLoc.Z + w.Z));
            }
        }
        meta["decoded"] = true;
        decodedOk++;
    }

    object gridOut;
    if (points.Count == 0)
    {
        Console.Error.WriteLine("[landscape] no height samples decoded; emitting per-component metadata only");
        gridOut = new { resolution = 0, note = "pixel decode failed for every component; see components[].decoded" };
    }
    else
    {
        double minX = points.Min(p => p.x), maxX = points.Max(p => p.x);
        double minY = points.Min(p => p.y), maxY = points.Max(p => p.y);
        const int maxRes = 2048; // ~8 MB/map as JSON at 1024; 2048 is the native texel ceiling on this game's landscapes
        if (gridRes > maxRes)
            Console.Error.WriteLine($"[landscape] WARNING: --gridres {gridRes} exceeds the {maxRes} ceiling, clamping to {maxRes}");
        int res = Math.Max(1, Math.Min(gridRes, maxRes));
        var sum = new double[res, res];
        var cnt = new int[res, res];
        double spanX = Math.Max(maxX - minX, 1e-6), spanY = Math.Max(maxY - minY, 1e-6);
        foreach (var (x, y, z) in points)
        {
            int gx = Math.Clamp((int)((x - minX) / spanX * res), 0, res - 1);
            int gy = Math.Clamp((int)((y - minY) / spanY * res), 0, res - 1);
            sum[gx, gy] += z; cnt[gx, gy]++;
        }
        var rows = new List<double?[]>();
        for (int gy = 0; gy < res; gy++)
        {
            var row = new double?[res];
            for (int gx = 0; gx < res; gx++)
                row[gx] = cnt[gx, gy] > 0 ? sum[gx, gy] / cnt[gx, gy] : null;
            rows.Add(row);
        }
        gridOut = new
        {
            resolution = res,
            worldMinX = minX, worldMaxX = maxX, worldMinY = minY, worldMaxY = maxY,
            note = "row[y][x] = average world Z of samples in that cell, null where no sample landed",
            heights = rows,
        };
    }

    var outp2 = dumpOut ?? "heights.json";
    File.WriteAllText(outp2, JsonConvert.SerializeObject(new
    {
        package = landscapePkg,
        components = compMeta,
        componentsDecoded = decodedOk,
        componentsFailed = decodedFail,
        samplePoints = points.Count,
        grid = gridOut,
    }, Formatting.None));
    Console.Error.WriteLine($"[landscape] {decodedOk} decoded, {decodedFail} failed, {points.Count} samples -> {outp2} ({new FileInfo(outp2).Length:N0} B)");
    return 0;
}

// ALevelBounds' BoxComponent, plus a scan for anything World-Partition
// related so a silently-streamed map (none seen so far -- every Tyr map is one
// PersistentLevel with no __ExternalActors__) doesn't get treated as fully
// baked without checking.
if (worldBoundsPkg != null)
{
    CUE4Parse.UE4.Assets.IPackage pkg;
    try { pkg = provider.LoadPackage(worldBoundsPkg); }
    catch (Exception ex) { Console.Error.WriteLine($"[worldbounds] FAIL {worldBoundsPkg}: {ex.GetType().Name}: {ex.Message}"); return 1; }
    var exports = pkg.GetExports().ToList();
    var boundsActor = exports.FirstOrDefault(e => e.ExportType == "LevelBounds") as AActor;
    object? bounds = null;
    if (boundsActor != null)
    {
        var bc = boundsActor.GetOrDefault<FPackageIndex>("BoxComponent")?.Load();
        var loc = bc?.GetOrDefault<FVector>("RelativeLocation");
        var rot = bc?.GetOrDefault<FRotator>("RelativeRotation");
        var scl = bc?.GetOrDefault<FVector>("RelativeScale3D");
        var ext = bc?.GetOrDefault<FVector>("BoxExtent");
        bool extTagPresent = bc is CUE4Parse.UE4.Assets.Exports.AbstractPropertyHolder h && h.Properties.Any(t => t.Name.ToString() == "BoxExtent");
        bounds = new
        {
            location = loc == null ? null : new { x = loc.Value.X, y = loc.Value.Y, z = loc.Value.Z },
            rotation = rot == null ? null : new { pitch = rot.Value.Pitch, yaw = rot.Value.Yaw, roll = rot.Value.Roll },
            scale = scl == null ? null : new { x = scl.Value.X, y = scl.Value.Y, z = scl.Value.Z },
            boxExtent = ext == null ? null : new { x = ext.Value.X, y = ext.Value.Y, z = ext.Value.Z },
            boxExtentTagPresent = extTagPresent,
            note = "if boxExtentTagPresent is false, BoxExtent was not serialized (equals the class CDO default, commonly 1,1,1 for a runtime-fitted ALevelBounds box) -- scale then IS the world half-extent, not a multiplier on it",
        };
    }
    var wpHits = provider.Files.Keys
        .Where(k => k.Contains(System.IO.Path.GetDirectoryName(worldBoundsPkg.Replace('/', System.IO.Path.DirectorySeparatorChar)) ?? "", StringComparison.OrdinalIgnoreCase)
                 && (k.Contains("ExternalActors", StringComparison.OrdinalIgnoreCase) || k.Contains("WorldPartition", StringComparison.OrdinalIgnoreCase)))
        .ToList();
    var wpExportHits = exports.Select(e => e.ExportType).Distinct()
        .Where(t => t.Contains("WorldPartition", StringComparison.OrdinalIgnoreCase) || t.Contains("DataLayer", StringComparison.OrdinalIgnoreCase)).ToList();
    var outp3 = dumpOut ?? "worldbounds.json";
    File.WriteAllText(outp3, JsonConvert.SerializeObject(new
    {
        package = worldBoundsPkg,
        levelBounds = bounds,
        levelBoundsFound = boundsActor != null,
        externalActorOrWpFiles = wpHits,
        worldPartitionExportTypes = wpExportHits,
        isWorldPartition = wpHits.Count > 0 || wpExportHits.Count > 0,
    }, Formatting.Indented));
    Console.Error.WriteLine($"[worldbounds] levelBounds={boundsActor != null} isWorldPartition={(wpHits.Count > 0 || wpExportHits.Count > 0)} -> {outp3}");
    return 0;
}

// LOD selection for the two mesh-export modes. Default is FirstLod, which is
// what every existing caller got and still gets -- passing nothing changes
// nothing. `--meshlod all` asks CUE4Parse-Conversion for every LOD the mesh
// carries, which the glTF writer emits as one glTF mesh per LOD in the same
// .glb. That is the point: a renderer downstream can pick a different LOD per
// CATEGORY (a cliff at LOD0, a tree at whatever LOD lands under its triangle
// target) and the alternative -- one export run per LOD -- would mean
// re-mounting the paks and re-deciding per map which meshes need which level.
// One file, every level, chosen at build time.
static CUE4Parse_Conversion.Meshes.ELodFormat LodOf(string? s) =>
    s?.ToLowerInvariant() switch
    {
        "all" => CUE4Parse_Conversion.Meshes.ELodFormat.AllLods,
        _ => CUE4Parse_Conversion.Meshes.ELodFormat.FirstLod,
    };

// Export one UStaticMesh package to glTF (or OBJ) via CUE4Parse-Conversion's
// MeshExporter. Materials/textures are left off by default: a map scene made
// of hundreds of instanced rocks does not need a PBR texture per instance,
// and skipping material resolution also sidesteps whatever a cooked build
// dislikes about a usmap dumped from an older game build.
// Batch form of --meshexport: one package path per line. Exists because a map
// has hundreds of distinct meshes and each process start pays ~2s to mount the
// paks and the usmap -- which dominated the run at one process per mesh. Same
// exporter, same options, one provider.
if (meshExportList != null)
{
    var fmt0 = meshFormat?.ToLowerInvariant() switch
    {
        "obj" => CUE4Parse_Conversion.Meshes.EMeshFormat.OBJ,
        "actorx" => CUE4Parse_Conversion.Meshes.EMeshFormat.ActorX,
        "ueformat" => CUE4Parse_Conversion.Meshes.EMeshFormat.UEFormat,
        _ => CUE4Parse_Conversion.Meshes.EMeshFormat.Gltf2,
    };
    var opts = new ExporterOptions
    {
        LodFormat = LodOf(meshLod),
        MeshFormat = fmt0,
        ExportMaterials = false,
    };
    var outDir0 = new DirectoryInfo(meshOutDir ?? ".");
    outDir0.Create();
    int okN = 0;
    var fails = new List<object>();
    foreach (var line in File.ReadAllLines(meshExportList))
    {
        var path = line.Trim();
        if (path.Length == 0) continue;
        try
        {
            var mp = LoadPkg(provider, path);
            var m = mp.GetExports().OfType<UStaticMesh>().FirstOrDefault();
            if (m == null) { fails.Add(new { path, error = "no UStaticMesh export" }); continue; }
            var ex = new MeshExporter(m, opts);
            if (!ex.TryWriteToDir(outDir0, out _, out var saved)) { fails.Add(new { path, error = "TryWriteToDir false" }); continue; }
            okN++;
        }
        catch (Exception ex) { fails.Add(new { path, error = ex.GetType().Name + ": " + ex.Message }); }
    }
    var outpM = dumpOut ?? "meshexportlist.json";
    File.WriteAllText(outpM, JsonConvert.SerializeObject(new { exported = okN, failed = fails }, Formatting.Indented));
    Console.Error.WriteLine($"[meshexportlist] {okN} exported, {fails.Count} failed -> {outpM}");
    return 0;
}

if (meshExportPkg != null)
{
    CUE4Parse.UE4.Assets.IPackage pkg;
    try { pkg = provider.LoadPackage(meshExportPkg); }
    catch (Exception ex) { Console.Error.WriteLine($"[meshexport] FAIL load {meshExportPkg}: {ex.GetType().Name}: {ex.Message}"); return 1; }
    var mesh = pkg.GetExports().OfType<UStaticMesh>().FirstOrDefault();
    // Vehicle skins ship as SKELETAL meshes (SK_*). Looking only for a static
    // mesh refused all thirteen of them with "no UStaticMesh export", which
    // read like the asset was missing rather than the wrong type being asked
    // for. MeshExporter takes either.
    USkeletalMesh? skel = mesh == null
        ? pkg.GetExports().OfType<USkeletalMesh>().FirstOrDefault() : null;
    if (mesh == null && skel == null)
    { Console.Error.WriteLine($"[meshexport] no static or skeletal mesh export in {meshExportPkg}"); return 1; }

    var fmt = meshFormat?.ToLowerInvariant() switch
    {
        "obj" => CUE4Parse_Conversion.Meshes.EMeshFormat.OBJ,
        "actorx" => CUE4Parse_Conversion.Meshes.EMeshFormat.ActorX,
        "ueformat" => CUE4Parse_Conversion.Meshes.EMeshFormat.UEFormat,
        _ => CUE4Parse_Conversion.Meshes.EMeshFormat.Gltf2,
    };
    var options = new ExporterOptions
    {
        LodFormat = LodOf(meshLod),
        MeshFormat = fmt,
        ExportMaterials = false,
    };
    try
    {
        var exporter = mesh != null
            ? new MeshExporter(mesh, options)
            : new MeshExporter(skel!, options);
        if (skel != null && (exporter.MeshLods?.Count ?? 0) == 0)
        {
            // Every skeletal mesh in this game comes back with LODModels null,
            // no materials and no reference skeleton -- the object loads and
            // none of it populates. It is not a skin problem and not a format
            // problem: the base vehicle meshes fail identically, in all four
            // export formats. Say so rather than returning a bare false.
            Console.Error.WriteLine(
                $"[meshexport] {skel.Name}: skeletal mesh loaded but empty " +
                $"(lods={skel.LODModels?.Length ?? -1}, materials={skel.Materials?.Length ?? -1}, " +
                $"refSkeleton={(skel.ReferenceSkeleton != null)}). This CUE4Parse build cannot " +
                $"read UE 5.6 skeletal meshes from this title; the usmap is also several builds " +
                $"stale. Static meshes are unaffected.");
            return 1;
        }
        var outDir = new DirectoryInfo(meshOutDir ?? ".");
        outDir.Create();
        if (!exporter.TryWriteToDir(outDir, out var label, out var savedPath))
        { Console.Error.WriteLine($"[meshexport] TryWriteToDir returned false for {meshExportPkg}"); return 1; }
        Console.Error.WriteLine($"[meshexport] {(mesh != null ? mesh.Name : skel!.Name)} -> {savedPath} (label={label})");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[meshexport] FAIL export {meshExportPkg}: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

// The landscape's REAL material, as opposed to draping minimap art over the
// terrain. Reports, for one map package:
//
//   * the UMaterialInterface assigned to each Landscape actor
//     (`LandscapeMaterial`), which is the thing to then --dump for its
//     parameters;
//   * every LandscapeComponent's weightmap allocation -- which painted layer
//     lives in which channel of which weightmap texture, plus the component's
//     SectionBase so the tiles can be stitched into one map-wide image;
//   * with --texout, every distinct weightmap texture decoded to PNG.
//
// Weightmaps are the part that cannot be reconstructed any other way: the
// per-layer albedo/normal/GRH textures are ordinary assets --texture already
// exports, but "which layer is painted where" only exists as these per-
// component RGBA tiles plus the allocation table below.
if (landscapeMatPkg != null)
{
    CUE4Parse.UE4.Assets.IPackage lpkg;
    try { lpkg = provider.LoadPackage(landscapeMatPkg); }
    catch (Exception ex) { Console.Error.WriteLine($"[landscapemat] FAIL {landscapeMatPkg}: {ex.GetType().Name}: {ex.Message}"); return 1; }
    var lexports = lpkg.GetExports().ToList();

    var landscapes = new List<object>();
    foreach (var e in lexports)
    {
        if (e.ExportType is not ("Landscape" or "LandscapeStreamingProxy")) continue;
        var rc = e.GetOrDefault<FPackageIndex>("RootComponent")?.Load();
        landscapes.Add(new
        {
            name = e.Name, type = e.ExportType,
            landscapeMaterial = e.GetOrDefault<FPackageIndex>("LandscapeMaterial")?.ResolvedObject?.Package?.Name.ToString(),
            landscapeHoleMaterial = e.GetOrDefault<FPackageIndex>("LandscapeHoleMaterial")?.ResolvedObject?.Package?.Name.ToString(),
            targetLayers = e.Properties.Where(t => t.Name.ToString().Contains("Layer", StringComparison.OrdinalIgnoreCase))
                                       .Select(t => t.Name.ToString()).Distinct().ToArray(),
            location = rc == null ? null : new { x = rc.GetOrDefault<FVector>("RelativeLocation").X, y = rc.GetOrDefault<FVector>("RelativeLocation").Y, z = rc.GetOrDefault<FVector>("RelativeLocation").Z },
            // The landscape's own yaw is what turns quad space into world
            // space, and it is NOT zero on these maps (-90 on Divide). Every
            // consumer of the weightmaps and of the RVT bake needs it: both
            // are in quad space, so without the yaw they land rotated.
            rotation = rc == null ? null : new { pitch = rc.GetOrDefault<FRotator>("RelativeRotation").Pitch, yaw = rc.GetOrDefault<FRotator>("RelativeRotation").Yaw, roll = rc.GetOrDefault<FRotator>("RelativeRotation").Roll },
            scale = rc == null ? null : new { x = rc.GetOrDefault<FVector>("RelativeScale3D", new FVector(1, 1, 1)).X, y = rc.GetOrDefault<FVector>("RelativeScale3D", new FVector(1, 1, 1)).Y, z = rc.GetOrDefault<FVector>("RelativeScale3D", new FVector(1, 1, 1)).Z },
        });
    }

    var comps = new List<object>();
    var wmTextures = new Dictionary<string, FPackageIndex>();
    foreach (var comp in lexports.Where(e => e.ExportType == "LandscapeComponent"))
    {
        var texIdxs = comp.GetOrDefault<FPackageIndex[]>("WeightmapTextures") ?? Array.Empty<FPackageIndex>();
        var names = new List<string?>();
        foreach (var ti in texIdxs)
        {
            // Weightmaps are exports of the MAP package itself (same as the
            // heightmap textures), so Package.Name is the map, not the
            // texture -- key on the object name.
            var n = ti?.Name;
            names.Add(n);
            if (n != null && ti != null && !wmTextures.ContainsKey(n)) wmTextures[n] = ti;
        }
        var allocs = new List<object>();
        var raw = comp.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("WeightmapLayerAllocations") ?? Array.Empty<CUE4Parse.UE4.Assets.Objects.FStructFallback>();
        foreach (var al in raw)
        {
            var li = al.GetOrDefault<FPackageIndex>("LayerInfo");
            allocs.Add(new
            {
                layerInfo = li?.ResolvedObject?.Package?.Name.ToString() ?? li?.Name,
                layerName = li?.Name,
                textureIndex = al.GetOrDefault<byte>("WeightmapTextureIndex"),
                channel = al.GetOrDefault<byte>("WeightmapTextureChannel"),
            });
        }
        var sb = comp.GetOrDefault<FVector4>("WeightmapScaleBias");
        comps.Add(new
        {
            component = comp.Name,
            owner = comp.Outer?.Name.ToString(),
            sectionBaseX = comp.GetOrDefault<int>("SectionBaseX"),
            sectionBaseY = comp.GetOrDefault<int>("SectionBaseY"),
            componentSizeQuads = comp.GetOrDefault<int>("ComponentSizeQuads"),
            subsectionSizeQuads = comp.GetOrDefault<int>("SubsectionSizeQuads"),
            numSubsections = comp.GetOrDefault<int>("NumSubsections"),
            weightmapScaleBias = new { x = sb.X, y = sb.Y, z = sb.Z, w = sb.W },
            weightmapTextures = names,
            layerAllocations = allocs,
        });
    }

    var written = new List<object>();
    if (texOut != null)
    {
        Directory.CreateDirectory(texOut);
        foreach (var (name, idx) in wmTextures)
        {
            try
            {
                var tex = idx.Load() as UTexture2D;
                var bmp = tex?.Decode()?.ToSkBitmap();
                if (bmp == null) { written.Add(new { texture = name, error = "decode returned null" }); continue; }
                var file = Path.Combine(texOut, name.Replace('/', '_').TrimStart('_') + ".png");
                using (var image = SKImage.FromBitmap(bmp))
                using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                using (var fs = File.Create(file))
                    data.SaveTo(fs);
                written.Add(new { texture = name, file = Path.GetFileName(file), width = bmp.Width, height = bmp.Height, format = tex?.Format.ToString() });
            }
            catch (Exception ex) { written.Add(new { texture = name, error = ex.GetType().Name + ": " + ex.Message }); }
        }
    }

    var outpL = dumpOut ?? "landscapemat.json";
    File.WriteAllText(outpL, JsonConvert.SerializeObject(new
    {
        package = landscapeMatPkg,
        landscapes,
        componentCount = comps.Count,
        distinctWeightmapTextures = wmTextures.Count,
        components = comps,
        weightmapTexturesWritten = written,
    }, Formatting.Indented));
    Console.Error.WriteLine($"[landscapemat] {landscapes.Count} landscape actor(s), {comps.Count} component(s), {wmTextures.Count} weightmap texture(s), {written.Count} written -> {outpL}");
    return 0;
}

// Batch texture export: one package path per line, PNG per texture, named
// after the package path. Same reason as --meshexportlist -- the per-process
// pak mount dominates otherwise.
if (texListFile != null)
{
    Directory.CreateDirectory(texOut ?? ".");
    var rows = new List<object>();
    foreach (var line in File.ReadAllLines(texListFile))
    {
        var path = line.Trim();
        if (path.Length == 0) continue;
        try
        {
            var tp = LoadPkg(provider, path);
            var tex = tp.GetExports().OfType<UTexture2D>().FirstOrDefault();
            if (tex == null) { rows.Add(new { path, error = "no UTexture2D export" }); continue; }
            var bmp = tex.Decode()?.ToSkBitmap();
            if (bmp == null) { rows.Add(new { path, error = "decode returned null" }); continue; }
            var file = Path.Combine(texOut ?? ".", path.Replace('/', '_').TrimStart('_') + ".png");
            using (var image = SKImage.FromBitmap(bmp))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.Create(file))
                data.SaveTo(fs);
            rows.Add(new { path, file = Path.GetFileName(file), width = bmp.Width, height = bmp.Height, format = tex.Format.ToString(), srgb = tex.SRGB });
        }
        catch (Exception ex) { rows.Add(new { path, error = ex.GetType().Name + ": " + ex.Message }); }
    }
    var outpT = dumpOut ?? "texturelist.json";
    File.WriteAllText(outpT, JsonConvert.SerializeObject(rows, Formatting.Indented));
    Console.Error.WriteLine($"[texturelist] {rows.Count} entr(ies) -> {outpT}");
    return 0;
}

// Diagnostic: per-ISM/HISM detail for one package -- does the level's copy
// of a Packed-Level-Actor component actually carry PerInstanceSMData, or is
// the data only on the Blueprint class template?
if (ismProbePkg != null)
{
    CUE4Parse.UE4.Assets.IPackage ipkg;
    try { ipkg = provider.LoadPackage(ismProbePkg); }
    catch (Exception ex) { Console.Error.WriteLine($"[ismprobe] FAIL {ismProbePkg}: {ex.GetType().Name}: {ex.Message}"); return 1; }
    var rows = new List<object>();
    foreach (var exp in ipkg.GetExports())
    {
        if (exp is not UStaticMeshComponent smc) continue;
        var ism = exp as UInstancedStaticMeshComponent;
        FPackageIndex? sm = null;
        try { sm = exp.GetOrDefault<FPackageIndex>("StaticMesh"); } catch { }
        rows.Add(new
        {
            name = exp.Name, type = exp.ExportType, clr = exp.GetType().Name,
            outer = exp.Outer?.Name.ToString(),
            outerClass = exp.Outer?.Class?.Name.ToString(),
            outerClassPkg = exp.Outer?.Class?.Outer?.Name.ToString(),
            staticMesh = sm?.Name, staticMeshResolvedOwner = sm?.ResolvedObject?.Package?.Name.ToString(),
            perInstanceCount = ism?.PerInstanceSMData?.Length,
            relLoc = exp.GetOrDefault<FVector>("RelativeLocation"),
            relRot = exp.GetOrDefault<FRotator>("RelativeRotation"),
            relScale = exp.GetOrDefault<FVector>("RelativeScale3D"),
            propNames = (exp as AbstractPropertyHolder)?.Properties.Select(t => t.Name.ToString()).ToArray(),
        });
    }
    var actorRows = new List<object>();
    foreach (var exp in ipkg.GetExports())
    {
        if (exp is not AActor act) continue;
        if (exp.Outer?.Class?.Name.ToString() != "Level") continue;
        UObject? rc = null;
        try { rc = act.GetOrDefault<FPackageIndex>("RootComponent")?.Load(); } catch { }
        actorRows.Add(new
        {
            name = act.Name, type = act.ExportType, clr = act.GetType().Name,
            classPkg = act.Class?.Outer?.Name.ToString(),
            loc = rc?.GetOrDefault<FVector>("RelativeLocation"),
            rot = rc?.GetOrDefault<FRotator>("RelativeRotation"),
            scale = rc?.GetOrDefault<FVector>("RelativeScale3D"),
            propNames = act.Properties.Select(t => t.Name.ToString()).ToArray(),
        });
    }
    var outpI = dumpOut ?? "ismprobe.json";
    File.WriteAllText(outpI, JsonConvert.SerializeObject(new { package = ismProbePkg, meshComponents = rows, levelActors = actorRows }, Formatting.Indented));
    Console.Error.WriteLine($"[ismprobe] {rows.Count} mesh component(s), {actorRows.Count} level actor(s) -> {outpI}");
    return 0;
}

// Diagnostic: tally every export in a package by ExportType, and for each
// export that carries a "StaticMesh" object property, resolve it and report
// the mesh. Written to answer "why did --meshscene only see 22 meshes in a
// map that visibly has cliffs and mesas" -- it says exactly which component
// classes hold the geometry, instead of guessing.
if (exportTypesPkg != null)
{
    CUE4Parse.UE4.Assets.IPackage epkg;
    try { epkg = provider.LoadPackage(exportTypesPkg); }
    catch (Exception ex) { Console.Error.WriteLine($"[exporttypes] FAIL {exportTypesPkg}: {ex.GetType().Name}: {ex.Message}"); return 1; }
    var byType = new Dictionary<string, int>();
    var clrByType = new Dictionary<string, string>();
    var withMesh = new Dictionary<string, int>();
    var meshNames = new Dictionary<string, int>();
    var outerClassByType = new Dictionary<string, string>();
    foreach (var exp in epkg.GetExports())
    {
        var t = exp.ExportType;
        byType[t] = byType.GetValueOrDefault(t) + 1;
        clrByType[t] = exp.GetType().Name;
        outerClassByType[t] = exp.Outer?.Class?.Name.ToString() ?? "(null)";
        FPackageIndex? smIdx = null;
        try { smIdx = exp.GetOrDefault<FPackageIndex>("StaticMesh"); } catch { }
        if (smIdx != null && !smIdx.IsNull)
        {
            withMesh[t] = withMesh.GetValueOrDefault(t) + 1;
            var nm = smIdx.Name ?? "?";
            meshNames[nm] = meshNames.GetValueOrDefault(nm) + 1;
        }
    }
    var outpE = dumpOut ?? "exporttypes.json";
    File.WriteAllText(outpE, JsonConvert.SerializeObject(new
    {
        package = exportTypesPkg,
        totalExports = byType.Values.Sum(),
        types = byType.OrderByDescending(kv => kv.Value).Select(kv => new
        {
            type = kv.Key, count = kv.Value, clr = clrByType[kv.Key],
            outerClass = outerClassByType[kv.Key],
            withStaticMesh = withMesh.GetValueOrDefault(kv.Key),
        }),
        distinctMeshRefs = meshNames.Count,
        meshRefs = meshNames.OrderByDescending(kv => kv.Value).Select(kv => new { mesh = kv.Key, refs = kv.Value }),
    }, Formatting.Indented));
    Console.Error.WriteLine($"[exporttypes] {byType.Values.Sum()} export(s), {byType.Count} type(s), {meshNames.Count} distinct StaticMesh ref(s) -> {outpE}");
    return 0;
}

// ---- transform algebra for composing placed-actor / component / instance
// transforms. Written out rather than reusing FTransform because CUE4Parse's
// FTransform composition order is easy to get backwards; these three
// functions are self-consistent by construction (QRotV is the standard
// quaternion sandwich, QMul the standard Hamilton product, so
// QRotV(QMul(a,b),v) == QRotV(a,QRotV(b,v))), which is all the correctness
// this needs.
static FQuat QMul(FQuat a, FQuat b) => new FQuat(
    a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
    a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
    a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
    a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

static FVector QRotV(FQuat q, FVector v)
{
    float tx = 2f * (q.Y * v.Z - q.Z * v.Y);
    float ty = 2f * (q.Z * v.X - q.X * v.Z);
    float tz = 2f * (q.X * v.Y - q.Y * v.X);
    return new FVector(
        v.X + q.W * tx + (q.Y * tz - q.Z * ty),
        v.Y + q.W * ty + (q.Z * tx - q.X * tz),
        v.Z + q.W * tz + (q.X * ty - q.Y * tx));
}

// parent o child, UE SceneComponent attachment semantics: the child's
// translation is scaled by the parent's scale, then rotated by the parent's
// rotation, then offset by the parent's translation.
static (FVector T, FQuat R, FVector S) Compose((FVector T, FQuat R, FVector S) p, (FVector T, FQuat R, FVector S) c)
{
    var scaled = new FVector(p.S.X * c.T.X, p.S.Y * c.T.Y, p.S.Z * c.T.Z);
    var rt = QRotV(p.R, scaled);
    return (new FVector(p.T.X + rt.X, p.T.Y + rt.Y, p.T.Z + rt.Z),
            QMul(p.R, c.R),
            new FVector(p.S.X * c.S.X, p.S.Y * c.S.Y, p.S.Z * c.S.Z));
}

// World transform of a component that lives directly in a level (as opposed
// to inside a Packed Level Actor's class package, which pass A composes by
// hand). Walks the AttachParent chain -- each link's transform is relative
// to the next -- and ends at the actor's root component, whose
// RelativeLocation for a level actor IS its world location.
//
// This exists because pass B used to read the per-instance translation as
// world space, on the reading that the component "sits at identity in an
// already-composed level". That is true of five of the six maps and false of
// Wind Valley, whose foliage lives in a World-Partition cell actor
// (`InstancedFoliageActor_25600_0_3_0`) placed at (12800, 89600, 12800). All
// 375,603 of its instances therefore landed 128 m west, 896 m south and 128 m
// BELOW the landscape -- under the terrain mesh, which reads as a map with no
// buildings on it.
static (FVector T, FQuat R, FVector S) LevelCompWorldXf(UObject comp)
{
    var chain = new List<UObject>();
    var seen = new HashSet<UObject>();
    UObject? cur = comp;
    while (cur != null && seen.Add(cur))
    {
        chain.Add(cur);
        UObject? parent = null;
        try { parent = cur.GetOrDefault<FPackageIndex>("AttachParent")?.Load(); } catch { }
        cur = parent;
    }
    // A cooked component only serialises AttachParent when it differs from
    // the archetype, so the chain is usually just the component itself.
    // Attach it under its owning actor's root by hand in that case.
    if (chain.Count == 1)
    {
        UObject? rc = null;
        // Outer is a ResolvedObject (the owning actor), not the UObject itself.
        try { rc = comp.Outer?.Object?.Value?.GetOrDefault<FPackageIndex>("RootComponent")?.Load(); } catch { }
        if (rc != null && !ReferenceEquals(rc, comp)) chain.Add(rc);
    }
    var xf = (T: new FVector(0, 0, 0), R: new FQuat(0, 0, 0, 1), S: new FVector(1, 1, 1));
    for (int i = chain.Count - 1; i >= 0; i--) xf = Compose(xf, XfOf(chain[i]));
    return xf;
}

static bool HasProp(UObject? o, string n) =>
    o is AbstractPropertyHolder h && h.Properties.Any(t => t.Name.ToString() == n);

// A cooked component export only serialises a transform component that
// DIFFERS from its archetype, so "RelativeScale3D absent" means 1,1,1 and
// not 0,0,0 -- reading it with GetOrDefault alone silently collapses every
// unscaled instance to a point. `sources` is archetype-first, most-derived
// last, so the nearest override wins.
static (FVector T, FQuat R, FVector S) XfOf(params UObject?[] sources)
{
    FVector loc = new FVector(0, 0, 0), scl = new FVector(1, 1, 1);
    FRotator rot = new FRotator(0, 0, 0);
    foreach (var src in sources)
    {
        if (src == null) continue;
        if (HasProp(src, "RelativeLocation")) loc = src.GetOrDefault<FVector>("RelativeLocation");
        if (HasProp(src, "RelativeRotation")) rot = src.GetOrDefault<FRotator>("RelativeRotation");
        if (HasProp(src, "RelativeScale3D")) scl = src.GetOrDefault<FVector>("RelativeScale3D");
    }
    return (loc, rot.Quaternion(), scl);
}

// Inventory + scene description for one map package.
//
// THE THING THIS HAS TO GET RIGHT, and got wrong until 2026-08-19: these
// maps are built out of UE5 **Packed Level Actors** (`APackedLevelActor`,
// the `BP_LI_*` blueprints under `/Game/World/<Map>/PLA/`). Every cliff,
// mesa, rock formation and building is an ISM/HISM component of one of
// those blueprints -- and a Packed Level Actor's geometry lives in the
// BLUEPRINT CLASS package, not in the map. The map package does contain a
// component export per template, but it carries only CachedBounds and
// AttachParent: no `StaticMesh`, no `PerInstanceSMData`, because neither
// differs from the archetype. Scanning the map package alone therefore
// sees the map's foliage (a real InstancedFoliageActor, with real data) and
// its 72 loose StaticMeshActors, and NOTHING ELSE -- pebbles and tarps on
// Divide, against 81 packed level actors holding the actual silhouette.
//
// So the walk is class-driven: for every top-level placed actor whose class
// resolves to a `/Game/...` package, load that class package, take its
// component templates, and compose
//     world = actorTransform o componentRelative o perInstanceTransform.
// A level-side component export of the same name (minus `_GEN_VARIABLE`) is
// consulted first for per-placement overrides, and marked consumed so the
// level-resident ISM pass below cannot emit it twice.
if (meshScenePkg != null)
{
    CUE4Parse.UE4.Assets.IPackage pkg;
    try { pkg = provider.LoadPackage(meshScenePkg); }
    catch (Exception ex) { Console.Error.WriteLine($"[meshscene] FAIL {meshScenePkg}: {ex.GetType().Name}: {ex.Message}"); return 1; }

    var meshIndexByPath = new Dictionary<string, int>();
    var meshList = new List<Dictionary<string, object?>>();
    var instances = new List<Dictionary<string, object?>>();
    int ismComponents = 0, actorMeshes = 0, bpActors = 0, bpComponents = 0;
    int splineSkipped = 0, unresolvedComponents = 0, classPkgFailures = 0;
    int gcComponents = 0, gcResolved = 0, gcUnresolved = 0, gcInstances = 0, gcRootComponents = 0;
    // Diagnostic only, deliberately NOT acted on here: static mesh components
    // that ARE their actor's RootComponent. Those would be double-composed by
    // the pass-A algebra for exactly the reason the geometry-collection branch
    // guards against (see the comment there). Counted rather than fixed
    // because acting on it would change the geometry of all six maps, and this
    // change is scoped to the destructibles; the number is here so the next
    // pass knows whether there is anything to fix.
    int smcRootComponents = 0;
    // one entry per distinct UGeometryCollection asset: which route resolved
    // it and to what. Small (tens of entries) and it is the only way to see
    // that a destructible was matched to the RIGHT mesh rather than to some
    // mesh -- see GcMeshes below for why that distinction is the whole risk.
    var gcResolutions = new Dictionary<string, object>();
    // Diagnostic for the 812-unresolved investigation (2026-08-31): a bare
    // count says a component had no StaticMesh but not WHICH, so it cannot
    // tell "new geometry the walker is dropping" from "empty placeholder
    // that never had a mesh". Keyed by owning class package + component
    // name + component ExportType, so a stale-mappings failure (one class,
    // hundreds of actors) is visually distinct from real new content.
    var unresolvedDetail = new Dictionary<string, int>();

    int MeshEntryFor(UStaticMesh mesh)
    {
        var path = mesh.Owner?.Name ?? mesh.Name;
        if (meshIndexByPath.TryGetValue(path, out var idx)) return idx;
        var bounds = mesh.RenderData?.Bounds;
        var entry = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["name"] = mesh.Name,
            ["originX"] = bounds?.Origin.X, ["originY"] = bounds?.Origin.Y, ["originZ"] = bounds?.Origin.Z,
            ["extentX"] = bounds?.BoxExtent.X, ["extentY"] = bounds?.BoxExtent.Y, ["extentZ"] = bounds?.BoxExtent.Z,
            ["lod0Triangles"] = mesh.RenderData?.LODs?.ElementAtOrDefault(0)?.Sections?.Sum(s => (int)s.NumTriangles),
            // every LOD's triangle count, so a renderer can pick a cheaper
            // one for geometry that only has to read as a silhouette at
            // map-overview zoom instead of paying LOD0 for all of it
            ["lodTriangles"] = mesh.RenderData?.LODs?.Select(l => l.Sections?.Sum(s => (int)s.NumTriangles) ?? 0).ToArray(),
        };
        idx = meshList.Count;
        meshList.Add(entry);
        meshIndexByPath[path] = idx;
        return idx;
    }

    // `a` indexes bpActorList -- the placed actor this instance came out of.
    // Without it an out-of-place instance is untraceable: you can see that
    // something landed 19 km off the map but not which packed level actor
    // put it there. Cheap (one small int per instance) and it is the first
    // thing you want when a composition looks wrong.
    var bpActorList = new List<object>();
    int curActor = -1;

    void Emit(int mi, string source, (FVector T, FQuat R, FVector S) x)
    {
        instances.Add(new Dictionary<string, object?>
        {
            ["mesh"] = mi, ["source"] = source, ["a"] = curActor,
            ["x"] = x.T.X, ["y"] = x.T.Y, ["z"] = x.T.Z,
            ["qx"] = x.R.X, ["qy"] = x.R.Y, ["qz"] = x.R.Z, ["qw"] = x.R.W,
            ["sx"] = x.S.X, ["sy"] = x.S.Y, ["sz"] = x.S.Z,
        });
    }

    // classPkg -> component name (sans _GEN_VARIABLE) -> archetype chain,
    // most-derived first. Cached: a map has hundreds of placed actors
    // sharing a handful of classes (808 bushes off one blueprint on Divide).
    //
    // Two dictionaries, because a destructible prop's geometry is NOT on a
    // UStaticMeshComponent at all (see GcMeshes below). The geometry-collection
    // side is matched on the exact ExportType string rather than on a CLR
    // type: `GeometryCollectionISMPoolComponent` and
    // `GeometryCollectionDebugDrawComponent` are unrelated classes with no
    // RestCollection, and folding them in here would only inflate the
    // unresolved count with components that were never geometry.
    var templateCache = new Dictionary<string,
        (Dictionary<string, List<UStaticMeshComponent>> Smc, Dictionary<string, List<UObject>> Gcc)>();

    (Dictionary<string, List<UStaticMeshComponent>> Smc, Dictionary<string, List<UObject>> Gcc)
        ClassTemplates(string classPkg)
    {
        if (templateCache.TryGetValue(classPkg, out var got)) return got;
        var result = new Dictionary<string, List<UStaticMeshComponent>>();
        var gcResult = new Dictionary<string, List<UObject>>();
        var seen = new HashSet<string>();
        string? cursor = classPkg;
        for (int depth = 0; depth < 8 && cursor != null && seen.Add(cursor); depth++)
        {
            if (!cursor.StartsWith("/Game", StringComparison.OrdinalIgnoreCase)) break;
            CUE4Parse.UE4.Assets.IPackage cpkg;
            try { cpkg = provider.LoadPackage(cursor); }
            catch { classPkgFailures++; break; }
            string? super = null;
            foreach (var e in cpkg.GetExports())
            {
                var n = e.Name.EndsWith("_GEN_VARIABLE", StringComparison.Ordinal)
                    ? e.Name.Substring(0, e.Name.Length - "_GEN_VARIABLE".Length) : e.Name;
                if (e is UStaticMeshComponent smc)
                {
                    if (!result.TryGetValue(n, out var chain)) result[n] = chain = new List<UStaticMeshComponent>();
                    chain.Add(smc);
                }
                else if (e.ExportType == "GeometryCollectionComponent")
                {
                    if (!gcResult.TryGetValue(n, out var chain)) gcResult[n] = chain = new List<UObject>();
                    chain.Add(e);
                }
                else if (e is UStruct us && super == null)
                {
                    try { super = us.SuperStruct?.ResolvedObject?.Package?.Name.ToString(); } catch { }
                }
            }
            cursor = super;
        }
        templateCache[classPkg] = (result, gcResult);
        return (result, gcResult);
    }

    static UStaticMesh? FirstMesh(IEnumerable<UObject?> chain)
    {
        foreach (var c in chain)
        {
            if (c == null) continue;
            try
            {
                var m = c.GetOrDefault<FPackageIndex>("StaticMesh")?.Load() as UStaticMesh;
                if (m != null) return m;
            }
            catch { }
        }
        return null;
    }

    // RootProxyData.MeshTransforms, one per proxy mesh. Read rather than
    // assumed: every one observed in these six maps IS identity, but "it was
    // identity in the sample" is exactly how geometry ends up 40 metres off
    // in the one asset nobody checked. The struct may come back as a native
    // FTransform or, if the type is not resolved against this usmap, as a
    // property bag -- both are tried before falling back to identity.
    static List<(FVector T, FQuat R, FVector S)> MeshTransformsOf(CUE4Parse.UE4.Assets.Objects.FStructFallback rpd)
    {
        var outp = new List<(FVector, FQuat, FVector)>();
        try
        {
            var ts = rpd.GetOrDefault<FTransform[]>("MeshTransforms");
            if (ts != null && ts.Length > 0)
            {
                foreach (var t in ts) outp.Add((t.Translation, t.Rotation, t.Scale3D));
                return outp;
            }
        }
        catch { }
        try
        {
            var ts = rpd.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("MeshTransforms");
            if (ts != null)
                foreach (var t in ts)
                    outp.Add((t.GetOrDefault("Translation", new FVector(0, 0, 0)),
                              t.GetOrDefault("Rotation", new FQuat(0, 0, 0, 1)),
                              t.GetOrDefault("Scale3D", new FVector(1, 1, 1))));
        }
        catch { }
        return outp;
    }

    // The intact mesh behind a destructible prop.
    //
    // A destructible carries NO StaticMesh anywhere: its geometry is a Chaos
    // UGeometryCollection on a UGeometryCollectionComponent, and the only
    // UStaticMeshComponent in the chain is the empty one contributed by the
    // shared base class /Game/Blueprints/Environments/BP_Destructible_Prop_Base.
    // FirstMesh above therefore returned null for every one of them, and the
    // walk dropped 37 barriers and walls on Wind Valley and 185 debris piles
    // and crashed-ship parts on Fields -- geometry near the map centre, not
    // backdrop.
    //
    // The collection names its own intact mesh, so this does not have to
    // guess. In priority order:
    //   1. RootProxyData.ProxyMeshes -- UE's own "what this looks like before
    //      it breaks", with a transform per proxy mesh.
    //   2. AutoInstanceMeshes, and ONLY when it holds exactly one entry:
    //      several entries mean per-fragment instancing, and nothing in the
    //      asset says which of them is the intact silhouette, so picking one
    //      would be a guess dressed up as a lookup.
    //   3. the sibling naming convention, GC_X -> SM_X in the collection's own
    //      folder. LAST, because it is the only route that can be quietly
    //      wrong: two of the three collections checked by hand keep their
    //      source mesh in a DIFFERENT folder from the collection
    //      (GC_RidgeWall_01_Pile_Debris_01 is filed under
    //      Props/Interactive_Field_01, SM_RidgeWall_01_Pile_Debris_01 under
    //      Structures/RidgeWall_01), so a folder-local guess would miss and,
    //      on a map where some other SM_ happened to sit there, hit the wrong
    //      thing.
    // If none of the three resolves it stays unresolved and draws nothing,
    // which is the honest outcome: a destructible rendered as the wrong mesh
    // is worse than one rendered as nothing.
    List<(UStaticMesh Mesh, (FVector T, FQuat R, FVector S) Xf)> GcMeshes(UObject rest)
    {
        var identity = (new FVector(0, 0, 0), new FQuat(0, 0, 0, 1), new FVector(1, 1, 1));
        var found = new List<(UStaticMesh, (FVector, FQuat, FVector))>();
        var restPath = rest.Owner?.Name ?? rest.Name;
        string route = "unresolved";
        int proxyCount = 0, aimCount = 0;

        CUE4Parse.UE4.Assets.Objects.FStructFallback? rpd = null;
        try { rpd = rest.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("RootProxyData"); } catch { }
        if (rpd != null)
        {
            FPackageIndex[] pm = Array.Empty<FPackageIndex>();
            try { pm = rpd.GetOrDefault<FPackageIndex[]>("ProxyMeshes") ?? Array.Empty<FPackageIndex>(); } catch { }
            proxyCount = pm.Length;
            var xfs = MeshTransformsOf(rpd);
            for (int i = 0; i < pm.Length; i++)
            {
                UStaticMesh? sm = null;
                try { sm = pm[i].Load() as UStaticMesh; } catch { }
                if (sm == null) continue;
                found.Add((sm, i < xfs.Count ? xfs[i] : identity));
            }
            if (found.Count > 0) route = "rootProxy";
        }

        if (found.Count == 0)
        {
            var aim = Array.Empty<CUE4Parse.UE4.Assets.Objects.FStructFallback>();
            try { aim = rest.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("AutoInstanceMeshes") ?? aim; } catch { }
            aimCount = aim.Length;
            if (aim.Length == 1)
            {
                UStaticMesh? sm = null;
                try { sm = aim[0].GetOrDefault<FPackageIndex>("Mesh")?.Load() as UStaticMesh; } catch { }
                if (sm != null) { found.Add((sm, identity)); route = "autoInstance"; }
            }
        }

        if (found.Count == 0)
        {
            var cut = restPath.LastIndexOf('/') + 1;
            var leaf = restPath.Substring(cut);
            if (leaf.StartsWith("GC_", StringComparison.Ordinal))
            {
                var guess = restPath.Substring(0, cut) + "SM_" + leaf.Substring(3);
                UStaticMesh? sm = null;
                try { sm = provider.LoadPackage(guess).GetExports().OfType<UStaticMesh>().FirstOrDefault(); } catch { }
                if (sm != null) { found.Add((sm, identity)); route = "sibling"; }
            }
        }

        if (!gcResolutions.ContainsKey(restPath))
            gcResolutions[restPath] = new
            {
                route, proxyCount, aimCount,
                meshes = found.Select(f => f.Item1.Owner?.Name ?? f.Item1.Name).ToArray(),
            };
        return found.Select(f => (f.Item1, f.Item2)).ToList();
    }

    var consumed = new HashSet<UObject>(ReferenceEqualityComparer.Instance);
    var exports = pkg.GetExports().ToList();

    // component exports grouped by the name of the actor export that owns them
    var compsByActor = new Dictionary<string, Dictionary<string, UObject>>();
    foreach (var e in exports)
    {
        var owner = e.Outer?.Name.ToString();
        if (owner == null) continue;
        if (!compsByActor.TryGetValue(owner, out var d)) compsByActor[owner] = d = new Dictionary<string, UObject>();
        d[e.Name] = e;
    }

    // ---- pass A: blueprint-class actors (Packed Level Actors and friends)
    foreach (var e in exports)
    {
        if (e is not AActor actor) continue;
        if (!IsWorldFrameActor(actor)) continue;
        string? classPkg = null;
        try { classPkg = actor.Class?.Outer?.Name.ToString(); } catch { }
        if (classPkg == null || !classPkg.StartsWith("/Game", StringComparison.OrdinalIgnoreCase)) continue;
        var tpls = ClassTemplates(classPkg);
        if (tpls.Smc.Count == 0 && tpls.Gcc.Count == 0) continue;

        UObject? arc = null;
        try { arc = actor.GetOrDefault<FPackageIndex>("RootComponent")?.Load(); } catch { }
        var actorXf = XfOf(arc);
        bpActors++;
        curActor = bpActorList.Count;
        bpActorList.Add(new
        {
            name = actor.Name, cls = actor.ExportType, classPkg,
            x = actorXf.T.X, y = actorXf.T.Y, z = actorXf.T.Z,
            qx = actorXf.R.X, qy = actorXf.R.Y, qz = actorXf.R.Z, qw = actorXf.R.W,
            sx = actorXf.S.X, sy = actorXf.S.Y, sz = actorXf.S.Z,
        });
        compsByActor.TryGetValue(actor.Name, out var levelComps);

        foreach (var kv in tpls.Smc)
        {
            var name = kv.Key;
            var chain = kv.Value;
            UObject? lvl = null;
            if (levelComps != null && levelComps.TryGetValue(name, out var lc)) { lvl = lc; consumed.Add(lc); }

            // A spline mesh is deformed along its spline at runtime; the
            // undeformed source mesh placed at the component transform is
            // not what the map looks like, so it is skipped rather than
            // drawn wrong (this is the PCG tank-track terrain drape).
            bool isSpline = (lvl != null && lvl.ExportType.Contains("SplineMesh", StringComparison.OrdinalIgnoreCase))
                || chain.Any(c => c.ExportType.Contains("SplineMesh", StringComparison.OrdinalIgnoreCase));
            if (isSpline) { splineSkipped++; continue; }

            var meshSources = new List<UObject?>();
            if (lvl != null) meshSources.Add(lvl);
            foreach (var c in chain) meshSources.Add(c);
            var mesh = FirstMesh(meshSources);
            if (mesh == null)
            {
                unresolvedComponents++;
                var lvlType = lvl?.ExportType ?? "(no level export)";
                var arcType = chain.Count > 0 ? chain[0].ExportType : "(no archetype)";
                var key = classPkg + " | " + name + " | lvl=" + lvlType + " | arc=" + arcType;
                unresolvedDetail[key] = unresolvedDetail.GetValueOrDefault(key) + 1;
                continue;
            }

            // archetype-first, so an override on the level export wins
            var relSources = new List<UObject?>();
            for (int i = chain.Count - 1; i >= 0; i--) relSources.Add(chain[i]);
            if (lvl != null) relSources.Add(lvl);
            if (lvl != null && arc != null && (ReferenceEquals(lvl, arc) || lvl.Name == arc.Name))
                smcRootComponents++;
            var compWorld = Compose(actorXf, XfOf(relSources.ToArray()));

            var data = (lvl as UInstancedStaticMeshComponent)?.PerInstanceSMData;
            if (data == null || data.Length == 0)
                foreach (var c in chain)
                    if (c is UInstancedStaticMeshComponent ic && ic.PerInstanceSMData != null && ic.PerInstanceSMData.Length > 0)
                    { data = ic.PerInstanceSMData; break; }

            var mi = MeshEntryFor(mesh);
            bpComponents++;
            if (data != null && data.Length > 0)
                foreach (var inst in data)
                {
                    var tr = inst.TransformData;
                    Emit(mi, "pla", Compose(compWorld, (tr.Translation, tr.Rotation, tr.Scale3D)));
                }
            else
                Emit(mi, "bpcomp", compWorld);
        }

        // Chaos geometry collections on the same actor.
        //
        // Composition is actor o component-relative o proxy-mesh, EXCEPT when
        // the collection component is the actor's own RootComponent, which on
        // these destructibles it always is. A root component's serialised
        // RelativeLocation IS the actor's world transform -- it is the very
        // object `actorXf` was read from -- so composing the two applies it
        // twice. That is not a subtle error: it put a barrier whose actor sits
        // at (-12387, 54013) at (-24774, 108026), and every actor with yaw 180
        // at exactly (0, 0), because R(180)*T cancels T. Caught by reading the
        // level's own actor transforms back with --ismprobe and comparing.
        //
        // The archetype chain is still consulted in that case, because
        // `actorXf` is XfOf(RootComponent) with NO archetype fallback, and a
        // cooked component only serialises what differs: the 5M wall's scale
        // of 1.1 lives on the blueprint template and is absent from the level
        // copy, so reading the level copy alone silently returns 1.0.
        //
        // Components that are NOT the root (none here, but the branch is not
        // hypothetical -- a destructible with a scene root and a collection
        // hung under it composes the ordinary way) keep the normal path.
        foreach (var kv in tpls.Gcc)
        {
            var name = kv.Key;
            var chain = kv.Value;
            UObject? lvl = null;
            if (levelComps != null && levelComps.TryGetValue(name, out var lc)) { lvl = lc; consumed.Add(lc); }
            gcComponents++;

            var restSources = new List<UObject?>();
            if (lvl != null) restSources.Add(lvl);
            foreach (var c in chain) restSources.Add(c);
            UObject? rest = null;
            foreach (var s in restSources)
            {
                if (s == null) continue;
                try { rest = s.GetOrDefault<FPackageIndex>("RestCollection")?.Load(); } catch { }
                if (rest != null) break;
            }
            if (rest == null) { gcUnresolved++; continue; }

            var proxies = GcMeshes(rest);
            if (proxies.Count == 0) { gcUnresolved++; continue; }

            var gcRel = new List<UObject?>();
            for (int i = chain.Count - 1; i >= 0; i--) gcRel.Add(chain[i]);
            if (lvl != null) gcRel.Add(lvl);
            var gcIsRoot = lvl != null && arc != null
                && (ReferenceEquals(lvl, arc) || lvl.Name == arc.Name);
            if (gcIsRoot) gcRootComponents++;
            var gcWorld = gcIsRoot ? XfOf(gcRel.ToArray()) : Compose(actorXf, XfOf(gcRel.ToArray()));

            gcResolved++;
            bpComponents++;
            foreach (var pr in proxies)
            {
                Emit(MeshEntryFor(pr.Mesh), "gc", Compose(gcWorld, pr.Xf));
                gcInstances++;
            }
        }
    }

    // ---- pass B: level-resident ISM/HISM (the InstancedFoliageActor and
    // any engine-class actor).
    //
    // PerInstanceSMData transforms are COMPONENT-LOCAL, not world. This pass
    // used to emit them as-is, on the reading that the component "sits at
    // identity in an already-composed level" -- true on five of the six maps
    // and false on Wind Valley (see LevelCompWorldXf). The composition below
    // is a no-op wherever that old reading held, so the five maps it was
    // right about are unchanged.
    curActor = -1; // these belong to no placed BP actor -- see Emit
    foreach (var exp in exports)
    {
        if (exp is not UInstancedStaticMeshComponent ism) continue;
        if (consumed.Contains(exp)) continue;
        UStaticMesh? mesh = null;
        try { mesh = ism.GetOrDefault<FPackageIndex>("StaticMesh")?.Load() as UStaticMesh; } catch { }
        var data = ism.PerInstanceSMData;
        if (mesh == null || data == null) continue;
        ismComponents++;
        var mi = MeshEntryFor(mesh);
        // NOT offset by TranslatedInstanceSpaceOrigin: these components do
        // carry one (UE5 stores a far-from-pivot HISM's instances relative to
        // it for float precision), but CUE4Parse's PerInstanceSMData reader
        // has already folded it back in. Adding it here a second time moved
        // each component by its own origin and made the map worse, not
        // better -- measured, not assumed.
        var compWorld = LevelCompWorldXf(ism);
        foreach (var inst in data)
        {
            var tr = inst.TransformData;
            Emit(mi, "ism", Compose(compWorld, (tr.Translation, tr.Rotation, tr.Scale3D)));
        }
    }

    // ---- pass C: loose StaticMeshActors
    foreach (var exp in exports)
    {
        if (exp is not AActor actor || !actor.ExportType.Contains("StaticMeshActor", StringComparison.OrdinalIgnoreCase)) continue;
        UObject? rc = null;
        try { rc = actor.GetOrDefault<FPackageIndex>("RootComponent")?.Load(); } catch { }
        if (rc == null || consumed.Contains(rc)) continue;
        UStaticMesh? mesh = null;
        try { mesh = rc.GetOrDefault<FPackageIndex>("StaticMesh")?.Load() as UStaticMesh; } catch { }
        if (mesh == null) continue;
        var xf = XfOf(rc);
        actorMeshes++;
        var mi = MeshEntryFor(mesh);
        instances.Add(new Dictionary<string, object?>
        {
            ["mesh"] = mi, ["source"] = "actor", ["worldFrame"] = IsWorldFrameActor(actor),
            ["x"] = xf.T.X, ["y"] = xf.T.Y, ["z"] = xf.T.Z,
            ["qx"] = xf.R.X, ["qy"] = xf.R.Y, ["qz"] = xf.R.Z, ["qw"] = xf.R.W,
            ["sx"] = xf.S.X, ["sy"] = xf.S.Y, ["sz"] = xf.S.Z,
        });
    }

    var outp = dumpOut ?? "meshscene.json";
    File.WriteAllText(outp, JsonConvert.SerializeObject(new
    {
        package = meshScenePkg,
        ismComponents, actorMeshes, bpActors, bpComponents,
        splineSkipped, unresolvedComponents, classPkgFailures,
        gcComponents, gcResolved, gcUnresolved, gcInstances, gcRootComponents,
        smcRootComponents, gcResolutions,
        unresolvedDetail = unresolvedDetail.OrderByDescending(kv => kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value),
        bpActorList,
        meshes = meshList,
        instances,
    }, Formatting.None));
    Console.Error.WriteLine($"[meshscene] {meshList.Count} mesh(es), {bpActors} bp actor(s)/{bpComponents} component(s), "
        + $"{ismComponents} level ISM, {actorMeshes} SMActor, {instances.Count} instance(s) "
        + $"(spline skipped {splineSkipped}, unresolved {unresolvedComponents}, class load fail {classPkgFailures}, "
        + $"geometry collections {gcResolved}/{gcComponents} -> {gcInstances} inst) -> {outp} ({new FileInfo(outp).Length:N0} B)");
    return 0;
}

if (dumpPkgs.Count > 0)
{
    var dict = new Dictionary<string, object>();
    foreach (var pkg in dumpPkgs)
    {
        try { dict[pkg] = LoadPkg(provider, pkg).GetExports().ToList(); Console.Error.WriteLine($"[dump] OK {pkg}"); }
        catch (Exception ex) { Console.Error.WriteLine($"[dump] FAIL {pkg}: {ex.GetType().Name}: {ex.Message}"); dict[pkg] = new { error = ex.GetType().Name + ": " + ex.Message }; }
    }
    object payload = dict.Count == 1 ? dict.Values.First() : dict;
    var outp = dumpOut ?? "dump.json";
    File.WriteAllText(outp, JsonConvert.SerializeObject(payload));
    Console.Error.WriteLine($"[dump] wrote {outp} ({new FileInfo(outp).Length:N0} B)");
    return 0;
}
Console.Error.WriteLine("no action (use --list or --dump)");
return 0;
