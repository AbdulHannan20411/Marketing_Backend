using System.Collections.Concurrent;
using PdfSharp.Fonts;

namespace Marketing.Infrastructure.Billing;

/// <summary>
/// Finds a font for the invoice renderer.
/// </summary>
/// <remarks>
/// PDFsharp on .NET ships no font discovery, so without a resolver the first render throws — inside
/// a download the customer asked for. This probes the usual locations on Windows and on the Linux
/// images a container would use, rather than embedding a font file in the repository.
/// <para>
/// If a deployment has no fonts at all, that is a deliberate loud failure with a message naming what
/// to install. Falling back to a metrics-only default would render an invoice with the wrong glyph
/// widths, and a financial document that silently looks broken is worse than one that fails.
/// </para>
/// </remarks>
public sealed class InvoiceFontResolver : IFontResolver
{
    /// <summary>Family name the renderer asks for.</summary>
    public const string FamilyName = "Invoice Sans";

    /// <summary>
    /// Candidate font files, most preferred first, as (regular, bold) pairs.
    /// </summary>
    /// <remarks>
    /// All are metrically ordinary sans faces. Liberation and DejaVu are what the common .NET Linux
    /// base images carry once <c>fontconfig</c> is present; the Windows entries cover a developer
    /// machine and Windows hosting.
    /// </remarks>
    private static readonly (string Regular, string Bold)[] Candidates =
    [
        (@"C:\Windows\Fonts\segoeui.ttf", @"C:\Windows\Fonts\segoeuib.ttf"),
        (@"C:\Windows\Fonts\arial.ttf", @"C:\Windows\Fonts\arialbd.ttf"),
        ("/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
         "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf"),
        ("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
         "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"),
        ("/usr/share/fonts/TTF/DejaVuSans.ttf", "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf"),
    ];

    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    private const string RegularFace = "invoice-regular";
    private const string BoldFace = "invoice-bold";

    /// <inheritdoc />
    public byte[]? GetFont(string faceName)
    {
        // Cached, because a resolver is consulted per face per document and reading a few hundred
        // kilobytes off disk for every invoice download is pointless work.
        return Cache.GetOrAdd(faceName, Load);

        static byte[] Load(string face)
        {
            var pair = Candidates.FirstOrDefault(candidate => File.Exists(candidate.Regular));

            if (pair.Regular is null)
            {
                throw new InvalidOperationException(
                    "No font was found for invoice rendering. Install a sans-serif TrueType font — "
                    + "on Debian or Ubuntu, 'apt-get install fonts-liberation'.");
            }

            var wanted = string.Equals(face, BoldFace, StringComparison.Ordinal) && File.Exists(pair.Bold)
                ? pair.Bold
                : pair.Regular;

            return File.ReadAllBytes(wanted);
        }
    }

    /// <inheritdoc />
    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        // Italic is mapped onto the regular face rather than refused: nothing in the invoice layout
        // asks for it, and a missing face would fail the render instead of the document simply not
        // being slanted.
        new FontResolverInfo(bold ? BoldFace : RegularFace);
}
