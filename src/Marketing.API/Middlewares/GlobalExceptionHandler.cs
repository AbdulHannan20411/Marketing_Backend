using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using ValidationException = Marketing.Common.Exceptions.ValidationException;

namespace Marketing.API.Middlewares;

/// <summary>
/// Translates every unhandled exception into an RFC 7807 <see cref="ProblemDetails"/> response.
/// <para>
/// Implemented as an <see cref="IExceptionHandler"/> rather than a custom middleware so it composes
/// with the framework's own exception handling and status-code pages.
/// </para>
/// <para>
/// The central rule: a client learns <em>what</em> went wrong, never <em>where</em>. Expected
/// failures return their own message; anything else returns a generic message plus an exception id
/// that ties the response to the full detail in the logs.
/// </para>
/// </summary>
public sealed partial class GlobalExceptionHandler : IExceptionHandler
{
    private const string ProblemTypeBase = "https://docs.marketing-platform.io/errors/";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="problemDetailsService">Framework service that writes the response body.</param>
    /// <param name="environment">Host environment; gates developer-only detail.</param>
    /// <param name="logger">Logger.</param>
    public GlobalExceptionHandler(
        IProblemDetailsService problemDetailsService,
        IHostEnvironment environment,
        ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var exceptionId = Guid.NewGuid().ToString("N");
        var correlationId = httpContext.Items[AppConstants.Headers.CorrelationId] as string
                            ?? httpContext.TraceIdentifier;

        var problemDetails = Translate(exception, exceptionId);

        Log(exception, exceptionId, correlationId, problemDetails.Status ?? 500);

        httpContext.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;
        httpContext.Response.Headers[AppConstants.Headers.ExceptionId] = exceptionId;

        problemDetails.Instance = httpContext.Request.Path;
        problemDetails.Extensions["correlationId"] = correlationId;
        problemDetails.Extensions["exceptionId"] = exceptionId;

        if (_environment.IsDevelopment())
        {
            // Stack traces are a developer convenience and an attacker's map of the internals.
            // They exist on a developer machine and nowhere else.
            problemDetails.Extensions["exception"] = exception.ToString();
        }

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails,
        });
    }

    private static ProblemDetails Translate(Exception exception, string exceptionId) =>
        exception switch
        {
            ValidationException validation => BuildValidationProblem(validation),
            AppException application => BuildApplicationProblem(application, exceptionId),
            OperationCanceledException => new ProblemDetails
            {
                Status = StatusCodes.Status499ClientClosedRequest,
                Title = "Request cancelled",
                Detail = "The request was cancelled before it completed.",
                Type = $"{ProblemTypeBase}request_cancelled",
            },
            PostgresException postgres => BuildDatabaseProblem(postgres),
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred",
                Detail =
                    $"The request could not be completed. Quote reference {exceptionId} when contacting support.",
                Type = $"{ProblemTypeBase}internal_error",
            },
        };

    private static ValidationProblemDetails BuildValidationProblem(ValidationException exception)
    {
        var problem = new ValidationProblemDetails(
            exception.Errors.ToDictionary(entry => entry.Key, entry => entry.Value))
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred",
            Type = $"{ProblemTypeBase}{exception.ErrorCode}",
        };

        problem.Extensions["errorCode"] = exception.ErrorCode;

        return problem;
    }

    private static ProblemDetails BuildApplicationProblem(AppException exception, string exceptionId)
    {
        var problem = new ProblemDetails
        {
            Status = (int)exception.StatusCode,
            Title = ToTitle(exception.ErrorCode),
            Detail = exception.IsClientSafe
                ? exception.Message
                : $"The request could not be completed. Quote reference {exceptionId} when contacting support.",
            Type = $"{ProblemTypeBase}{exception.ErrorCode}",
        };

        problem.Extensions["errorCode"] = exception.ErrorCode;

        foreach (var (key, value) in exception.Extensions)
        {
            problem.Extensions[key] = value;
        }

        return problem;
    }

    /// <summary>
    /// Maps the PostgreSQL error codes that represent a client mistake rather than a server fault,
    /// so a duplicate key surfaces as a 409 instead of a 500.
    /// </summary>
    private static ProblemDetails BuildDatabaseProblem(PostgresException exception) =>
        exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Duplicate record",
                Detail = "A record with the same unique value already exists.",
                Type = $"{ProblemTypeBase}duplicate_record",
            },
            PostgresErrorCodes.ForeignKeyViolation => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Related record missing",
                Detail = "The operation references a record that does not exist or is still in use.",
                Type = $"{ProblemTypeBase}reference_violation",
            },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "A database error occurred",
                Detail = "The request could not be completed.",
                Type = $"{ProblemTypeBase}database_error",
            },
        };

    private void Log(Exception exception, string exceptionId, string correlationId, int statusCode)
    {
        // Expected failures - a wrong password, a missing record - are information, not incidents.
        // Logging them at error level trains everyone to ignore the error log.
        if (statusCode < StatusCodes.Status500InternalServerError)
        {
            LogExpectedFailure(statusCode, exceptionId, correlationId, exception.Message);
            return;
        }

        LogUnhandledException(exception, exceptionId, correlationId);
    }

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Information,
        Message = "Request failed with {StatusCode}. ExceptionId: {ExceptionId}. CorrelationId: {CorrelationId}. Reason: {Reason}")]
    private partial void LogExpectedFailure(int statusCode, string exceptionId, string correlationId, string reason);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Error,
        Message = "Unhandled exception. ExceptionId: {ExceptionId}. CorrelationId: {CorrelationId}.")]
    private partial void LogUnhandledException(Exception exception, string exceptionId, string correlationId);

    private static string ToTitle(string errorCode) =>
        string.Join(' ', errorCode.Split('_').Select(word =>
            word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
}
