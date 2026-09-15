using System.Linq.Expressions;
using Marketing.DataAccess.Entities;

namespace Marketing.Application.Services;

/// <summary>Which stored templates belong to the WhatsApp Business Account connected now.</summary>
/// <remarks>
/// Templates are stored per tenant, and a sync never deletes one. So when a tenant moved from Meta's
/// test number to their own, every template synced from the test account - <c>hello_world</c> above
/// all - stayed listed as approved, and failed only when a campaign actually sent it. Meta approves a
/// template for one account; on any other it does not exist.
/// <para>
/// Drafts that have never reached Meta carry no account and count as everyone's. With no account
/// connected nothing is filtered: nothing can be sent either, and the connection check says so.
/// </para>
/// </remarks>
public static class TemplateAccount
{
    /// <summary>The rule as a query filter.</summary>
    /// <param name="connectedWabaId">The connected account, or null when none is.</param>
    public static Expression<Func<MessageTemplate, bool>> BelongsTo(string? connectedWabaId)
    {
        if (string.IsNullOrEmpty(connectedWabaId))
        {
            return template => true;
        }

        return template => template.WabaId == connectedWabaId
                           || (template.WabaId == null && template.MetaTemplateId == null);
    }

    /// <summary>The same rule, for a template already loaded.</summary>
    /// <param name="templateWabaId">Account the template was synced from.</param>
    /// <param name="metaTemplateId">Meta's id for the template, null for a local draft.</param>
    /// <param name="connectedWabaId">The connected account, or null when none is.</param>
    public static bool IsOn(string? templateWabaId, string? metaTemplateId, string? connectedWabaId) =>
        string.IsNullOrEmpty(connectedWabaId)
        || string.Equals(templateWabaId, connectedWabaId, StringComparison.Ordinal)
        || (templateWabaId is null && metaTemplateId is null);
}
