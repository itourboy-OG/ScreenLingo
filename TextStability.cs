using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLingo;

public static class TextStability
{
    /// <summary>Rejects regions whose visible pixels changed between two captures, including moving chat and tickers.</summary>
    public static ImmutableArray<TextRegion> StationaryRegions(CapturedImage first, CapturedImage second, ImmutableArray<TextRegion> regions)
    {
        if (first.Width != second.Width || first.Height != second.Height) return [];
        byte[] before = GrayPixels(first);
        byte[] after = GrayPixels(second);
        return regions.Where(region => IsStationary(before, after, first.Width, first.Height, region.Bounds)).ToImmutableArray();
    }

    private static bool IsStationary(byte[] before, byte[] after, int width, int height, PixelRect bounds)
    {
        int left = Math.Clamp(bounds.X, 0, width);
        int top = Math.Clamp(bounds.Y, 0, height);
        int right = Math.Clamp(bounds.X + bounds.Width, 0, width);
        int bottom = Math.Clamp(bounds.Y + bounds.Height, 0, height);
        int changed = 0;
        int pixels = (right - left) * (bottom - top);
        if (pixels <= 0) return false;
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
            if (Math.Abs(before[y * width + x] - after[y * width + x]) > 25) changed++;
        return changed <= pixels * 0.06;
    }

    private static byte[] GrayPixels(CapturedImage image)
    {
        using MemoryStream input = new(image.Png);
        BitmapFrame frame = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        FormatConvertedBitmap gray = new(frame, PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[checked(image.Width * image.Height)];
        gray.CopyPixels(pixels, image.Width, 0);
        return pixels;
    }
}
