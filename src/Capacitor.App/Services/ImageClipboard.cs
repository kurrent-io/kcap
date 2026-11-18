using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Capacitor.App.Services;

/// Puts a picture the chat shows on the clipboard as a bitmap. A vector image is rasterised at
/// its natural size, since the clipboard carries pixels.
public static class ImageClipboard {
    // The platform clipboard may read the bitmap after the set returns, so the last one copied stays
    // referenced until the next copy replaces it; only one this class made is disposed then.
    static (Bitmap Bitmap, bool Owned)? _held;
    const double MaxEdge = 4096;

    public static async Task CopyAsync(Visual owner, IImage? image) {
        if (image is null || TopLevel.GetTopLevel(owner)?.Clipboard is not { } clipboard) return;
        try {
            if (image is Bitmap bitmap) {
                await SetAsync(clipboard, bitmap, owned: false);
                return;
            }
            if (Rasterise(image) is { } rendered) await SetAsync(clipboard, rendered, owned: true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: copy image failed: {ex.Message}");
        }
    }

    /// Copies a local image file at full size rather than the downscaled preview of it; an SVG,
    /// which has no pixels of its own, falls back to the preview's own image.
    public static async Task CopyFileAsync(Visual owner, string path, IImage? preview) {
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) {
            await CopyAsync(owner, preview);
            return;
        }
        if (TopLevel.GetTopLevel(owner)?.Clipboard is not { } clipboard) return;
        try {
            var bitmap = await Task.Run(() => {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var buffer = ToolPreviewImages.ReadBounded(stream);
                return buffer is null ? null : new Bitmap(buffer);
            });
            if (bitmap is null) {
                await CopyAsync(owner, preview);
                return;
            }
            await SetAsync(clipboard, bitmap, owned: true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: copy image failed: {ex.Message}");
        }
    }

    internal static RenderTargetBitmap? Rasterise(IImage image) {
        var natural = image.Size;
        if (natural.Width < 1 || natural.Height < 1) return null;
        // An SVG's declared size is unbounded; the cap keeps one from allocating gigabytes.
        var scale = Math.Min(1, MaxEdge / Math.Max(natural.Width, natural.Height));
        var size = new Size(natural.Width * scale, natural.Height * scale);
        var rendered = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)));
        using (var context = rendered.CreateDrawingContext())
            context.DrawImage(image, new Rect(size));
        return rendered;
    }

    static async Task SetAsync(IClipboard clipboard, Bitmap bitmap, bool owned) {
        try {
            await clipboard.SetBitmapAsync(bitmap);
        } catch {
            if (owned) bitmap.Dispose();
            throw;
        }
        var previous = _held;
        _held = (bitmap, owned);
        if (previous is { Owned: true } held && !ReferenceEquals(held.Bitmap, bitmap)) held.Bitmap.Dispose();
    }
}
