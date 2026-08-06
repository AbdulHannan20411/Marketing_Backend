using Marketing.Application.DTOs.Contacts;

namespace Marketing.Application.Interfaces;

/// <summary>Contact writes for the resolved tenant.</summary>
public interface IContactWriteService
{
    /// <summary>Creates a contact.</summary>
    public Task<ContactResponse> CreateAsync(CreateContactRequest request, CancellationToken cancellationToken = default);

    /// <summary>Updates a contact. Omitted fields are left unchanged.</summary>
    public Task<ContactResponse> UpdateAsync(
        string contactId,
        UpdateContactRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a contact.</summary>
    public Task DeleteAsync(string contactId, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes several contacts.</summary>
    public Task<BulkOperationResult> BulkDeleteAsync(
        BulkContactRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Applies tags to several contacts, leaving existing tags in place.</summary>
    public Task<BulkOperationResult> BulkTagAsync(BulkTagRequest request, CancellationToken cancellationToken = default);

    /// <summary>Adds several contacts to groups, leaving existing memberships in place.</summary>
    public Task<BulkOperationResult> BulkGroupAsync(
        BulkGroupRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams every contact as CSV.
    /// <para>
    /// Streamed rather than buffered: an export of a large contact book would otherwise sit on the
    /// large object heap in its entirety before a single byte reached the client.
    /// </para>
    /// </summary>
    public IAsyncEnumerable<string> ExportAsync(CancellationToken cancellationToken = default);
}

/// <summary>The two-step CSV import wizard.</summary>
public interface IContactImportService
{
    /// <summary>
    /// Parses an upload, stages it, and returns a preview with detected columns, sample rows and a
    /// duplicate count.
    /// </summary>
    /// <param name="fileName">Name of the uploaded file.</param>
    /// <param name="content">File contents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ImportPreview> PreviewAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>Commits a staged batch using the operator's column mapping.</summary>
    /// <param name="batchId">Batch returned by the preview call.</param>
    /// <param name="request">Column mapping and options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ImportResult> CommitAsync(
        string batchId,
        CommitImportRequest request,
        CancellationToken cancellationToken = default);
}
