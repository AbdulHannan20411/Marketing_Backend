using AwesomeAssertions;
using Marketing.Application.Services;

namespace Marketing.UnitTests.Services;

/// <summary>How a Meta send failure is worded, and whether it is worth retrying.</summary>
public sealed class MetaSendErrorsTests
{
    [Fact]
    public void Hello_world_from_a_real_number_is_explained_and_not_retried()
    {
        // The failure that prompted this: the report said "Code 131058" and the message was retried
        // three times, when Meta refuses a test-only template for every recipient, every time.
        var error = MetaSendErrors.Describe(131058);

        error.Should().NotBeNull();
        error!.Permanent.Should().BeTrue();
        error.Reason.Should().Contain("test numbers");
    }

    [Fact]
    public void A_number_awaiting_display_name_approval_says_so_and_stops_trying()
    {
        // Meta's own words are "WhatsApp provided number needs display name approval before message
        // can be sent". Unlisted, the code was treated as a blip: eighteen retries in one morning,
        // and a report that said "Code 131037" to someone who could have fixed it in WhatsApp
        // Manager in a minute.
        var error = MetaSendErrors.Describe(131037);

        error.Should().NotBeNull();
        error!.Permanent.Should().BeTrue();
        error.Reason.Should().Contain("display name");
    }

    [Theory]
    [InlineData(80007)]
    [InlineData(130429)]
    [InlineData(131048)]
    [InlineData(131056)]
    public void Rate_limits_are_retried(int code)
    {
        // The same message can go through a little later, so these must keep their retries.
        MetaSendErrors.Describe(code)!.Permanent.Should().BeFalse();
    }

    [Theory]
    [InlineData(131026)]
    [InlineData(132001)]
    [InlineData(132015)]
    public void Failures_Meta_will_repeat_are_permanent(int code)
    {
        MetaSendErrors.Describe(code)!.Permanent.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(999_999)]
    public void An_unlisted_or_missing_code_is_left_to_the_raw_message(int? code)
    {
        // Null keeps the original message, which still carries the code and trace for support.
        MetaSendErrors.Describe(code).Should().BeNull();
    }
}
