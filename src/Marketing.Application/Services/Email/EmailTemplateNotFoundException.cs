using System.Net;
using Marketing.Common.Exceptions;

namespace Marketing.Application.Services.Email;

/// <summary>Raised when an email template key does not exist.</summary>
/// <remarks>
/// Its own type rather than <see cref="NotFoundException"/>, which always reports
/// <c>resource_not_found</c>. The editor distinguishes this code, and the mock API it was built
/// against already returns it.
/// </remarks>
public sealed class EmailTemplateNotFoundException : AppException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="key">The key that was asked for.</param>
    public EmailTemplateNotFoundException(string key)
        : base($"There is no email template called \"{key}\".")
    {
        Extensions["key"] = key;
    }

    /// <inheritdoc />
    public override HttpStatusCode StatusCode => HttpStatusCode.NotFound;

    /// <inheritdoc />
    public override string ErrorCode => "email_template_not_found";
}
