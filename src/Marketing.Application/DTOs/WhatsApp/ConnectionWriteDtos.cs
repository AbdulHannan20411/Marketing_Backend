namespace Marketing.Application.DTOs.WhatsApp;

/// <summary>
/// Completes Meta Embedded Signup.
/// <para>
/// The client runs the signup flow in a popup and Meta hands it these three values back. The code
/// is exchanged for an access token server-side, because the app secret required to redeem it must
/// never reach the browser.
/// </para>
/// </summary>
/// <param name="Code">Authorisation code from the signup callback.</param>
/// <param name="WabaId">WhatsApp Business Account identifier from the callback.</param>
/// <param name="PhoneNumberId">Phone number identifier from the callback.</param>
public sealed record ConnectWhatsAppRequest(string Code, string WabaId, string PhoneNumberId);

/// <summary>
/// Connects an account using a token supplied directly, bypassing Embedded Signup.
/// <para>
/// An operator tool, restricted to platform staff. It exists so the messaging path can be
/// exercised against Meta's test number before an app has been through review - not as an
/// onboarding route. A token pasted here has arrived through an unaudited channel, which is why
/// tenant administrators cannot use it.
/// </para>
/// </summary>
/// <param name="AccessToken">A system-user access token with WhatsApp permissions.</param>
/// <param name="WabaId">WhatsApp Business Account identifier.</param>
/// <param name="PhoneNumberId">Phone number identifier to send from.</param>
public sealed record ManualConnectWhatsAppRequest(string AccessToken, string WabaId, string PhoneNumberId);
