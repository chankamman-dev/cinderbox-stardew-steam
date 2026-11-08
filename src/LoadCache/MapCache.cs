using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using StardewModdingAPI;
using xTile;
using xTile.Format;

namespace LoadCache;

// Mod maps come as TMX (Tiled XML); parsing them is ~4x slower per byte than xTile's binary TBIN.
// The first time a TMX is parsed, the result is written to the cache as TBIN, read back and compared
// with the parsed map (MapCompare). Only when both are identical is the TBIN kept; from then on the
// TMX is skipped and the TBIN loaded instead, as long as the TMX's size and write time are unchanged.
// A map the binary format cannot carry gets a .bad marker and is left alone.
//
// Limits: a TMX that pulls in an external .tsx tileset is only re-cached when the .tmx itself changes.
sealed class MapCache
{
    const string Version = "1";
    readonly string dir;
    readonly IMonitor monitor;
    readonly HashSet<string> bad = new(StringComparer.Ordinal);
    readonly object gate = new();
    int logged;

    public long Hits, Stored, RejectedFormat, Failed;
    public double HitMs, HitKb, StoreMs;

    public MapCache(string dir, IMonitor monitor)
    {
        this.dir = dir;
        this.monitor = monitor;
        Directory.CreateDirectory(dir);
    }

    static string Key(string fullPath)
    {
        byte[] h = SHA1.HashData(Encoding.UTF8.GetBytes(fullPath));
        return Convert.ToHexString(h, 0, 10).ToLowerInvariant();
    }

    static string Stamp(FileInfo f) => $"{Version}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";

    /// <summary>Returns the cached map, or null (then WantsStore says whether the parsed map should be cached).</summary>
    public Map? TryLoad(string filePath, out bool wantsStore)
    {
        wantsStore = false;
        var file = new FileInfo(filePath);
        if (!file.Exists) return null;
        string key = Key(file.FullName);
        string tbin = Path.Combine(dir, key + ".tbin"), meta = Path.Combine(dir, key + ".meta"), badMark = Path.Combine(dir, key + ".bad");
        lock (gate)
        {
            if (bad.Contains(key)) return null;
            if (File.Exists(badMark)) { bad.Add(key); return null; }
        }
        if (File.Exists(meta) && File.Exists(tbin))
        {
            string stamp;
            try { stamp = File.ReadAllText(meta).Split('\n')[0].Trim(); } catch { stamp = ""; }
            if (stamp == Stamp(file))
            {
                long t = Stopwatch.GetTimestamp();
                try
                {
                    using FileStream fs = File.OpenRead(tbin);
                    Map map = FormatManager.Instance.BinaryFormat.Load(fs);
                    double ms = Ms(t);
                    lock (gate) { Hits++; HitMs += ms; HitKb += file.Length / 1024.0; }
                    return map;
                }
                catch (Exception e)
                {
                    Log($"缓存的地图读不出来，重建：{Rel(file.FullName)}：{e.Message}", LogLevel.Debug);
                    TryDelete(tbin); TryDelete(meta);
                }
            }
        }
        wantsStore = true;
        return null;
    }

    public void Store(string filePath, Map map)
    {
        var file = new FileInfo(filePath);
        string key = Key(file.FullName);
        string tbin = Path.Combine(dir, key + ".tbin"), meta = Path.Combine(dir, key + ".meta"), badMark = Path.Combine(dir, key + ".bad");
        long t = Stopwatch.GetTimestamp();
        try
        {
            using var ms = new MemoryStream();
            FormatManager.Instance.BinaryFormat.Store(map, ms);
            ms.Position = 0;
            Map back = FormatManager.Instance.BinaryFormat.Load(ms);
            List<string> diff = MapCompare.Diff(map, back);
            if (diff.Count > 0)
            {
                lock (gate) { bad.Add(key); RejectedFormat++; }
                File.WriteAllText(badMark, file.FullName + "\n" + string.Join("\n", diff));
                Log($"地图不缓存（二进制格式存不下）：{Rel(file.FullName)}：{diff[0]}", LogLevel.Debug);
                return;
            }
            string tmp = tbin + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, tbin, overwrite: true);
            File.WriteAllText(meta, Stamp(file) + "\n" + file.FullName + "\n");
            lock (gate) { Stored++; StoreMs += Ms(t); }
        }
        catch (Exception e)
        {
            lock (gate) { bad.Add(key); Failed++; }
            Log($"地图缓存写入失败，跳过：{Rel(file.FullName)}：{e.GetType().Name}: {e.Message}", LogLevel.Debug);
        }
    }

    public void ClearDisk()
    {
        try
        {
            foreach (string f in Directory.GetFiles(dir)) TryDelete(f);
            lock (gate) bad.Clear();
        }
        catch (Exception e) { monitor.Log($"清空地图缓存失败：{e.Message}", LogLevel.Warn); }
    }

    public int DiskFiles() { try { return Directory.GetFiles(dir, "*.tbin").Length; } catch { return 0; } }
    public long DiskBytes() { try { long n = 0; foreach (var f in Directory.GetFiles(dir, "*.tbin")) n += new FileInfo(f).Length; return n; } catch { return 0; } }

    void Log(string message, LogLevel level)
    {
        if (logged++ < 40) monitor.Log(message, level);
    }

    static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    static string Rel(string path)
    {
        int i = path.IndexOf("/Mods/", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? path[(i + 6)..] : path;
    }
}
