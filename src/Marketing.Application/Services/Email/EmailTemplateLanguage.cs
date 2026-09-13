using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Marketing.Application.Services.Email;

/// <summary>Whether a template is rendered into HTML or into plain text.</summary>
public enum EmailRenderMode
{
    /// <summary>Values are HTML-encoded, except the layout's raw content slot.</summary>
    Html,

    /// <summary>Values are inserted exactly as given.</summary>
    Text,
}

/// <summary>One piece of a parsed template.</summary>
public abstract record TemplateNode;

/// <summary>Literal text, emitted unchanged.</summary>
/// <param name="Value">The text.</param>
public sealed record TextNode(string Value) : TemplateNode;

/// <summary>A value to substitute.</summary>
/// <param name="Name">Variable name.</param>
/// <param name="Raw">Whether it was written with triple braces and is inserted without encoding.</param>
public sealed record VariableNode(string Name, bool Raw) : TemplateNode;

/// <summary>An <c>{{#if}}</c> block.</summary>
/// <param name="Name">The variable whose non-blankness picks the branch.</param>
/// <param name="WhenTrue">Rendered when the value is non-blank.</param>
/// <param name="WhenFalse">Rendered otherwise; empty when the block has no <c>{{else}}</c>.</param>
public sealed record ConditionNode(
    string Name,
    IReadOnlyList<TemplateNode> WhenTrue,
    IReadOnlyList<TemplateNode> WhenFalse) : TemplateNode;

/// <summary>A template's nodes, and anything wrong with how it was written.</summary>
/// <param name="Nodes">The parsed nodes.</param>
/// <param name="Problems">Human-readable parse problems. Empty when the template is well formed.</param>
public sealed record ParsedTemplate(IReadOnlyList<TemplateNode> Nodes, IReadOnlyList<string> Problems);

/// <summary>The three editable parts of an email template.</summary>
/// <param name="Subject">Subject line template.</param>
/// <param name="HtmlBody">HTML body template.</param>
/// <param name="TextBody">Plain-text body template.</param>
public sealed record EmailTemplateDraft(string Subject, string HtmlBody, string TextBody);

/// <summary>A fully rendered email.</summary>
/// <param name="Subject">Rendered subject, on one line.</param>
/// <param name="Html">Rendered HTML body.</param>
/// <param name="Text">Rendered plain-text body.</param>
public sealed record RenderedEmail(string Subject, string Html, string Text);

/// <summary>What a template is allowed to reference.</summary>
/// <param name="AllowedVariables">Every variable name the template may use.</param>
/// <param name="IsLayout">Whether this is the shared layout, which alone may use the raw content slot.</param>
public sealed record TemplateRules(IReadOnlyCollection<string> AllowedVariables, bool IsLayout);

/// <summary>
/// The email template language, ported from the web client's renderer.
/// </summary>
/// <remarks>
/// The editor previews templates in the browser, so a preview is only worth trusting if this renders
/// exactly what the browser renders. That is why the language is deliberately tiny - values,
/// one raw content slot, and a non-nesting <c>{{#if}}</c> - and why every rule and message here
/// follows the client's <c>email-template-renderer.ts</c> line for line rather than being reworded.
/// <para>
/// HTML encoding uses <see cref="WebUtility.HtmlEncode(string)"/>. The client's encoder matches it for
/// everything these templates emit; the only difference is that this one also writes characters from
/// U+00A0 to U+00FF as numeric entities, which renders identically.
/// </para>
/// </remarks>
public static partial class EmailTemplateLanguage
{
    /// <summary>The shared layout every other template is rendered inside.</summary>
    public const string LayoutKey = "layout.base";

    /// <summary>The variable the layout receives each email's rendered body in.</summary>
    public const string ContentVariable = "content";

    /// <summary>The raw content slot, as written in the layout.</summary>
    public const string ContentToken = "{{{content}}}";

    /// <summary>Longest subject a template may declare.</summary>
    public const int SubjectMaxLength = 200;

    private const string NamePattern = "[A-Za-z][A-Za-z0-9_]*";

    /// <summary>Supplied to every template and to the layout by the sending code.</summary>
    public static readonly IReadOnlyList<string> SystemVariables =
        ["appName", "appInitial", "supportEmail", "clientBaseUrl", "year"];

    /// <summary>Available to the layout only.</summary>
    public static readonly IReadOnlyList<string> LayoutVariables = ["subject", "preheader"];

    /// <summary>Parses a template into nodes, collecting every problem rather than stopping at the first.</summary>
    /// <param name="source">Template source.</param>
    public static ParsedTemplate Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var problems = new List<string>();
        var root = new List<TemplateNode>();

        // Nesting is refused, so one open condition is the most there can be.
        OpenCondition? open = null;

        List<TemplateNode> Target() => open is null ? root : open.InElse ? open.WhenFalse : open.WhenTrue;

        void PushText(string value)
        {
            if (value.Length == 0)
            {
                return;
            }

            var stray = value.IndexOf("{{", StringComparison.Ordinal);

            if (stray != -1)
            {
                var near = value.Substring(stray, Math.Min(32, value.Length - stray)).Split('\n')[0];
                problems.Add("Unrecognised tag near \"" + near + "\".");
            }

            Target().Add(new TextNode(value));
        }

        var last = 0;

        foreach (Match match in TagPattern().Matches(source))
        {
            PushText(source[last..match.Index]);
            last = match.Index + match.Length;

            if (match.Groups[1].Success)
            {
                Target().Add(new VariableNode(match.Groups[1].Value, Raw: true));
            }
            else if (match.Groups[2].Success)
            {
                if (open is not null)
                {
                    problems.Add(
                        "Conditions cannot be nested. Close {{#if " + open.Name + "}} before opening another.");
                }
                else
                {
                    open = new OpenCondition(match.Groups[2].Value);
                }
            }
            else if (match.Groups[3].Success)
            {
                if (open is null)
                {
                    problems.Add("{{else}} appears outside an {{#if}} block.");
                }
                else if (open.InElse)
                {
                    problems.Add("{{#if " + open.Name + "}} has more than one {{else}}.");
                }
                else
                {
                    open.InElse = true;
                }
            }
            else if (match.Groups[4].Success)
            {
                if (open is null)
                {
                    problems.Add("{{/if}} has no matching {{#if}}.");
                }
                else
                {
                    root.Add(new ConditionNode(open.Name, open.WhenTrue, open.WhenFalse));
                    open = null;
                }
            }
            else if (match.Groups[5].Success)
            {
                Target().Add(new VariableNode(match.Groups[5].Value, Raw: false));
            }
        }

        PushText(source[last..]);

        if (open is not null)
        {
            problems.Add("{{#if " + open.Name + "}} is never closed with {{/if}}.");
            root.Add(new ConditionNode(open.Name, open.WhenTrue, open.WhenFalse));
        }

        return new ParsedTemplate(root, problems);
    }

    /// <summary>Renders a template. A missing value renders as empty.</summary>
    /// <remarks>Validation, not rendering, is where unknown names are caught.</remarks>
    /// <param name="source">Template source.</param>
    /// <param name="values">Values by variable name.</param>
    /// <param name="mode">Whether values are HTML-encoded.</param>
    public static string Render(string source, IReadOnlyDictionary<string, string> values, EmailRenderMode mode)
    {
        ArgumentNullException.ThrowIfNull(values);

        var output = new StringBuilder();
        RenderNodes(Parse(source).Nodes, values, mode, output);
        return output.ToString();
    }

    /// <summary>Every variable a template refers to, including inside conditions, in first-seen order.</summary>
    /// <param name="source">Template source.</param>
    public static IReadOnlyList<string> UsedVariables(string source)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Walk(Parse(source).Nodes, node =>
        {
            var name = NameOf(node);

            if (name is not null && seen.Add(name))
            {
                names.Add(name);
            }
        });

        return names;
    }

    /// <summary>Whether all three parts of a draft parse without problems.</summary>
    /// <param name="draft">The draft.</param>
    public static bool Parses(EmailTemplateDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return Parse(draft.Subject).Problems.Count == 0
               && Parse(draft.HtmlBody).Problems.Count == 0
               && Parse(draft.TextBody).Problems.Count == 0;
    }

    /// <summary>
    /// Renders a body, and wraps it in the layout when one is given.
    /// </summary>
    /// <remarks>
    /// The rendered subject has line breaks flattened. A value carrying <c>\r\n</c> would otherwise
    /// become a header injection the moment it reached the mail library.
    /// </remarks>
    /// <param name="body">The email's own template.</param>
    /// <param name="layout">The shared layout, or null to render the body alone.</param>
    /// <param name="values">Values by variable name, including system variables.</param>
    public static RenderedEmail RenderEmail(
        EmailTemplateDraft body,
        EmailTemplateDraft? layout,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(values);

        var subject = SubjectBreaks().Replace(Render(body.Subject, values, EmailRenderMode.Text), " ").Trim();
        var html = Render(body.HtmlBody, values, EmailRenderMode.Html);
        var text = Render(body.TextBody, values, EmailRenderMode.Text);

        if (layout is null)
        {
            return new RenderedEmail(subject, html, text);
        }

        return new RenderedEmail(
            subject,
            Render(layout.HtmlBody, WithLayoutValues(values, subject, html), EmailRenderMode.Html),
            Render(layout.TextBody, WithLayoutValues(values, subject, text), EmailRenderMode.Text));
    }

    /// <summary>The rules a stored template is held to: its own variables, the system's, and the layout's when it is the layout.</summary>
    /// <param name="key">Template key.</param>
    /// <param name="ownVariables">The template's own variable names.</param>
    public static TemplateRules RulesFor(string key, IEnumerable<string> ownVariables)
    {
        ArgumentNullException.ThrowIfNull(ownVariables);

        var isLayout = string.Equals(key, LayoutKey, StringComparison.Ordinal);

        IEnumerable<string> allowed = [.. ownVariables, .. SystemVariables];

        if (isLayout)
        {
            allowed = allowed.Concat(LayoutVariables);
        }

        return new TemplateRules([.. allowed.Distinct(StringComparer.Ordinal)], isLayout);
    }

    /// <summary>Everything that would stop a draft being saved. Empty means valid.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="rules">What the template may reference.</param>
    public static IReadOnlyList<string> Validate(EmailTemplateDraft draft, TemplateRules rules)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(rules);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(draft.Subject))
        {
            problems.Add("The subject cannot be empty.");
        }

        if (draft.Subject.Contains('\r', StringComparison.Ordinal) || draft.Subject.Contains('\n', StringComparison.Ordinal))
        {
            problems.Add("The subject must be a single line.");
        }

        if (draft.Subject.Length > SubjectMaxLength)
        {
            problems.Add(
                "The subject is over " + SubjectMaxLength.ToString(CultureInfo.InvariantCulture)
                + " characters. Inboxes cut subjects off long before that.");
        }

        if (string.IsNullOrWhiteSpace(draft.HtmlBody))
        {
            problems.Add("The HTML body cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(draft.TextBody))
        {
            problems.Add(
                "The plain-text body cannot be empty. Some inboxes show nothing else, and spam filters read it.");
        }

        if (ScriptTag().IsMatch(draft.HtmlBody))
        {
            problems.Add("Remove the <script> tag. Email clients strip scripts, and some treat the message as spam.");
        }

        var allowed = new HashSet<string>(rules.AllowedVariables, StringComparer.Ordinal);
        var unknown = new List<string>();
        var unknownSeen = new HashSet<string>(StringComparer.Ordinal);
        var misusedRaw = false;

        (string Label, string Source, bool IsBody)[] fields =
        [
            ("Subject", draft.Subject, false),
            ("HTML body", draft.HtmlBody, true),
            ("Plain-text body", draft.TextBody, true),
        ];

        foreach (var (label, source, isBody) in fields)
        {
            var parsed = Parse(source);

            foreach (var problem in parsed.Problems)
            {
                problems.Add(label + ": " + problem);
            }

            var contentSlots = 0;

            Walk(parsed.Nodes, node =>
            {
                if (node is VariableNode { Raw: true } raw)
                {
                    if (rules.IsLayout && isBody && string.Equals(raw.Name, ContentVariable, StringComparison.Ordinal))
                    {
                        contentSlots++;
                    }
                    else
                    {
                        misusedRaw = true;
                    }

                    return;
                }

                if (NameOf(node) is { } name && !allowed.Contains(name) && unknownSeen.Add(name))
                {
                    unknown.Add(name);
                }
            });

            if (rules.IsLayout && isBody && contentSlots != 1)
            {
                problems.Add(
                    label + ": the layout must contain " + ContentToken
                    + " exactly once. It marks where each email's own content goes.");
            }
        }

        if (misusedRaw)
        {
            problems.Add(
                "Triple braces insert HTML without escaping, so they are reserved for " + ContentToken
                + " in the layout. Use double braces.");
        }

        foreach (var name in unknown)
        {
            problems.Add("{{" + name + "}} isn't available in this template.");
        }

        return problems;
    }

    private static void RenderNodes(
        IReadOnlyList<TemplateNode> nodes,
        IReadOnlyDictionary<string, string> values,
        EmailRenderMode mode,
        StringBuilder output)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case TextNode text:
                    output.Append(text.Value);
                    break;

                case VariableNode variable:
                    var value = ValueOf(values, variable.Name);
                    output.Append(mode == EmailRenderMode.Html && !variable.Raw ? WebUtility.HtmlEncode(value) : value);
                    break;

                case ConditionNode condition:
                    RenderNodes(
                        string.IsNullOrWhiteSpace(ValueOf(values, condition.Name)) ? condition.WhenFalse : condition.WhenTrue,
                        values,
                        mode,
                        output);
                    break;
            }
        }
    }

    private static string ValueOf(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) ? value ?? string.Empty : string.Empty;

    private static Dictionary<string, string> WithLayoutValues(
        IReadOnlyDictionary<string, string> values,
        string subject,
        string content) =>
        new(values, StringComparer.Ordinal)
        {
            ["subject"] = subject,
            [ContentVariable] = content,
        };

    private static string? NameOf(TemplateNode node) => node switch
    {
        VariableNode variable => variable.Name,
        ConditionNode condition => condition.Name,
        _ => null,
    };

    private static void Walk(IReadOnlyList<TemplateNode> nodes, Action<TemplateNode> visit)
    {
        foreach (var node in nodes)
        {
            visit(node);

            if (node is ConditionNode condition)
            {
                Walk(condition.WhenTrue, visit);
                Walk(condition.WhenFalse, visit);
            }
        }
    }

    [GeneratedRegex(
        @"\{\{\{\s*(" + NamePattern + @")\s*\}\}\}|\{\{\s*(?:#if\s+(" + NamePattern + @")|(else)|(/if)|("
        + NamePattern + @"))\s*\}\}")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex SubjectBreaks();

    [GeneratedRegex(@"<script\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptTag();

    private sealed class OpenCondition(string name)
    {
        public string Name { get; } = name;

        public List<TemplateNode> WhenTrue { get; } = [];

        public List<TemplateNode> WhenFalse { get; } = [];

        public bool InElse { get; set; }
    }
}
