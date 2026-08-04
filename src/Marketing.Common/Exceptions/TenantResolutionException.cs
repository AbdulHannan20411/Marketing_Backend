using System.Net;

namespace Marketing.Common.Exceptions;

/// <summary>
/// Raised when an operation requires a tenant but the authenticated principal has none - a
/// platform administrator calling a tenant-scoped endpoint without impersonating a tenant, or a
/// token issued before the tenant claim existed.
/// <para>
/// This is a hard failure by design. Falling back to "no tenant filter" on a tenant-scoped query
/// is the single most dangerous bug this codebase can have, so the request is rejected instead.
/// </para>
/// </summary>
public sealed class TenantResolutionException : AppException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">Reason the tenant could not be resolved.</param>
    public TenantResolutionException(string message = "The request could not be associated with a tenant.")
        : base(message)
    {
    }

    /// <inheritdoc />
    public override HttpStatusCode StatusCode => HttpStatusCode.Forbidden;

    /// <inheritdoc />
    public override string ErrorCode => "tenant_not_resolved";
}
