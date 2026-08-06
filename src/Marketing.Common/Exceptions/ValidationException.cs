using System.Net;

namespace Marketing.Common.Exceptions;

/// <summary>
/// Raised when input fails validation. Carries a per-field error dictionary shaped for
/// <c>ValidationProblemDetails</c>.
/// </summary>
public sealed class ValidationException : AppException
{
    /// <summary>Initialises a new instance with no field-level detail.</summary>
    public ValidationException()
        : base("One or more validation errors occurred.")
    {
        Errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    }

    /// <summary>Initialises a new instance from a field/messages map.</summary>
    /// <param name="errors">Property name to error messages.</param>
    public ValidationException(IDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = new Dictionary<string, string[]>(errors, StringComparer.Ordinal);
    }

    /// <summary>Initialises a new instance for a single field.</summary>
    /// <param name="propertyName">Property that failed validation.</param>
    /// <param name="errorMessage">Reason it failed.</param>
    public ValidationException(string propertyName, string errorMessage)
        : this(new Dictionary<string, string[]>(StringComparer.Ordinal) { [propertyName] = [errorMessage] })
    {
    }

    /// <summary>Validation failures keyed by property name.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    /// <summary>
    /// 422 rather than 400.
    /// <para>
    /// The client renders 422 as inline field errors with no toast, and 400 as a generic error
    /// toast. Field-level validation belongs against the field, so this is the status that
    /// produces the intended behaviour.
    /// </para>
    /// </summary>
    public override HttpStatusCode StatusCode => HttpStatusCode.UnprocessableEntity;

    /// <inheritdoc />
    public override string ErrorCode => "validation_failed";
}
