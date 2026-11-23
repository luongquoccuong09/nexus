using SkiaSharp;

namespace Nexus.Web.Modules.People;

/// <summary>
/// Server-side avatar image sanitizer and re-encoder (M1.11) using MIT-licensed SkiaSharp.
/// Validates actual image format, blocks decompression bombs, center-crops to 256x256,
/// and re-encodes as clean PNG (stripping all EXIF/XMP/IPTC/ICC metadata).
/// </summary>
public static class AvatarProcessor
{
    public const int MaxUploadBytes = 2 * 1024 * 1024;
    public const int MaxSourceDimensionPx = 8192;
    public const int AvatarDimensionPx = 256;

    private static readonly HashSet<SKEncodedImageFormat> AllowedFormats =
    [
        SKEncodedImageFormat.Png,
        SKEncodedImageFormat.Jpeg,
        SKEncodedImageFormat.Webp,
        SKEncodedImageFormat.Gif
    ];

    public static async Task<MemoryStream> ReencodeToPngAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxUploadBytes)
            {
                throw new InvalidOperationException($"Avatar exceeds maximum upload size of {MaxUploadBytes / (1024 * 1024)} MB.");
            }
            buffer.Write(chunk, 0, read);
        }

        if (total == 0)
        {
            throw new InvalidOperationException("Avatar image payload is empty.");
        }

        buffer.Position = 0;

        using var codec = SKCodec.Create(buffer);
        if (codec is null || !AllowedFormats.Contains(codec.EncodedFormat))
        {
            throw new InvalidOperationException("Unsupported or corrupted image file. Upload a valid PNG, JPEG, WebP, or GIF image.");
        }

        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            codec.Info.Width > MaxSourceDimensionPx || codec.Info.Height > MaxSourceDimensionPx)
        {
            throw new InvalidOperationException("Avatar image dimensions are outside allowed bounds.");
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
        {
            throw new InvalidOperationException("Unable to decode avatar image.");
        }

        var cropSide = Math.Min(decoded.Width, decoded.Height);
        var cropX = (decoded.Width - cropSide) / 2f;
        var cropY = (decoded.Height - cropSide) / 2f;
        var srcRect = new SKRect(cropX, cropY, cropX + cropSide, cropY + cropSide);
        var dstRect = new SKRect(0, 0, AvatarDimensionPx, AvatarDimensionPx);

        using var target = new SKBitmap(new SKImageInfo(AvatarDimensionPx, AvatarDimensionPx, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        using (var sourceImage = SKImage.FromBitmap(decoded))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(sourceImage, srcRect, dstRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        using var cleanImage = SKImage.FromBitmap(target);
        using var pngData = cleanImage.Encode(SKEncodedImageFormat.Png, 100);
        if (pngData is null)
        {
            throw new InvalidOperationException("Failed to re-encode avatar image.");
        }

        var output = new MemoryStream();
        pngData.SaveTo(output);
        output.Position = 0;
        return output;
    }
}
