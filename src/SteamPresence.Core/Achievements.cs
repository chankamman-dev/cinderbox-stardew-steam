using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamPresence.Core;

// Unlocks Stardew's Steam achievements from the game's own calls (Game1.getPlatformAchievement,
// hooked by the SMAPI side), the way the desktop SteamHelper would. The game re-offers every
// achievement the save holds when a day starts (Stats.AllowRetroactiveAchievements, as on PC), so
// nothing needs to persist here: what is not sent this run is offered again next time.
//
// Each achievement is one bit of a stat in the schema: stats/<statId>/bits/<bitIndex>/name. The bit
// index is the KEY of the child (there is no "bit" field; lom-android's first build read it as 0
// and mapped everything to bit 0). The bits are OR-ed into the current values and stored with the
// stats CRC Steam just reported. Only bits that are not set yet are written, so achievements the
// account already has are left alone and never shown as new.
static class Achievements
{
    static readonly ConcurrentQueue<string> incoming = new();
    static readonly HashSet<string> done = new(); // checked against Steam this run
    static readonly ConcurrentQueue<Toast> toasts = new();
    static Dictionary<string, (string name, string desc, string icon)>? info;

    public sealed record Toast(string Title, string Name, byte[]? Icon);

    public static void Offer(string apiName)
    {
        if (string.IsNullOrWhiteSpace(apiName)) return;
        lock (done) if (done.Contains(apiName)) return;
        incoming.Enqueue(apiName);
        Interlocked.Exchange(ref lastOffer, Environment.TickCount64);
    }

    // Sent once the game has been quiet for a while: loading a save offers every held achievement
    // in one burst right at the memory peak of the load, and nothing here is urgent.
    const long QuietMs = 10_000;
    static long lastOffer;

    public static bool HasPending => !incoming.IsEmpty && Environment.TickCount64 - Interlocked.Read(ref lastOffer) >= QuietMs;

    public static Toast? NextToast() => toasts.TryDequeue(out var t) ? t : null;

    /// <summary>Test card, no Steam involved.</summary>
    public static void TestToast(Action<string> warn)
    {
        var icon = info?.Values.FirstOrDefault().icon;
        toasts.Enqueue(new Toast("已解锁成就", "测试：Steam 成就提示", icon == null ? null : Icon(icon, warn)));
    }

    public static void Flush(SteamClient client, StatsReplies replies, Action<string> info1, Action<string> warn)
    {
        var pending = new HashSet<string>();
        while (incoming.TryDequeue(out var n)) pending.Add(n);
        lock (done) pending.ExceptWith(done);
        if (pending.Count == 0) return;
        try
        {
            FlushCore(client, replies, pending, info1, warn);
            lock (done) done.UnionWith(pending);
        }
        catch
        {
            foreach (var n in pending) incoming.Enqueue(n); // tried again later
            Interlocked.Exchange(ref lastOffer, Environment.TickCount64);
            throw;
        }
    }

    static void FlushCore(SteamClient client, StatsReplies replies, HashSet<string> pending, Action<string> info1, Action<string> warn)
    {
        ulong me = client.SteamID!.ConvertToUInt64();
        var get = new ClientMsgProtobuf<CMsgClientGetUserStats>(EMsg.ClientGetUserStats);
        get.SourceJobID = client.GetNextJobID();
        get.Body.game_id = Presence.AppId;
        get.Body.schema_local_version = -1;
        get.Body.crc_stats = 0;
        get.Body.steam_id_for_user = me;
        var gotTask = replies.ExpectStats();
        client.Send(get);
        var st = Wait(gotTask, "读取成就");
        if ((EResult)st.eresult != EResult.OK) throw new Exception($"读取成就失败：{(EResult)st.eresult}");

        var schema = new KeyValue();
        if (st.schema == null || !schema.TryReadAsBinary(new MemoryStream(st.schema)))
            throw new Exception("成就 schema 读不出来");
        KeyValue stats = schema["stats"];
        Remember(stats);

        var values = st.stats.ToDictionary(s => s.stat_id, s => s.stat_value);
        var changed = new Dictionary<uint, uint>();
        var known = new HashSet<string>();
        var unlocked = new List<string>();
        foreach (KeyValue stat in stats.Children)
        {
            if (!uint.TryParse(stat.Name, out uint id)) continue;
            foreach (KeyValue bit in stat["bits"].Children)
            {
                string? name = bit["name"].Value;
                if (name == null || !pending.Contains(name)) continue;
                if (!int.TryParse(bit.Name, out int index) || index < 0 || index > 31) continue;
                known.Add(name);
                uint mask = 1u << index;
                uint cur = changed.TryGetValue(id, out uint c) ? c : values.GetValueOrDefault(id);
                if ((cur & mask) != 0) continue;
                changed[id] = cur | mask;
                unlocked.Add(name);
                info1($"解锁 {name}（stat {id} 位 {index}）");
            }
        }
        foreach (string n in pending.Except(known)) warn($"Steam 成就表里没有：{n}");

        if (changed.Count > 0)
        {
            var store = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2);
            store.SourceJobID = client.GetNextJobID();
            store.Body.game_id = Presence.AppId;
            store.Body.settor_steam_id = me;
            store.Body.settee_steam_id = me;
            store.Body.crc_stats = st.crc_stats;
            store.Body.explicit_reset = false;
            foreach (var (id, value) in changed)
                store.Body.stats.Add(new CMsgClientStoreUserStats2.Stats { stat_id = id, stat_value = value });
            var storedTask = replies.ExpectStore();
            client.Send(store);
            var r = Wait(storedTask, "写入成就");
            if ((EResult)r.eresult != EResult.OK) throw new Exception($"写入成就失败：{(EResult)r.eresult}");
            info1($"已在 Steam 解锁 {unlocked.Count} 个成就：{string.Join("、", unlocked)}");
            QueueToasts(unlocked, warn);
        }
        info1($"核对成就 {pending.Count} 个，新解锁 {unlocked.Count} 个");
    }

    static T Wait<T>(Task<T> t, string what)
    {
        if (!t.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException($"{what}：30 秒没有回应");
        return t.Result;
    }

    // stats/<id>/bits/<n>/{name, display/{name,desc}/<lang>, display/icon}
    static void Remember(KeyValue stats)
    {
        var map = new Dictionary<string, (string, string, string)>();
        foreach (KeyValue stat in stats.Children)
            foreach (KeyValue bit in stat["bits"].Children)
            {
                string? id = bit["name"].Value;
                if (id == null) continue;
                KeyValue d = bit["display"];
                string icon = bit["icon"].Value ?? d["icon"].Value ?? "";
                map[id] = (Local(d["name"], id), Local(d["desc"], ""), icon);
            }
        info = map;
    }

    static string Local(KeyValue kv, string fallback)
    {
        foreach (string lang in new[] { "schinese", "tchinese", "english" })
            if (!string.IsNullOrEmpty(kv[lang].Value)) return kv[lang].Value!;
        return string.IsNullOrEmpty(kv.Value) ? fallback : kv.Value!;
    }

    static void QueueToasts(List<string> unlocked, Action<string> warn)
    {
        // A first sync of an old save can unlock many at once: one card for the lot.
        if (unlocked.Count > 3)
        {
            var names = unlocked.Take(4).Select(Name);
            string first = info != null && info.TryGetValue(unlocked[0], out var f) ? f.icon : "";
            toasts.Enqueue(new Toast($"已同步 {unlocked.Count} 个成就到 Steam", string.Join("、", names) + "…", Icon(first, warn)));
            return;
        }
        foreach (string id in unlocked)
        {
            string icon = info != null && info.TryGetValue(id, out var i) ? i.icon : "";
            toasts.Enqueue(new Toast("已解锁成就", Name(id), Icon(icon, warn)));
        }
    }

    static string Name(string id) => info != null && info.TryGetValue(id, out var i) ? i.name : id;

    static readonly string[] Cdns =
    {
        "https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/413150/",
        "https://shared.akamai.steamstatic.com/community_assets/images/apps/413150/",
        "https://shared.fastly.steamstatic.com/community_assets/images/apps/413150/",
    };

    // Fetched on the Steam thread before the card is queued; kept in files/steam-presence/icons.
    static byte[]? Icon(string file, Action<string> warn)
    {
        if (string.IsNullOrEmpty(file) || file.IndexOfAny(new[] { '/', '\\' }) >= 0) return null;
        string dir = Path.Combine(TokenStore.Dir(), "icons");
        string path = Path.Combine(dir, file);
        try
        {
            if (File.Exists(path)) return File.ReadAllBytes(path);
            using var http = new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(10) };
            foreach (string cdn in Cdns)
            {
                try
                {
                    byte[] bytes = http.GetByteArrayAsync(cdn + file).GetAwaiter().GetResult();
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(path, bytes);
                    return bytes;
                }
                catch (HttpRequestException) { }
            }
            warn($"成就图标下载不了：{file}");
        }
        catch (Exception e)
        {
            warn($"成就图标出错：{e.Message}");
        }
        return null;
    }
}

// Steam answers stats reads and stores with messages SteamKit2 has no handler for.
sealed class StatsReplies : ClientMsgHandler
{
    TaskCompletionSource<CMsgClientGetUserStatsResponse>? stats;
    TaskCompletionSource<CMsgClientStoreUserStatsResponse>? store;

    public Task<CMsgClientGetUserStatsResponse> ExpectStats()
    {
        lock (this) return (stats = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    public Task<CMsgClientStoreUserStatsResponse> ExpectStore()
    {
        lock (this) return (store = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        switch (packetMsg.MsgType)
        {
            case EMsg.ClientGetUserStatsResponse:
            {
                var m = new ClientMsgProtobuf<CMsgClientGetUserStatsResponse>(packetMsg);
                if (m.Body.game_id != Presence.AppId) return;
                lock (this) stats?.TrySetResult(m.Body);
                break;
            }
            case EMsg.ClientStoreUserStatsResponse:
            {
                var m = new ClientMsgProtobuf<CMsgClientStoreUserStatsResponse>(packetMsg);
                if (m.Body.game_id != Presence.AppId) return;
                lock (this) store?.TrySetResult(m.Body);
                break;
            }
        }
    }
}
