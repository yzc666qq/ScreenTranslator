using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Services;

public sealed class GdiScreenCaptureService : IScreenCaptureService
{
    public Task<ScreenCaptureFrame> CaptureAsync(
        ScreenRegion region,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (region.IsEmpty)
        {
            throw new ArgumentException("截图区域不能为空。", nameof(region));
        }

        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                region.X,
                region.Y,
                0,
                0,
                new Size(region.Width, region.Height),
                CopyPixelOperation.SourceCopy);
        }

        var rectangle = new Rectangle(0, 0, region.Width, region.Height);
        var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            var stride = region.Width * 4;
            var pixels = new byte[stride * region.Height];

            if (bitmapData.Stride == stride)
            {
                Marshal.Copy(bitmapData.Scan0, pixels, 0, pixels.Length);
            }
            else
            {
                for (var row = 0; row < region.Height; row++)
                {
                    var sourceRow = IntPtr.Add(bitmapData.Scan0, row * bitmapData.Stride);
                    Marshal.Copy(sourceRow, pixels, row * stride, stride);
                }
            }

            return Task.FromResult(new ScreenCaptureFrame(region.Width, region.Height, stride, pixels));
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }
}
