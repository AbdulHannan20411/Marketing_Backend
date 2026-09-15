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
