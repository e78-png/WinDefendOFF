using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// 產生 WinDefendOFF 的應用程式圖示 (.ico) 與預覽圖 (.png)。
// 圖案：藍色盾牌 + 白色電源符號，象徵「Defender 開關」。
var outputDir = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "assets");

Directory.CreateDirectory(outputDir);

static Bitmap RenderIcon(int size)
{
    var bmp = new Bitmap(size, size);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.Transparent);

    float w = size * 0.62f;
    float h = size * 0.72f;
    float x = (size - w) / 2f;
    float y = size * 0.10f;

    var points = new[]
    {
        new PointF(x + w / 2f, y),
        new PointF(x + w, y + h * 0.20f),
        new PointF(x + w, y + h * 0.55f),
        new PointF(x + w / 2f, y + h),
        new PointF(x, y + h * 0.55f),
        new PointF(x, y + h * 0.20f),
    };

    using var path = new GraphicsPath();
    path.AddPolygon(points);

    using var brush = new LinearGradientBrush(
        new PointF(x, y),
        new PointF(x + w, y + h),
        Color.FromArgb(56, 118, 220),
        Color.FromArgb(28, 62, 130));
    g.FillPath(brush, path);

    float cx = size / 2f;
    float cy = y + h * 0.46f;
    float r = size * 0.20f;

    using var pen = new Pen(Color.White, Math.Max(1f, size * 0.075f))
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
    };
    g.DrawArc(pen, cx - r, cy - r, r * 2f, r * 2f, -60, 300);
    g.DrawLine(pen, cx, cy - r * 1.15f, cx, cy + r * 0.15f);

    return bmp;
}

static void WriteIco(string path, int[] sizes)
{
    var images = sizes.Select(RenderIcon).ToArray();

    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);

    bw.Write((ushort)0);          // reserved
    bw.Write((ushort)1);          // type: icon
    bw.Write((ushort)sizes.Length);

    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        using var ms = new MemoryStream();
        images[i].Save(ms, ImageFormat.Png);
        var bytes = ms.ToArray();

        byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
        bw.Write(dim);
        bw.Write(dim);
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write((uint)bytes.Length);
        bw.Write((uint)offset);
        offset += bytes.Length;
    }

    foreach (var img in images)
    {
        using var ms = new MemoryStream();
        img.Save(ms, ImageFormat.Png);
        bw.Write(ms.ToArray());
    }
}

var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };

var icoPath = Path.Combine(outputDir, "WinDefendOFF.ico");
WriteIco(icoPath, sizes);
Console.WriteLine($"ICO: {icoPath} ({new FileInfo(icoPath).Length} bytes)");

using (var png = RenderIcon(256))
{
    var pngPath = Path.Combine(outputDir, "WinDefendOFF.png");
    png.Save(pngPath, ImageFormat.Png);
    Console.WriteLine($"PNG: {pngPath}");
}
