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

    /// Copies a picture the app keeps for its own lifetime, such as a markdown image.
    public static async Task CopyAsync(Visual owner, IImage? image) {
        if (image is null || TopLevel.GetTopLevel(owner)?.Clipboard is not { } clipboard) return;
        try {
            if (image is Bitmap bitmap) await SetAsync(clipboard, bitmap, owned: false);
            else if (Rasterise(image) is { } rendered) await SetAsync(clipboard, rendered, owned: true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: copy image failed: {ex.Message}");
        }
    }

    /// Copies a local image file at full size rather than the downscaled preview of it. The preview
    /// stands in when the file cannot be read or decoded, drawn into a bitmap of this class's own;
    /// the caller keeps the preview alive until the returned task completes.
    public static async Task CopyFileAsync(Visual owner, string path, IImage? preview) {
        if (TopLevel.GetTopLevel(owner)?.Clipboard is not { } clipboard) return;
        try {
            var bitmap = path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? null : await DecodeAsync(path);
            bitmap ??= preview is null ? null : Rasterise(preview);
            if (bitmap is not null) await SetAsync(clipboard, bitmap, owned: true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: copy image failed: {ex.Message}");
        }
    }

    static Task<Bitmap?> DecodeAsync(string path) => Task.Run(() => {
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var buffer = ToolPreviewImages.ReadBounded(stream);
            return buffer is null ? null : new Bitmap(buffer);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: reading image for copy failed: {ex.Message}");
            return null;
        }
    });

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
