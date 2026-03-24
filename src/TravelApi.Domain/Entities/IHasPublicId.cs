namespace TravelApi.Domain.Entities;

public interface IHasPublicId
{
    Guid PublicId { get; set; }
}
