using Marketing.Application.DTOs.Payments;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Common.Constants;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Payments;

/// <summary>
/// Translations between stored payment state and the shapes the client is promised.
/// </summary>
/// <remarks>
/// Gathered in one place because both the customer side and the review side produce the same
/// response, and because two of the mappings are easy to get subtly wrong: the billing cycle
/// crosses this module's wire PascalCase while remaining camelCase everywhere else, and the proof
/// URL has to be a path the client can fetch with a bearer token.
/// </remarks>
internal static class PaymentMapping
{
    /// <summary>Base path the customer's own endpoints live under.</summary>
    private const string CustomerBase = "/api/v1/billing/payment-requests";

    /// <summary>Base path the payment channel endpoints live under.</summary>
    private const string ChannelBase = "/api/v1/billing/payment-channels";

    /// <summary>The price of a plan for one cycle.</summary>
    /// <param name="plan">Plan being bought.</param>
    /// <param name="cycle">Period being bought.</param>
    public static decimal PriceFor(SubscriptionPlan plan, BillingCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return cycle == BillingCycle.Yearly ? plan.YearlyPrice : plan.MonthlyPrice;
    }

    /// <summary>
    /// The billing cycle as this module's wire value.
    /// </summary>
    /// <remarks>
    /// PascalCase, unlike everywhere else in the API. Converted by hand rather than by giving the
    /// shared enum a converter, which would change the wire value of every other billing endpoint.
    /// </remarks>
    /// <param name="cycle">Stored cycle.</param>
    public static string ToWire(BillingCycle cycle) => cycle.ToString();

    /// <summary>Reads this module's wire value back into the shared enum.</summary>
    /// <param name="value">Value supplied by the client.</param>
    /// <exception cref="Common.Exceptions.ValidationException">The value is not a known cycle.</exception>
    public static BillingCycle ParseCycle(string? value) =>
        Enum.TryParse<BillingCycle>(value, ignoreCase: true, out var cycle)
            ? cycle
            : throw new Common.Exceptions.ValidationException(
                "billingCycle", "Choose either a monthly or a yearly plan.");

    /// <summary>Reads a channel from the client's wire value.</summary>
    /// <param name="value">Value supplied by the client.</param>
    /// <exception cref="Common.Exceptions.ValidationException">The value is not a known channel.</exception>
    public static PaymentChannel ParseChannel(string? value) =>
        Enum.TryParse<PaymentChannel>(value, ignoreCase: true, out var channel)
            ? channel
            : throw new Common.Exceptions.ValidationException(
                "channel", "Choose how you sent the payment.");

    /// <summary>Describes a configured channel.</summary>
    /// <param name="setting">Stored channel.</param>
    public static PaymentChannelDetails ToDetails(PaymentChannelSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        return new PaymentChannelDetails(
            setting.Channel,
            setting.DisplayName,
            setting.AccountTitle,
            setting.AccountNumber,

            // Null rather than empty for a wallet: the client relabels its account field from the
            // presence of a bank name.
            string.IsNullOrWhiteSpace(setting.BankName) ? null : setting.BankName,

            // An API path, fetched with the bearer token. Empty when no code has been uploaded,
            // which the client renders as the account details alone.
            string.IsNullOrEmpty(setting.QrStorageKey)
                ? string.Empty
                : $"{ChannelBase}/{setting.Channel}/qr",
            setting.Instructions,
            setting.IsActive);
    }

    /// <summary>Describes a request for either side of the flow.</summary>
    /// <param name="request">Stored request.</param>
    /// <param name="adminIds">Admin account identifiers, keyed by tenant.</param>
    public static PaymentRequestResponse ToResponse(
        PaymentRequest request,
        IReadOnlyDictionary<long, string> adminIds)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(adminIds);

        var id = PublicId.From(PublicId.PaymentRequest, request.Id);

        return new PaymentRequestResponse(
            id,
            request.Status,
            PublicId.From(PublicId.Plan, request.SubscriptionPlanId),
            request.PlanName,
            ToWire(request.BillingCycle),
            request.Amount,
            request.Currency,
            request.Channel,
            request.Reference,
            request.Note,
            $"{CustomerBase}/{id}/proof",
            request.ProofFileName,
            request.ProofContentType,
            request.Organisation,
            request.SubmittedByName,
            request.SubmittedByEmail,
            request.TenantId is { } tenantId ? adminIds.GetValueOrDefault(tenantId) : null,
            request.SubmittedAt,
            request.ReviewedAt,
            request.ReviewedByName,

            // Only ever populated on a rejection. A reason left behind on an approved request would
            // read as a contradiction in the customer's history.
            request.Status == PaymentRequestStatus.Rejected ? request.RejectionReason : null);
    }

    /// <summary>
    /// Maps each request's tenant to that tenant's admin account identifier.
    /// </summary>
    /// <remarks>
    /// One query for the whole page rather than one per row. The identifier lets the platform queue
    /// deep-link into a workspace using the same <c>adminId</c> scoping as every other screen.
    /// </remarks>
    /// <param name="queries">Query executor.</param>
    /// <param name="requests">Requests being described.</param>
    /// <param name="users">User repository, read across tenants by platform staff.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<IReadOnlyDictionary<long, string>> LoadAdminIdsAsync(
        IQueryExecutor queries,
        IRepository<User> users,
        IReadOnlyList<PaymentRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(requests);

        var tenantIds = requests
            .Select(request => request.TenantId)
            .OfType<long>()
            .Distinct()
            .ToArray();

        if (tenantIds.Length == 0)
        {
            return new Dictionary<long, string>();
        }

        var admins = await queries.ToListAsync(
            users.Query()
                .Where(user => user.TenantId != null
                               && tenantIds.Contains(user.TenantId.Value)
                               && user.UserRoles.Any(userRole =>
                                   !userRole.IsDeleted && userRole.Role.Name == Roles.Admin))
                .Select(user => new AdminKey(user.TenantId!.Value, user.Id)),
            cancellationToken);

        // A workspace with more than one administrator resolves to the lowest key, so the link is
        // stable between page loads rather than depending on row order.
        return admins
            .GroupBy(admin => admin.TenantId)
            .ToDictionary(
                group => group.Key,
                group => PublicId.From(PublicId.AdminAccount, group.Min(admin => admin.UserId)));
    }

    private sealed record AdminKey(long TenantId, long UserId);
}
