namespace TravelApi.Contracts.Bsp;

public record BspImportRequest(
    string FileName,
    string Format,
    string Content);
