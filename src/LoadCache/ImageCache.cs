using System;
using System.Collections.Generic;
using System.IO;
using StardewModdingAPI;

namespace LoadCache;

// Decoded mod images, kept while a load is in progress so the same file is not decoded again and
// again (Content Patcher invalidates and re-applies its patches several times during a load, and
// many patches share one source image: in the measured load one file was decoded 194 times).
// Byte-capped LRU; the mod empties it a few seconds after the day starts, so nothing stays resident
// during play. Entries are checked against the file's size and write time, as Cinderbox's own
// (disabled by default) cache does. Decoded data is shared, which SMAPI already does for its own
// cache; nothing in the asset pipeline writes into a loaded IRawTextureData.
sealed class ImageCache
{
    sealed class Entry
    {
        public IRawTextureData Data = null!;
        public long Length, WriteTicks, Bytes;
        public double DecodeMs;
        public LinkedListNode<string> Node = null!;
    }

    readonly Dictionary<string, Entry> map = new(StringComparer.Ordinal);
    readonly LinkedList<string> lru = new();
    readonly object gate = new();
    long held;
    public long CapBytes;

    public long Hits, Misses, Stored, Evicted, Rejected;
    public double SavedMs;
    public long Held { get { lock (gate) return held; } }
    public int Count { get { lock (gate) return map.Count; } }

    public ImageCache(long capBytes) => CapBytes = capBytes;

    public bool TryGet(FileInfo file, out IRawTextureData data)
    {
        lock (gate)
        {
            if (map.TryGetValue(file.FullName, out Entry? e) && e.Length == file.Length && e.WriteTicks == file.LastWriteTimeUtc.Ticks)
            {
                lru.Remove(e.Node);
                lru.AddFirst(e.Node);
                Hits++;
                SavedMs += e.DecodeMs;
                data = e.Data;
                return true;
            }
            Misses++;
        }
        data = null!;
        return false;
    }

    public void Put(FileInfo file, IRawTextureData data, double decodeMs)
    {
        long bytes = (long)data.Width * data.Height * 4 + 64;
        lock (gate)
        {
            if (bytes > CapBytes / 2) { Rejected++; return; }
            if (map.TryGetValue(file.FullName, out Entry? old)) Remove(old, file.FullName);
            while (held + bytes > CapBytes && lru.Last != null)
            {
                string victim = lru.Last.Value;
                Remove(map[victim], victim);
                Evicted++;
            }
            var e = new Entry
            {
                Data = data, Length = file.Length, WriteTicks = file.LastWriteTimeUtc.Ticks,
                Bytes = bytes, DecodeMs = decodeMs, Node = new LinkedListNode<string>(file.FullName),
            };
            map[file.FullName] = e;
            lru.AddFirst(e.Node);
            held += bytes;
            Stored++;
        }
    }

    void Remove(Entry e, string key)
    {
        map.Remove(key);
        lru.Remove(e.Node);
        held -= e.Bytes;
    }

    public void Clear()
    {
        lock (gate)
        {
            map.Clear();
            lru.Clear();
            held = 0;
        }
    }
}
