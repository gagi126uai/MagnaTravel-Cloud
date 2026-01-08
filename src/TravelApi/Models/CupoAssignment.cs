namespace TravelApi.Models;

public class CupoAssignment
{
    public int Id { get; set; }
    public int CupoId { get; set; }
    public Cupo? Cupo { get; set; }
    public int? ReservationId { get; set; }
    public Reservation? Reservation { get; set; }
    public int Quantity { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}
