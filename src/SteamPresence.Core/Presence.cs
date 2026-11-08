using System;
using System.Reflection;
using System.Threading;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace SteamPresence.Core;

// Holds a Steam session while Stardew is on screen inside Cinderbox and reports the game as
// running (413150), so friends see "playing Stardew Valley" and Steam counts the playtime, as the
// desktop client would. Port of lom-android's SteamPlay.java onto the SteamKit2 that Cinderbox
// already ships.
//
// - Login: the refresh token Cinderbox saved when the player signed in to Steam there (read only,
//   through Cinderbox's own credential store, in this process); otherwise the mod's own login from
//   a QR code scanned once with the Steam mobile app (TokenStore).
// - A login id of its own, so Cinderbox's short download/ownership sessions and this one never
//   replace each other.
// - If another device is playing on this account, Steam says so (PlayingSessionState) and nothing
//   is reported until that ends.
// - Off screen (no game ticks) the game is cleared and the session closed.
//
// Called by the SMAPI mod through reflection with BCL types only.
public static class Presence
{
    public const uint AppId = 413150;
    const uint LoginId = 0x53445650; // "SDVP"

    enum Source { Override, Cinderbox, Own }

    static Action<string, int> log = (_, _) => { };
    static Func<bool> visible = () => true;
    static (string account, string token)? overrideCreds;
    static Thread? worker;
    static readonly object gate = new();

    static bool closed, blocked, stateSeen, rejected, loggedOn, cinderboxRejected, loginDismissed;
    static EResult logonResult;
    static CancellationTokenSource? loginCts;

    public static string Status { get; private set; } = "idle";

    /// <summary>QR code to show (modules[y, x], true = dark), or null when no scan is wanted.</summary>
    public static bool[,]? QrModules { get; private set; }

    /// <summary>Line shown under the QR code.</summary>
    public static string LoginMessage { get; private set; } = "";

    /// <param name="logger">message, level (0 trace, 1 info, 2 warn, 3 error)</param>
    /// <param name="isVisible">true while the game is on screen</param>
    /// <param name="account">optional account override (tests); null uses Cinderbox's or the mod's login</param>
    /// <param name="token">refresh token for the override</param>
    public static void Start(Action<string, int> logger, Func<bool> isVisible, string? account, string? token)
    {
        lock (gate)
        {
            if (worker != null) return;
            log = logger;
            visible = isVisible;
            overrideCreds = account != null && token != null ? (account, token) : null;
            worker = new Thread(Run) { IsBackground = true, Name = "SteamPresence" };
            worker.Start();
        }
    }

    /// <summary>The player closed the QR code: no scan until RequestLogin or the next launch.</summary>
    public static void DismissLogin()
    {
        loginDismissed = true;
        QrModules = null;
        try { loginCts?.Cancel(); } catch { }
    }

    public static void RequestLogin() => loginDismissed = false;

    /// <summary>Forget the mod's own (scanned) login. Cinderbox's login is never touched.</summary>
    /// <summary>The game unlocked (or re-offered) an achievement; API name as the desktop SteamHelper gets it.</summary>
    public static void Unlock(string apiName)
    {
        if (!achievementsOn) return;
        Achievements.Offer(apiName == "0" ? "a0" : apiName);
        lock (gate) Monitor.PulseAll(gate);
    }

    /// <summary>Next achievement card to show: { title, name, icon bytes or null }, or null.</summary>
    public static object?[]? NextToast()
    {
        var t = Achievements.NextToast();
        return t == null ? null : new object?[] { t.Title, t.Name, t.Icon };
    }

    /// <summary>Queue a test card without touching Steam.</summary>
    public static void TestToast() => Achievements.TestToast(Warn);

    static bool achievementsOn = true;

    /// <summary>Turn achievement unlocking off (config).</summary>
    public static void SetAchievements(bool on) => achievementsOn = on;

    public static void Logout()
    {
        TokenStore.Forget();
        Info("已删除模组自己的扫码登录记录");
    }

    // On screen = Android ranks the process as foreground or visible (the activity is shown, also
    // while the lower screen has focus). Game ticks are no good for this: a big modded save loads
    // for well over 15 seconds without one. The tick signal from the SMAPI side is only a fallback.
    static bool androidStateBroken;

    static bool Visible()
    {
        if (!androidStateBroken)
        {
            try
            {
                return AndroidVisible();
            }
            catch (Exception e)
            {
                androidStateBroken = true;
                Warn($"读不到 Android 进程状态，改用游戏帧判断前后台：{e.Message}");
            }
        }
        return visible();
    }

    // Separate and never inlined: if Mono.Android (or this type) is missing, the failure happens
    // when this method is compiled, inside the caller's try, instead of breaking the caller.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static bool AndroidVisible()
    {
        var info = new Android.App.ActivityManager.RunningAppProcessInfo();
        Android.App.ActivityManager.GetMyMemoryState(info);
        return (int)info.Importance <= 200; // FOREGROUND 100, FOREGROUND_SERVICE 125, VISIBLE 200
    }

    static void Trace(string m) => log(m, 0);
    static void Info(string m) => log(m, 1);
    static void Warn(string m) => log(m, 2);

    static void Run()
    {
        Info($"SteamKit2 {typeof(SteamClient).Assembly.GetName().Version}，Cinderbox 凭据类：{CinderboxCredentials.Describe()}");
        long backoff = 30;
        while (true)
        {
            if (rejected) { Status = "rejected"; Thread.Sleep(60_000); continue; }
            if (!Visible()) { Status = "hidden"; Thread.Sleep(2_000); continue; }

            (string account, string token)? creds;
            Source source;
            try { (creds, source) = FindCredentials(); }
            catch (Exception e) { Warn($"读取登录失败：{e.Message}"); Thread.Sleep(60_000); continue; }

            if (creds == null)
            {
                if (loginDismissed) { Status = "login dismissed"; Thread.Sleep(2_000); continue; }
                creds = QrLogin();
                source = Source.Own;
                if (creds == null) { Thread.Sleep(1_000); continue; }
            }

            long began = Environment.TickCount64;
            try
            {
                Session(creds.Value.account, creds.Value.token, source);
                if (Environment.TickCount64 - began > 120_000) backoff = 30;
                else if (!rejected && Visible() && logonResult == EResult.OK)
                {
                    // Ended on its own right away: do not hammer Steam with logons.
                    Status = $"retry in {backoff}s";
                    Thread.Sleep((int)backoff * 1000);
                    backoff = Math.Min(backoff * 2, 600);
                }
            }
            catch (Exception e)
            {
                Warn($"Steam 会话出错：{e.GetType().Name}: {e.Message}，{backoff} 秒后重试");
                Status = $"retry in {backoff}s";
                Thread.Sleep((int)backoff * 1000);
                backoff = Math.Min(backoff * 2, 600);
            }
        }
    }

    static bool toldSource;

    static ((string account, string token)? creds, Source source) FindCredentials()
    {
        if (overrideCreds != null) return (overrideCreds, Source.Override);
        if (!cinderboxRejected)
        {
            var c = CinderboxCredentials.Load();
            if (c != null)
            {
                if (!toldSource) { Info("使用 Cinderbox 里登录的 Steam 账号"); toldSource = true; }
                return (c, Source.Cinderbox);
            }
        }
        var own = TokenStore.Load(Warn);
        if (own != null)
        {
            if (!toldSource) { Info("使用模组扫码登录的 Steam 账号"); toldSource = true; }
            return (own, Source.Own);
        }
        return (null, Source.Own);
    }

    static SteamConfiguration Config() => SteamConfiguration.Create(b => b
        // WebSocket only: plain TCP to bare CM addresses drops under the proxy on the user's network.
        .WithProtocolTypes(ProtocolTypes.WebSocket)
        .WithHttpClientFactory(_ => new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler())));

    // One QR login: shows the code (QrModules) until the player approves it in the Steam app,
    // closes it, or the session fails. Returns the new login, already saved.
    static (string account, string token)? QrLogin()
    {
        Status = "qr";
        var client = new SteamClient(Config());
        var manager = new CallbackManager(client);
        bool connected = false, dropped = false, pumping = true;
        manager.Subscribe<SteamClient.ConnectedCallback>(_ => Signal(() => connected = true));
        manager.Subscribe<SteamClient.DisconnectedCallback>(_ => Signal(() => dropped = true));
        var pump = new Thread(() => { while (Volatile.Read(ref pumping)) manager.RunWaitCallbacks(TimeSpan.FromSeconds(1)); })
        { IsBackground = true, Name = "SteamPresence-qr" };
        pump.Start();
        var cts = new CancellationTokenSource();
        loginCts = cts;
        try
        {
            LoginMessage = "正在连接 Steam…";
            client.Connect();
            WaitFor(() => connected || dropped, 30_000);
            if (!connected) throw new Exception("30 秒内没连上 Steam");
            QrAuthSession session = client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
            {
                DeviceFriendlyName = "AYN Thor · Cinderbox 星露谷",
                IsPersistentSession = true,
            }).GetAwaiter().GetResult();
            void Show()
            {
                if (loginDismissed) return;
                QrModules = QrCode.Encode(session.ChallengeURL);
                LoginMessage = "用手机上的 Steam App 扫码（只需一次），好友就能看到你在玩星露谷";
            }
            Show();
            session.ChallengeURLChanged = Show;
            Info("没有可用的 Steam 登录，已在游戏里显示二维码，等待扫码");
            AuthPollResult result = session.PollingWaitForResultAsync(cts.Token).GetAwaiter().GetResult();
            QrModules = null;
            TokenStore.Save(result.AccountName, result.RefreshToken, Warn);
            Info("扫码登录成功");
            return (result.AccountName, result.RefreshToken);
        }
        catch (OperationCanceledException)
        {
            Info("二维码已关闭，这次启动不再提示（控制台命令 steam_login 可重新显示）");
            return null;
        }
        catch (Exception e)
        {
            Warn($"扫码登录没完成：{e.GetType().Name}: {e.Message}");
            QrModules = null;
            LoginMessage = "";
            Thread.Sleep(5_000);
            return null;
        }
        finally
        {
            QrModules = null;
            loginCts = null;
            cts.Dispose();
            try { client.Disconnect(); } catch { }
            Volatile.Write(ref pumping, false);
            pump.Join(3000);
        }
    }

    static void Session(string account, string token, Source source)
    {
        lock (gate) { closed = false; blocked = false; stateSeen = false; loggedOn = false; logonResult = EResult.Invalid; }
        var client = new SteamClient(Config());
        var manager = new CallbackManager(client);
        var user = client.GetHandler<SteamUser>()!;
        var friends = client.GetHandler<SteamFriends>()!;
        // Handlers go in before connecting (they are walked for every incoming message).
        var replies = new StatsReplies();
        client.AddHandler(replies);
        bool pumping = true;

        manager.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            Trace("已连上 Steam，登录中");
            user.LogOn(new SteamUser.LogOnDetails
            {
                Username = account,
                AccessToken = token,
                ShouldRememberPassword = true,
                LoginID = LoginId,
            });
        });
        manager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            Info($"Steam 连接断开（我们主动：{cb.UserInitiated}）");
            Signal(() => closed = true);
        });
        manager.Subscribe<SteamUser.LoggedOnCallback>(cb => Signal(() =>
        {
            logonResult = cb.Result;
            if (cb.Result == EResult.OK) loggedOn = true;
            else closed = true;
        }));
        manager.Subscribe<SteamUser.LoggedOffCallback>(cb =>
        {
            Info($"Steam 把会话登出了：{cb.Result}");
            Signal(() => closed = true);
        });
        manager.Subscribe<SteamUser.PlayingSessionStateCallback>(cb =>
        {
            Info($"游戏会话状态：别处在玩 {cb.PlayingBlocked}，app {cb.PlayingAppID}");
            Signal(() => { blocked = cb.PlayingBlocked; stateSeen = true; });
        });

        var pump = new Thread(() => { while (Volatile.Read(ref pumping)) manager.RunWaitCallbacks(TimeSpan.FromSeconds(1)); })
        { IsBackground = true, Name = "SteamPresence-callbacks" };
        pump.Start();
        bool playing = false;
        try
        {
            Status = "connecting";
            client.Connect();
            WaitFor(() => loggedOn || closed, 30_000);
            if (!loggedOn)
            {
                if (logonResult is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired
                    or EResult.Revoked or EResult.InvalidSignature or EResult.AccountLoginDeniedNeedTwoFactor
                    or EResult.InvalidLoginAuthCode or EResult.AccountDisabled)
                {
                    switch (source)
                    {
                        case Source.Cinderbox:
                            cinderboxRejected = true;
                            Warn($"Steam 拒绝了 Cinderbox 保存的登录（{logonResult}），改用模组自己的登录");
                            break;
                        case Source.Own:
                            TokenStore.Forget();
                            Warn($"Steam 拒绝了扫码登录（{logonResult}），需要重新扫码");
                            break;
                        default:
                            rejected = true;
                            Warn($"Steam 拒绝了登录（{logonResult}），停止");
                            break;
                    }
                    toldSource = false;
                    return;
                }
                throw new Exception(logonResult == EResult.Invalid ? "30 秒内没登录上" : $"登录失败：{logonResult}");
            }

            // A session that only logs on is invisible and friends never see the game it plays;
            // the desktop client is online while a game runs, so this one is too.
            friends.SetPersonaState(EPersonaState.Online);
            Info($"已登录 Steam（{Mask(account)}），在线");
            // Steam states right after logon whether another device is playing; give it a moment.
            WaitFor(() => stateSeen || closed, 3_000);

            while (true)
            {
                bool want;
                lock (gate)
                {
                    if (closed) break;
                    want = !blocked;
                }
                if (!Visible()) break;
                if (want != playing)
                {
                    GamesPlayed(client, want);
                    playing = want;
                    Info(want ? $"正在玩 {AppId}" : "另一台设备在玩，暂不上报");
                }
                Status = playing ? "playing" : "blocked";
                if (Achievements.HasPending)
                {
                    try { Achievements.Flush(client, replies, Info, Warn); }
                    catch (Exception e) { Warn($"成就没写进 Steam（下次连上再试）：{e.Message}"); WaitFor(() => closed, 30_000); }
                }
                WaitFor(() => closed || Achievements.HasPending, 2_000);
            }
            Info($"结束会话：断开 {closed}，在屏幕上 {Visible()}");
        }
        finally
        {
            try { if (playing && client.IsConnected) GamesPlayed(client, false); } catch { }
            try { if (client.IsConnected) user.LogOff(); } catch { }
            try { client.Disconnect(); } catch { }
            Volatile.Write(ref pumping, false);
            pump.Join(3000);
            if (Status is "playing" or "blocked" or "connecting") Status = "offline";
        }
    }

    static void GamesPlayed(SteamClient client, bool on)
    {
        var msg = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob);
        if (on)
            msg.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
            {
                game_id = AppId,
                process_id = (uint)Environment.ProcessId,
            });
        client.Send(msg);
    }

    static void Signal(Action change)
    {
        lock (gate) { change(); Monitor.PulseAll(gate); }
    }

    static void WaitFor(Func<bool> done, int ms)
    {
        long until = Environment.TickCount64 + ms;
        lock (gate)
        {
            while (!done())
            {
                long left = until - Environment.TickCount64;
                if (left <= 0) return;
                Monitor.Wait(gate, (int)Math.Min(left, 1000));
            }
        }
    }

    static string Mask(string account) => account.Length <= 2 ? "**" : account[..2] + new string('*', account.Length - 2);
}

// Cinderbox keeps the account name and refresh token from its own Steam login in
// <app files>/cinderbox-launcher/steam_credentials.enc, encrypted with an AndroidKeyStore key.
// Its own store class reads it; it is internal, so through reflection. Read only: nothing here
// writes, refreshes or deletes Cinderbox's login.
static class CinderboxCredentials
{
    const string StoreType = "Cinderbox.Launcher.SteamLogin.AndroidSteamCredentialStore, Cinderbox";

    public static string Describe() => Type.GetType(StoreType) != null ? "找到" : "找不到";

    public static (string account, string token)? Load()
    {
        var type = Type.GetType(StoreType);
        if (type == null) return null;
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        object store = Activator.CreateInstance(type, nonPublic: true)!;
        type.GetMethod("Load", any)!.Invoke(store, null);
        string? account = type.GetProperty("AccountName", any)?.GetValue(store) as string;
        string? token = type.GetProperty("RefreshToken", any)?.GetValue(store) as string;
        return string.IsNullOrEmpty(account) || string.IsNullOrEmpty(token) ? null : (account, token);
    }
}
