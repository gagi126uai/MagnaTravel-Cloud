namespace TravelApi.Application.Contracts.Files;

public record CreateFileRequest(
    string? Name,
    int? PayerId,
    DateTime? StartDate,
    string? Description
);
