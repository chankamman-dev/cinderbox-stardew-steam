using System;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace SteamPresence;

// Shows "playing Stardew Valley" on Steam while the game runs inside Cinderbox on Android.
//
// The Steam work lives in SteamPresence.Core.dll, loaded here at run time rather than referenced:
// it is compiled against the SteamKit2 (and Mono.Android) inside Cinderbox's APK, which SMAPI's
// assembly rewriter cannot see on disk. Anywhere else (desktop SMAPI) the mod stays idle.
//
// This side only feeds the core a "game on screen" signal and draws the one-time QR login.
public sealed class ModEntry : Mod
{
    // Game ticks stop when Android pauses the activity (home, another app in front); a short
    // silence is treated as "not on screen". Focus moving to the lower screen does not stop ticks.
    const long HiddenAfterMs = 120_000;
    long lastTick = Environment.TickCount64;

    PropertyInfo? statusProp, qrProp, messageProp;
    MethodInfo? dismiss, request, logout;

    Rectangle panel;
    bool panelShown;
    bool englishOnly;

    public override void Entry(IModHelper helper)
    {
        var config = helper.ReadConfig<ModConfig>();
        if (!config.Enabled)
        {
            Monitor.Log("config.json 里关掉了，不连 Steam。", LogLevel.Info);
            return;
        }
        if (Type.GetType("Cinderbox.Game.Paths.AppPaths, Cinderbox") == null)
        {
            Monitor.Log("不是在 Cinderbox 里运行，什么都不做。", LogLevel.Info);
            return;
        }

        helper.Events.GameLoop.UpdateTicked += (_, _) =>
        {
            Interlocked.Exchange(ref lastTick, Environment.TickCount64);
            PullToast();
        };
        try
        {
            Assembly core = Assembly.LoadFrom(Path.Combine(helper.DirectoryPath, "SteamPresence.Core.dll"));
            Type presence = core.GetType("SteamPresence.Core.Presence", throwOnError: true)!;
            statusProp = presence.GetProperty("Status");
            qrProp = presence.GetProperty("QrModules");
            messageProp = presence.GetProperty("LoginMessage");
            dismiss = presence.GetMethod("DismissLogin");
            request = presence.GetMethod("RequestLogin");
            logout = presence.GetMethod("Logout");
            unlock = presence.GetMethod("Unlock");
            nextToast = presence.GetMethod("NextToast");
            testToast = presence.GetMethod("TestToast");
            presence.GetMethod("SetAchievements")!.Invoke(null, new object[] { config.Achievements });
            showToasts = config.AchievementToasts;

            Action<string, int> log = (message, level) => Monitor.Log(message, level switch
            {
                0 => LogLevel.Trace,
                1 => LogLevel.Info,
                2 => LogLevel.Warn,
                _ => LogLevel.Error,
            });
            Func<bool> visible = () => Environment.TickCount64 - Interlocked.Read(ref lastTick) < HiddenAfterMs;
            presence.GetMethod("Start")!.Invoke(null, new object?[] { log, visible, null, null });
        }
        catch (Exception e)
        {
            Monitor.Log($"加载 Steam 核心失败，模组不工作：{e}", LogLevel.Error);
            return;
        }

        // Overlays go on the menu or HUD layer (UI coordinates); when neither is drawn this frame
        // (events, cutscenes) they fall back to the final Rendered pass.
        helper.Events.Display.RenderedActiveMenu += (_, e) => DrawOverlays(e.SpriteBatch);
        helper.Events.Display.RenderedHud += (_, e) => DrawOverlays(e.SpriteBatch);
        helper.Events.Display.Rendered += (_, e) =>
        {
            if (!drewThisFrame) DrawOverlays(e.SpriteBatch);
            drewThisFrame = false;
        };
        helper.Events.Input.ButtonPressed += OnButtonPressed;

        if (config.Achievements)
        {
            instance = this;
            var harmony = new Harmony(ModManifest.UniqueID);
            harmony.Patch(AccessTools.Method(typeof(Game1), nameof(Game1.getPlatformAchievement)),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(AfterPlatformAchievement)));
        }
        if (config.ToastTestOnStart)
            helper.Events.GameLoop.GameLaunched += (_, _) => testToast?.Invoke(null, null);
        helper.ConsoleCommands.Add("steam_toast_test", "显示一个测试成就提示（不碰 Steam）。", (_, _) => testToast?.Invoke(null, null));

        helper.ConsoleCommands.Add("steam_presence", "显示 Steam 在线状态。", (_, _) =>
            Monitor.Log($"Steam 在线状态：{statusProp?.GetValue(null)}", LogLevel.Info));
        helper.ConsoleCommands.Add("steam_login", "重新显示 Steam 扫码登录（关掉二维码之后用）。", (_, _) => request?.Invoke(null, null));
        helper.ConsoleCommands.Add("steam_logout", "删除模组自己的扫码登录记录（不影响 Cinderbox 的登录）。", (_, _) => logout?.Invoke(null, null));
    }

    static ModEntry? instance;
    MethodInfo? unlock, nextToast, testToast;
    bool showToasts = true, drewThisFrame;
    AchievementToast? toast;

    // Game1.getPlatformAchievement(which): every achievement the game grants, and on day start
    // every one the save already holds (as on PC). The core sorts out what is new on Steam.
    static void AfterPlatformAchievement(string which)
    {
        try { instance?.unlock?.Invoke(null, new object[] { which }); }
        catch (Exception e) { instance?.Monitor.Log($"成就转发失败：{e.Message}", LogLevel.Warn); }
    }

    // On the game thread: textures are made here.
    void PullToast()
    {
        if (toast != null || nextToast == null) return;
        if (nextToast.Invoke(null, null) is not object?[] t) return;
        if (!showToasts) return;
        try { toast = new AchievementToast((string)t[0]!, (string)t[1]!, t[2] as byte[]); }
        catch (Exception e) { Monitor.Log($"成就提示出错：{e.Message}", LogLevel.Warn); }
    }

    void DrawOverlays(SpriteBatch b)
    {
        if (drewThisFrame) return;
        drewThisFrame = true;
        if (toast != null)
        {
            try { toast.Draw(b); }
            catch (Exception e) { Monitor.Log($"成就提示绘制失败：{e.Message}", LogLevel.Warn); toast.Dispose(); toast = null; }
            if (toast?.Finished == true) { toast.Dispose(); toast = null; }
        }
        DrawLogin(b);
    }

    bool[,]? Qr() => qrProp?.GetValue(null) as bool[,];

    void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!panelShown || Qr() == null) return;
        bool close = e.Button is SButton.ControllerB or SButton.Escape or SButton.MouseRight;
        if (e.Button == SButton.MouseLeft)
        {
            Vector2 p = e.Cursor.GetScaledScreenPixels();
            close = !panel.Contains((int)p.X, (int)p.Y);
        }
        // Nothing reaches the menu underneath while the code is up.
        Helper.Input.Suppress(e.Button);
        if (close)
        {
            dismiss?.Invoke(null, null);
            panelShown = false;
        }
    }

    void DrawLogin(SpriteBatch b)
    {
        bool[,]? m = Qr();
        if (m == null) { panelShown = false; return; }

        int w = Game1.uiViewport.Width, h = Game1.uiViewport.Height;
        int n = m.GetLength(0), quiet = 4, total = n + quiet * 2;
        int px = Math.Max(2, (int)(Math.Min(w, h) * 0.55f) / total);
        int side = px * total;
        SpriteFont font = Game1.smallFont;
        int pad = 24, textWidth = Math.Max(side, (int)(w * 0.45f));

        string message = englishOnly ? "Scan with the Steam mobile app (once) so friends see you playing." : messageProp?.GetValue(null) as string ?? "";
        string hint = englishOnly ? "B / Esc / tap outside: close" : "按 B、Esc 或点空白处关闭（本次启动不再提示）";
        string text;
        Vector2 textSize;
        try
        {
            text = Game1.parseText(message, font, textWidth) + "\n" + Game1.parseText(hint, font, textWidth);
            textSize = font.MeasureString(text);
        }
        catch (ArgumentException)
        {
            // The loaded font has no CJK glyphs (game not in Chinese).
            englishOnly = true;
            return;
        }

        int panelW = Math.Max(side, (int)textSize.X) + pad * 2;
        int panelH = side + pad * 3 + (int)textSize.Y;
        panel = new Rectangle((w - panelW) / 2, (h - panelH) / 2, panelW, panelH);
        panelShown = true;

        b.Draw(Game1.staminaRect, new Rectangle(0, 0, w, h), Color.Black * 0.6f);
        b.Draw(Game1.staminaRect, panel, Color.White);
        int x0 = panel.X + (panelW - side) / 2, y0 = panel.Y + pad;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (m[y, x])
                    b.Draw(Game1.staminaRect, new Rectangle(x0 + (quiet + x) * px, y0 + (quiet + y) * px, px, px), Color.Black);
        b.DrawString(font, text, new Vector2(panel.X + (panelW - textSize.X) / 2, y0 + side + pad), Color.Black);
    }
}

public sealed class ModConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Unlock Steam achievements the game grants (and the ones the save already holds, as on PC).</summary>
    public bool Achievements { get; set; } = true;

    /// <summary>Show a Steam-style card for each achievement newly unlocked on Steam.</summary>
    public bool AchievementToasts { get; set; } = true;

    /// <summary>Show a test card at startup (no Steam involved).</summary>
    public bool ToastTestOnStart { get; set; } = false;
}
