using System.Text;
using Nexus.Web.Infrastructure.Storage;
using SkiaSharp;

namespace Nexus.Web.Modules.Files;

public sealed record InspectedFilePayload(
    string SafeFileName,
    string CanonicalContentType,
    byte[] ProcessedBytes,
    byte[]? ThumbnailPngBytes,
    int? ImageWidth,
    int? ImageHeight);

/// <summary>
/// Magic-byte file type validator, allowlist enforcer, and SkiaSharp image re-encoder + thumbnail generator (M3.2, M3.4).
/// Never trusts client-supplied Content-Type headers.
/// </summary>
public static class FileContentInspector
{
    public const int MaxThumbnailEdgePx = 400;
    public const int MaxImageDimensionPx = 4096;

    private static readonly Dictionary<string, string> ZipExtensionToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".zip"] = "application/zip"
    };

    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".ps1", ".sh", ".msi", ".vbs", ".js", ".mjs",
        ".html", ".htm", ".xhtml", ".svg", ".xml", ".php", ".jsp", ".asp", ".aspx", ".com", ".scr"
    };

    public static bool TryInspectAndProcess(
        byte[] rawBytes,
        string originalFileName,
        long maxFileSizeBytes,
        out InspectedFilePayload? payload,
        out string error)
    {
        payload = null;
        error = string.Empty;

        if (rawBytes.Length == 0)
        {
            error = "Uploaded file is empty.";
            return false;
        }

        if (rawBytes.Length > maxFileSizeBytes)
        {
            error = $"File exceeds maximum allowed size of {maxFileSizeBytes / (1024 * 1024)} MB.";
            return false;
        }

        var safeName = StorageKeyHelper.SanitizeFileName(originalFileName);
        var ext = Path.GetExtension(safeName);

        if (!string.IsNullOrEmpty(ext) && ForbiddenExtensions.Contains(ext))
        {
            error = $"File extension '{ext}' is not allowed.";
            return false;
        }

        // Reject known executable headers immediately (PE/MZ, ELF, Mach-O)
        if (HasExecutableSignature(rawBytes))
        {
            error = "Executable files are forbidden.";
            return false;
        }

        // 1. Raster images (PNG, JPEG, GIF, WebP) — verify magic bytes + re-encode via SkiaSharp (strips EXIF) + thumbnail
        var detectedImageMime = DetectImageMagicBytes(rawBytes);
        if (detectedImageMime is not null)
        {
            return TryProcessImage(rawBytes, safeName, detectedImageMime, out payload, out error);
        }

        // 2. PDF (%PDF-)
        if (rawBytes.Length >= 5 &&
            rawBytes[0] == 0x25 && rawBytes[1] == 0x50 && rawBytes[2] == 0x44 && rawBytes[3] == 0x46 && rawBytes[4] == 0x2D)
        {
            if (!string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                safeName = Path.ChangeExtension(safeName, ".pdf");
            }
            payload = new InspectedFilePayload(safeName, "application/pdf", rawBytes, null, null, null);
            return true;
        }

        // 3. ZIP / OpenXML documents (.docx, .xlsx, .pptx, .zip)
        if (rawBytes.Length >= 4 &&
            rawBytes[0] == 0x50 && rawBytes[1] == 0x4B &&
            ((rawBytes[2] == 0x03 && rawBytes[3] == 0x04) || (rawBytes[2] == 0x05 && rawBytes[3] == 0x06)))
        {
            if (string.IsNullOrEmpty(ext) || !ZipExtensionToMime.TryGetValue(ext, out var mime))
            {
                error = "ZIP container must use an allowlisted extension (.docx, .xlsx, .pptx, or .zip).";
                return false;
            }
            payload = new InspectedFilePayload(safeName, mime, rawBytes, null, null, null);
            return true;
        }

        // 4. Plain text / CSV (strict UTF-8, no NUL bytes, no HTML/SVG/script payloads)
        if (string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsSafePlainText(rawBytes))
            {
                error = "Text/CSV file contains binary or disallowed markup content.";
                return false;
            }
            var mime = string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase) ? "text/csv" : "text/plain";
            payload = new InspectedFilePayload(safeName, mime, rawBytes, null, null, null);
            return true;
        }

        error = "Unsupported or unrecognized file type. Allowed: PNG, JPEG, WebP, GIF, PDF, TXT, CSV, DOCX, XLSX, PPTX, ZIP.";
        return false;
    }

    private static bool TryProcessImage(
        byte[] rawBytes,
        string safeName,
        string detectedMime,
        out InspectedFilePayload? payload,
        out string error)
    {
        payload = null;
        error = string.Empty;

        using var decoded = SKBitmap.Decode(rawBytes);
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
        {
            error = "Unable to decode image payload.";
            return false;
        }

        if (decoded.Width > MaxImageDimensionPx || decoded.Height > MaxImageDimensionPx)
        {
            error = $"Image dimensions exceed maximum allowed {MaxImageDimensionPx}x{MaxImageDimensionPx} pixels.";
            return false;
        }

        // Re-encode full image to strip EXIF/GPS/COM metadata (M3.4)
        var useJpeg = string.Equals(detectedMime, "image/jpeg", StringComparison.Ordinal);
        using var fullImage = SKImage.FromBitmap(decoded);
        using var reencoded = fullImage.Encode(useJpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, useJpeg ? 90 : 100);
        if (reencoded is null)
        {
            error = "Failed to re-encode image.";
            return false;
        }

        var canonicalMime = useJpeg ? "image/jpeg" : "image/png";
        var targetExt = useJpeg ? ".jpg" : ".png";
        var ext = Path.GetExtension(safeName);
        if (string.IsNullOrEmpty(ext) || ForbiddenExtensions.Contains(ext))
        {
            safeName = Path.ChangeExtension(safeName, targetExt);
        }

        // Generate thumbnail <= 400px wide/tall (UX-08)
        var thumbBytes = CreateThumbnailPng(decoded);

        payload = new InspectedFilePayload(
            safeName,
            canonicalMime,
            reencoded.ToArray(),
            thumbBytes,
            decoded.Width,
            decoded.Height);
        return true;
    }

    private static byte[] CreateThumbnailPng(SKBitmap source)
    {
        var maxEdge = Math.Max(source.Width, source.Height);
        if (maxEdge <= MaxThumbnailEdgePx)
        {
            using var img = SKImage.FromBitmap(source);
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 90);
            return encoded.ToArray();
        }

        var scale = (double)MaxThumbnailEdgePx / maxEdge;
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var resized = source.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), SKSamplingOptions.Default);
        if (resized is null)
        {
            using var fallback = SKImage.FromBitmap(source);
            using var enc = fallback.Encode(SKEncodedImageFormat.Png, 90);
            return enc.ToArray();
        }

        using var thumbImg = SKImage.FromBitmap(resized);
        using var thumbEncoded = thumbImg.Encode(SKEncodedImageFormat.Png, 90);
        return thumbEncoded.ToArray();
    }

    private static string? DetectImageMagicBytes(byte[] b)
    {
        if (b.Length >= 8 &&
            b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 &&
            b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
        {
            return "image/png";
        }

        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (b.Length >= 6 &&
            b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38 &&
            (b[4] == 0x37 || b[4] == 0x39) && b[5] == 0x61)
        {
            return "image/gif";
        }

        if (b.Length >= 12 &&
            b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50)
        {
            return "image/webp";
        }

        return null;
    }

    private static bool HasExecutableSignature(byte[] b)
    {
        // Windows PE (MZ)
        if (b.Length >= 2 && b[0] == 0x4D && b[1] == 0x5A) return true;
        // Linux ELF
        if (b.Length >= 4 && b[0] == 0x7F && b[1] == 0x45 && b[2] == 0x4C && b[3] == 0x46) return true;
        // Mach-O
        if (b.Length >= 4 &&
            ((b[0] == 0xFE && b[1] == 0xED && b[2] == 0xFA && (b[3] == 0xCE || b[3] == 0xCF)) ||
             (b[0] == 0xCF && b[1] == 0xFA && b[2] == 0xED && b[3] == 0xFE)))
        {
            return true;
        }
        return false;
    }

    private static bool IsSafePlainText(byte[] rawBytes)
    {
        if (Array.IndexOf(rawBytes, (byte)0) >= 0) return false;
        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var text = utf8.GetString(rawBytes);
            var head = text.AsSpan(0, Math.Min(text.Length, 256)).TrimStart();
            if (head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
                head.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
                head.StartsWith("<script", StringComparison.OrdinalIgnoreCase) ||
                head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
