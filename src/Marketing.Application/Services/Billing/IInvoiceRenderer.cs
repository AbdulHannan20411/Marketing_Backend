using Marketing.Application.DTOs.Billing;

namespace Marketing.Application.Services.Billing;

/// <summary>
/// Everything that appears on a rendered invoice.
/// </summary>
/// <remarks>
/// Assembled by the billing service and handed to the renderer whole, so the layout code performs no
/// lookups of its own and cannot accidentally read across a tenant boundary while formatting a
/// document.
/// </remarks>
/// <param name="Invoice">The invoice, exactly as <c>/billing/history</c> reports it.</param>
/// <param name="BillTo">
/// The workspace's billing profile. Null when they have not filled one in, in which case the
/// document falls back to the organisation name rather than printing an empty address block.
/// </param>
/// <param name="WorkspaceName">Organisation the invoice is addressed to.</param>
/// <param name="IssuerName">Trading name of the platform, taken from the email sender identity.</param>
public sealed record InvoiceDocument(
    InvoiceResponse Invoice,
    BillingProfileResponse? BillTo,
    string WorkspaceName,
    string IssuerName);

/// <summary>
/// Renders an invoice as a PDF.
/// </summary>
/// <remarks>
/// A seam, like the import file readers and the failed-record workbook writer: the PDF library lives
/// in Infrastructure, and the layer that decides <em>what</em> an invoice says holds no opinion about
/// the format it is drawn in.
/// </remarks>
public interface IInvoiceRenderer
{
    /// <summary>Media type the rendered document is served under.</summary>
    public string ContentType { get; }

    /// <summary>
    /// Renders the document.
    /// </summary>
    /// <param name="document">Everything that appears on the invoice.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendered bytes.</returns>
    public Task<byte[]> RenderAsync(InvoiceDocument document, CancellationToken cancellationToken = default);
}
