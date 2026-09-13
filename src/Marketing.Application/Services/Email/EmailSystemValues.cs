using System.Globalization;
using Marketing.Application.Configurations;

namespace Marketing.Application.Services.Email;

/// <summary>
/// The values every email template receives, whatever it is.
/// </summary>
/// <remarks>
/// Built from configuration, never from the request. A link or a name taken from an inbound header
/// would let a caller put their own site into an email the platform sends.
/// </remarks>
public static class EmailSystemValues
{
    /// <summary>Builds the system values.</summary>
    /// <param name="options">Email configuration.</param>
    /// <param name="now">The current time, for the footer year.</param>
    public static IReadOnlyDictionary<string, string> Build(EmailOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);

        var appName = options.FromName.Trim();

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["appName"] = appName,

            // The logo mark. Upper-cased invariantly, so a Turkish locale cannot turn "i" into "İ".
            ["appInitial"] = appName.Length == 0 ? string.Empty : new string(char.ToUpperInvariant(appName[0]), 1),

            // Falls back to the sending address, which is usually a no-reply mailbox - a poor thing to
            // put in a "Need help?" footer, so the support address should be configured before launch.
            ["supportEmail"] = string.IsNullOrWhiteSpace(options.SupportAddress)
                ? options.FromAddress
                : options.SupportAddress.Trim(),

            ["clientBaseUrl"] = options.ClientBaseUrl.TrimEnd('/'),
            ["year"] = now.Year.ToString(CultureInfo.InvariantCulture),
        };
    }
}
