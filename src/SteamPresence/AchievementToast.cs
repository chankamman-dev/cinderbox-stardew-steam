using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SteamPresence;

// Steam-style "achievement unlocked" card in the bottom-right corner: rises from below the screen
// edge, stays a few seconds, and leaves the way it came. Cards come from the core only after
// Steam accepted the unlock, so achievements the account already has never show one.
// Time only runs while the card is actually drawn (not during a paused/hidden game).
sealed class AchievementToast
{
    const float InMs = 350, HoldMs = 5000, OutMs = 300;
    const int W = 420, H = 96, Margin = 18, IconSize = 64;

    static readonly Color Back = new(23, 29, 37);
    static readonly Color Edge = new(61, 68, 80);
    static readonly Color Title = new(143, 152, 160);
    static readonly Color Name = new(236, 240, 244);

    readonly string title, name;
    readonly Texture2D? icon;
    float elapsed;
    long lastDraw;

    public AchievementToast(string title, string name, byte[]? iconBytes)
    {
        this.title = title;
        this.name = name;
        if (iconBytes != null)
        {
            try { icon = Texture2D.FromStream(Game1.graphics.GraphicsDevice, new MemoryStream(iconBytes)); }
            catch { icon = null; }
        }
    }

    public bool Finished => elapsed >= InMs + HoldMs + OutMs;

    public void Dispose() => icon?.Dispose();

    public void Draw(SpriteBatch b)
    {
        long now = Environment.TickCount64;
        if (lastDraw != 0) elapsed += Math.Min(100, now - lastDraw);
        lastDraw = now;

        // 0 = off screen below, 1 = in place. Out is the entry played backwards.
        float shown = elapsed < InMs ? EaseOut(elapsed / InMs)
            : elapsed < InMs + HoldMs ? 1f
            : 1f - EaseIn(Math.Min(1f, (elapsed - InMs - HoldMs) / OutMs));

        int w = Game1.uiViewport.Width, h = Game1.uiViewport.Height;
        int x = w - W - Margin;
        int y = h - (int)((H + Margin) * shown);
        var box = new Rectangle(x, y, W, H);

        b.Draw(Game1.staminaRect, box, Back * 0.96f);
        b.Draw(Game1.staminaRect, new Rectangle(x, y, W, 2), Edge);
        int pad = (H - IconSize) / 2;
        var iconBox = new Rectangle(x + pad, y + pad, IconSize, IconSize);
        if (icon != null) b.Draw(icon, iconBox, Color.White);
        else b.Draw(Game1.staminaRect, iconBox, Edge);

        SpriteFont font = Game1.smallFont;
        float textX = iconBox.Right + 14, maxW = box.Right - 12 - textX;
        DrawLine(b, font, title, new Vector2(textX, y + 18), 0.75f, Title, maxW);
        DrawLine(b, font, name, new Vector2(textX, y + 46), 0.95f, Name, maxW);
    }

    static void DrawLine(SpriteBatch b, SpriteFont font, string text, Vector2 at, float scale, Color color, float maxW)
    {
        string s = Fit(font, Sanitize(font, text), scale, maxW);
        b.DrawString(font, s, at, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
    }

    // The game's font only has the glyphs of the current language.
    static string Sanitize(SpriteFont font, string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) sb.Append(font.Characters.Contains(c) ? c : font.DefaultCharacter ?? '?');
        return sb.ToString();
    }

    static string Fit(SpriteFont font, string s, float scale, float maxW)
    {
        if (font.MeasureString(s).X * scale <= maxW) return s;
        while (s.Length > 1 && font.MeasureString(s + "…").X * scale > maxW) s = s[..^1];
        return s + (font.Characters.Contains('…') ? "…" : "...");
    }

    static float EaseOut(float t) => 1f - (float)Math.Pow(1f - t, 3);
    static float EaseIn(float t) => t * t * t;
}
