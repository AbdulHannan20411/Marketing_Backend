using System.Net;
using System.Text;
using AwesomeAssertions;
using Marketing.Application.DTOs.Ai;
using Marketing.Application.Validators;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Marketing.UnitTests.Services;

/// <summary>
/// The Gemini provider: what it sends, and how every failure is translated.
/// </summary>
/// <remarks>
/// The rule most worth pinning is that nothing the provider says, and never the key, reaches the
/// caller. Every failure test also checks the key is absent from the exception.
/// </remarks>
public sealed class GeminiAiServiceTests
{
    private const string Key = "test-key-not-real";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (GeminiAiService Service, StubHandler Handler) Create(
        Func<CancellationToken, Task<HttpResponseMessage>> respond,
        string apiKey = Key,
        int timeoutSeconds = 30)
    {
        var handler = new StubHandler(respond);
        var options = Options.Create(new GeminiOptions
        {
            ApiKey = apiKey,
            Model = "gemini-test-model",
            TimeoutSeconds = timeoutSeconds,
        });

        return (new GeminiAiService(new HttpClient(handler), options, NullLogger<GeminiAiService>.Instance), handler);
    }

    private static Func<CancellationToken, Task<HttpResponseMessage>> Respond(HttpStatusCode status, string json) =>
        _ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task Returns_the_answer_and_sends_the_key_in_a_header_never_the_url()
    {
        var (service, handler) = Create(Respond(HttpStatusCode.OK, """
            {"candidates":[{"content":{"parts":[{"text":"thinking...","thought":true},{"text":"Smile! "},{"text":"20% off."}]},"finishReason":"STOP"}]}
            """));

        var answer = await service.GenerateAsync("Promo for a dental clinic", Ct);

        answer.Should().Be("Smile! 20% off.");
        handler.Request!.RequestUri!.ToString().Should()
            .Be("https://generativelanguage.googleapis.com/v1beta/models/gemini-test-model:generateContent")
            .And.NotContain(Key);
        handler.Request.Headers.GetValues("x-goog-api-key").Should().ContainSingle().Which.Should().Be(Key);
        handler.Body.Should().Contain("Promo for a dental clinic").And.Contain("maxOutputTokens");
    }

    [Fact]
    public async Task Refuses_without_calling_the_provider_when_no_key_is_configured()
    {
        var (service, handler) = Create(Respond(HttpStatusCode.OK, "{}"), apiKey: " ");

        var act = () => service.GenerateAsync("Anything", Ct);

        (await act.Should().ThrowAsync<BusinessRuleException>()).Which.ErrorCode.Should().Be("ai_not_configured");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_provider_429_is_a_rate_limit_the_user_can_wait_out()
    {
        var (service, _) = Create(Respond(HttpStatusCode.TooManyRequests,
            """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"Quota exceeded"}}"""));

        var act = () => service.GenerateAsync("Anything", Ct);

        var thrown = await act.Should().ThrowAsync<RateLimitException>();
        thrown.Which.ErrorCode.Should().Be("ai_rate_limited");
        thrown.Which.Message.Should().NotContain("Quota exceeded").And.NotContain(Key);
    }

    [Fact]
    public async Task A_provider_5xx_is_transient()
    {
        var (service, _) = Create(Respond(HttpStatusCode.ServiceUnavailable, """{"error":{"status":"UNAVAILABLE"}}"""));

        var act = () => service.GenerateAsync("Anything", Ct);

        (await act.Should().ThrowAsync<ExternalServiceException>()).Which.IsTransient.Should().BeTrue();
    }

    [Fact]
    public async Task An_invalid_key_is_a_non_transient_failure_that_does_not_echo_the_provider()
    {
        var (service, _) = Create(Respond(HttpStatusCode.BadRequest,
            """{"error":{"code":400,"status":"INVALID_ARGUMENT","message":"API key not valid."}}"""));

        var act = () => service.GenerateAsync("Anything", Ct);

        var thrown = await act.Should().ThrowAsync<ExternalServiceException>();
        thrown.Which.IsTransient.Should().BeFalse();
        thrown.Which.IsClientSafe.Should().BeFalse();
        thrown.Which.Message.Should().NotContain(Key);
    }

    [Theory]
    [InlineData("""{"promptFeedback":{"blockReason":"SAFETY"}}""")]
    [InlineData("""{"candidates":[{"content":{"parts":[]},"finishReason":"SAFETY"}]}""")]
    public async Task A_withheld_answer_is_reported_as_blocked(string json)
    {
        var (service, _) = Create(Respond(HttpStatusCode.OK, json));

        var act = () => service.GenerateAsync("Anything", Ct);

        (await act.Should().ThrowAsync<BusinessRuleException>()).Which.ErrorCode.Should().Be("ai_response_blocked");
    }

    [Theory]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"   "}]},"finishReason":"MAX_TOKENS"}]}""")]
    [InlineData("not json at all")]
    public async Task An_empty_or_malformed_response_is_an_external_failure(string body)
    {
        var (service, _) = Create(Respond(HttpStatusCode.OK, body));

        var act = () => service.GenerateAsync("Anything", Ct);

        await act.Should().ThrowAsync<ExternalServiceException>();
    }

    [Fact]
    public async Task A_network_failure_is_transient()
    {
        var (service, _) = Create(_ => throw new HttpRequestException("connection refused"));

        var act = () => service.GenerateAsync("Anything", Ct);

        (await act.Should().ThrowAsync<ExternalServiceException>()).Which.IsTransient.Should().BeTrue();
    }

    [Fact]
    public async Task Our_own_timeout_is_a_transient_failure_not_a_cancellation()
    {
        var (service, _) = Create(
            async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            timeoutSeconds: 1);

        var act = () => service.GenerateAsync("Anything", Ct);

        (await act.Should().ThrowAsync<ExternalServiceException>()).Which.IsTransient.Should().BeTrue();
    }

    [Fact]
    public async Task The_caller_cancelling_stays_a_cancellation()
    {
        var (service, _) = Create(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        caller.CancelAfter(50);

        var act = () => service.GenerateAsync("Anything", caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void The_validator_rejects_an_empty_prompt(string prompt)
    {
        new AiGenerateRequestValidator().Validate(new AiGenerateRequest(prompt)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void The_validator_enforces_the_maximum_length()
    {
        var validator = new AiGenerateRequestValidator();

        validator.Validate(new AiGenerateRequest(new string('a', AiGenerateRequest.MaxPromptLength))).IsValid.Should().BeTrue();
        validator.Validate(new AiGenerateRequest(new string('a', AiGenerateRequest.MaxPromptLength + 1))).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Admins_get_the_assistant_by_default_and_employees_do_not()
    {
        Permissions.IsKnown(Permissions.Ai.AssistantUse).Should().BeTrue();
        Permissions.ForRole(Roles.Admin).Should().Contain(Permissions.Ai.AssistantUse);
        Permissions.ForRole(Roles.Employee).Should().NotContain(Permissions.Ai.AssistantUse);
    }

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(cancellationToken);
        }
    }
}
