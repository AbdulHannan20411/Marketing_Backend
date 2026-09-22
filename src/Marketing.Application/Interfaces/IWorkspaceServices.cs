using Marketing.Application.DTOs.Workspace;
using static Marketing.Common.Constants.ContractEnums;
using Marketing.Common.Requests;
using Marketing.Common.Responses;

namespace Marketing.Application.Interfaces;

/// <summary>Employees and permission sets for the resolved tenant.</summary>
public interface IEmployeeService
{
    /// <summary>Returns every employee with their effective permissions.</summary>
    public Task<IReadOnlyList<EmployeeResponse>> GetEmployeesAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns one page of employees, searched by name or email.</summary>
    /// <param name="query">Paging and search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<EmployeeResponse>> GetEmployeesAsync(
        OptionalPageRequest query,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every permission set.</summary>
    public Task<IReadOnlyList<PermissionSetResponse>> GetPermissionSetsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invites an employee.
    /// <para>
    /// Refused with a 422 naming the limit when the plan's seat allowance is already used.
    /// </para>
    /// </summary>
    public Task<EmployeeResponse> InviteAsync(
        InviteEmployeeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an employee's effective permissions.
    /// <para>
    /// Rotates their security stamp, so the change takes effect at their next refresh rather than
    /// whenever their current access token happens to expire.
    /// </para>
    /// </summary>
    public Task<EmployeeResponse> UpdatePermissionsAsync(
        string employeeId,
        UpdatePermissionsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Changes an employee's account state.</summary>
    public Task<EmployeeResponse> UpdateStatusAsync(
        string employeeId,
        UpdateEmployeeStatusRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Changes an employee's role.</summary>
    /// <remarks>
    /// Rotates their security stamp, so the new role takes effect on their next request rather
    /// than whenever their current token happens to lapse.
    /// </remarks>
    public Task<EmployeeResponse> UpdateRoleAsync(
        string employeeId,
        UpdateEmployeeRoleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Changes an employee's name, job title or email. Omitted fields are left alone.</summary>
    public Task<EmployeeResponse> UpdateAsync(
        string employeeId,
        UpdateEmployeeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces an employee's access to the workspace's WhatsApp numbers.</summary>
    /// <param name="employeeId">Employee identifier.</param>
    /// <param name="request">The complete access set and their default number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<EmployeeResponse> UpdateWhatsAppAccessAsync(
        string employeeId,
        DTOs.WhatsApp.UpdateWhatsAppAccessRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Issues a fresh invitation, invalidating any outstanding one.</summary>
    public Task ResendInviteAsync(string employeeId, CancellationToken cancellationToken = default);

    /// <summary>Withdraws a pending invitation and removes the account it was for.</summary>
    public Task RevokeInviteAsync(string employeeId, CancellationToken cancellationToken = default);

    /// <summary>Removes an employee and ends their sessions.</summary>
    public Task DeleteAsync(string employeeId, CancellationToken cancellationToken = default);

    /// <summary>Overwrites several employees' permissions with a saved set.</summary>
    public Task<IReadOnlyList<EmployeeResponse>> ApplyPermissionSetAsync(
        string permissionSetId,
        ApplyPermissionSetRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a permission set.</summary>
    public Task<PermissionSetResponse> CreatePermissionSetAsync(
        PermissionSetDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces a permission set.</summary>
    public Task<PermissionSetResponse> UpdatePermissionSetAsync(
        string permissionSetId,
        PermissionSetDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a permission set. System sets cannot be deleted.</summary>
    public Task DeletePermissionSetAsync(string permissionSetId, CancellationToken cancellationToken = default);
}

/// <summary>Notifications for the signed-in user.</summary>
public interface INotificationService
{
    /// <summary>Returns the caller's notifications, newest first.</summary>
    public Task<IReadOnlyList<AppNotification>> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns one page of the caller's notifications, newest first, with the bell counts.</summary>
    /// <param name="query">Paging and filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<NotificationFeed> GetPageAsync(
        NotificationQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Marks one notification read and returns the full updated list.</summary>
    public Task<IReadOnlyList<AppNotification>> MarkReadAsync(
        string notificationId,
        CancellationToken cancellationToken = default);

    /// <summary>Marks every notification read and returns the full updated list.</summary>
    public Task<IReadOnlyList<AppNotification>> MarkAllReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns which groups the caller still wants, all six of them.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the caller's switches and returns the state as stored.
    /// </summary>
    /// <remarks>
    /// Partial by design: a category the body does not mention keeps whatever it had, so an older
    /// client cannot silently reset a switch it has never heard of.
    /// </remarks>
    /// <param name="wanted">The categories to change, keyed by category. Unknown keys are ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<NotificationPreferences> UpdatePreferencesAsync(
        IReadOnlyDictionary<string, bool> wanted,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Narrows a set of recipients to those who still want to hear about this kind.
    /// </summary>
    /// <remarks>
    /// Called before the rows are written, so a silenced notification is never stored, never
    /// pushed and never counted - which is the only way a switch can be honest about the bell,
    /// whose counts are computed server-side. Filtering one recipient out leaves the others alone.
    /// <para>
    /// Security and system notifications pass through untouched, whatever is stored for them.
    /// </para>
    /// </remarks>
    /// <param name="userIds">Intended recipients.</param>
    /// <param name="kind">What they would be told about.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<long>> WhoWantsAsync(
        IReadOnlyCollection<long> userIds,
        NotificationKind kind,
        CancellationToken cancellationToken = default);
}

/// <summary>Global search across the resolved tenant.</summary>
public interface ISearchService
{
    /// <summary>
    /// Searches contacts, campaigns, templates, employees and the static destinations.
    /// <para>
    /// Results respect the caller's permissions: someone without <c>contacts.view</c> never sees a
    /// contact, however well it matches.
    /// </para>
    /// </summary>
    /// <param name="term">Search term. A blank term returns nothing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<SearchResultGroup>> SearchAsync(
        string? term,
        CancellationToken cancellationToken = default);
}
