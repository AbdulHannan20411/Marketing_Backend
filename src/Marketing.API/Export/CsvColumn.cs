namespace Marketing.API.Export;

/// <summary>
/// One column of a CSV export: its heading, and how to read it from a row.
/// </summary>
/// <remarks>
/// Columns are declared in the controller that owns the resource, never accepted from the caller.
/// A generic export that let the client name entities and columns would be a query surface with no
/// schema: it would sidestep each resource's own permission, and the first time somebody added a
/// column to a projection it would become exportable whether or not it was meant to be. The shared
/// part is the writing, which is genuinely identical everywhere; the shape of each file is a
/// decision that belongs with the endpoint.
/// </remarks>
/// <typeparam name="TRow">Row type being exported.</typeparam>
/// <param name="Heading">Column heading, written in the first line.</param>
/// <param name="Value">
/// Reads the cell from a row. Returning <see langword="null"/> writes an empty cell, which is how
/// an absent value should appear - not as the word "null".
/// </param>
public sealed record CsvColumn<TRow>(string Heading, Func<TRow, string?> Value);
