using System.Globalization;
using TravelApi.Services.Bsp.Models;

namespace TravelApi.Services.Bsp;

public class CsvBspImportParser : IBspImportParser
{
    public string Format => "csv";

    public async Task<BspParsedResult> ParseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var records = new List<BspParsedRecord>();
        var lineNumber = 0;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
            if (lineNumber == 0 && line.Contains("TicketNumber", StringComparison.OrdinalIgnoreCase))
            {
                lineNumber++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                lineNumber++;
                continue;
            }

            var columns = line.Split(',', StringSplitOptions.TrimEntries);
            if (columns.Length < 7)
            {
                throw new InvalidOperationException($"Formato CSV inválido en línea {lineNumber + 1}.");
            }

            var record = new BspParsedRecord(
                lineNumber + 1,
                line,
                columns[0],
                columns[1],
                DateTime.Parse(columns[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                columns[3],
                decimal.Parse(columns[4], CultureInfo.InvariantCulture),
                decimal.Parse(columns[5], CultureInfo.InvariantCulture),
                decimal.Parse(columns[6], CultureInfo.InvariantCulture));

            records.Add(record);
            lineNumber++;
        }

        return new BspParsedResult(records);
    }
}
