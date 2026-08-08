using System.Text.RegularExpressions;
using Marketing.Application.DTOs.Billing;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>Stored payment instruments and the invoice address for the resolved tenant.</summary>
public interface IBillingProfileService
{
    /// <summary>Returns every stored instrument, the default one first.</summary>
    public Task<IReadOnlyList<PaymentMethodResponse>> GetPaymentMethodsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Stores a processor token as a payment instrument.</summary>
    public Task<PaymentMethodResponse> AddPaymentMethodAsync(
        AddPaymentMethodRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Removes an instrument. The last one cannot be removed while a subscription renews.</summary>
    public Task RemovePaymentMethodAsync(string paymentMethodId, CancellationToken cancellationToken = default);

    /// <summary>Makes one instrument the one renewals and retries charge.</summary>
    public Task<PaymentMethodResponse> SetDefaultPaymentMethodAsync(
        string paymentMethodId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the invoice address, empty rather than absent when never set.</summary>
    public Task<BillingProfileResponse> GetProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates the invoice address.</summary>
    public Task<BillingProfileResponse> UpdateProfileAsync(
        UpdateBillingProfileRequest request,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IBillingProfileService" />
public sealed partial class BillingProfileService : IBillingProfileService
{
    private readonly IRepository<PaymentMethod> _methods;
    private readonly IRepository<BillingProfile> _profiles;
    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    public BillingProfileService(
        IRepository<PaymentMethod> methods,
        IRepository<BillingProfile> profiles,
        IRepository<TenantSubscription> subscriptions,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext)
    {
        _methods = methods;
        _profiles = profiles;
        _subscriptions = subscriptions;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentMethodResponse>> GetPaymentMethodsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _queries.ToListAsync(
            _methods.Query()
                .OrderByDescending(method => method.IsDefault)
                .ThenByDescending(method => method.CreatedOn),
            cancellationToken);

        return [.. rows.Select(Map)];
    }

    /// <inheritdoc />
    public async Task<PaymentMethodResponse> AddPaymentMethodAsync(
        AddPaymentMethodRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureNotCardData(request.ProviderToken);

        var tenantId = _tenantContext.RequireTenantId();
        var existing = await _queries.ToListAsync(_methods.Query(asNoTracking: false), cancellationToken);

        // The first instrument is always the default, whatever the caller asked for: a tenant with
        // one stored card and no default would silently fail its next renewal.
        var makeDefault = request.MakeDefault || existing.Count == 0;

        if (makeDefault)
        {
            foreach (var other in existing)
            {
                other.IsDefault = false;
            }
        }

        var method = new PaymentMethod
        {
            TenantId = tenantId,
            Kind = request.Kind,
            ProviderToken = request.ProviderToken.Trim(),
            IsDefault = makeDefault,
        };

        _methods.Add(method);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(method);
    }

    /// <inheritdoc />
    public async Task RemovePaymentMethodAsync(
        string paymentMethodId,
        CancellationToken cancellationToken = default)
    {
        var method = await LoadAsync(paymentMethodId, cancellationToken);

        var remaining = await _queries.CountAsync(
            _methods.Query().Where(other => other.Id != method.Id),
            cancellationToken);

        if (remaining == 0 && await HasRenewingSubscriptionAsync(cancellationToken))
        {
            // Removing the only instrument while renewal is on guarantees a failed charge and a
            // suspended workspace. Refusing is kinder than letting it lapse silently.
            throw new BusinessRuleException(
                "payment_method_required",
                "This is your only payment method and your subscription renews automatically. "
                + "Add another one first, or turn off auto-renew.");
        }

        _methods.Remove(method);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PaymentMethodResponse> SetDefaultPaymentMethodAsync(
        string paymentMethodId,
        CancellationToken cancellationToken = default)
    {
        var method = await LoadAsync(paymentMethodId, cancellationToken);

        var all = await _queries.ToListAsync(_methods.Query(asNoTracking: false), cancellationToken);

        foreach (var other in all)
        {
            other.IsDefault = other.Id == method.Id;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(method);
    }

    /// <inheritdoc />
    public async Task<BillingProfileResponse> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var profile = await _queries.FirstOrDefaultAsync(_profiles.Query(), cancellationToken);

        // Never a 404. "Not filled in yet" is a normal state the settings form renders empty from,
        // whereas a 404 would put the screen into an error state.
        return profile is null ? Empty() : Map(profile);
    }

    /// <inheritdoc />
    public async Task<BillingProfileResponse> UpdateProfileAsync(
        UpdateBillingProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await _queries.FirstOrDefaultAsync(
            _profiles.Query(asNoTracking: false),
            cancellationToken);

        if (profile is null)
        {
            profile = new BillingProfile { TenantId = _tenantContext.RequireTenantId() };

            _profiles.Add(profile);
        }

        profile.CompanyName = request.CompanyName?.Trim() ?? profile.CompanyName;
        profile.AddressLine1 = request.AddressLine1?.Trim() ?? profile.AddressLine1;
        profile.AddressLine2 = request.AddressLine2?.Trim() ?? profile.AddressLine2;
        profile.City = request.City?.Trim() ?? profile.City;
        profile.Region = request.Region?.Trim() ?? profile.Region;
        profile.PostalCode = request.PostalCode?.Trim() ?? profile.PostalCode;
        profile.TaxId = request.TaxId?.Trim() ?? profile.TaxId;

        if (request.Country is { Length: > 0 } country)
        {
            profile.Country = Countries.ToStorageCode(country)
                              ?? throw new ValidationException("country", $"\"{country}\" is not a country we recognise.");
        }

        if (request.BillingEmail is { Length: > 0 } email)
        {
            profile.BillingEmail = ContactRules.NormaliseEmail(email) ?? profile.BillingEmail;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(profile);
    }

    private async Task<PaymentMethod> LoadAsync(string paymentMethodId, CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.PaymentMethod, paymentMethodId, "payment method");

        return await _methods.GetForUpdateAsync(id, cancellationToken)
               ?? throw new NotFoundException("Payment method", paymentMethodId);
    }

    private Task<bool> HasRenewingSubscriptionAsync(CancellationToken cancellationToken) =>
        _queries.CountAsync(
            _subscriptions.Query().Where(subscription =>
                subscription.AutoRenew && subscription.Status == SubscriptionStatus.Active),
            cancellationToken).ContinueWith(task => task.Result > 0, TaskScheduler.Default);

    /// <summary>
    /// Refuses anything that looks like a real card number.
    /// </summary>
    /// <remarks>
    /// A backstop, not the mechanism. Tokenisation happens in the browser; this exists so that a
    /// mis-wired client sends a rejected request rather than quietly putting a PAN in the database
    /// and this platform into PCI scope.
    /// </remarks>
    private static void EnsureNotCardData(string token)
    {
        var digits = new string([.. token.Where(char.IsAsciiDigit)]);

        if (digits.Length is >= 13 and <= 19 && CardLikePattern().IsMatch(token.Trim()))
        {
            throw new ValidationException(
                "providerToken",
                "Send the processor's token, not card details. Card numbers are never accepted here.");
        }
    }

    private static PaymentMethodResponse Map(PaymentMethod method) =>
        new(
            PublicId.From(PublicId.PaymentMethod, method.Id),
            method.Kind,
            method.Brand,
            method.Last4,
            method.ExpiryMonth,
            method.ExpiryYear,
            method.IsDefault,
            method.CreatedOn);

    private static BillingProfileResponse Map(BillingProfile profile) =>
        new(
            profile.CompanyName,
            profile.AddressLine1,
            profile.AddressLine2,
            profile.City,
            profile.Region,
            profile.PostalCode,
            Countries.ToDisplayName(profile.Country),
            profile.TaxId,
            profile.BillingEmail);

    private static BillingProfileResponse Empty() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty);

    [GeneratedRegex(@"^[\d\s-]{13,25}$")]
    private static partial Regex CardLikePattern();
}
