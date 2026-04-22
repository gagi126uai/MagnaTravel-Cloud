namespace TravelApi.Infrastructure.Services.ReservationsServiceProxy;

public class ReservationsServiceOptions
{
    public const string SectionName = "Services:Reservations";

    public string? BaseUrl { get; set; }
    public string? InternalToken { get; set; }
}
