using TravelApi.Services.Bsp.Models;

namespace TravelApi.Services.Bsp;

public interface IBspImportParser
{
    string Format { get; }
    Task<BspParsedResult> ParseAsync(Stream stream, CancellationToken cancellationToken);
}
