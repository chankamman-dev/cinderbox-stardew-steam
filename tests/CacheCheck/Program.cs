using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SkiaSharp;
using TMXTile;
using xTile;
using xTile.Format;

// PC check for LoadCache with real mod files pulled from the device:
//  - every .tmx: parse with TMXTile (as SMAPI does), store as TBIN, load back, MapCompare.Diff must be empty;
//    then time TMX parse vs TBIN load.
//  - every .png: SMAPI's decode (SKBitmap.Decode + PreMultiply + per-pixel Color) vs LoadCache.Core.Png.Decode;
//    bytes must be identical; print both timings.
// Usage: CacheCheck <samples dir>
// TMXTile.dll (2020) asks for xTile 2.0.4.0; the game ships xTile 1.0.0.0. SMAPI redirects this at run
// time; here the default load context is told the same.
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) => name.Name == "xTile" ? typeof(Map).Assembly : null;
RegisterTmx(); // separate method: TMXTile's types must not be touched before the handler is in place

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static void RegisterTmx() => FormatManager.Instance.RegisterMapFormat(new TMXFormat(16, 16, 4, 4));
string root = args[0];
int bad = 0;

Console.WriteLine("== 地图 TMX → TBIN 往返 ==");
double tmxMs = 0, tbinMs = 0; long tmxBytes = 0, tbinBytes = 0;
foreach (string f in Directory.EnumerateFiles(root, "*.tmx", SearchOption.AllDirectories).OrderBy(x => x))
{
    try
    {
        long t0 = Stopwatch.GetTimestamp();
        Map map = FormatManager.Instance.LoadMap(f);
        double parse = Ms(t0);
        using var ms = new MemoryStream();
        FormatManager.Instance.BinaryFormat.Store(map, ms);
        ms.Position = 0;
        long t1 = Stopwatch.GetTimestamp();
        Map back = FormatManager.Instance.BinaryFormat.Load(ms);
        double load = Ms(t1);
        var diff = LoadCache.MapCompare.Diff(map, back);
        tmxMs += parse; tbinMs += load; tmxBytes += new FileInfo(f).Length; tbinBytes += ms.Length;
        if (diff.Count > 0) { bad++; Console.WriteLine($"DIFF  {Path.GetFileName(f)}: {string.Join(" | ", diff.Take(3))}"); }
        else Console.WriteLine($"ok    {Path.GetFileName(f),-48} tmx {parse,7:F1} ms ({new FileInfo(f).Length / 1024,5} KB)  tbin {load,6:F1} ms ({ms.Length / 1024,5} KB)");
    }
    catch (Exception e) { bad++; Console.WriteLine($"ERROR {Path.GetFileName(f)}: {e.GetType().Name}: {e.Message}"); }
}
Console.WriteLine($"地图合计：TMX {tmxMs:F0} ms / {tmxBytes / 1024} KB，TBIN {tbinMs:F0} ms / {tbinBytes / 1024} KB");

Console.WriteLine();
Console.WriteLine("== PNG：SMAPI 解码 vs 快速解码 ==");
double slow = 0, fast = 0;
foreach (string f in Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories).OrderBy(x => x))
{
    try
    {
        long t0 = Stopwatch.GetTimestamp();
        (int w, int h, byte[] a) = SmapiDecode(f);
        double s = Ms(t0);
        int w2 = 0, h2 = 0; byte[]? b = null; GCHandle handle = default;
        long t1 = Stopwatch.GetTimestamp();
        try
        {
            LoadCache.Core.Png.Decode(f, (ww, hh) =>
            {
                w2 = ww; h2 = hh; b = new byte[ww * hh * 4];
                handle = GCHandle.Alloc(b, GCHandleType.Pinned);
                return handle.AddrOfPinnedObject();
            });
        }
        finally { if (handle.IsAllocated) handle.Free(); }
        double q = Ms(t1);
        slow += s; fast += q;
        bool same = w == w2 && h == h2 && a.AsSpan().SequenceEqual(b!);
        int maxDelta = 0, differing = 0;
        if (!same && w == w2 && h == h2)
            for (int i = 0; i < a.Length; i++) { int d = Math.Abs(a[i] - b![i]); if (d != 0) { differing++; if (d > maxDelta) maxDelta = d; } }
        if (!same) bad++;
        Console.WriteLine($"{(same ? "ok   " : "DIFF ")} {Path.GetFileName(f),-44} {w}x{h,-5} smapi {s,7:F1} ms  fast {q,7:F1} ms" + (same ? "" : $"  不同字节 {differing}，最大差 {maxDelta}"));
    }
    catch (Exception e) { bad++; Console.WriteLine($"ERROR {Path.GetFileName(f)}: {e.GetType().Name}: {e.Message}"); }
}
Console.WriteLine($"图片合计：SMAPI 解码 {slow:F0} ms，快速解码 {fast:F0} ms");
Console.WriteLine(bad == 0 ? "RESULT all ok" : $"RESULT {bad} problem(s)");
return bad == 0 ? 0 : 1;

static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

// Mirrors StardewModdingAPI.Framework.ContentManagers.ModContentManager.DecodeRawImageData, with Color replaced by raw bytes R,G,B,A.
static (int, int, byte[]) SmapiDecode(string path)
{
    SKPMColor[] px; int w, h;
    using (FileStream fs = File.OpenRead(path))
    {
        using SKBitmap bmp = SKBitmap.Decode(fs) ?? throw new InvalidDataException("not a PNG");
        px = SKPMColor.PreMultiply(bmp.Pixels);
        w = bmp.Width; h = bmp.Height;
    }
    var bytes = new byte[px.Length * 4];
    for (int i = 0; i < px.Length; i++)
    {
        SKPMColor c = px[i];
        if (c.Alpha == 0) continue; // Color.Transparent = 0,0,0,0
        bytes[i * 4] = c.Red; bytes[i * 4 + 1] = c.Green; bytes[i * 4 + 2] = c.Blue; bytes[i * 4 + 3] = c.Alpha;
    }
    return (w, h, bytes);
}
