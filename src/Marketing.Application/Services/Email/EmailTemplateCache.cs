using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Marketing.Application.Services.Email;

/// <summary>
/// Holds resolved templates between sends.
/// </summary>
/// <remarks>
/// In-process and deliberately small: there are a handful of templates, and an expiry bounds how long
/// an edit made on another instance takes to reach this one. Saving or resetting a template evicts it
/// here immediately; other instances pick the change up within <see cref="Lifetime"/>.
/// <para>
/// The current time is passed in rather than read from a clock, so this can be a singleton with no
/// dependencies whose lifetime could be shorter than its own.
/// </para>
/// </remarks>
public sealed class EmailTemplateCache
{
    /// <summary>How long a resolved template is served before it is read again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Returns a cached template that has not yet expired.</summary>
    /// <param name="key">Template key.</param>
    /// <param name="now">The current time.</param>
    /// <param name="draft">The cached template, when found.</param>
    public bool TryGet(string key, DateTimeOffset now, [NotNullWhen(true)] out EmailTemplateDraft? draft)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresOn > now)
        {
            draft = entry.Draft;
            return true;
        }

        draft = null;
        return false;
    }

    /// <summary>Caches a resolved template.</summary>
    /// <param name="key">Template key.</param>
    /// <param name="draft">The template.</param>
    /// <param name="now">The current time.</param>
    public void Set(string key, EmailTemplateDraft draft, DateTimeOffset now) =>
        _entries[key] = new Entry(draft, now.Add(Lifetime));

    /// <summary>Drops a template, so the next send reads it again.</summary>
    /// <param name="key">Template key.</param>
    public void Evict(string key) => _entries.TryRemove(key, out _);

    private sealed record Entry(EmailTemplateDraft Draft, DateTimeOffset ExpiresOn);
}
