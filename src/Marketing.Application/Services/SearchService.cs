using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Marketing.Business.Extensions;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <inheritdoc cref="ISearchService" />
public sealed class SearchService : ISearchService
{
    /// <summary>Hits per group. The client renders four and the cap keeps the query cheap.</summary>
    private const int PerGroup = 4;

    private readonly IRepository<Contact> _contacts;
    private readonly IRepository<Campaign> _campaigns;
    private readonly IRepository<MessageTemplate> _templates;
    private readonly IUserRepository _users;
    private readonly IQueryExecutor _queries;
    private readonly ICurrentUser _currentUser;

    /// <summary>Initialises a new instance.</summary>
    public SearchService(
        IRepository<Contact> contacts,
        IRepository<Campaign> campaigns,
        IRepository<MessageTemplate> templates,
        IUserRepository users,
        IQueryExecutor queries,
        ICurrentUser currentUser)
    {
        _contacts = contacts;
        _campaigns = campaigns;
        _templates = templates;
        _users = users;
        _queries = queries;
        _currentUser = currentUser;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResultGroup>> SearchAsync(
        string? term,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var groups = new List<SearchResultGroup>(7);
        var trimmed = term.Trim();

        // Each group is gated on the permission that governs the underlying screen. A user without
        // contacts.view must never see a contact here, however well it matches - global search is
        // exactly the kind of surface that leaks records past a permission check.
        if (_currentUser.HasPermission(Permissions.Contacts.View))
        {
            var contacts = await _queries.ToListAsync(
                _contacts.Query()
                    .WhereMatchesSearch(trimmed)
                    .OrderBy(contact => contact.FullName)
                    .Take(PerGroup)
                    .Select(contact => new { contact.Id, contact.FullName, contact.PhoneNumber }),
                cancellationToken);

            AddGroup(groups, SearchResultKind.Contact, "Contacts", [.. contacts.Select(contact =>
                new SearchResult(
                    PublicId.From(PublicId.Contact, contact.Id),
                    SearchResultKind.Contact,
                    contact.FullName,
                    contact.PhoneNumber,
                    "users",
                    "/contacts"))]);
        }

        if (_currentUser.HasPermission(Permissions.WhatsApp.CampaignsReports)
            || _currentUser.HasPermission(Permissions.WhatsApp.CampaignsCreate))
        {
            var campaigns = await _queries.ToListAsync(
                _campaigns.Query()
                    .WhereNameMatches(trimmed)
                    .OrderByDescending(campaign => campaign.CreatedOn)
                    .Take(PerGroup)
                    .Select(campaign => new { campaign.Id, campaign.Name, campaign.TemplateName }),
                cancellationToken);

            AddGroup(groups, SearchResultKind.Campaign, "Campaigns", [.. campaigns.Select(campaign =>
                new SearchResult(
                    PublicId.From(PublicId.Campaign, campaign.Id),
                    SearchResultKind.Campaign,
                    campaign.Name,
                    campaign.TemplateName,
                    "megaphone",
                    "/campaigns"))]);
        }

        if (_currentUser.HasPermission(Permissions.WhatsApp.TemplatesView))
        {
            var templates = await _queries.ToListAsync(
                _templates.Query()
                    .WhereNameMatches(trimmed)
                    .OrderBy(template => template.Name)
                    .Take(PerGroup)
                    .Select(template => new { template.Id, template.Name, template.Language }),
                cancellationToken);

            AddGroup(groups, SearchResultKind.Template, "Templates", [.. templates.Select(template =>
                new SearchResult(
                    PublicId.From(PublicId.Template, template.Id),
                    SearchResultKind.Template,
                    template.Name,
                    template.Language,
                    "document",
                    "/templates"))]);
        }

        if (_currentUser.HasPermission(Permissions.Settings.Employees))
        {
            var employees = await _queries.ToListAsync(
                _users.Query()
                    .WhereNameMatches(trimmed)
                    .OrderBy(user => user.DisplayName)
                    .Take(PerGroup)
                    .Select(user => new { user.Id, user.DisplayName, user.Email }),
                cancellationToken);

            AddGroup(groups, SearchResultKind.Employee, "Employees", [.. employees.Select(employee =>
                new SearchResult(
                    PublicId.From(PublicId.Employee, employee.Id),
                    SearchResultKind.Employee,
                    employee.DisplayName,
                    employee.Email,
                    "userGroup",
                    "/settings/employees"))]);
        }

        AddGroup(groups, SearchResultKind.Report, "Reports", MatchStatic(trimmed, SearchResultKind.Report,
            Permissions.Reports.View,
            [("Delivery report", "Failures and delivery rates", "chartBar", "/reports"),
             ("Campaign performance", "Sends, reads and clicks", "trendingUp", "/reports")]));

        AddGroup(groups, SearchResultKind.Subscription, "Subscription", MatchStatic(trimmed,
            SearchResultKind.Subscription,
            Permissions.Settings.Subscription,
            [("Subscription", "Plan, usage and renewal", "creditCard", "/subscription"),
             ("Billing history", "Invoices and payments", "document", "/billing")]));

        AddGroup(groups, SearchResultKind.Setting, "Settings", MatchStatic(trimmed, SearchResultKind.Setting,
            Permissions.Settings.Company,
            [("Company profile", "Name, branding and contact details", "cog", "/settings/company"),
             ("Employees", "Invitations and permissions", "userGroup", "/settings/employees"),
             ("Integrations", "Connected services", "sparkles", "/settings/integrations")]));

        return groups;
    }

    /// <summary>Matches the static destinations - screens rather than records - by title.</summary>
    private List<SearchResult> MatchStatic(
        string term,
        SearchResultKind kind,
        string permission,
        (string Title, string Subtitle, string Icon, string Route)[] entries)
    {
        if (!_currentUser.HasPermission(permission))
        {
            return [];
        }

        return [.. entries
            .Where(entry =>
                entry.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || entry.Subtitle.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(PerGroup)
            .Select(entry => new SearchResult(entry.Route, kind, entry.Title, entry.Subtitle, entry.Icon, entry.Route))];
    }

    /// <summary>Appends a group, skipping it when empty - the contract omits empty groups.</summary>
    private static void AddGroup(
        List<SearchResultGroup> groups,
        SearchResultKind kind,
        string label,
        List<SearchResult> results)
    {
        if (results.Count > 0)
        {
            groups.Add(new SearchResultGroup(kind, label, results));
        }
    }
}
