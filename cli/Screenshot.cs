using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Fo4Cli;

// Captures the game window's client area from the screen (GDI). Works for windowed / borderless; exclusive
// fullscreen usually captures black, which is detected and reported. Optional HUD hiding via the `tm` console
// command, run through F4Exec before and after the capture.
static class Screenshot
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    public record Shot(string Path, int Width, int Height, bool Black, byte[] Bytes, string Mime);

    public static string DefaultDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "fo4-cli");

    public static Shot Take(string? outPath = null, int maxWidth = 0, bool hideHud = false, bool jpeg = false)
    {
        SetProcessDPIAware();
        var game = Process.GetProcessesByName("Fallout4").FirstOrDefault()
                   ?? throw new F4ExecUnavailable("Fallout 4 is not running");
        var hwnd = game.MainWindowHandle;
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Fallout 4 has no main window yet");

        if (hideHud) Pipe.Exec(["tm"]);
        try
        {
            ShowWindow(hwnd, 9); // SW_RESTORE
            SetForegroundWindow(hwnd);
            Thread.Sleep(hideHud ? 400 : 250); // let the window come to the front (and the HUD redraw)

            GetClientRect(hwnd, out var rc);
            var origin = new POINT();
            ClientToScreen(hwnd, ref origin);
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0) throw new InvalidOperationException("game window is minimised or has no client area");

            using var full = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(w, h));
            bool black = IsBlack(full);

            Bitmap img = full;
            if (maxWidth > 0 && w > maxWidth)
            {
                int nh = (int)Math.Round(h * (double)maxWidth / w);
                img = new Bitmap(maxWidth, nh, PixelFormat.Format24bppRgb);
                using var g2 = Graphics.FromImage(img);
                g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g2.DrawImage(full, 0, 0, maxWidth, nh);
            }
            try
            {
                outPath ??= System.IO.Path.Combine(DefaultDir, $"fo4_{DateTime.Now:yyyyMMdd_HHmmss}{(jpeg ? ".jpg" : ".png")}");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath))!);
                using var ms = new MemoryStream();
                if (jpeg || outPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                {
                    var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using var ps = new EncoderParameters(1);
                    ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                    img.Save(ms, enc, ps);
                    jpeg = true;
                }
                else img.Save(ms, ImageFormat.Png);
                var bytes = ms.ToArray();
                File.WriteAllBytes(outPath, bytes);
                return new Shot(System.IO.Path.GetFullPath(outPath), img.Width, img.Height, black, bytes, jpeg ? "image/jpeg" : "image/png");
            }
            finally { if (!ReferenceEquals(img, full)) img.Dispose(); }
        }
        finally
        {
            if (hideHud) Pipe.Exec(["tm"]);
        }
    }

    // Exclusive fullscreen (and a covered window on some drivers) gives an all-black capture.
    static bool IsBlack(Bitmap b)
    {
        int step = Math.Max(1, Math.Min(b.Width, b.Height) / 40);
        for (int y = 0; y < b.Height; y += step)
            for (int x = 0; x < b.Width; x += step)
            {
                var c = b.GetPixel(x, y);
                if (c.R > 8 || c.G > 8 || c.B > 8) return false;
            }
        return true;
    }

    public const string BlackHint =
        "capture is all black: the game is probably in exclusive fullscreen. Set it to borderless " +
        "(Fallout4Prefs.ini: bFull Screen=0, bBorderless=1) or windowed, or keep the window uncovered.";

    public static int Cmd(List<string> a)
    {
        string? outPath = null; int maxWidth = 0; bool nohud = false, jpeg = false;
        for (int i = 0; i < a.Count; i++)
        {
            switch (a[i])
            {
                case "-o": case "--out": outPath = a[++i]; break;
                case "--width": maxWidth = int.Parse(a[++i]); break;
                case "--nohud": nohud = true; break;
                case "--jpg": jpeg = true; break;
                default: throw new ArgumentException($"unknown screenshot option '{a[i]}'");
            }
        }
        var s = Take(outPath, maxWidth, nohud, jpeg);
        Console.WriteLine($"{s.Path} ({s.Width}x{s.Height})");
        if (s.Black) { Console.Error.WriteLine("warning: " + BlackHint); return 3; }
        return 0;
    }
}
