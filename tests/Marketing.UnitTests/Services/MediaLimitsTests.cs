using AwesomeAssertions;
using Marketing.Application.DTOs.WhatsApp;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>What WhatsApp accepts for each kind of file.</summary>
/// <remarks>
/// Meta enforces these itself, but only after the bytes have crossed the network - a 100 MB document
/// refused at the far end is a minute of somebody's time and a file this platform stored for nothing.
/// </remarks>
public sealed class MediaLimitsTests
{
    [Theory]
    [InlineData(MediaKind.Image, "image/jpeg", true)]
    [InlineData(MediaKind.Image, "image/gif", false)]
    [InlineData(MediaKind.Video, "video/mp4", true)]
    [InlineData(MediaKind.Video, "video/quicktime", false)]
    [InlineData(MediaKind.Document, "application/pdf", true)]
    [InlineData(MediaKind.Audio, "audio/ogg", true)]
    [InlineData(MediaKind.Audio, "image/png", false)]
    public void Only_the_media_types_whatsapp_supports_are_accepted(MediaKind kind, string mimeType, bool expected)
    {
        MediaLimits.Accepts(kind, mimeType).Should().Be(expected);
    }

    [Fact]
    public void Browser_parameters_on_a_media_type_are_ignored()
    {
        // Browsers send "audio/ogg; codecs=opus" for a recorded voice note, and a literal comparison
        // would refuse a file WhatsApp accepts.
        MediaLimits.Accepts(MediaKind.Audio, "audio/ogg; codecs=opus").Should().BeTrue();
    }

    [Theory]
    [InlineData(MediaKind.Image, 5)]
    [InlineData(MediaKind.Video, 16)]
    [InlineData(MediaKind.Document, 100)]
    [InlineData(MediaKind.Audio, 16)]
    public void Each_kind_carries_metas_size_ceiling(MediaKind kind, int megabytes)
    {
        MediaLimits.MaximumBytesFor(kind).Should().Be(megabytes * 1024L * 1024L);
    }

    [Theory]
    [InlineData("image/png", MediaKind.Image)]
    [InlineData("video/3gpp", MediaKind.Video)]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", MediaKind.Document)]
    [InlineData("audio/mpeg", MediaKind.Audio)]
    public void An_inbound_files_kind_is_read_from_its_media_type(string mimeType, MediaKind expected)
    {
        // Meta describes an inbound file by media type alone, so this is how a download is filed.
        MediaLimits.KindFor(mimeType).Should().Be(expected);
    }

    [Fact]
    public void An_unsupported_type_belongs_to_no_kind()
    {
        MediaLimits.KindFor("application/x-msdownload").Should().BeNull();
        MediaLimits.KindFor(null).Should().BeNull();
    }
}
