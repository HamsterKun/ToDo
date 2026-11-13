using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DesktopNote.Services;

/// <summary>
/// 读取系统壁纸并按窗口位置裁剪出一块，供界面做模糊背景（伪毛玻璃 / 真壁纸模糊）。
/// </summary>
public static class Backdrop
{
    private static BitmapSource? _image;
    private static int _style = 10;
    private static bool _tile;
    private static bool _loaded;

    public static void Reload()
    {
        _loaded = true;
        _image = null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false);
            var styleText = key?.GetValue("WallpaperStyle") as string;
            int.TryParse(styleText, out _style);
            if (_style == 0 && styleText == null) _style = 10;
            _tile = (key?.GetValue("TileWallpaper") as string) == "1";

            var path = key?.GetValue("WallPaper") as string;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (File.Exists(transcoded)) path = transcoded;
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            _image = bmp;
        }
        catch
        {
            _image = null;
        }
    }

    /// <summary>
    /// 取窗口区域壁纸并做软件高斯近似模糊。
    /// 之所以自己糊而不是用 BlurEffect：WPF 的 Effect 会把内容画到中间层，圆角裁剪会失效，
    /// 溢出到窗口边缘外就是一圈脏边。
    /// </summary>
    public static BitmapSource? BlurredCrop(Int32Rect rectPhysical, double blurRadius)
    {
        var crop = Crop(rectPhysical);
        if (crop == null) return null;

        int radius = (int)Math.Round(blurRadius);
        return radius < 1 ? crop : Blur(crop, radius);
    }

    private static BitmapSource Blur(BitmapSource src, int radius)
    {
        var formatted = src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        int w = formatted.PixelWidth;
        int h = formatted.PixelHeight;
        if (w < 2 || h < 2) return formatted;

        int stride = w * 4;
        var buffer = new byte[stride * h];
        formatted.CopyPixels(buffer, stride, 0);

        var temp = new byte[buffer.Length];

        // 三次盒式模糊 ≈ 高斯
        for (int i = 0; i < 3; i++)
        {
            BlurHorizontal(buffer, temp, w, h, radius, stride);
            BlurVertical(temp, buffer, w, h, radius, stride);
        }

        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
        result.Freeze();
        return result;
    }

    private static void BlurHorizontal(byte[] src, byte[] dst, int w, int h, int r, int stride)
    {
        int window = r * 2 + 1;

        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            int sumB = 0, sumG = 0, sumR = 0;

            for (int i = -r; i <= r; i++)
            {
                int xi = Math.Clamp(i, 0, w - 1) * 4 + row;
                sumB += src[xi];
                sumG += src[xi + 1];
                sumR += src[xi + 2];
            }

            for (int x = 0; x < w; x++)
            {
                int o = row + x * 4;
                dst[o] = (byte)(sumB / window);
                dst[o + 1] = (byte)(sumG / window);
                dst[o + 2] = (byte)(sumR / window);
                dst[o + 3] = 255;

                int outIdx = Math.Clamp(x - r, 0, w - 1) * 4 + row;
                int inIdx = Math.Clamp(x + r + 1, 0, w - 1) * 4 + row;
                sumB += src[inIdx] - src[outIdx];
                sumG += src[inIdx + 1] - src[outIdx + 1];
                sumR += src[inIdx + 2] - src[outIdx + 2];
            }
        }
    }

    private static void BlurVertical(byte[] src, byte[] dst, int w, int h, int r, int stride)
    {
        int window = r * 2 + 1;

        for (int x = 0; x < w; x++)
        {
            int col = x * 4;
            int sumB = 0, sumG = 0, sumR = 0;

            for (int i = -r; i <= r; i++)
            {
                int yi = Math.Clamp(i, 0, h - 1) * stride + col;
                sumB += src[yi];
                sumG += src[yi + 1];
                sumR += src[yi + 2];
            }

            for (int y = 0; y < h; y++)
            {
                int o = y * stride + col;
                dst[o] = (byte)(sumB / window);
                dst[o + 1] = (byte)(sumG / window);
                dst[o + 2] = (byte)(sumR / window);
                dst[o + 3] = 255;

                int outIdx = Math.Clamp(y - r, 0, h - 1) * stride + col;
                int inIdx = Math.Clamp(y + r + 1, 0, h - 1) * stride + col;
                sumB += src[inIdx] - src[outIdx];
                sumG += src[inIdx + 1] - src[outIdx + 1];
                sumR += src[inIdx + 2] - src[outIdx + 2];
            }
        }
    }

    /// <summary>按屏幕物理像素矩形裁剪壁纸；取不到时返回 null（界面退化为纯色玻璃）。</summary>
    public static BitmapSource? Crop(Int32Rect rectPhysical)
    {
        if (!_loaded) Reload();
        var img = _image;
        if (img == null) return null;

        try
        {
            int iw = img.PixelWidth, ih = img.PixelHeight;
            if (iw <= 0 || ih <= 0) return null;

            int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            int vw = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
            int vh = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

            double sx, sy, ox, oy;

            switch (_style)
            {
                case 2: // 拉伸
                    sx = vw / (double)iw;
                    sy = vh / (double)ih;
                    ox = vx;
                    oy = vy;
                    break;
                case 6: // 适应
                {
                    var s = Math.Min(vw / (double)iw, vh / (double)ih);
                    sx = sy = s;
                    ox = vx + (vw - iw * s) / 2;
                    oy = vy + (vh - ih * s) / 2;
                    break;
                }
                case 0 when !_tile: // 居中
                {
                    var s = Math.Min(vw / (double)iw, vh / (double)ih);
                    if (s > 1) s = 1;
                    sx = sy = s;
                    ox = vx + (vw - iw * s) / 2;
                    oy = vy + (vh - ih * s) / 2;
                    break;
                }
                default: // 填充 / 跨屏 / 平铺
                {
                    var s = Math.Max(vw / (double)iw, vh / (double)ih);
                    sx = sy = s;
                    ox = vx + (vw - iw * s) / 2;
                    oy = vy + (vh - ih * s) / 2;
                    break;
                }
            }

            double ix = (rectPhysical.X - ox) / sx;
            double iy = (rectPhysical.Y - oy) / sy;
            double iw2 = rectPhysical.Width / sx;
            double ih2 = rectPhysical.Height / sy;

            int x0 = (int)Math.Floor(ix);
            int y0 = (int)Math.Floor(iy);
            int x1 = (int)Math.Ceiling(ix + iw2);
            int y1 = (int)Math.Ceiling(iy + ih2);

            x0 = Math.Max(0, Math.Min(x0, iw - 2));
            y0 = Math.Max(0, Math.Min(y0, ih - 2));
            x1 = Math.Max(x0 + 2, Math.Min(x1, iw));
            y1 = Math.Max(y0 + 2, Math.Min(y1, ih));

            var crop = new CroppedBitmap(img, new Int32Rect(x0, y0, x1 - x0, y1 - y0));
            crop.Freeze();
            return crop;
        }
        catch
        {
            return null;
        }
    }
}
