using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using xTile;
using xTile.Format;

namespace LoadCache;

// Faster save loading for Stardew in Cinderbox. Measured on the user's Thor (LoadProfiler): of a
// 46 s load, 18.8 s went to decoding mod PNGs (the same files over and over) and 9.8 s to parsing
// TMX maps. Three things, each switchable in config.json, none of which changes what is loaded:
//  1. ImageCache: decoded images are kept during a load and dropped a few seconds after the day
//     starts (ImageCache.cs).
//  2. FastDecode: PNGs are decoded straight into the game's pixel format (LoadCache.Core, loaded
//     at run time because it is built against the SkiaSharp inside Cinderbox's APK).
//  3. MapCache: TMX maps are cached as verified TBIN files under Mods/LoadCache/cache (MapCache.cs).
// Everything hooks SMAPI's own loading code with Harmony; when a hook cannot do its job it steps
// aside and SMAPI's code runs as before.
public sealed class ModEntry : Mod
{
    static ModEntry? instance;
    ModConfig config = new();
    ImageCache? images;
    MapCache? maps;
    Action<string, Func<int, int, IntPtr>>? decodeCore;
    ConstructorInfo? rawCtor;
    int fastFailures;
    long fastCount; double fastMs;
    long slowCount; double slowMs;
    int clearAt = -1;
    bool reportedThisLoad;

    public override void Entry(IModHelper helper)
    {
        instance = this;
        config = helper.ReadConfig<ModConfig>();
        if (Type.GetType("Cinderbox.Game.Paths.AppPaths, Cinderbox") == null && !config.AllowOutsideCinderbox)
        {
            Monitor.Log("不是在 Cinderbox 里运行，什么都不做（config.json 里 AllowOutsideCinderbox 可强制开启）。", LogLevel.Info);
            return;
        }

        var harmony = new Harmony(ModManifest.UniqueID);

        if (config.ImageCache || config.FastDecode)
        {
            var mcm = AccessTools.TypeByName("StardewModdingAPI.Framework.ContentManagers.ModContentManager");
            var decode = mcm == null ? null : AccessTools.Method(mcm, "DecodeRawImageData", new[] { typeof(FileInfo) });
            var raw = AccessTools.TypeByName("StardewModdingAPI.Framework.Content.RawTextureData");
            rawCtor = raw == null ? null : AccessTools.Constructor(raw, new[] { typeof(int), typeof(int), typeof(Color[]) });
            if (decode == null || rawCtor == null)
                Monitor.Log("找不到 SMAPI 的图片解码入口，图片缓存和快速解码关闭。", LogLevel.Warn);
            else
            {
                if (config.ImageCache) images = new ImageCache(Math.Max(16, config.ImageCacheMB) * 1024L * 1024L);
                if (config.FastDecode) LoadCore(helper);
                harmony.Patch(decode, prefix: new HarmonyMethod(typeof(ModEntry), nameof(DecodePrefix)), postfix: new HarmonyMethod(typeof(ModEntry), nameof(DecodePostfix)));
            }
        }

        if (config.MapCache)
        {
            var loadMap = AccessTools.Method(typeof(FormatManager), nameof(FormatManager.LoadMap), new[] { typeof(string) });
            if (loadMap == null) Monitor.Log("找不到 xTile 的地图加载入口，地图缓存关闭。", LogLevel.Warn);
            else
            {
                maps = new MapCache(Path.Combine(helper.DirectoryPath, "cache", "maps"), Monitor);
                harmony.Patch(loadMap, prefix: new HarmonyMethod(typeof(ModEntry), nameof(MapPrefix)), postfix: new HarmonyMethod(typeof(ModEntry), nameof(MapPostfix)));
            }
        }

        helper.Events.GameLoop.DayStarted += (_, _) => { clearAt = Game1.ticks + 60 * config.ReleaseAfterSeconds; reportedThisLoad = false; };
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => Release("回到标题");
        helper.Events.GameLoop.UpdateTicked += (_, _) =>
        {
            if (clearAt >= 0 && Game1.ticks >= clearAt) { clearAt = -1; Release("读档完成"); }
        };
        helper.ConsoleCommands.Add("load_cache", "显示读档缓存的统计。", (_, _) => Monitor.Log(Stats(), LogLevel.Info));
        helper.ConsoleCommands.Add("load_cache_clear", "清空内存里的图片缓存和磁盘上的地图缓存。", (_, _) =>
        {
            images?.Clear();
            maps?.ClearDisk();
            Monitor.Log("已清空。", LogLevel.Info);
        });
        Monitor.Log($"图片缓存 {(images != null ? $"开（上限 {config.ImageCacheMB} MB）" : "关")}，快速解码 {(decodeCore != null ? "开" : "关")}，地图缓存 {(maps != null ? $"开（已有 {maps.DiskFiles()} 张）" : "关")}", LogLevel.Info);
    }

    void LoadCore(IModHelper helper)
    {
        try
        {
            Assembly core = Assembly.LoadFrom(Path.Combine(helper.DirectoryPath, "LoadCache.Core.dll"));
            Type png = core.GetType("LoadCache.Core.Png", throwOnError: true)!;
            decodeCore = (Action<string, Func<int, int, IntPtr>>)Delegate.CreateDelegate(typeof(Action<string, Func<int, int, IntPtr>>), png.GetMethod("Decode")!);
            Monitor.Log($"快速解码：SkiaSharp {png.GetProperty("Version")?.GetValue(null)}", LogLevel.Trace);
        }
        catch (Exception e)
        {
            decodeCore = null;
            Monitor.Log($"快速解码不可用，用 SMAPI 原来的解码：{e.GetType().Name}: {e.Message}", LogLevel.Warn);
        }
    }

    void Release(string why)
    {
        if (images != null && images.Count > 0)
        {
            images.Clear();
            if (config.CollectAfterRelease) GC.Collect();
        }
        if (!reportedThisLoad && config.LogStats)
        {
            reportedThisLoad = true;
            Monitor.Log($"{why}，读档缓存统计：\n{Stats()}", LogLevel.Info);
        }
    }

    string Stats()
    {
        string a = images == null ? "图片缓存：关" :
            $"图片缓存：命中 {images.Hits} 次（省约 {images.SavedMs / 1000:F1} 秒），未命中 {images.Misses}，存入 {images.Stored}，挤出 {images.Evicted}，太大不存 {images.Rejected}，当前 {images.Count} 张 {images.Held / 1024 / 1024} MB";
        string b = decodeCore == null ? "快速解码：关" :
            $"快速解码：{fastCount} 张共 {fastMs / 1000:F1} 秒" + (fastFailures > 0 ? $"，失败回退 {fastFailures} 次" : "") + (slowCount > 0 ? $"；原解码 {slowCount} 张 {slowMs / 1000:F1} 秒" : "");
        string c = maps == null ? "地图缓存：关" :
            $"地图缓存：命中 {maps.Hits} 张（读 {maps.HitMs / 1000:F1} 秒，按 TMX 速度约需 {maps.HitKb * 0.45 / 1000:F1} 秒），新存 {maps.Stored} 张（花 {maps.StoreMs / 1000:F1} 秒），格式存不下 {maps.RejectedFormat}，出错 {maps.Failed}；磁盘 {maps.DiskFiles()} 张 {maps.DiskBytes() / 1024 / 1024} MB";
        return a + "\n" + b + "\n" + c;
    }

    // ---- images ----

    static bool DecodePrefix(FileInfo file, ref IRawTextureData __result, out long __state)
    {
        __state = 0;
        ModEntry? m = instance;
        if (m == null) return true;
        try
        {
            if (m.images != null && m.images.TryGet(file, out IRawTextureData cached))
            {
                __result = cached;
                return false;
            }
            if (m.decodeCore != null)
            {
                long t = Stopwatch.GetTimestamp();
                IRawTextureData data = m.FastDecode(file.FullName);
                double ms = Ms(t);
                m.fastCount++; m.fastMs += ms;
                m.images?.Put(file, data, ms);
                __result = data;
                return false;
            }
        }
        catch (Exception e)
        {
            if (m.fastFailures++ < 3) m.Monitor.Log($"快速解码失败，这张用 SMAPI 原来的解码：{file.Name}：{e.GetType().Name}: {e.Message}", LogLevel.Warn);
        }
        __state = Stopwatch.GetTimestamp();
        return true;
    }

    static void DecodePostfix(FileInfo file, IRawTextureData __result, long __state)
    {
        ModEntry? m = instance;
        if (m == null || __state <= 0 || __result == null) return;
        double ms = Ms(__state);
        m.slowCount++; m.slowMs += ms;
        m.images?.Put(file, __result, ms);
    }

    IRawTextureData FastDecode(string path)
    {
        Color[]? data = null;
        int width = 0, height = 0;
        GCHandle handle = default;
        try
        {
            decodeCore!(path, (w, h) =>
            {
                width = w; height = h;
                data = GC.AllocateUninitializedArray<Color>(checked(w * h));
                handle = GCHandle.Alloc(data, GCHandleType.Pinned);
                return handle.AddrOfPinnedObject();
            });
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
        }
        if (data == null) throw new InvalidDataException("decoder returned nothing");
        return (IRawTextureData)rawCtor!.Invoke(new object[] { width, height, data });
    }

    // ---- maps ----

    static bool MapPrefix(string filePath, ref Map __result, out bool __state)
    {
        __state = false;
        ModEntry? m = instance;
        if (m?.maps == null || filePath == null || !filePath.EndsWith(".tmx", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            Map? cached = m.maps.TryLoad(filePath, out bool wantsStore);
            if (cached != null) { __result = cached; return false; }
            __state = wantsStore;
        }
        catch (Exception e)
        {
            m.Monitor.Log($"地图缓存读取出错，用原来的解析：{Path.GetFileName(filePath)}：{e.Message}", LogLevel.Debug);
        }
        return true;
    }

    static void MapPostfix(string filePath, Map __result, bool __state)
    {
        if (__state && __result != null) instance?.maps?.Store(filePath, __result);
    }

    static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
}

public sealed class ModConfig
{
    /// <summary>Keep decoded mod images during a load so repeated loads of one file decode once.</summary>
    public bool ImageCache { get; set; } = true;

    /// <summary>Memory the image cache may use while a load is in progress.</summary>
    public int ImageCacheMB { get; set; } = 256;

    /// <summary>Seconds after the day starts before the image cache is emptied.</summary>
    public int ReleaseAfterSeconds { get; set; } = 5;

    /// <summary>Force a full GC when the image cache is emptied. Frees the memory at once but stalls the game about half a second every morning; off by default.</summary>
    public bool CollectAfterRelease { get; set; } = false;

    /// <summary>Decode PNGs straight into the game's pixel format instead of converting pixel by pixel.</summary>
    public bool FastDecode { get; set; } = true;

    /// <summary>Cache TMX maps as verified TBIN files under Mods/LoadCache/cache.</summary>
    public bool MapCache { get; set; } = true;

    /// <summary>Log the statistics after each load.</summary>
    public bool LogStats { get; set; } = true;

    /// <summary>Also run outside Cinderbox (desktop SMAPI).</summary>
    public bool AllowOutsideCinderbox { get; set; } = false;
}
