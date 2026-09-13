using Marketing.Application.Configurations;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.DataAccess.Seed;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marketing.Application.Services.Email;

/// <summary>Turns a stored email template and a set of values into a message ready to queue.</summary>
public interface IEmailTemplateRenderer
{
    /// <summary>
    /// Renders a template inside the shared layout, addressed to one recipient.
    /// </summary>
    /// <remarks>
    /// System values are added here and override any caller value of the same name, so no caller can
    /// change the product name or the support address a customer sees. Callers still set reply-to
    /// themselves, as before.
    /// </remarks>
    /// <param name="key">Template key, for example <c>auth.invitation</c>.</param>
    /// <param name="toAddress">Recipient address.</param>
    /// <param name="toName">Recipient display name.</param>
    /// <param name="values">The template's own values. Missing values render as empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<EmailMessage> RenderAsync(
        string key,
        string toAddress,
        string toName,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the template that would be used for a key.
    /// </summary>
    /// <remarks>
    /// The stored template when it exists and parses; the shipped default otherwise.
    /// </remarks>
    /// <param name="key">Template key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<EmailTemplateDraft> ResolveAsync(string key, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IEmailTemplateRenderer" />
public sealed partial class EmailTemplateRenderer : IEmailTemplateRenderer
{
    private readonly IRepository<EmailTemplate> _templates;
    private readonly IQueryExecutor _queries;
    private readonly EmailTemplateCache _cache;
    private readonly IDateTimeProvider _clock;
    private readonly EmailOptions _options;
    private readonly ILogger<EmailTemplateRenderer> _logger;

    /// <summary>Initialises a new instance.</summary>
    public EmailTemplateRenderer(
        IRepository<EmailTemplate> templates,
        IQueryExecutor queries,
        EmailTemplateCache cache,
        IDateTimeProvider clock,
        IOptions<EmailOptions> options,
        ILogger<EmailTemplateRenderer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _templates = templates;
        _queries = queries;
        _cache = cache;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EmailMessage> RenderAsync(
        string key,
        string toAddress,
        string toName,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(values);

        var body = await ResolveAsync(key, cancellationToken);

        var layout = string.Equals(key, EmailTemplateLanguage.LayoutKey, StringComparison.Ordinal)
            ? null
            : await ResolveAsync(EmailTemplateLanguage.LayoutKey, cancellationToken);

        var merged = new Dictionary<string, string>(values, StringComparer.Ordinal);

        foreach (var (name, value) in EmailSystemValues.Build(_options, _clock.UtcNow))
        {
            merged[name] = value;
        }

        var rendered = EmailTemplateLanguage.RenderEmail(body, layout, merged);

        return new EmailMessage(
            toAddress,
            toName,

            // Line breaks are already flattened by the renderer. The remaining control characters go
            // too, as they did before templates existed: none belongs in a header.
            WithoutControlCharacters(rendered.Subject),
            rendered.Html,
            rendered.Text);
    }

    /// <inheritdoc />
    public async Task<EmailTemplateDraft> ResolveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var now = _clock.UtcNow;

        if (_cache.TryGet(key, now, out var cached))
        {
            return cached;
        }

        // Not tenant-scoped, so this reads the same row whichever workspace's email is being sent.
        var stored = await _queries.FirstOrDefaultAsync(
            _templates.Query()
                .Where(template => template.Key == key)
                .Select(template => new EmailTemplateDraft(template.Subject, template.HtmlBody, template.TextBody)),
            cancellationToken);

        if (stored is not null)
        {
            if (EmailTemplateLanguage.Parses(stored))
            {
                _cache.Set(key, stored, now);
                return stored;
            }

            LogStoredTemplateUnusable(key);
        }
        else
        {
            LogStoredTemplateMissing(key);
        }

        // Not cached, so a corrected row is picked up on the very next send rather than after the
        // cache lifetime.
        var shipped = EmailTemplateDefaults.Find(key)
                      ?? throw new InvalidOperationException(
                          $"No email template exists for \"{key}\", either stored or shipped.");

        return new EmailTemplateDraft(shipped.Subject, shipped.HtmlBody, shipped.TextBody);
    }

    private static string WithoutControlCharacters(string value) =>
        new([.. value.Where(character => !char.IsControl(character))]);

    [LoggerMessage(
        EventId = 2720,
        Level = LogLevel.Warning,
        Message = "Email template {Key} has no stored row; the shipped default is being used.")]
    private partial void LogStoredTemplateMissing(string key);

    [LoggerMessage(
        EventId = 2721,
        Level = LogLevel.Warning,
        Message = "Stored email template {Key} does not parse; the shipped default is being used instead.")]
    private partial void LogStoredTemplateUnusable(string key);
}
