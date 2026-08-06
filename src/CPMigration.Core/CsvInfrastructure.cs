using System.Text;
using Microsoft.VisualBasic.FileIO;
using CPMigration.Shared;

namespace CPMigration.Core;

public sealed record CsvFormat(Encoding Encoding, char Delimiter);

public static class CsvFormatDetector
{
    private static readonly char[] Delimiters = [';', ',', '\t', '|'];

    public static CsvFormat Detect(string path)
    {
        var encoding = DetectEncoding(path);
        var lines = File.ReadLines(path, encoding).Where(x => !string.IsNullOrWhiteSpace(x)).Take(20).ToArray();
        if (lines.Length == 0) return new CsvFormat(encoding, ';');

        var delimiter = Delimiters
            .Select(d => new
            {
                Delimiter = d,
                Counts = lines.Select(line => CountOutsideQuotes(line, d)).ToArray()
            })
            .Where(x => x.Counts.Max() > 0)
            .OrderByDescending(x => x.Counts.GroupBy(v => v).Max(g => g.Count()))
            .ThenByDescending(x => x.Counts.Average())
            .Select(x => x.Delimiter)
            .FirstOrDefault(';');

        return new CsvFormat(encoding, delimiter);
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var count = 0;
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
                quoted = !quoted;
            }
            else if (!quoted && line[i] == delimiter) count++;
        }
        return count;
    }

    private static Encoding DetectEncoding(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Span<byte> bom = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        var read = stream.Read(bom);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return new UTF8Encoding(true);
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;

        stream.Position = 0;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        try { while (reader.Read() >= 0) { } return new UTF8Encoding(false); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1252); }
    }
}

public sealed class CsvDocumentReader : IDisposable
{
    private readonly TextFieldParser _parser;
    public CsvFormat Format { get; }
    public string[] Headers { get; }
    public long LogicalRowNumber { get; private set; } = 1;

    public CsvDocumentReader(string path)
    {
        Format = CsvFormatDetector.Detect(path);
        _parser = new TextFieldParser(path, Format.Encoding)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        _parser.SetDelimiters(Format.Delimiter.ToString());
        Headers = NormalizeHeaders(_parser.ReadFields() ?? []);
    }

    public bool TryRead(out string[] fields, out Exception? error)
    {
        fields = [];
        error = null;
        if (_parser.EndOfData) return false;
        try
        {
            LogicalRowNumber++;
            fields = _parser.ReadFields() ?? [];
            return true;
        }
        catch (MalformedLineException ex)
        {
            LogicalRowNumber++;
            error = ex;
            return true;
        }
    }

    private static string[] NormalizeHeaders(string[] values)
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return values.Select((value, index) =>
        {
            var baseName = string.IsNullOrWhiteSpace(value) ? $"COLUNA_{index + 1}" : value.Trim();
            baseName = new string(baseName.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
            if (!used.TryAdd(baseName, 1))
            {
                used[baseName]++;
                baseName = $"{baseName}_{used[baseName]}";
            }
            return baseName;
        }).ToArray();
    }

    public void Dispose() => _parser.Dispose();
}
