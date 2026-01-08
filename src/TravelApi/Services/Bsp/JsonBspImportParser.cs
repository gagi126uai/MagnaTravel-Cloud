using System.Globalization;
using System.Text.Json;
using TravelApi.Services.Bsp.Models;

namespace TravelApi.Services.Bsp;

public class JsonBspImportParser : IBspImportParser
{
    public string Format => "json";

    public async Task<BspParsedResult> ParseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("El archivo JSON debe contener un arreglo de registros BSP.");
        }

        var records = new List<BspParsedRecord>();
        var lineNumber = 1;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var rawContent = element.GetRawText();
            var record = new BspParsedRecord(
                lineNumber,
                rawContent,
                element.GetProperty("ticketNumber").GetString() ?? string.Empty,
                element.GetProperty("reservationReference").GetString() ?? string.Empty,
                DateTime.Parse(element.GetProperty("issueDate").GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                element.GetProperty("currency").GetString() ?? "USD",
                element.GetProperty("baseAmount").GetDecimal(),
                element.GetProperty("taxAmount").GetDecimal(),
                element.GetProperty("totalAmount").GetDecimal());

            records.Add(record);
            lineNumber++;
        }

        return new BspParsedResult(records);
    }
}
