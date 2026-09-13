using System.Reflection;
using System.Runtime.ExceptionServices;
using AwesomeAssertions;
using Marketing.Application.Services;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Turning a contacts export's explicit selection into rows to export.
/// </summary>
/// <remarks>
/// Identifiers that fail to parse used to be dropped silently. When all of them failed, the export
/// answered with a header and no rows - a file that says "these contacts are empty" when what
/// actually happened is "your selection was not understood".
/// </remarks>
public sealed class ContactExportSelectionTests
{
    private static readonly MethodInfo ResolveMethod =
        typeof(ContactWriteService).GetMethod("ResolveExportSelection", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static List<long>? Resolve(IReadOnlyList<string>? ids)
    {
        try
        {
            return (List<long>?)ResolveMethod.Invoke(null, [ids]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            // Reflection wraps whatever the method throws. Unwrapped so the test asserts on the
            // exception a real caller would see.
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    [Fact]
    public void No_selection_means_the_filters_apply_instead()
    {
        Resolve(null).Should().BeNull();
        Resolve([]).Should().BeNull();
    }

    [Fact]
    public void A_partly_stale_selection_keeps_what_still_identifies_a_contact()
    {
        // A selection can go stale between being ticked and being exported. One unreadable id must
        // not cost the operator the rest of their selection.
        var valid = PublicId.From(PublicId.Contact, 7);

        Resolve([valid, "not-an-id"]).Should().Equal(7L);
    }

    [Fact]
    public void A_selection_where_nothing_identifies_a_contact_is_refused_not_exported_empty()
    {
        var act = () => Resolve(["not-an-id", "123abc"]);

        act.Should().Throw<ValidationException>()
            .Which.Errors.Should().ContainKey("ids");
    }
}
