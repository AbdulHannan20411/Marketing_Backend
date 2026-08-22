using System.Globalization;
using Marketing.Application.DTOs.BusinessDiscovery;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.BusinessDiscovery;

/// <summary>Discovering businesses near a point and importing them as contacts.</summary>
public interface IBusinessDiscoveryService
{
    /// <summary>Categories the picker offers.</summary>
    public IReadOnlyList<BusinessCategoryResponse> GetCategories();

    /// <summary>Turns typed text into candidate points.</summary>
    /// <param name="query">What the user typed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<PlaceSuggestionResponse>> SearchPlacesAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>Names a dropped pin, or returns null.</summary>
    /// <param name="latitude">Degrees north.</param>
    /// <param name="longitude">Degrees east.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PlaceSuggestionResponse?> ReverseGeocodeAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default);

    /// <summary>Finds one page of businesses near a point.</summary>
    /// <param name="request">Where, how far, and what kind.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<BusinessSearchResponse> SearchAsync(
        BusinessSearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Imports selected businesses from a previous search.</summary>
    /// <param name="request">Which businesses, from which search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<BusinessImportResponse> ImportAsync(
        BusinessImportRequest request,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IBusinessDiscoveryService" />
public sealed partial class BusinessDiscoveryService : IBusinessDiscoveryService
{
    /// <summary>Largest radius accepted, whatever the client offers.</summary>
    private const double MaximumRadiusKm = 50;

    /// <summary>Largest page accepted.</summary>
    private const int MaximumPageSize = 50;

    /// <summary>Total results one search will hand out, however many pages are asked for.</summary>
    /// <remarks>
    /// A review UI cannot usefully show more, and every page beyond this is a provider charge for
    /// results nobody reads.
    /// </remarks>
    private const int MaximumResultsPerSearch = 200;

    /// <summary>Searches one user may run in an hour.</summary>
    private const int SearchesPerUserPerHour = 30;

    /// <summary>Searches one workspace may run in a day.</summary>
    private const int SearchesPerTenantPerDay = 200;

    /// <summary>
    /// How long a page of results stays cached.
    /// </summary>
    /// <remarks>
    /// Businesses do not move often, and a day-old address is far cheaper than a fresh charge for
    /// every pin nudge.
    /// </remarks>
    private static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a search's results remain resolvable for an import.
    /// </summary>
    /// <remarks>
    /// Nobody reviews a list of businesses for longer than this. Keeping it short bounds how much
    /// provider data is held, and an expired search asks the user to search again rather than
    /// quietly billing them for a second query.
    /// </remarks>
    private static readonly TimeSpan SearchResultLifetime = TimeSpan.FromHours(1);

    private readonly IPlaceProvider _provider;
    private readonly ICacheService _cache;
    private readonly IRepository<Contact> _contacts;
    private readonly IRepository<ContactGroup> _groups;
    private readonly IRepository<ContactGroupMember> _members;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<BusinessDiscoveryService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public BusinessDiscoveryService(
        IPlaceProvider provider,
        ICacheService cache,
        IRepository<Contact> contacts,
        IRepository<ContactGroup> groups,
        IRepository<ContactGroupMember> members,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        ICurrentUser currentUser,
        ILogger<BusinessDiscoveryService> logger)
    {
        _provider = provider;
        _cache = cache;
        _contacts = contacts;
        _groups = groups;
        _members = members;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<BusinessCategoryResponse> GetCategories() => BusinessCategories.All;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlaceSuggestionResponse>> SearchPlacesAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        EnsureProviderConfigured();

        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 3)
        {
            return [];
        }

        var suggestions = await _provider.GeocodeAsync(query.Trim(), cancellationToken);

        return [.. suggestions.Select(ToResponse)];
    }

    /// <inheritdoc />
    public async Task<PlaceSuggestionResponse?> ReverseGeocodeAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        EnsureProviderConfigured();
        EnsureCoordinatesValid(latitude, longitude);

        var suggestion = await _provider.ReverseGeocodeAsync(latitude, longitude, cancellationToken);

        // Null is a legitimate answer, not a failure. The client shows the pin unnamed and searches
        // by coordinates, which is the part that actually matters.
        return suggestion is null ? null : ToResponse(suggestion);
    }

    /// <inheritdoc />
    public async Task<BusinessSearchResponse> SearchAsync(
        BusinessSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureProviderConfigured();

        var tenantId = _tenantContext.RequireTenantId();
        var (radiusKm, pageSize, page) = Validate(request);

        await EnforceSearchQuotaAsync(tenantId, cancellationToken);

        // Rounded to about a hundred metres before it reaches the key, so nudging the pin a few
        // metres hits the same cache entry rather than paying the provider again.
        var fingerprint = Fingerprint(request.Latitude, request.Longitude, radiusKm, request.Category, page, pageSize);
        var cacheKey = AppConstants.CacheKeys.BusinessSearch(tenantId, fingerprint);

        var cached = await _cache.GetAsync<CachedPage>(cacheKey, cancellationToken);
        ProviderSearchResult result;

        if (cached is not null)
        {
            result = new ProviderSearchResult(cached.Items, cached.Total, cached.NextPageToken);
        }
        else
        {
            // Providers page by cursor, not by number, so page N can only be fetched with the token
            // page N-1 handed back. It is read from that page's cache entry; asking for a page whose
            // predecessor has expired is refused rather than silently answered with page one.
            var pageToken = await ResolvePageTokenAsync(tenantId, request, radiusKm, page, pageSize, cancellationToken);

            if (page > 1 && pageToken is null)
            {
                throw new BusinessRuleException(
                    "search_expired",
                    "That search has expired. Search again to keep browsing the results.");
            }

            var providerType = BusinessCategories.ToProviderType(request.Category)!;

            result = await _provider.SearchAsync(
                request.Latitude, request.Longitude, radiusKm, providerType, pageSize, pageToken, cancellationToken);

            await _cache.SetAsync(
                cacheKey,
                new CachedPage([.. result.Items], result.Total, result.NextPageToken),
                SearchCacheLifetime,
                cancellationToken);
        }

        // Capped regardless of what the provider would keep offering: a review screen cannot use
        // more, and each further page is a charge for results nobody looks at.
        var hasNextPage = result.NextPageToken is not null && page * pageSize < MaximumResultsPerSearch;

        var existing = await FindExistingPhoneNumbersAsync(result.Items, cancellationToken);
        var searchId = await RememberResultsAsync(tenantId, request, result.Items, cancellationToken);

        var items = result.Items
            .Select(business => ToResponse(business, existing))
            .ToList();

        LogSearchCompleted(tenantId, request.Category, items.Count, cached is not null);

        return new BusinessSearchResponse(items, page, pageSize, result.Total, hasNextPage, searchId);
    }

    /// <inheritdoc />
    public async Task<BusinessImportResponse> ImportAsync(
        BusinessImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tenantId = _tenantContext.RequireTenantId();

        if (request.BusinessIds is not { Count: > 0 })
        {
            throw new ValidationException(nameof(request.BusinessIds), "Choose at least one business to import.");
        }

        var stored = await _cache.GetAsync<StoredSearch>(
            AppConstants.CacheKeys.BusinessSearchResults(tenantId, request.SearchId),
            cancellationToken);

        if (stored is null)
        {
            // Refused rather than re-queried. Silently going back to the provider would put a
            // charge on the workspace that nobody asked for and nobody sees.
            throw new BusinessRuleException(
                "search_expired",
                "That search has expired. Search again and re-select the businesses you want.");
        }

        var byId = stored.Items.ToDictionary(business => business.Id, StringComparer.Ordinal);
        var group = await ResolveGroupAsync(request.GroupName, cancellationToken);

        var failures = new List<BusinessImportFailure>();
        var imported = 0;
        var skipped = 0;

        // Read once for the whole batch rather than per business, and re-read here rather than
        // trusting the flags from search time - the two calls can be minutes apart.
        var existing = await FindExistingPhoneNumbersAsync(stored.Items, cancellationToken);

        foreach (var businessId in request.BusinessIds.Distinct(StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(businessId, out var business))
            {
                failures.Add(new BusinessImportFailure(
                    businessId,
                    "Unknown business",
                    "That business was not part of the search being imported."));

                continue;
            }

            var normalised = NormalisePhone(business.Phone);

            if (normalised is null)
            {
                failures.Add(new BusinessImportFailure(
                    businessId,
                    business.Name,
                    "The phone number could not be normalised to an international format."));

                continue;
            }

            if (existing.Contains(normalised))
            {
                // Skipped, never merged and never duplicated. The file importer treats a matching
                // number as the same person, and the two paths must agree or the same spreadsheet
                // produces different results depending on which door it came through.
                skipped++;
                continue;
            }

            var contact = new Contact
            {
                TenantId = tenantId,
                FullName = business.Name,
                PhoneNumber = PhoneNumbers.ToDisplayForm(normalised),
                NormalizedPhoneNumber = normalised,
                Country = ContactRules.ResolveCountry(null, normalised),
                Status = ContactStatus.Subscribed,
                Lifecycle = ContactLifecycle.Lead,
            };

            _contacts.Add(contact);

            if (group is not null)
            {
                _members.Add(new ContactGroupMember
                {
                    TenantId = tenantId,
                    Contact = contact,
                    ContactGroup = group,
                });
            }

            // Added to the set immediately so two businesses sharing a number inside one batch
            // produce one contact and one skip, rather than colliding on the unique index.
            existing.Add(normalised);
            imported++;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogImportCompleted(tenantId, imported, skipped, failures.Count);

        return new BusinessImportResponse(imported, skipped, failures.Count, failures);
    }

    /// <summary>
    /// Normalises a provider's phone number the way the rest of the platform does, or null.
    /// </summary>
    /// <remarks>
    /// Routed through the same rules the file importer uses rather than a second implementation.
    /// The duplicate check is only meaningful if both paths agree on what makes two numbers the
    /// same, and a provider hands back every format a business has ever printed on a sign.
    /// </remarks>
    private static string? NormalisePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone) || !PhoneNumbers.IsPlausible(phone))
        {
            return null;
        }

        return ContactRules.NormalisePhone(phone);
    }

    /// <summary>Refuses clearly when no provider key is configured.</summary>
    /// <remarks>
    /// Checked up front rather than discovered as an authentication failure mid-call, so a
    /// deployment without a key says so instead of surfacing a provider error to an end user.
    /// </remarks>
    private void EnsureProviderConfigured()
    {
        if (!_provider.IsConfigured)
        {
            throw new BusinessRuleException(
                "provider_not_configured",
                "Business search is not available on this workspace yet.");
        }
    }

    private static void EnsureCoordinatesValid(double latitude, double longitude)
    {
        if (latitude is < -90 or > 90 || double.IsNaN(latitude))
        {
            throw new ValidationException("latitude", "Latitude must be between -90 and 90.");
        }

        if (longitude is < -180 or > 180 || double.IsNaN(longitude))
        {
            throw new ValidationException("longitude", "Longitude must be between -180 and 180.");
        }
    }

    /// <summary>Checks everything the client checks, because the client checks it for the user.</summary>
    private static (double RadiusKm, int PageSize, int Page) Validate(BusinessSearchRequest request)
    {
        EnsureCoordinatesValid(request.Latitude, request.Longitude);

        if (!BusinessCategories.IsKnown(request.Category))
        {
            throw new ValidationException("category", "Choose a business category from the list.");
        }

        if (request.RadiusKm > MaximumRadiusKm)
        {
            // Its own code, because the client renders a specific message for it rather than a
            // generic validation failure.
            throw new BusinessRuleException(
                "radius_too_large",
                $"The search radius cannot be more than {MaximumRadiusKm:0} kilometres.");
        }

        if (request.RadiusKm < 1)
        {
            throw new ValidationException("radiusKm", "The search radius must be at least 1 kilometre.");
        }

        return (
            request.RadiusKm,
            Math.Clamp(request.PageSize, 1, MaximumPageSize),
            Math.Max(1, request.Page));
    }

    /// <summary>Counts this search against the user's and the workspace's allowances.</summary>
    /// <remarks>
    /// Two windows, because they answer different questions: one user hammering the radius control
    /// is a different problem from a workspace steadily spending its budget over a day.
    /// <para>
    /// A cache that is unavailable does not block the feature. The counters are a cost control, not
    /// a security boundary, and refusing every search because Redis is down would turn a billing
    /// safeguard into an outage.
    /// </para>
    /// </remarks>
    private async Task EnforceSearchQuotaAsync(long tenantId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var userKey = AppConstants.CacheKeys.BusinessSearchQuota(
            tenantId,
            _currentUser.UserId ?? 0,
            now.ToString("yyyyMMddHH", CultureInfo.InvariantCulture));

        var tenantKey = AppConstants.CacheKeys.BusinessSearchTenantQuota(
            tenantId,
            now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

        var userCount = await _cache.IncrementAsync(userKey, TimeSpan.FromHours(1), cancellationToken);

        if (userCount > SearchesPerUserPerHour)
        {
            throw new RateLimitException(
                "search_limit_reached",
                "Business search limit reached. Please try again later.");
        }

        var tenantCount = await _cache.IncrementAsync(tenantKey, TimeSpan.FromDays(1), cancellationToken);

        if (tenantCount > SearchesPerTenantPerDay)
        {
            throw new RateLimitException(
                "search_limit_reached",
                "Business search limit reached. Please try again later.");
        }
    }

    /// <summary>The cursor that reaches a given page, read from the page before it.</summary>
    private async Task<string?> ResolvePageTokenAsync(
        long tenantId,
        BusinessSearchRequest request,
        double radiusKm,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (page <= 1)
        {
            return null;
        }

        var previous = await _cache.GetAsync<CachedPage>(
            AppConstants.CacheKeys.BusinessSearch(
                tenantId,
                Fingerprint(request.Latitude, request.Longitude, radiusKm, request.Category, page - 1, pageSize)),
            cancellationToken);

        return previous?.NextPageToken;
    }

    /// <summary>
    /// Stores the page against a search identifier so an import can resolve ids without re-querying.
    /// </summary>
    private async Task<string> RememberResultsAsync(
        long tenantId,
        BusinessSearchRequest request,
        IReadOnlyList<ProviderBusiness> items,
        CancellationToken cancellationToken)
    {
        // Deterministic from the search itself, so paging through one search keeps one identifier
        // and each page adds to the same stored set rather than starting a new one.
        var searchId = "srch_" + Fingerprint(
            request.Latitude, request.Longitude, request.RadiusKm, request.Category, 0, 0);

        var key = AppConstants.CacheKeys.BusinessSearchResults(tenantId, searchId);
        var stored = await _cache.GetAsync<StoredSearch>(key, cancellationToken);

        var merged = stored is null
            ? items.ToList()
            : [.. stored.Items.Concat(items).GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First())];

        await _cache.SetAsync(key, new StoredSearch(merged), SearchResultLifetime, cancellationToken);

        return searchId;
    }

    /// <summary>
    /// Which of these businesses are already contacts, matched on the normalised phone number.
    /// </summary>
    /// <remarks>
    /// The same key the file importer uses. Consistency between the two paths matters more than
    /// sophistication here: a business the file importer would call a duplicate has to be called a
    /// duplicate here, or the same spreadsheet produces different results through different doors.
    /// </remarks>
    private async Task<HashSet<string>> FindExistingPhoneNumbersAsync(
        IReadOnlyList<ProviderBusiness> businesses,
        CancellationToken cancellationToken)
    {
        var numbers = businesses
            .Select(business => NormalisePhone(business.Phone))
            .Where(number => number is not null)
            .Select(number => number!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (numbers.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        // The tenant filter applies, so a business another workspace has already imported is still
        // new to this one.
        var found = await _queries.ToListAsync(
            _contacts.Query()
                .Where(contact => numbers.Contains(contact.NormalizedPhoneNumber))
                .Select(contact => contact.NormalizedPhoneNumber),
            cancellationToken);

        return [.. found];
    }

    /// <summary>Finds or creates the group imported contacts should join.</summary>
    private async Task<ContactGroup?> ResolveGroupAsync(string? groupName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return null;
        }

        var name = groupName.Trim();

        var existing = await _queries.FirstOrDefaultAsync(
            _groups.Query(asNoTracking: false).Where(group => group.Name == name),
            cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var created = new ContactGroup
        {
            TenantId = _tenantContext.RequireTenantId(),
            Name = name,
            Description = "Created by business discovery.",
        };

        _groups.Add(created);

        return created;
    }

    /// <summary>A stable, short key for one search.</summary>
    private static string Fingerprint(
        double latitude,
        double longitude,
        double radiusKm,
        string category,
        int page,
        int pageSize)
    {
        // Three decimal places is about a hundred metres. Any finer and every pin nudge is a fresh
        // provider charge for what is, to a person, the same search.
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{latitude:F3}:{longitude:F3}:{radiusKm:F1}:{category}:{page}:{pageSize}");

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text)))[..24].ToLowerInvariant();
    }

    private static PlaceSuggestionResponse ToResponse(PlaceSuggestion suggestion) =>
        new(suggestion.Id, suggestion.Label, suggestion.Latitude, suggestion.Longitude, suggestion.Country);

    private static DiscoveredBusinessResponse ToResponse(ProviderBusiness business, HashSet<string> existing)
    {
        var normalised = NormalisePhone(business.Phone);

        return new DiscoveredBusinessResponse(
            business.Id,
            business.Name,
            normalised ?? business.Phone,
            business.Address,
            business.Latitude,
            business.Longitude,
            business.Category,
            business.Website,
            business.Rating,
            business.OpeningHours,

            // Null rather than false when there is no number to match on. False would claim the
            // business is new, which is a guess the badge would present as a fact.
            normalised is null ? null : existing.Contains(normalised));
    }

    /// <summary>One cached page of provider results.</summary>
    private sealed record CachedPage(List<ProviderBusiness> Items, int Total, string? NextPageToken);

    /// <summary>Every business seen in one search, for resolving an import.</summary>
    private sealed record StoredSearch(List<ProviderBusiness> Items);

    [LoggerMessage(
        EventId = 3501,
        Level = LogLevel.Information,
        Message = "Business search for tenant {TenantId}, category {Category}: {ResultCount} results, "
                  + "served from cache: {FromCache}.")]
    private partial void LogSearchCompleted(long tenantId, string category, int resultCount, bool fromCache);

    [LoggerMessage(
        EventId = 3502,
        Level = LogLevel.Information,
        Message = "Business import for tenant {TenantId}: {Imported} imported, {Skipped} skipped, "
                  + "{Failed} failed.")]
    private partial void LogImportCompleted(long tenantId, int imported, int skipped, int failed);
}
