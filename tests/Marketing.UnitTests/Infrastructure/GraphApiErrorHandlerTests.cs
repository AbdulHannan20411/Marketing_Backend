using System.Net;
using System.Text;
using AwesomeAssertions;
using Marketing.Common.Exceptions;
using Marketing.Infrastructure.WhatsApp.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketing.UnitTests.Infrastructure;

/// <summary>
/// Turning a Graph API error into an exception a caller can act on, and a log line that is safe.
/// </summary>
public sealed class GraphApiErrorHandlerTests
{
    private const string HelloWorldRefusal =
        """
        {"error":{"message":"(#131058) Hello World templates can only be sent from the Public Test Numbers",
        "type":"OAuthException","code":131058,"fbtrace_id":"AXOYmpeGlbFy2"}}
        """;

    private sealed class CannedResponse(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private static async Task<ExternalServiceException> FailureFor(HttpStatusCode status, string body)
    {
        using var handler = new GraphApiErrorHandler(NullLogger<GraphApiErrorHandler>.Instance)
        {
            InnerHandler = new CannedResponse(status, body),
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.facebook.com/v21.0/1/messages");

        var send = () => invoker.SendAsync(request, TestContext.Current.CancellationToken);

        return (await send.Should().ThrowAsync<ExternalServiceException>()).Which;
    }

    [Fact]
    public async Task Meta_error_code_travels_with_the_exception()
    {
        var failure = await FailureFor(HttpStatusCode.BadRequest, HelloWorldRefusal);

        failure.ProviderErrorCode.Should().Be(131058);
        failure.IsTransient.Should().BeFalse();

        // Unchanged: onboarding reads "Code 190" out of this text to spot a rejected token.
        failure.Message.Should().Contain("Code 131058");
    }

    [Fact]
    public async Task Metas_reason_for_the_user_travels_with_the_exception_masked()
    {
        var failure = await FailureFor(
            HttpStatusCode.BadRequest,
            """
            {"error":{"message":"Invalid parameter","code":100,"error_subcode":2388024,
            "error_user_title":"Content already exists",
            "error_user_msg":"Content in this language already exists for +923001234567.","fbtrace_id":"T"}}
            """);

        failure.ProviderUserMessage.Should().Be("Content in this language already exists for +**********67.");
    }

    [Fact]
    public async Task No_user_message_leaves_it_null()
    {
        var failure = await FailureFor(HttpStatusCode.BadRequest, HelloWorldRefusal);

        failure.ProviderUserMessage.Should().BeNull();
    }

    [Fact]
    public async Task A_rate_limit_code_is_transient()
    {
        var failure = await FailureFor(
            HttpStatusCode.BadRequest,
            """{"error":{"message":"Rate limit hit","code":130429,"fbtrace_id":"T"}}""");

        failure.ProviderErrorCode.Should().Be(130429);
        failure.IsTransient.Should().BeTrue();
    }

    [Fact]
    public async Task A_body_that_is_not_json_leaves_no_code()
    {
        var failure = await FailureFor(HttpStatusCode.BadGateway, "<html>Bad gateway</html>");

        failure.ProviderErrorCode.Should().BeNull();
        failure.IsTransient.Should().BeTrue();
    }

    [Fact]
    public void Phone_numbers_keep_only_their_last_two_digits()
    {
        GraphErrorText.Mask("Recipient +923001234567 is not valid")
            .Should().Be("Recipient +**********67 is not valid");

        GraphErrorText.Mask("Recipient +44 7700 900123 is not valid")
            .Should().Be("Recipient +** **** ****23 is not valid");
    }

    [Fact]
    public void Meta_error_codes_stay_readable()
    {
        // Six digits, below the phone threshold: the code is the point of the message.
        const string message = "(#131058) Hello World templates can only be sent from the Public Test Numbers";

        GraphErrorText.Mask(message).Should().Be(message);
    }

    [Fact]
    public void Access_tokens_are_removed()
    {
        var token = "EAA" + new string('x', 40);

        var masked = GraphErrorText.Mask($"Invalid OAuth access token {token}.");

        masked.Should().Be("Invalid OAuth access token [token].");
    }

    [Fact]
    public void Long_messages_are_capped_and_empty_ones_stay_empty()
    {
        GraphErrorText.Mask(new string('a', 1_000)).Should().HaveLength(300);
        GraphErrorText.Mask(null).Should().BeEmpty();
        GraphErrorText.Mask("   ").Should().BeEmpty();
    }
}
