using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Application.DTOs.Imports;

/// <summary>
/// An accepted upload, decoupled from the transport.
/// </summary>
/// <remarks>
/// A stream and a name rather than a framework form-file type, so the Application layer stays
/// independent of ASP.NET Core and the upload path is testable without a request.
/// </remarks>
/// <param name="FileName">Name the operator uploaded it under. Shown back, never used as a path.</param>
/// <param name="Content">Readable stream positioned at the start. The caller owns and disposes it.</param>
/// <param name="SizeBytes">Size of the upload.</param>
/// <param name="DuplicateStrategy">What the commit should do with a number that already exists.</param>
public sealed record ImportUploadCommand(
    string FileName,
    Stream Content,
    long SizeBytes,
    ImportDuplicateStrategy DuplicateStrategy);

/// <summary>A file being served back to the operator.</summary>
/// <param name="Content">Readable stream. The caller disposes it.</param>
/// <param name="FileName">Name the browser saves it under.</param>
/// <param name="ContentType">Media type.</param>
public sealed record ImportDownload(Stream Content, string FileName, string ContentType);
