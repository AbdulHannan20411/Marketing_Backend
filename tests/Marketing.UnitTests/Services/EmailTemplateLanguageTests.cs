using AwesomeAssertions;
using Marketing.Application.Services.Email;
using Marketing.DataAccess.Seed;

namespace Marketing.UnitTests.Services;

/// <summary>
/// The email template language.
/// </summary>
/// <remarks>
/// Ported case for case from the web client's <c>email-template-renderer.spec.ts</c>. The editor
/// previews in the browser, so the server rendering a template differently from that spec is a
/// preview that lies about what customers receive.
/// </remarks>
public sealed class EmailTemplateLanguageTests
{
    private static readonly TemplateRules BodyRules = new(["name", "inviterName", "appName"], IsLayout: false);
    private static readonly TemplateRules LayoutRules = new(["appName", "subject"], IsLayout: true);

    private static readonly EmailTemplateDraft Valid = new("Hi {{name}}", "<p>{{name}}</p>", "{{name}}");

    private static Dictionary<string, string> Values(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);

    [Fact]
    public void Html_encoding_matches_what_webutility_encodes()
    {
        EmailTemplateLanguage.Render("{{name}}", Values(("name", "<a href=\"x\">Tom & Jerry's</a>")), EmailRenderMode.Html)
            .Should().Be("&lt;a href=&quot;x&quot;&gt;Tom &amp; Jerry&#39;s&lt;/a&gt;");
    }

    [Fact]
    public void Double_brace_values_are_escaped_in_html_but_not_in_plain_text()
    {
        var values = Values(("name", "<b>Ayesha</b>"));

        EmailTemplateLanguage.Render("Hi {{name}}", values, EmailRenderMode.Html).Should().Be("Hi &lt;b&gt;Ayesha&lt;/b&gt;");
        EmailTemplateLanguage.Render("Hi {{name}}", values, EmailRenderMode.Text).Should().Be("Hi <b>Ayesha</b>");
    }

    [Fact]
    public void Whitespace_inside_tags_is_tolerated()
    {
        EmailTemplateLanguage.Render("{{ name }}", Values(("name", "A")), EmailRenderMode.Text).Should().Be("A");
    }

    [Fact]
    public void A_missing_value_renders_as_empty()
    {
        EmailTemplateLanguage.Render("[{{name}}]", Values(), EmailRenderMode.Html).Should().Be("[]");
    }

    [Fact]
    public void The_branch_is_chosen_by_whether_the_value_is_non_blank()
    {
        const string source = "{{#if inviterName}}{{inviterName}} invited you{{else}}You were invited{{/if}}";

        EmailTemplateLanguage.Render(source, Values(("inviterName", "Amara")), EmailRenderMode.Text).Should().Be("Amara invited you");
        EmailTemplateLanguage.Render(source, Values(("inviterName", "   ")), EmailRenderMode.Text).Should().Be("You were invited");
        EmailTemplateLanguage.Render(source, Values(), EmailRenderMode.Text).Should().Be("You were invited");
    }

    [Fact]
    public void Triple_brace_values_are_inserted_raw()
    {
        EmailTemplateLanguage.Render("<td>{{{content}}}</td>", Values(("content", "<p>x</p>")), EmailRenderMode.Html)
            .Should().Be("<td><p>x</p></td>");
    }

    [Fact]
    public void Nested_conditions_are_refused()
    {
        EmailTemplateLanguage.Parse("{{#if a}}{{#if b}}x{{/if}}{{/if}}").Problems
            .Should().Contain(problem => problem.Contains("cannot be nested", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unclosed_condition_is_reported()
    {
        EmailTemplateLanguage.Parse("{{#if a}}x").Problems.Should().Equal("{{#if a}} is never closed with {{/if}}.");
    }

    [Fact]
    public void Stray_else_and_end_tags_are_reported()
    {
        EmailTemplateLanguage.Parse("{{else}}{{/if}}").Problems.Should().HaveCount(2);
    }

    [Fact]
    public void An_unrecognised_tag_is_reported()
    {
        EmailTemplateLanguage.Parse("Hello {{user.name}}").Problems[0].Should().Contain("Unrecognised tag");
    }

    [Fact]
    public void Every_variable_used_is_listed_including_inside_conditions()
    {
        EmailTemplateLanguage.UsedVariables("{{a}} {{#if b}}{{c}}{{else}}{{d}}{{/if}}")
            .Order(StringComparer.Ordinal).Should().Equal("a", "b", "c", "d");
    }

    [Fact]
    public void The_body_is_wrapped_in_the_layout_and_the_layout_gets_the_rendered_subject()
    {
        var layout = new EmailTemplateDraft(
            "{{subject}}",
            "<title>{{subject}}</title><main>{{{content}}}</main>",
            "== {{{content}}} ==");

        var email = EmailTemplateLanguage.RenderEmail(
            new EmailTemplateDraft("Hi {{name}}", "<p>{{name}}</p>", "{{name}}"),
            layout,
            Values(("name", "A&B")));

        email.Subject.Should().Be("Hi A&B");
        email.Html.Should().Be("<title>Hi A&amp;B</title><main><p>A&amp;B</p></main>");
        email.Text.Should().Be("== A&B ==");
    }

    [Fact]
    public void Line_breaks_in_the_subject_are_flattened_so_a_value_cannot_inject_headers()
    {
        var email = EmailTemplateLanguage.RenderEmail(
            new EmailTemplateDraft("Hi {{name}}", "x", "x"),
            layout: null,
            Values(("name", "A\r\nBcc: victim@example.com")));

        email.Subject.Should().Be("Hi A Bcc: victim@example.com");
    }

    [Fact]
    public void A_valid_draft_has_no_problems()
    {
        EmailTemplateLanguage.Validate(Valid, BodyRules).Should().BeEmpty();
    }

    [Fact]
    public void A_single_line_non_empty_subject_and_both_bodies_are_required()
    {
        EmailTemplateLanguage.Validate(new EmailTemplateDraft(string.Empty, " ", string.Empty), BodyRules)
            .Should().HaveCount(3);

        EmailTemplateLanguage.Validate(Valid with { Subject = "a\nb" }, BodyRules)
            .Should().Contain("The subject must be a single line.");
    }

    [Fact]
    public void Script_tags_are_refused_whatever_their_case()
    {
        EmailTemplateLanguage.Validate(Valid with { HtmlBody = "<SCRIPT>x</SCRIPT>" }, BodyRules).Should().HaveCount(1);
    }

    [Fact]
    public void Variables_the_template_does_not_offer_are_refused()
    {
        EmailTemplateLanguage.Validate(Valid with { TextBody = "{{password}}" }, BodyRules)
            .Should().Equal("{{password}} isn't available in this template.");
    }

    [Fact]
    public void Triple_braces_are_reserved_for_the_layout_content_slot()
    {
        EmailTemplateLanguage.Validate(Valid with { HtmlBody = "{{{name}}}" }, BodyRules)
            .Should().Contain(problem => problem.StartsWith("Triple braces", StringComparison.Ordinal));
    }

    [Fact]
    public void The_layout_must_contain_the_content_slot_exactly_once_in_each_body()
    {
        var layout = new EmailTemplateDraft("{{subject}}", "{{{content}}}", "{{{content}}}");

        EmailTemplateLanguage.Validate(layout, LayoutRules).Should().BeEmpty();

        var missing = EmailTemplateLanguage.Validate(layout with { TextBody = "nothing" }, LayoutRules);
        missing.Should().ContainSingle().Which.Should().Contain("Plain-text body");

        EmailTemplateLanguage.Validate(layout with { HtmlBody = "{{{content}}}{{{content}}}" }, LayoutRules)
            .Should().HaveCount(1);
    }

    [Fact]
    public void Every_shipped_template_is_valid_under_its_own_rules()
    {
        // A fresh database is seeded from these. A shipped template the editor would refuse to save
        // is one no Super Admin could ever fix without first breaking something else.
        foreach (var template in EmailTemplateDefaults.All)
        {
            var rules = EmailTemplateLanguage.RulesFor(template.Key, template.Variables.Select(variable => variable.Name));
            var draft = new EmailTemplateDraft(template.Subject, template.HtmlBody, template.TextBody);

            EmailTemplateLanguage.Validate(draft, rules)
                .Should().BeEmpty($"the shipped '{template.Key}' template is what a fresh database is seeded with");
        }
    }

    [Fact]
    public void The_layout_ships_first_and_every_key_is_unique()
    {
        EmailTemplateDefaults.All.Should().NotBeEmpty();
        EmailTemplateDefaults.All[0].Key.Should().Be(EmailTemplateLanguage.LayoutKey);
        EmailTemplateDefaults.All.Select(template => template.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_shipped_email_renders_inside_the_layout_with_no_tag_left_behind()
    {
        var layoutDefault = EmailTemplateDefaults.Find(EmailTemplateLanguage.LayoutKey)!;
        var layout = new EmailTemplateDraft(layoutDefault.Subject, layoutDefault.HtmlBody, layoutDefault.TextBody);

        var system = Values(
            ("appName", "NextReach"),
            ("appInitial", "N"),
            ("supportEmail", "support@nextreach.io"),
            ("clientBaseUrl", "https://app.nextreach.io"),
            ("year", "2026"));

        foreach (var template in EmailTemplateDefaults.All.Where(template => template.Key != EmailTemplateLanguage.LayoutKey))
        {
            var values = new Dictionary<string, string>(system, StringComparer.Ordinal);

            foreach (var variable in template.Variables)
            {
                values[variable.Name] = variable.Sample;
            }

            var email = EmailTemplateLanguage.RenderEmail(
                new EmailTemplateDraft(template.Subject, template.HtmlBody, template.TextBody),
                layout,
                values);

            email.Subject.Should().NotBeNullOrWhiteSpace(template.Key);
            email.Html.Should().NotContain("{{", template.Key).And.Contain("NextReach", template.Key);
            email.Text.Should().NotContain("{{", template.Key);
        }
    }

    [Fact]
    public void The_default_hash_cannot_be_fooled_by_moving_text_between_parts()
    {
        // Concatenating the parts without a separator would make these two identical, so a subject
        // edit that happened to move a character into the body would read as "not customised".
        EmailTemplateDefaults.Hash("ab", "c", "d").Should().NotBe(EmailTemplateDefaults.Hash("a", "bc", "d"));
        EmailTemplateDefaults.Hash("a", "b", "c").Should().Be(EmailTemplateDefaults.Hash("a", "b", "c"));
    }
}

/// <summary>Caching resolved templates between sends.</summary>
public sealed class EmailTemplateCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly EmailTemplateDraft Draft = new("s", "h", "t");

    [Fact]
    public void A_cached_template_is_served_until_it_expires()
    {
        var cache = new EmailTemplateCache();
        cache.Set("auth.welcome", Draft, Now);

        cache.TryGet("auth.welcome", Now.AddMinutes(4), out var hit).Should().BeTrue();
        hit.Should().Be(Draft);

        cache.TryGet("auth.welcome", Now + EmailTemplateCache.Lifetime, out _)
            .Should().BeFalse("an edit made on another instance must show up within the lifetime");
    }

    [Fact]
    public void Evicting_a_template_takes_effect_immediately()
    {
        var cache = new EmailTemplateCache();
        cache.Set("auth.welcome", Draft, Now);

        cache.Evict("auth.welcome");

        cache.TryGet("auth.welcome", Now, out _).Should().BeFalse();
    }
}
