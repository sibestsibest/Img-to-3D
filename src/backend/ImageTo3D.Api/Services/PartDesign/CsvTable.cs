using System.Globalization;
using System.Text;

namespace ImageTo3D.Api.Services.PartDesign;

/// <summary>
/// RFC 4180 CSV with a UTF-8 BOM (so Excel detects the encoding) and invariant number formatting.
/// Text cells that a spreadsheet would evaluate as a formula (leading = + - @, tab or CR) are
/// prefixed with an apostrophe; numeric cells are written as numbers and never altered.
/// </summary>
public sealed class CsvTable(params string[] header)
{
    private readonly List<string[]> _rows = [];

    public int ColumnCount => header.Length;

    public void Add(params string[] cells)
    {
        if (cells.Length != header.Length)
            throw new ArgumentException($"Expected {header.Length} cells, got {cells.Length}.", nameof(cells));
        _rows.Add(cells);
    }

    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
    }

    public static string Num(double? value) => value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "";

    public static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Bool(bool value) => value ? "yes" : "no";

    public byte[] ToBytes()
    {
        var sb = new StringBuilder();
        foreach (var row in _rows.Prepend(header))
            sb.Append(string.Join(',', row.Select(Quote))).Append("\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Quote(string cell) =>
        cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;
}
