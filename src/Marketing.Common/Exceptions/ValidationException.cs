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

    /// <inheritdoc />
    public override HttpStatusCode StatusCode => HttpStatusCode.BadRequest;

    /// <inheritdoc />
    public override string ErrorCode => "validation_failed";
}
