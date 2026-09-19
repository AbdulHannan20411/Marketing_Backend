namespace Marketing.Application.Services.Security;

/// <summary>What a user agent says about the device, as a person would describe it.</summary>
/// <param name="Browser">Browser family: Chrome, Edge, Firefox, Safari, Opera or Other.</param>
/// <param name="OperatingSystem">Windows, macOS, Android, iOS, Linux or Other.</param>
/// <param name="DeviceType"><c>desktop</c>, <c>mobile</c> or <c>tablet</c>.</param>
public sealed record DeviceDescription(string Browser, string OperatingSystem, string DeviceType)
{
    /// <summary>"Chrome / Windows" - what the device list shows.</summary>
    public string Label => $"{Browser} / {OperatingSystem}";
}

/// <summary>Reads browser and operating system out of a user agent string.</summary>
/// <remarks>
/// Families only, never versions. The device list is evidence for a person deciding whether a login
/// is shared, and "Chrome / Windows" answers that; "Chrome 128.0.6613.120" answers nothing and changes
/// every few weeks, which would make one laptop look like several.
/// <para>
/// Order matters throughout: Edge and Opera also say "Chrome", Chrome also says "Safari", and every
/// Android browser also says "Linux".
/// </para>
/// </remarks>
public static class UserAgentParser
{
    /// <summary>Describes the device behind a user agent.</summary>
    /// <param name="userAgent">The raw header, or null.</param>
    public static DeviceDescription Parse(string? userAgent)
    {
        var agent = userAgent ?? string.Empty;

        return new DeviceDescription(BrowserOf(agent), OperatingSystemOf(agent), DeviceTypeOf(agent));
    }

    private static string BrowserOf(string agent)
    {
        if (Has(agent, "Edg/") || Has(agent, "Edge/"))
        {
            return "Edge";
        }

        if (Has(agent, "OPR/") || Has(agent, "Opera"))
        {
            return "Opera";
        }

        if (Has(agent, "Firefox/") || Has(agent, "FxiOS/"))
        {
            return "Firefox";
        }

        if (Has(agent, "Chrome/") || Has(agent, "CriOS/"))
        {
            return "Chrome";
        }

        return Has(agent, "Safari/") ? "Safari" : "Other";
    }

    private static string OperatingSystemOf(string agent)
    {
        if (Has(agent, "Windows"))
        {
            return "Windows";
        }

        if (Has(agent, "Android"))
        {
            return "Android";
        }

        if (Has(agent, "iPhone") || Has(agent, "iPad") || Has(agent, "iPod"))
        {
            return "iOS";
        }

        if (Has(agent, "Mac OS X") || Has(agent, "Macintosh"))
        {
            return "macOS";
        }

        return Has(agent, "Linux") || Has(agent, "X11") ? "Linux" : "Other";
    }

    private static string DeviceTypeOf(string agent)
    {
        if (Has(agent, "iPad") || Has(agent, "Tablet") || (Has(agent, "Android") && !Has(agent, "Mobile")))
        {
            return "tablet";
        }

        return Has(agent, "Mobile") || Has(agent, "iPhone") ? "mobile" : "desktop";
    }

    private static bool Has(string agent, string token) =>
        agent.Contains(token, StringComparison.OrdinalIgnoreCase);
}
