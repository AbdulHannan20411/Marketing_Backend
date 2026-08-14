using Marketing.Application.DTOs.Payments;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Payments;

/// <summary>
/// Configuring where customers send money.
/// </summary>
/// <remarks>
/// Platform-only, and deliberately so: these fields decide where a customer's money goes. Changing
/// an account number is as consequential as approving a payment, and both are actions a tenant
/// administrator must never be able to take.
/// </remarks>
public interface IPaymentChannelAdminService
{
    /// <summary>Returns every channel, including the ones not currently offered.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<PaymentChannelDetails>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates a channel's account details and wording.</summary>
    /// <param name="channel">Channel to update.</param>
    /// <param name="draft">New values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PaymentChannelDetails> UpdateAsync(
        PaymentChannel channel,
        PaymentChannelDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces a channel's QR image.</summary>
    /// <param name="channel">Channel to update.</param>
    /// <param name="fileName">Name the image arrived under.</param>
    /// <param name="content">The image. The caller owns and disposes it.</param>
    /// <param name="sizeBytes">Size of the upload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PaymentChannelDetails> UploadQrAsync(
        PaymentChannel channel,
        string fileName,
        Stream content,
        long sizeBytes,
        CancellationToken cancellationToken = default);
}

/// <summary>Editable fields of a payment channel.</summary>
/// <param name="DisplayName">Name shown on the payment method card.</param>
/// <param name="AccountTitle">Name the account is held in.</param>
/// <param name="AccountNumber">Wallet number or bank account number.</param>
/// <param name="BankName">Bank, for transfers. Null or blank for a wallet.</param>
/// <param name="Instructions">Step-by-step wording shown under the account details.</param>
/// <param name="IsActive">Whether the channel is offered to customers.</param>
public sealed record PaymentChannelDraft(
    string DisplayName,
    string AccountTitle,
    string AccountNumber,
    string? BankName,
    IReadOnlyList<string> Instructions,
    bool IsActive);

/// <inheritdoc cref="IPaymentChannelAdminService" />
public sealed class PaymentChannelAdminService : IPaymentChannelAdminService
{
    /// <summary>Container QR images are stored under.</summary>
    private const string QrContainer = "payment-channel-qr";

    /// <summary>Largest QR image accepted.</summary>
    public const long MaxQrBytes = 2 * 1024 * 1024;

    private static readonly Dictionary<string, string> AcceptedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
    };

    private readonly IRepository<PaymentChannelSetting> _channels;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorage _storage;

    /// <summary>Initialises a new instance.</summary>
    public PaymentChannelAdminService(
        IRepository<PaymentChannelSetting> channels,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IFileStorage storage)
    {
        _channels = channels;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _storage = storage;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentChannelDetails>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _queries.ToListAsync(
            _channels.Query().OrderBy(channel => channel.SortOrder),
            cancellationToken);

        return [.. rows.Select(PaymentMapping.ToDetails)];
    }

    /// <inheritdoc />
    public async Task<PaymentChannelDetails> UpdateAsync(
        PaymentChannel channel,
        PaymentChannelDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var setting = await LoadAsync(channel, cancellationToken);

        if (string.IsNullOrWhiteSpace(draft.AccountNumber))
        {
            throw new ValidationException("accountNumber", "Enter the account or wallet number.");
        }

        if (string.IsNullOrWhiteSpace(draft.AccountTitle))
        {
            throw new ValidationException("accountTitle", "Enter the name the account is held in.");
        }

        // A channel with no account number is a channel that loses money. Refused rather than
        // hidden, because the failure would otherwise show up as a customer's missing payment.
        if (draft.IsActive && string.IsNullOrWhiteSpace(draft.AccountNumber))
        {
            throw new BusinessRuleException(
                "channel_not_configured",
                "Add the account details before offering this channel to customers.");
        }

        setting.DisplayName = draft.DisplayName.Trim();
        setting.AccountTitle = draft.AccountTitle.Trim();
        setting.AccountNumber = draft.AccountNumber.Trim();
        setting.BankName = string.IsNullOrWhiteSpace(draft.BankName) ? null : draft.BankName.Trim();
        setting.Instructions = [.. draft.Instructions.Where(line => !string.IsNullOrWhiteSpace(line))];
        setting.IsActive = draft.IsActive;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return PaymentMapping.ToDetails(setting);
    }

    /// <inheritdoc />
    public async Task<PaymentChannelDetails> UploadQrAsync(
        PaymentChannel channel,
        string fileName,
        Stream content,
        long sizeBytes,
        CancellationToken cancellationToken = default)
    {
        var setting = await LoadAsync(channel, cancellationToken);

        if (sizeBytes is <= 0 or > MaxQrBytes)
        {
            throw new ValidationException(
                "qr", $"Upload a QR image smaller than {MaxQrBytes / (1024 * 1024)} MB.");
        }

        if (!AcceptedTypes.TryGetValue(Path.GetExtension(fileName), out var contentType))
        {
            throw new ValidationException("qr", "Upload a PNG, JPG or WEBP image.");
        }

        var previous = setting.QrStorageKey;

        setting.QrStorageKey = await _storage.SaveAsync(QrContainer, fileName, content, cancellationToken);
        setting.QrContentType = contentType;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Removed only after the new key is committed. The other order risks deleting the live
        // image and then failing to save its replacement, leaving customers with no code at all.
        if (!string.IsNullOrEmpty(previous))
        {
            await _storage.DeleteAsync(previous, cancellationToken);
        }

        return PaymentMapping.ToDetails(setting);
    }

    private async Task<PaymentChannelSetting> LoadAsync(
        PaymentChannel channel,
        CancellationToken cancellationToken)
    {
        var setting = await _queries.FirstOrDefaultAsync(
            _channels.Query(asNoTracking: false).Where(candidate => candidate.Channel == channel),
            cancellationToken);

        return setting ?? throw new NotFoundException("Payment channel", channel.ToString());
    }
}
