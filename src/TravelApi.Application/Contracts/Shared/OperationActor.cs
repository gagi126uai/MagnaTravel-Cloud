namespace TravelApi.Application.Contracts.Shared;

public record OperationActor(
    string UserId,
    string UserName,
    IReadOnlyCollection<string> Roles)
{
    public bool IsAdmin => Roles.Any(role => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase));
}
