namespace Marketing.Application.DTOs.WhatsApp;

/// <summary>
/// The values the browser needs to launch Meta Embedded Signup.
/// </summary>
/// <remarks>
/// Served rather than baked into the bundle. All three differ between the development, staging and
/// production Meta apps, and a hard-coded identifier means a build per environment plus a signup
/// flow that silently onboards customers into the wrong app when someone forgets.
/// <para>
/// Nothing here is secret. The app identifier and configuration identifier are published to every
/// browser that opens the dialog by design; the app secret, which is the value that matters, stays
/// on the server and is why the code exchange happens there.
/// </para>
/// </remarks>
/// <param name="AppId">Meta app identifier, passed to the JavaScript SDK's initialisation.</param>
/// <param name="ConfigId">Which signup flow to open, from Facebook Login for Business.</param>
/// <param name="GraphVersion">
/// The Graph API version the server talks to, so the browser's SDK and the server's calls cannot
/// drift onto different versions of the same flow.
/// </param>
public sealed record SignupConfigResponse(string AppId, string ConfigId, string GraphVersion);
