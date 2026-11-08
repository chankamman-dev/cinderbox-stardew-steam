using System;
using System.Collections.Generic;
using xTile;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;

namespace LoadCache;

// Structural comparison of two xTile maps, used to prove that a map written as TBIN and read back
// is the same map the TMX parser produced. Anything that differs disqualifies the map from caching.
static class MapCompare
{
    const int MaxDiffs = 30;

    public static List<string> Diff(Map a, Map b)
    {
        var d = new List<string>();
        void Add(string s) { if (d.Count < MaxDiffs) d.Add(s); }

        if (a.Id != b.Id) Add($"地图 Id：{a.Id} / {b.Id}");
        if (a.Description != b.Description) Add("地图 Description 不同");
        Props("地图", a.Properties, b.Properties, Add);

        if (a.TileSheets.Count != b.TileSheets.Count) { Add($"图块集数量 {a.TileSheets.Count} / {b.TileSheets.Count}"); return d; }
        for (int i = 0; i < a.TileSheets.Count; i++)
        {
            TileSheet x = a.TileSheets[i], y = b.TileSheets[i];
            if (x.Id != y.Id) Add($"图块集 {i} Id：{x.Id} / {y.Id}");
            if (x.ImageSource != y.ImageSource) Add($"图块集 {x.Id} ImageSource：{x.ImageSource} / {y.ImageSource}");
            if (x.SheetSize != y.SheetSize) Add($"图块集 {x.Id} SheetSize");
            if (x.TileSize != y.TileSize) Add($"图块集 {x.Id} TileSize");
            if (x.Margin != y.Margin) Add($"图块集 {x.Id} Margin");
            if (x.Spacing != y.Spacing) Add($"图块集 {x.Id} Spacing");
            if (x.Description != y.Description) Add($"图块集 {x.Id} Description");
            Props($"图块集 {x.Id}", x.Properties, y.Properties, Add);
        }

        if (a.Layers.Count != b.Layers.Count) { Add($"图层数量 {a.Layers.Count} / {b.Layers.Count}"); return d; }
        for (int i = 0; i < a.Layers.Count; i++)
        {
            Layer la = a.Layers[i], lb = b.Layers[i];
            if (la.Id != lb.Id) Add($"图层 {i} Id：{la.Id} / {lb.Id}");
            if (la.LayerSize != lb.LayerSize) { Add($"图层 {la.Id} 大小"); continue; }
            if (la.TileSize != lb.TileSize) Add($"图层 {la.Id} TileSize");
            if (la.Visible != lb.Visible) Add($"图层 {la.Id} Visible");
            if (la.Description != lb.Description) Add($"图层 {la.Id} Description");
            Props($"图层 {la.Id}", la.Properties, lb.Properties, Add);
            int w = la.LayerSize.Width, h = la.LayerSize.Height;
            for (int y = 0; y < h && d.Count < MaxDiffs; y++)
                for (int x = 0; x < w && d.Count < MaxDiffs; x++)
                {
                    Tile? p = la.Tiles[x, y], q = lb.Tiles[x, y];
                    if ((p == null) != (q == null)) { Add($"图层 {la.Id} ({x},{y}) 一边为空"); continue; }
                    if (p == null) continue;
                    string? why = TileDiff(p, q!);
                    if (why != null) Add($"图层 {la.Id} ({x},{y}) {why}");
                }
        }
        return d;
    }

    static string? TileDiff(Tile p, Tile q)
    {
        if (p.GetType() != q.GetType()) return $"类型 {p.GetType().Name} / {q.GetType().Name}";
        if (p.TileSheet?.Id != q.TileSheet?.Id) return $"图块集 {p.TileSheet?.Id} / {q.TileSheet?.Id}";
        if (p.TileIndex != q.TileIndex) return $"TileIndex {p.TileIndex} / {q.TileIndex}";
        if (p.BlendMode != q.BlendMode) return "BlendMode";
        if (p is AnimatedTile ap && q is AnimatedTile aq)
        {
            if (ap.FrameInterval != aq.FrameInterval) return "FrameInterval";
            if (ap.TileFrames.Length != aq.TileFrames.Length) return "帧数";
            for (int i = 0; i < ap.TileFrames.Length; i++)
            {
                if (ap.TileFrames[i].TileIndex != aq.TileFrames[i].TileIndex) return $"帧 {i} TileIndex";
                if (ap.TileFrames[i].TileSheet?.Id != aq.TileFrames[i].TileSheet?.Id) return $"帧 {i} 图块集";
                string? fp = PropsDiff(ap.TileFrames[i].Properties, aq.TileFrames[i].Properties);
                if (fp != null) return $"帧 {i} 属性 {fp}";
            }
        }
        return PropsDiff(p.Properties, q.Properties) is { } s ? "属性 " + s : null;
    }

    static void Props(string what, IPropertyCollection a, IPropertyCollection b, Action<string> add)
    {
        string? s = PropsDiff(a, b);
        if (s != null) add($"{what} 属性 {s}");
    }

    static string? PropsDiff(IPropertyCollection a, IPropertyCollection b)
    {
        if (a.Count != b.Count) return $"数量 {a.Count} / {b.Count}";
        foreach (KeyValuePair<string, PropertyValue> kv in a)
        {
            if (!b.TryGetValue(kv.Key, out PropertyValue? other)) return $"缺 {kv.Key}";
            if (kv.Value.Type != other.Type) return $"{kv.Key} 类型 {kv.Value.Type.Name} / {other.Type.Name}";
            if (kv.Value.ToString() != other.ToString()) return $"{kv.Key} 值 {kv.Value} / {other}";
        }
        return null;
    }
}
