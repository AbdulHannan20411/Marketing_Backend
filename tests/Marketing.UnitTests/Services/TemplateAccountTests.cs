using AwesomeAssertions;
using Marketing.Application.Services;
using Marketing.DataAccess.Entities;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Which templates count as the connected WhatsApp account's.
/// </summary>
/// <remarks>
/// Written after a tenant moved from Meta's test number to their own and the campaign picker still
/// offered <c>hello_world</c> from the test account, which Meta then refused at send.
/// </remarks>
public sealed class TemplateAccountTests
{
    [Theory]
    [InlineData("waba-own", "meta-1", "waba-own", true)]
    [InlineData("waba-test", "meta-1", "waba-own", false)]
    [InlineData(null, "meta-1", "waba-own", false)]
    [InlineData(null, null, "waba-own", true)]
    [InlineData("waba-test", "meta-1", null, true)]
    public void A_template_belongs_to_the_account_it_was_synced_from(
        string? templateWabaId,
        string? metaTemplateId,
        string? connectedWabaId,
        bool expected)
    {
        // Rows, in order: synced from this account; left over from the test account; synced before
        // accounts were recorded, or no longer listed by Meta; a local draft Meta has never seen;
        // nothing connected, so nothing is hidden.
        TemplateAccount.IsOn(templateWabaId, metaTemplateId, connectedWabaId).Should().Be(expected);

        // The query filter and the in-memory check must never disagree, or the picker would offer a
        // template that campaign creation then refuses.
        var template = new MessageTemplate
        {
            Name = "hello_world",
            BodyText = "Hello World",
            WabaId = templateWabaId,
            MetaTemplateId = metaTemplateId,
        };

        TemplateAccount.BelongsTo(connectedWabaId).Compile()(template).Should().Be(expected);
    }
}
