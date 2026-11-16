using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DesktopNote.Services;

/// <summary>
/// 读取桌面背景并按窗口物理像素位置裁剪，供界面做模糊背景（伪毛玻璃）。
///
/// 主路径：GDI BitBlt 直接从桌面背景窗口（WorkerW / Progman）截图。
///   - 坐标系 = 物理屏幕坐标，多屏无偏移。
///   - 兼容 Wallpaper Engine、Lively 等第三方壁纸引擎。
///
/// 降级路径：截图失败时退到读注册表壁纸文件（原有逻辑），
///   多屏下可能有轻微位移，但至少不会全黑。
/// </summary>
public static class Backdrop
{
    // ── 降级路径用的状态 ──────────────────────────────────────────────
    private static BitmapSource? _fallbackImage;
    private static int _style = 10;
    private static bool _tile;
    private static bool _fallbackLoaded;

    // ── 壁纸背景窗口句柄缓存 ──────────────────────────────────────────
    private static IntPtr _wallpaperHwnd = IntPtr.Zero;
    private static DateTime _hwndRefreshedAt = DateTime.MinValue;

    public static void Reload()
    {
        // 强制下次截图时重新查找壁纸窗口
        _wallpaperHwnd = IntPtr.Zero;
        _hwndRefreshedAt = DateTime.MinValue;

        // 同时刷新降级路径的壁纸
        _fallbackLoaded = false;
        _fallbackImage = null;
        LoadFallback();
    }

    // ── 公共入口 ──────────────────────────────────────────────────────

    /// <summary>
    /// 取窗口区域壁纸并做软件高斯近似模糊。
    /// rectPhysical 是物理屏幕像素坐标（与 GetWindowRect 返回值一致）。
    /// </summary>
    public static BitmapSource? BlurredCrop(Int32Rect rectPhysical, double blurRadius)
    {
        var crop = Crop(rectPhysical);
        if (crop == null) return null;

        int radius = (int)Math.Round(blurRadius);
        return radius < 1 ? crop : Blur(crop, radius);
    }

    // ── 裁剪 ──────────────────────────────────────────────────────────

    /// <summary>按物理像素矩形裁剪壁纸；取不到时返回 null。</summary>
    public static BitmapSource? Crop(Int32Rect rectPhysical)
    {
        // 1. 优先用 GDI 截桌面背景窗口
        var gdi = CropFromDesktop(rectPhysical);
        if (gdi != null) return gdi;

        // 2. 降级：读注册表壁纸文件
        return CropFromFile(rectPhysical);
    }

    // ── GDI 截图路径 ──────────────────────────────────────────────────

    private static BitmapSource? CropFromDesktop(Int32Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return null;

        // 壁纸窗口句柄每 30 秒刷新一次（WE 重新加载壁纸时句柄可能变）
        if (_wallpaperHwnd == IntPtr.Zero || (DateTime.Now - _hwndRefreshedAt).TotalSeconds > 30)
        {
            _wallpaperHwnd = NativeMethods.FindWallpaperWindow();
            _hwndRefreshedAt = DateTime.Now;
        }

        IntPtr hwnd = _wallpaperHwnd;
        IntPtr hdcSrc = IntPtr.Zero;
        IntPtr hdcDst = IntPtr.Zero;
        IntPtr hBmp = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;

        try
        {
            hdcSrc = NativeMethods.GetDC(hwnd);
            if (hdcSrc == IntPtr.Zero) return null;

            hdcDst = NativeMethods.CreateCompatibleDC(hdcSrc);
            if (hdcDst == IntPtr.Zero) return null;

            hBmp = NativeMethods.CreateCompatibleBitmap(hdcSrc, r.Width, r.Height);
            if (hBmp == IntPtr.Zero) return null;

            hOld = NativeMethods.SelectObject(hdcDst, hBmp);

            bool ok = NativeMethods.BitBlt(hdcDst, 0, 0, r.Width, r.Height,
                                            hdcSrc, r.X, r.Y, NativeMethods.SRCCOPY);
            if (!ok) return null;

            // HBITMAP → BitmapSource（走 interop）
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBmp, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hOld != IntPtr.Zero && hdcDst != IntPtr.Zero)
                NativeMethods.SelectObject(hdcDst, hOld);
            if (hBmp != IntPtr.Zero) NativeMethods.DeleteObject(hBmp);
            if (hdcDst != IntPtr.Zero) NativeMethods.DeleteDC(hdcDst);
            if (hdcSrc != IntPtr.Zero) NativeMethods.ReleaseDC(hwnd, hdcSrc);
        }
    }

    // ── 降级：读注册表壁纸文件（单屏较准，多屏可能有偏） ─────────────

    private static void LoadFallback()
    {
        _fallbackLoaded = true;
        _fallbackImage = null;

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
                var transcoded = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
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
            _fallbackImage = bmp;
        }
        catch
        {
            _fallbackImage = null;
        }
    }

    private static BitmapSource? CropFromFile(Int32Rect rectPhysical)
    {
        if (!_fallbackLoaded) LoadFallback();
        var img = _fallbackImage;
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

    // ── 高斯模糊（三次盒式模糊近似） ─────────────────────────────────

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
                sumB += src[xi]; sumG += src[xi + 1]; sumR += src[xi + 2];
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
                sumB += src[yi]; sumG += src[yi + 1]; sumR += src[yi + 2];
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
}
