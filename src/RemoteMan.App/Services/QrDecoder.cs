using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace RemoteMan.App.Services;

/// <summary>Reads the otpauth:// link out of a 2FA setup QR code (screenshot, clipboard or image file).</summary>
public static class QrDecoder
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public static string? Decode(BitmapSource source)
    {
        // Bgr24 drops alpha: WPF often hands clipboard screenshots over with an all-zero alpha channel.
        BitmapSource bmp = source.Format == PixelFormats.Bgr24 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = (w * 3 + 3) & ~3;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);
        if (stride != w * 3)
        {
            // Pack rows tightly for the luminance source.
            var packed = new byte[w * 3 * h];
            for (var y = 0; y < h; y++) Buffer.BlockCopy(px, y * stride, packed, y * w * 3, w * 3);
            px = packed;
        }
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = [BarcodeFormat.QR_CODE] },
        };
        return reader.Decode(new RGBLuminanceSource(px, w, h, RGBLuminanceSource.BitmapFormat.BGR24))?.Text;
    }

    public static string? DecodeFile(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(Path.GetFullPath(path));
        img.EndInit();
        return Decode(img);
    }

    public static bool ClipboardHasImage() =>
        Clipboard.ContainsImage() || (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().Any(IsImageFile));

    /// <summary>Decodes a copied image or a copied image file. Null when there is none or no QR code in it.</summary>
    public static string? DecodeClipboard()
    {
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) return Decode(img);
        if (Clipboard.ContainsFileDropList())
            foreach (var f in Clipboard.GetFileDropList().Cast<string>().Where(IsImageFile))
                if (DecodeFile(f) is { } text) return text;
        return null;
    }

    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
