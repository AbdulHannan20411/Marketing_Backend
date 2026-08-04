using System.Net;

namespace Marketing.Common.Exceptions;

/// <summary>
/// Raised when a requested resource does not exist, or exists but belongs to another tenant.
/// <para>
/// Cross-tenant reads deliberately surface as "not found" rather than "forbidden": telling a
/// caller that an identifier exists in someone else's tenant is itself a data leak.
/// </para>
/// </summary>
public sealed class NotFoundException : AppException
{
    /// <summary>Initialises a new instance with a pre-built message.</summary>
    public NotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance describing a missing entity by type and key.</summary>
    /// <param name="resourceName">Logical resource name, for example <c>Contact</c>.</param>
    /// <param name="key">The identifier that was searched for.</param>
    public NotFoundException(string resourceName, object key)
        : base($"{resourceName} '{key}' was not found.")
    {
        Extensions["resource"] = resourceName;
    }

    /// <inheritdoc />
    public override HttpStatusCode StatusCode => HttpStatusCode.NotFound;

    /// <inheritdoc />
    public override string ErrorCode => "resource_not_found";
}
