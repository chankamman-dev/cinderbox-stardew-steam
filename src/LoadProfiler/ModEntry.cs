using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Enums;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using xTile;

namespace LoadProfiler;

// Measures where startup and save loading spend their time, to decide what a disk cache could
// save. Read only: it times the steps, it changes nothing about what they produce.
//
// - Mod file parsing, which is what a cache could skip: PNG decoding
//   (SMAPI ModContentManager.DecodeRawImageData; Cinderbox's in-memory cache hits never get there)
//   and map parsing (xTile FormatManager.LoadMap: mod .tmx/.tbin; the game's own maps are .xnb).
// - Applying image and map patches (AssetDataForImage.PatchImage, AssetDataForMap.PatchMap).
// - Slow event handlers per mod: Cinderbox's SMAPI has a built-in profiler (AndroidPaths.
//   EnableEventProfiling); it is switched on here at 100 ms until a minute after the day starts.
// - A timeline of the phases and GC counts. Everything not covered (data edits, game and mod
//   logic, textures to the GPU) is what is left of each phase.
//
// Report: Mods/LoadProfiler/report-<time>.txt, summary in the SMAPI log.
public sealed class ModEntry : Mod
{
    static readonly object gate = new();
    static readonly List<Sample> samples = new();
    static string modsRoot = "";
    static string phase = "启动";

    readonly List<(string what, double at, int gc0, int gc1, int gc2)> marks = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    bool titleSeen, dayStarted, reported;
    int ticksAfterDay;
    PropertyInfo? profiling, profilingThreshold;

    sealed record Sample(string Kind, string Phase, string File, double Ms, long Bytes, string Extra);

    public override void Entry(IModHelper helper)
    {
        modsRoot = Path.GetFullPath(Path.Combine(helper.DirectoryPath, ".."));
        Mark("模组加载中（本模组 Entry）");
        Patch();
        SetEventProfiling(true, 100);

        helper.Events.GameLoop.GameLaunched += (_, _) => Mark("GameLaunched（所有模组 Entry 完成）");
        helper.Events.Display.RenderedActiveMenu += (_, _) =>
        {
            if (!titleSeen && Game1.activeClickableMenu is TitleMenu) { titleSeen = true; phase = "标题"; Mark("标题画面第一次画出"); }
        };
        helper.Events.Specialized.LoadStageChanged += (_, e) =>
        {
            if (e.NewStage == LoadStage.SaveParsed || e.NewStage == LoadStage.CreatedBasicInfo) phase = "读档";
            Mark($"读档阶段：{e.NewStage}");
        };
        helper.Events.GameLoop.SaveLoaded += (_, _) => Mark("SaveLoaded");
        helper.Events.GameLoop.DayStarted += (_, _) =>
        {
            if (dayStarted) return;
            dayStarted = true;
            Mark("DayStarted（第一天开始）");
        };
        helper.Events.GameLoop.UpdateTicked += (_, _) =>
        {
            if (!dayStarted || reported) return;
            if (++ticksAfterDay == 1) { phase = "进入游戏后"; Mark("进入游戏后第一帧"); }
            if (ticksAfterDay == 60 * 60) // about a minute in: after the post-load burst
            {
                Mark("进入游戏一分钟");
                reported = true;
                SetEventProfiling(false, 100);
                Report();
            }
        };
        helper.ConsoleCommands.Add("load_profile", "立即写出读档计时报告。", (_, _) => Report());
    }

    void Mark(string what) =>
        marks.Add((what, clock.Elapsed.TotalSeconds, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)));

    void SetEventProfiling(bool on, int thresholdMs)
    {
        try
        {
            var type = AccessTools.TypeByName("StardewModdingAPI.Mobile.AndroidPaths");
            profiling ??= type?.GetProperty("EnableEventProfiling", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            profilingThreshold ??= type?.GetProperty("EventProfilingThreshold", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (profiling == null || profilingThreshold == null) { Monitor.Log("没有 Cinderbox 的事件耗时分析（不是 Cinderbox 版 SMAPI？）", LogLevel.Info); return; }
            profilingThreshold.GetSetMethod(true)!.Invoke(null, new object[] { thresholdMs });
            profiling.GetSetMethod(true)!.Invoke(null, new object[] { on });
            Monitor.Log($"事件耗时分析：{(on ? $"开（超过 {thresholdMs} ms 记录）" : "关")}", LogLevel.Info);
        }
        catch (Exception e)
        {
            Monitor.Log($"开关事件耗时分析失败：{e.Message}", LogLevel.Warn);
        }
    }

    void Patch()
    {
        var h = new Harmony(ModManifest.UniqueID);
        void Try(string what, MethodBase? target, string prefix, string postfix)
        {
            if (target == null) { Monitor.Log($"找不到 {what}，这一项不计时", LogLevel.Warn); return; }
            try
            {
                h.Patch(target, prefix: new HarmonyMethod(typeof(ModEntry), prefix), postfix: new HarmonyMethod(typeof(ModEntry), postfix));
            }
            catch (Exception e)
            {
                Monitor.Log($"挂不上 {what}：{e.Message}", LogLevel.Warn);
            }
        }

        var mcm = AccessTools.TypeByName("StardewModdingAPI.Framework.ContentManagers.ModContentManager");
        Try("图片解码", mcm == null ? null : AccessTools.Method(mcm, "DecodeRawImageData", new[] { typeof(FileInfo) }), nameof(Begin), nameof(AfterDecode));
        Try("地图解析", AccessTools.Method(typeof(xTile.Format.FormatManager), nameof(xTile.Format.FormatManager.LoadMap), new[] { typeof(string) }), nameof(Begin), nameof(AfterMap));

        var img = AccessTools.TypeByName("StardewModdingAPI.Framework.Content.AssetDataForImage");
        if (img != null)
            foreach (var m in AccessTools.GetDeclaredMethods(img).Where(m => m.Name == "PatchImage"))
                Try("图片修改", m, nameof(Begin), nameof(AfterPatchImage));
        var map = AccessTools.TypeByName("StardewModdingAPI.Framework.Content.AssetDataForMap");
        Try("地图修改", map == null ? null : AccessTools.Method(map, "PatchMap"), nameof(Begin), nameof(AfterPatchMap));
    }

    static void Begin(out long __state) => __state = Stopwatch.GetTimestamp();

    static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    static void Add(string kind, string file, double ms, long bytes, string extra)
    {
        lock (gate) samples.Add(new Sample(kind, phase, file, ms, bytes, extra));
    }

    static string Rel(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return full.StartsWith(modsRoot, StringComparison.OrdinalIgnoreCase) ? full[(modsRoot.Length + 1)..] : full;
        }
        catch { return path; }
    }

    static long Size(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

    static void AfterDecode(FileInfo file, long __state, IRawTextureData __result) =>
        Add("图片解码", Rel(file.FullName), Ms(__state), file.Exists ? file.Length : 0, __result == null ? "" : $"{__result.Width}x{__result.Height}");

    static void AfterMap(string filePath, long __state, Map __result) =>
        Add(Path.GetExtension(filePath).ToLowerInvariant() == ".tmx" ? "地图解析 tmx" : "地图解析 " + Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
            Rel(filePath), Ms(__state), Size(filePath), __result == null ? "" : $"{__result.Layers.Count} 层 {__result.TileSheets.Count} 图块集");

    static void AfterPatchImage(object __instance, long __state) =>
        Add("图片修改", AssetName(__instance), Ms(__state), 0, "");

    static void AfterPatchMap(object __instance, long __state) =>
        Add("地图修改", AssetName(__instance), Ms(__state), 0, "");

    static string AssetName(object assetData)
    {
        try { return (assetData as IAssetInfo)?.Name?.Name ?? "?"; }
        catch { return "?"; }
    }

    void Report()
    {
        List<Sample> s;
        lock (gate) s = samples.ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"读档计时报告 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("== 时间线（秒，自本模组加载起）==");
        double prev = 0;
        foreach (var m in marks)
        {
            sb.AppendLine($"{m.at,8:F1}  (+{m.at - prev,6:F1})  GC {m.gc0}/{m.gc1}/{m.gc2}  {m.what}");
            prev = m.at;
        }

        sb.AppendLine();
        sb.AppendLine("== 各阶段：可缓存的文件解析 vs 修改（毫秒合计 / 次数）==");
        foreach (var byPhase in s.GroupBy(x => x.Phase))
        {
            sb.AppendLine($"[{byPhase.Key}]");
            foreach (var k in byPhase.GroupBy(x => x.Kind).OrderByDescending(g => g.Sum(x => x.Ms)))
                sb.AppendLine($"  {k.Key,-12} {k.Sum(x => x.Ms),10:F0} ms  {k.Count(),6} 次  文件 {k.Sum(x => x.Bytes) / 1024.0 / 1024.0,7:F1} MB");
        }

        sb.AppendLine();
        sb.AppendLine("== 按模组文件夹：文件解析耗时（全阶段）==");
        foreach (var g in s.Where(x => x.Kind.StartsWith("图片解码") || x.Kind.StartsWith("地图解析"))
                     .GroupBy(x => TopFolder(x.File)).OrderByDescending(g => g.Sum(x => x.Ms)).Take(30))
        {
            double img = g.Where(x => x.Kind == "图片解码").Sum(x => x.Ms), map = g.Where(x => x.Kind.StartsWith("地图")).Sum(x => x.Ms);
            sb.AppendLine($"  {g.Sum(x => x.Ms),8:F0} ms  图片 {img,7:F0} ms/{g.Count(x => x.Kind == "图片解码"),4} 张  地图 {map,7:F0} ms/{g.Count(x => x.Kind.StartsWith("地图")),4} 张  {g.Key}");
        }

        sb.AppendLine();
        sb.AppendLine("== 最慢的 40 个文件 ==");
        foreach (var x in s.Where(x => x.Kind.StartsWith("图片解码") || x.Kind.StartsWith("地图解析")).OrderByDescending(x => x.Ms).Take(40))
            sb.AppendLine($"  {x.Ms,8:F1} ms  {x.Kind,-10} {x.Bytes / 1024,6} KB  {x.Extra,-18} [{x.Phase}] {x.File}");

        sb.AppendLine();
        sb.AppendLine("== 地图：每 KB 耗时（估算转 TBIN 的收益）==");
        foreach (var k in s.Where(x => x.Kind.StartsWith("地图解析")).GroupBy(x => x.Kind))
        {
            double kb = k.Sum(x => x.Bytes) / 1024.0;
            sb.AppendLine($"  {k.Key,-12} {k.Count(),5} 次  {kb,9:F0} KB  {k.Sum(x => x.Ms),8:F0} ms  {(kb > 0 ? k.Sum(x => x.Ms) / kb : 0),6:F2} ms/KB");
        }

        sb.AppendLine();
        sb.AppendLine("== 重复解析（同一文件解析多次）==");
        foreach (var g in s.Where(x => x.Kind.StartsWith("地图解析") || x.Kind == "图片解码").GroupBy(x => x.File).Where(g => g.Count() > 1).OrderByDescending(g => g.Sum(x => x.Ms)).Take(15))
            sb.AppendLine($"  {g.Count()} 次  {g.Sum(x => x.Ms),8:F0} ms  {g.Key}");
        sb.AppendLine();
        sb.AppendLine("各模组慢事件见 SMAPI 日志里 \"exceeds the 100ms warning threshold\" 的行。");

        string text = sb.ToString();
        string path = Path.Combine(Helper.DirectoryPath, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try { File.WriteAllText(path, text); } catch (Exception e) { Monitor.Log($"写报告失败：{e.Message}", LogLevel.Warn); }
        Monitor.Log("读档计时报告：" + path + "\n" + text, LogLevel.Info);
    }

    static string TopFolder(string rel)
    {
        string[] parts = rel.Replace('\\', '/').Split('/');
        return parts.Length >= 3 ? parts[0] + "/" + parts[1] : parts[0];
    }
}
