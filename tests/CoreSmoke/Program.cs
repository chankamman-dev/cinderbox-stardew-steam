using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;

// PC smoke test for SteamPresence.Core on the desktop .NET 10 runtime, with Cinderbox's own
// SteamKit2/protobuf-net/ZstdSharp/System.IO.Hashing. Uses a deliberately invalid token: checks
// that the core loads, reaches a Steam CM over WebSocket, logs on and stops on the rejection.
// Usage: CoreSmoke <asm dir> <core dll> [seconds]
if (args[0] == "qr")
{
    // CoreSmoke qr <core dll> <out dir> <text>...: writes each code as rows of 0/1 for decoding.
    var qr = Assembly.LoadFrom(args[1]).GetType("SteamPresence.Core.QrCode", true)!.GetMethod("EncodeWithMask")!;
    int mask = int.Parse(Environment.GetEnvironmentVariable("QR_MASK") ?? "-1");
    for (int i = 3; i < args.Length; i++)
    {
        var m = (bool[,])qr.Invoke(null, new object[] { args[i], mask })!;
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < m.GetLength(0); y++)
        {
            for (int x = 0; x < m.GetLength(1); x++) sb.Append(m[y, x] ? '1' : '0');
            sb.Append('\n');
        }
        File.WriteAllText(Path.Combine(args[2], $"qr{i - 3}.txt"), sb.ToString());
        Console.WriteLine($"qr{i - 3}: {m.GetLength(0)} modules, {args[i].Length} chars");
    }
    return 0;
}
string asmDir = args[0];
string coreDll = args[1];
int seconds = args.Length > 2 ? int.Parse(args[2]) : 60;

AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    string p = Path.Combine(asmDir, name.Name + ".dll");
    return File.Exists(p) && name.Name is "SteamKit2" or "protobuf-net.Core" or "protobuf-net" or "ZstdSharp" or "System.IO.Hashing"
        ? ctx.LoadFromAssemblyPath(p) : null;
};

var core = Assembly.LoadFrom(coreDll);
var presence = core.GetType("SteamPresence.Core.Presence", true)!;
Action<string, int> log = (m, l) => Console.WriteLine($"[{l}] {m}");
Func<bool> visible = () => true;
presence.GetMethod("Start")!.Invoke(null, new object?[] { log, visible, "smoketest_invalid_account_x", "not-a-real-token" });
var status = presence.GetProperty("Status")!;
for (int i = 0; i < seconds; i++)
{
    Thread.Sleep(1000);
    string s = (string)status.GetValue(null)!;
    if (s == "rejected") { Console.WriteLine("RESULT rejected (expected)"); return 0; }
}
Console.WriteLine($"RESULT timeout, status {status.GetValue(null)}");
return 1;
