using System.Globalization;
using Marketing.Application.Services.Billing;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Marketing.Infrastructure.Billing;

/// <summary>
/// Draws an invoice with MigraDoc.
/// </summary>
/// <remarks>
/// PDFsharp/MigraDoc rather than a nicer fluent library, because it is MIT: this document is
/// generated for paying customers of a commercial product, and a dependency whose licence turns on
/// the licensee's revenue is a liability to take on for one page of output.
/// <para>
/// Everything drawn here arrives on <see cref="InvoiceDocument"/>. The renderer performs no lookups,
/// so formatting a document can never read across a tenant boundary.
/// </para>
/// </remarks>
public sealed class MigraDocInvoiceRenderer : IInvoiceRenderer
{
    /// <summary>Brand green, matching the application's primary colour.</summary>
    private static readonly Color Brand = new(0x16, 0xA3, 0x4A);

    private static readonly Color Ink = new(0x1E, 0x29, 0x3B);
    private static readonly Color Muted = new(0x6B, 0x72, 0x80);
    private static readonly Color Line = new(0xE5, 0xE7, 0xEB);

    static MigraDocInvoiceRenderer()
    {
        // PDFsharp on .NET has no font discovery of its own outside Windows GDI, so a resolver is
        // installed once for the process. Without it the first render throws rather than falling
        // back, and it throws inside a download the customer asked for.
        GlobalFontSettings.FontResolver ??= new InvoiceFontResolver();
    }

    /// <inheritdoc />
    public string ContentType => "application/pdf";

    /// <inheritdoc />
    public Task<byte[]> RenderAsync(InvoiceDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var pdf = new Document { Info = { Title = $"Invoice {document.Invoice.Number}", Author = document.IssuerName } };

        var style = pdf.Styles[StyleNames.Normal]!;

        style.Font.Name = InvoiceFontResolver.FamilyName;
        style.Font.Size = 10;
        style.Font.Color = Ink;

        var section = pdf.AddSection();

        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(2);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        WriteHeader(section, document);
        WriteParties(section, document);
        WriteLines(section, document);
        WriteTotals(section, document);
        WriteFooter(section, document);

        var renderer = new PdfDocumentRenderer { Document = pdf };

        renderer.RenderDocument();

        using var buffer = new MemoryStream();

        renderer.PdfDocument.Save(buffer);

        return Task.FromResult(buffer.ToArray());
    }

    /// <summary>The invoice number, its status and when it was issued.</summary>
    private static void WriteHeader(Section section, InvoiceDocument document)
    {
        var invoice = document.Invoice;

        var title = section.AddParagraph(document.IssuerName);

        title.Format.Font.Size = 18;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = Brand;
        title.Format.SpaceAfter = Unit.FromPoint(2);

        var subtitle = section.AddParagraph("INVOICE");

        subtitle.Format.Font.Size = 22;
        subtitle.Format.Font.Bold = true;
        subtitle.Format.SpaceAfter = Unit.FromPoint(12);

        var meta = section.AddTable();

        meta.Borders.Width = 0;
        meta.AddColumn(Unit.FromCentimeter(8.5));
        meta.AddColumn(Unit.FromCentimeter(8.5));

        AddMetaRow(meta, "Invoice number", invoice.Number);
        AddMetaRow(meta, "Status", invoice.Status.ToString());
        AddMetaRow(meta, "Issued", Date(invoice.IssuedAt));
        AddMetaRow(meta, "Due", Date(invoice.DueAt));

        if (invoice.PaidAt is { } paid)
        {
            AddMetaRow(meta, "Paid", Date(paid));
        }

        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(14);
    }

    /// <summary>Who the invoice is addressed to.</summary>
    private static void WriteParties(Section section, InvoiceDocument document)
    {
        var heading = section.AddParagraph("Billed to");

        heading.Format.Font.Bold = true;
        heading.Format.Font.Color = Muted;
        heading.Format.Font.Size = 9;
        heading.Format.SpaceAfter = Unit.FromPoint(4);

        // The billing profile when there is one, the organisation name when there is not. An empty
        // address block on a financial document looks like a rendering fault rather than a blank
        // the customer never filled in.
        foreach (var line in AddressLines(document))
        {
            section.AddParagraph(line).Format.SpaceAfter = Unit.FromPoint(1);
        }

        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(14);
    }

    /// <summary>The single line this platform bills: a plan, for a period.</summary>
    private static void WriteLines(Section section, InvoiceDocument document)
    {
        var invoice = document.Invoice;
        var table = section.AddTable();

        table.Borders.Width = 0;
        table.Rows.LeftIndent = 0;

        table.AddColumn(Unit.FromCentimeter(10.4));
        table.AddColumn(Unit.FromCentimeter(3.3));
        table.AddColumn(Unit.FromCentimeter(3.3));

        var header = table.AddRow();

        header.Shading.Color = new Color(0xF6, 0xFF, 0xF8);
        header.Format.Font.Bold = true;
        header.Format.Font.Size = 9;
        header.Format.Font.Color = Muted;
        header.TopPadding = Unit.FromPoint(6);
        header.BottomPadding = Unit.FromPoint(6);

        header.Cells[0].AddParagraph("Description");
        header.Cells[1].AddParagraph("Period");
        header.Cells[2].AddParagraph("Amount");
        header.Cells[1].Format.Alignment = ParagraphAlignment.Right;
        header.Cells[2].Format.Alignment = ParagraphAlignment.Right;

        var row = table.AddRow();

        row.TopPadding = Unit.FromPoint(8);
        row.BottomPadding = Unit.FromPoint(8);
        row.Borders.Bottom.Width = 0.5;
        row.Borders.Bottom.Color = Line;

        row.Cells[0].AddParagraph($"{invoice.PlanName} plan — billed {invoice.BillingCycle.ToString().ToLowerInvariant()}");
        row.Cells[1].AddParagraph($"{Date(invoice.PeriodStart)} – {Date(invoice.PeriodEnd)}");
        row.Cells[2].AddParagraph(Money(invoice.Amount, invoice.Currency));
        row.Cells[1].Format.Alignment = ParagraphAlignment.Right;
        row.Cells[2].Format.Alignment = ParagraphAlignment.Right;
    }

    /// <summary>Subtotal, tax and the total due.</summary>
    private static void WriteTotals(Section section, InvoiceDocument document)
    {
        var invoice = document.Invoice;
        var table = section.AddTable();

        table.Borders.Width = 0;
        table.AddColumn(Unit.FromCentimeter(13.7));
        table.AddColumn(Unit.FromCentimeter(3.3));

        AddTotalRow(table, "Subtotal", Money(invoice.Amount, invoice.Currency), bold: false);

        if (invoice.Tax > 0)
        {
            AddTotalRow(table, "Tax", Money(invoice.Tax, invoice.Currency), bold: false);
        }

        var total = AddTotalRow(
            table,
            "Total",
            Money(invoice.Amount + invoice.Tax, invoice.Currency),
            bold: true);

        total.Borders.Top.Width = 0.75;
        total.Borders.Top.Color = Line;
        total.TopPadding = Unit.FromPoint(6);
    }

    /// <summary>Tax identifier and the closing note.</summary>
    private static void WriteFooter(Section section, InvoiceDocument document)
    {
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(18);

        if (document.BillTo?.TaxId is { Length: > 0 } taxId)
        {
            var tax = section.AddParagraph($"Tax registration: {taxId}");

            tax.Format.Font.Size = 9;
            tax.Format.Font.Color = Muted;
        }

        var note = section.AddParagraph(
            document.Invoice.PaidAt is null
                ? $"Payable by {Date(document.Invoice.DueAt)}."
                : $"Paid on {Date(document.Invoice.PaidAt.Value)}. Thank you.");

        note.Format.Font.Size = 9;
        note.Format.Font.Color = Muted;
    }

    /// <summary>The address block, falling back to the organisation name.</summary>
    private static IEnumerable<string> AddressLines(InvoiceDocument document)
    {
        var profile = document.BillTo;

        var name = profile?.CompanyName is { Length: > 0 } company ? company : document.WorkspaceName;

        yield return name;

        if (profile is null)
        {
            yield break;
        }

        foreach (var line in new[] { profile.AddressLine1, profile.AddressLine2 })
        {
            if (line is { Length: > 0 })
            {
                yield return line;
            }
        }

        var locality = string.Join(
            ", ",
            new[] { profile.City, profile.Region, profile.PostalCode }.Where(part => part is { Length: > 0 }));

        if (locality.Length > 0)
        {
            yield return locality;
        }

        foreach (var line in new[] { profile.Country, profile.BillingEmail })
        {
            if (line is { Length: > 0 })
            {
                yield return line;
            }
        }
    }

    private static void AddMetaRow(Table table, string label, string value)
    {
        var row = table.AddRow();

        row.TopPadding = Unit.FromPoint(1);
        row.BottomPadding = Unit.FromPoint(1);

        var key = row.Cells[0].AddParagraph(label);

        key.Format.Font.Color = Muted;
        key.Format.Font.Size = 9;

        var text = row.Cells[1].AddParagraph(value);

        text.Format.Alignment = ParagraphAlignment.Right;
        text.Format.Font.Size = 9;
    }

    private static Row AddTotalRow(Table table, string label, string value, bool bold)
    {
        var row = table.AddRow();

        row.TopPadding = Unit.FromPoint(3);
        row.BottomPadding = Unit.FromPoint(3);
        row.Format.Font.Bold = bold;

        var key = row.Cells[0].AddParagraph(label);

        key.Format.Alignment = ParagraphAlignment.Right;

        var text = row.Cells[1].AddParagraph(value);

        text.Format.Alignment = ParagraphAlignment.Right;

        return row;
    }

    /// <summary>Formats money as the currency code and the amount, invariantly.</summary>
    /// <remarks>
    /// The ISO code rather than a symbol: this platform bills in several currencies and the reader
    /// of a PDF has no other context to tell "$" apart from "$".
    /// </remarks>
    private static string Money(decimal amount, string currency) =>
        $"{currency} {amount.ToString("N2", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Formats a date invariantly, in an order no reader can misinterpret.
    /// </summary>
    /// <remarks>
    /// A named month rather than a numeric one, because 04/08/2026 means two different days
    /// depending on which side of the Atlantic the customer reads it, and an invoice date is
    /// contractual.
    /// </remarks>
    private static string Date(DateTimeOffset value) =>
        value.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
