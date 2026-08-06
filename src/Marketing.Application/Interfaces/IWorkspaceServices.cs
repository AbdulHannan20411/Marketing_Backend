using Marketing.Application.DTOs.Workspace;

namespace Marketing.Application.Interfaces;

/// <summary>Employees and permission sets for the resolved tenant.</summary>
public interface IEmployeeService
{
    /// <summary>Returns every employee with their effective permissions.</summary>
    public Task<IReadOnlyList<EmployeeResponse>> GetEmployeesAsync(CancellationToken cancellationToken = default);

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

    /// <summary>Removes an employee and ends their sessions.</summary>
    public Task DeleteAsync(string employeeId, CancellationToken cancellationToken = default);

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

    /// <summary>Marks one notification read and returns the full updated list.</summary>
    public Task<IReadOnlyList<AppNotification>> MarkReadAsync(
        string notificationId,
        CancellationToken cancellationToken = default);

    /// <summary>Marks every notification read and returns the full updated list.</summary>
    public Task<IReadOnlyList<AppNotification>> MarkAllReadAsync(CancellationToken cancellationToken = default);
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
