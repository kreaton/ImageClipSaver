using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageClipToFile;

/// <summary>
/// Reads an image from the clipboard. WPF's Clipboard.GetImage() mangles many screenshots
/// (black or transparent results), so the PNG format is tried first, then the raw DIB.
/// </summary>
internal static class ClipboardImage
{
    private const int BI_BITFIELDS = 3;

    /// <summary>Returns the clipboard image, or null if there is none (or the clipboard stays locked).</summary>
    public static BitmapSource? TryGet()
    {
        // Another app may still hold the clipboard open right after it changed it
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return Read();
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        return null;
    }

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource? Read()
    {
        var data = Clipboard.GetDataObject();
        if (data == null)
            return null;

        // Snipping Tool, browsers and Office put a PNG on the clipboard; it keeps transparency intact
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is Stream png && Decode(png) is { } fromPng)
            return fromPng;

        // Print Screen and most other apps only offer a bitmap
        if (data.GetDataPresent(DataFormats.Dib) && data.GetData(DataFormats.Dib) is MemoryStream dib &&
            FromDib(dib.ToArray()) is { } fromDib)
            return fromDib;

        if (data.GetDataPresent(DataFormats.Bitmap) && Clipboard.GetImage() is { } bitmap)
        {
            bitmap.Freeze();
            return bitmap;
        }

        return null;
    }

    private static BitmapSource? Decode(Stream stream)
    {
        try
        {
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException or COMException)
        {
            return null;
        }
    }

    /// <summary>CF_DIB is a BMP file without its 14-byte file header, so add one and decode it as a BMP.</summary>
    private static BitmapSource? FromDib(byte[] dib)
    {
        if (dib.Length < 40)
            return null;

        var headerSize = BitConverter.ToInt32(dib, 0);
        var bitCount = BitConverter.ToInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var colorsUsed = BitConverter.ToInt32(dib, 32);

        var maskBytes = headerSize == 40 && compression == BI_BITFIELDS ? 12 : 0;
        var colors = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0;
        var pixelOffset = 14 + headerSize + maskBytes + colors * 4;

        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.TryWriteBytes(bmp.AsSpan(2), bmp.Length);
        BitConverter.TryWriteBytes(bmp.AsSpan(10), pixelOffset);
        dib.CopyTo(bmp, 14);

        var image = Decode(new MemoryStream(bmp));
        return image == null ? null : DropEmptyAlpha(image);
    }

    /// <summary>
    /// Screenshots often come as 32-bit bitmaps whose alpha channel is all zero, which would
    /// save as a fully transparent PNG. Treat such an alpha channel as "no alpha".
    /// </summary>
    private static BitmapSource DropEmptyAlpha(BitmapSource image)
    {
        if (image.Format != PixelFormats.Bgra32 && image.Format != PixelFormats.Pbgra32)
            return image;

        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
                return image;
        }

        var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        opaque.Freeze();
        return opaque;
    }
}
