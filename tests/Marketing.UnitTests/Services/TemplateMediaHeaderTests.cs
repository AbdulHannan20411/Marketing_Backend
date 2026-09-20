using System.Net;
using AwesomeAssertions;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.Campaigns;
using Marketing.Application.Services.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>Recognising a header example file by its bytes.</summary>
public sealed class MediaSignatureTests
{
    [Fact]
    public void Each_accepted_format_is_recognised_by_its_bytes()
    {
        MediaSignature.Detect([0xFF, 0xD8, 0xFF, 0xE0]).Should().Be((MediaKind.Image, "image/jpeg"));
        MediaSignature.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]).Should().Be((MediaKind.Image, "image/png"));
        MediaSignature.Detect("%PDF-1.7"u8).Should().Be((MediaKind.Document, "application/pdf"));
        MediaSignature.Detect([0, 0, 0, 0x20, .. "ftypisom"u8]).Should().Be((MediaKind.Video, "video/mp4"));
        MediaSignature.Detect([0, 0, 0, 0x20, .. "ftyp3gp4"u8]).Should().Be((MediaKind.Video, "video/3gpp"));
        MediaSignature.Detect("GIF89a"u8).Should().BeNull();
    }
}

/// <summary>Uploading a header example file.</summary>
public sealed class TemplateHeaderSampleUploadTests
{
    private static TemplateHeaderSampleService CreateService() =>
        new(
            Substitute.For<IRepository<TemplateHeaderSample>>(),
            Substitute.For<IFileStorage>(),
            Substitute.For<IQueryExecutor>(),
            Substitute.For<IUnitOfWork>(),
            new StubTenantContext { TenantId = 5 },
            new FixedDateTimeProvider(DateTimeOffset.UnixEpoch),
            NullLogger<TemplateHeaderSampleService>.Instance);

    private static Task<TemplateHeaderSampleResponse> UploadAsync(string kind, byte[] bytes, long? size = null) =>
        CreateService().UploadAsync(
            new TemplateHeaderSampleUpload(kind, "file.bin", size ?? bytes.LongLength, new MemoryStream(bytes)),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task An_image_over_5_MB_is_too_large()
    {
        var upload = () => UploadAsync("image", [0xFF, 0xD8, 0xFF], size: 6 * 1024 * 1024);

        var refusal = (await upload.Should().ThrowAsync<RequestRejectedException>()).Which;

        refusal.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        refusal.ErrorCode.Should().Be("file_too_large");
    }

    [Fact]
    public async Task A_png_declared_as_video_is_caught_by_its_bytes()
    {
        var upload = () => UploadAsync("video", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        (await upload.Should().ThrowAsync<RequestRejectedException>()).Which.ErrorCode.Should().Be("kind_mismatch");
    }

    [Fact]
    public async Task A_format_meta_does_not_take_is_unsupported()
    {
        var upload = () => UploadAsync("image", "GIF89a......"u8.ToArray());

        var refusal = (await upload.Should().ThrowAsync<RequestRejectedException>()).Which;

        refusal.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        refusal.ErrorCode.Should().Be("unsupported_media_type");
    }
}

/// <summary>The image, video or document a campaign sends in every header.</summary>
public sealed class CampaignHeaderMediaTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    private readonly List<MediaAsset> _media = [];
    private readonly IWhatsAppGateway _gateway = Substitute.For<IWhatsAppGateway>();
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();

    private readonly MessageTemplate _imageTemplate = new()
    {
        Id = 3,
        Name = "summer_sale",
        BodyText = "The sale is on.",
        HeaderKind = TemplateHeaderKind.Image,
    };

    public CampaignHeaderMediaTests()
    {
        _media.Add(new MediaAsset
        {
            Id = 91, TenantId = 5, Kind = MediaKind.Image, MimeType = "image/jpeg", SizeBytes = 400_000,
            FileName = "sale.jpg", StoragePath = "a",
        });
        _media.Add(new MediaAsset
        {
            Id = 92, TenantId = 5, Kind = MediaKind.Video, MimeType = "video/mp4", SizeBytes = 400_000,
            FileName = "sale.mp4", StoragePath = "b",
        });

        _storage.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream([1, 2, 3]));
        _gateway.UploadMediaAsync("phone-1", Arg.Any<Stream>(), "sale.jpg", "image/jpeg", "token", Arg.Any<CancellationToken>())
            .Returns("meta-media-1");
    }

    private CampaignHeaderMedia CreateService()
    {
        var media = Substitute.For<IRepository<MediaAsset>>();
        media.Query(Arg.Any<bool>()).Returns(_ => _media.AsQueryable());

        return new CampaignHeaderMedia(
            media,
            Substitute.For<IRepository<MessageTemplate>>(),
            new InMemoryQueryExecutor(),
            _storage,
            _gateway,
            Substitute.For<IMediaService>(),
            new FixedDateTimeProvider(Now),
            NullLogger<CampaignHeaderMedia>.Instance);
    }

    private static Campaign SaleCampaign() =>
        new() { Id = 7, TenantId = 5, Name = "Sale", TemplateName = "summer_sale", HeaderMediaId = 91 };

    [Fact]
    public async Task An_image_template_needs_an_image()
    {
        var none = () => CreateService().ValidateAsync(_imageTemplate, null, TestContext.Current.CancellationToken);
        var video = () => CreateService().ValidateAsync(_imageTemplate, "med_92", TestContext.Current.CancellationToken);

        (await none.Should().ThrowAsync<RequestRejectedException>()).Which.ErrorCode.Should().Be("header_media_required");
        (await video.Should().ThrowAsync<RequestRejectedException>()).Which.ErrorCode.Should().Be("header_media_kind_mismatch");

        (await CreateService().ValidateAsync(_imageTemplate, "med_91", TestContext.Current.CancellationToken)).Should().Be(91);
    }

    [Fact]
    public async Task A_template_without_a_media_header_refuses_one()
    {
        var text = new MessageTemplate { Name = "plain", BodyText = "Hello there.", HeaderKind = TemplateHeaderKind.Text };

        var validate = () => CreateService().ValidateAsync(text, "med_91", TestContext.Current.CancellationToken);

        (await validate.Should().ThrowAsync<RequestRejectedException>()).Which.ErrorCode.Should().Be("header_media_not_allowed");
    }

    [Fact]
    public async Task Media_from_another_workspace_is_not_found()
    {
        _media.Add(new MediaAsset { Id = 93, TenantId = 99, Kind = MediaKind.Image, MimeType = "image/jpeg", FileName = "x.jpg" });

        // The tenant filter does this in production; the in-memory query stands in for it here by
        // not listing the row at all.
        _media.RemoveAt(_media.Count - 1);

        var validate = () => CreateService().ValidateAsync(_imageTemplate, "med_93", TestContext.Current.CancellationToken);

        await validate.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task The_file_is_uploaded_once_and_reused_for_the_run()
    {
        var campaign = SaleCampaign();
        var service = CreateService();

        var first = await service.PrepareForRunAsync(campaign, _imageTemplate, "phone-1", "token", TestContext.Current.CancellationToken);
        var second = await service.PrepareForRunAsync(campaign, _imageTemplate, "phone-1", "token", TestContext.Current.CancellationToken);

        first.Should().Be(new MetaHeaderMedia("image", "meta-media-1", null));
        second.Should().Be(first);
        await _gateway.Received(1).UploadMediaAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_copy_26_days_old_is_uploaded_again_first()
    {
        var campaign = SaleCampaign();
        campaign.HeaderMetaMediaId = "old-media";
        campaign.HeaderMetaMediaPhoneNumberId = "phone-1";
        campaign.HeaderMetaMediaUploadedAt = Now.AddDays(-26);

        var header = await CreateService().PrepareForRunAsync(campaign, _imageTemplate, "phone-1", "token", TestContext.Current.CancellationToken);

        header!.MetaMediaId.Should().Be("meta-media-1");
        campaign.HeaderMetaMediaUploadedAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_failed_upload_stops_the_run_rather_than_sending_without_it()
    {
        _gateway.UploadMediaAsync(
                Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new ExternalServiceException("MetaWhatsAppCloudApi", "boom"));

        var prepare = () => CreateService().PrepareForRunAsync(
            SaleCampaign(), _imageTemplate, "phone-1", "token", TestContext.Current.CancellationToken);

        (await prepare.Should().ThrowAsync<BusinessRuleException>()).Which.Message.Should().Contain("could not be sent to WhatsApp");
    }
}
