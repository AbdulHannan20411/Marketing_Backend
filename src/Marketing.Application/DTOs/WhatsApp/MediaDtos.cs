using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.WhatsApp;

/// <summary>A stored file, as the client refers to it.</summary>
/// <param name="Id">Public identifier.</param>
/// <param name="Kind">What the file is.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="MimeType">Media type.</param>
/// <param name="SizeBytes">Size in bytes.</param>
/// <param name="Url">
/// Where to fetch the bytes from this platform. Never Meta's own URL: that one expires after 30
/// days and carries no tenant check.
/// </param>
/// <param name="UploadedAt">When it was stored.</param>
public sealed record MediaAssetResponse(
    string Id,
    MediaKind Kind,
    string FileName,
    string MimeType,
    long SizeBytes,
    string Url,
    DateTimeOffset UploadedAt);

/// <summary>A file on its way in, from an agent or a campaign.</summary>
/// <param name="Kind">What the caller says it is; checked against the media type.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="MimeType">Media type as declared by the browser.</param>
/// <param name="SizeBytes">Size in bytes, as declared before the bytes are read.</param>
/// <param name="Content">The bytes.</param>
public sealed record MediaUploadCommand(
    MediaKind Kind,
    string FileName,
    string MimeType,
    long SizeBytes,
    Stream Content);

/// <summary>A file on its way out, to be streamed to the caller.</summary>
/// <param name="Content">The bytes.</param>
/// <param name="ContentType">Media type to serve them as.</param>
/// <param name="FileName">Original file name.</param>
public sealed record MediaDownload(Stream Content, string ContentType, string FileName);

/// <summary>What WhatsApp accepts for each kind of file.</summary>
/// <remarks>
/// Meta enforces these itself and refuses anything outside them, but it does so after the upload has
/// crossed the network. Checking first turns a slow, opaque failure into an immediate one, and keeps
/// files this platform would have to store but could never send out of its storage entirely.
/// </remarks>
public static class MediaLimits
{
    private const long Kilobyte = 1024;
    private const long Megabyte = 1024 * Kilobyte;

    /// <summary>Accepted media types and size ceiling for each kind.</summary>
    private static readonly Dictionary<MediaKind, (string[] MimeTypes, long MaximumBytes)> Rules = new()
    {
        [MediaKind.Image] = (["image/jpeg", "image/png"], 5 * Megabyte),
        [MediaKind.Video] = (["video/mp4", "video/3gpp"], 16 * Megabyte),
        [MediaKind.Document] =
        ([
            "application/pdf",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ], 100 * Megabyte),
        [MediaKind.Audio] = (["audio/aac", "audio/mpeg", "audio/mp4", "audio/ogg"], 16 * Megabyte),
    };

    /// <summary>Largest file any kind allows, for the request body limit.</summary>
    public const long LargestUploadBytes = 100 * Megabyte;

    /// <summary>Media types accepted for a kind.</summary>
    /// <param name="kind">Kind of file.</param>
    public static IReadOnlyList<string> MimeTypesFor(MediaKind kind) =>
        Rules.TryGetValue(kind, out var rule) ? rule.MimeTypes : [];

    /// <summary>Size ceiling for a kind, in bytes.</summary>
    /// <param name="kind">Kind of file.</param>
    public static long MaximumBytesFor(MediaKind kind) =>
        Rules.TryGetValue(kind, out var rule) ? rule.MaximumBytes : 0;

    /// <summary>Whether a media type is one this kind accepts.</summary>
    /// <param name="kind">Kind of file.</param>
    /// <param name="mimeType">Media type to check.</param>
    public static bool Accepts(MediaKind kind, string? mimeType) =>
        mimeType is { Length: > 0 }
        && MimeTypesFor(kind).Contains(Bare(mimeType), StringComparer.OrdinalIgnoreCase);

    /// <summary>The kind a media type belongs to, or null when WhatsApp accepts none of it.</summary>
    /// <param name="mimeType">Media type, as reported by Meta or a browser.</param>
    public static MediaKind? KindFor(string? mimeType)
    {
        if (mimeType is not { Length: > 0 })
        {
            return null;
        }

        foreach (var (kind, rule) in Rules)
        {
            if (rule.MimeTypes.Contains(Bare(mimeType), StringComparer.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>Drops the parameters browsers append, such as <c>; codecs=opus</c>.</summary>
    private static string Bare(string mimeType)
    {
        var separator = mimeType.IndexOf(';', StringComparison.Ordinal);

        return (separator < 0 ? mimeType : mimeType[..separator]).Trim();
    }
}
