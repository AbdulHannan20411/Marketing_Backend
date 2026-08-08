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

    /// <summary>Soft-deletes several contacts, reporting each one it could not.</summary>
    public Task<BulkOperationResult> BulkDeleteAsync(
        BulkContactRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Adds, removes or replaces tags on several contacts.</summary>
    public Task<BulkOperationResult> BulkTagAsync(BulkTagRequest request, CancellationToken cancellationToken = default);

    /// <summary>Adds, removes or replaces group memberships on several contacts.</summary>
    public Task<BulkOperationResult> BulkGroupAsync(
        BulkGroupRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or removes members of one group.
    /// <para>
    /// The group detail screen's door into the same operation the contacts table reaches through
    /// <see cref="BulkGroupAsync"/>. One implementation serves both, so their counts cannot drift.
    /// </para>
    /// </summary>
    public Task<BulkOperationResult> SetGroupMembershipAsync(
        string groupId,
        MembershipRequest request,
        BulkMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>Adds or removes the contacts carrying one tag.</summary>
    public Task<BulkOperationResult> SetTagMembershipAsync(
        string tagId,
        MembershipRequest request,
        BulkMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>Folds several contacts into one and deletes the rest.</summary>
    public Task<ContactResponse> MergeAsync(
        MergeContactsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams matching contacts as CSV.
    /// <para>
    /// Streamed rather than buffered: an export of a large contact book would otherwise sit on the
    /// large object heap in its entirety before a single byte reached the client.
    /// </para>
    /// </summary>
    /// <param name="query">The same filters the list endpoint accepts, plus an explicit selection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public IAsyncEnumerable<string> ExportAsync(
        ContactExportQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>The CSV import wizard: upload and preview, commit, then poll.</summary>
public interface IContactImportService
{
    /// <summary>
    /// Parses an upload, stages it, and returns a preview with detected columns, sample rows and
    /// duplicate counts.
    /// </summary>
    /// <param name="fileName">Name of the uploaded file.</param>
    /// <param name="content">File contents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ImportPreview> PreviewAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>Commits a staged upload using the operator's column mapping.</summary>
    /// <param name="request">Upload identifier, mapping and options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ImportResult> CommitAsync(
        ImportCommitRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Re-reads the outcome of an import.</summary>
    /// <param name="jobId">Identifier returned by the commit call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ImportResult> GetJobAsync(string jobId, CancellationToken cancellationToken = default);
}
