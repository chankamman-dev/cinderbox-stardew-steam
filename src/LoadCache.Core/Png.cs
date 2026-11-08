using System;
using System.IO;
using SkiaSharp;

namespace LoadCache.Core;

// PNG decoding straight into the game's pixel layout. SMAPI decodes to Skia's native format,
// premultiplies in a second pass and then converts pixel by pixel in managed code to MonoGame's
// Color (R,G,B,A bytes = Skia Rgba8888). On Mono that per-pixel loop costs more than the decode.
// Here the codec writes premultiplied Rgba8888 directly into the Color[] the caller pins, so there
// is no conversion pass and no second buffer. Same bytes as SMAPI's path (checked by tests/CacheCheck).
//
// Compiled against the SkiaSharp inside Cinderbox's APK; loaded by the mod at run time.
public static class Png
{
    public static string Version => typeof(SKCodec).Assembly.GetName().Version?.ToString() ?? "?";

    /// <param name="alloc">Given width and height, returns the address of a pinned buffer of width*height*4 bytes.</param>
    public static void Decode(string path, Func<int, int, IntPtr> alloc)
    {
        using var stream = File.OpenRead(path);
        using SKCodec codec = SKCodec.Create(stream)
            ?? throw new InvalidDataException("Failed to load " + path + ". This doesn't seem to be a valid PNG image.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        IntPtr dst = alloc(info.Width, info.Height);
        SKCodecResult r = codec.GetPixels(info, dst);
        if (r != SKCodecResult.Success && r != SKCodecResult.IncompleteInput)
            throw new InvalidDataException($"Failed to decode {path}: {r}");
    }
}
