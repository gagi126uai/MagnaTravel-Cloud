namespace TravelApi.Models;

public class Cupo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ProductType { get; set; } = "Flight";
    public DateTime TravelDate { get; set; }
    public int Capacity { get; set; }
    public int OverbookingLimit { get; set; }
    public int Reserved { get; set; }
    public Guid RowVersion { get; set; } = Guid.NewGuid();

    public ICollection<CupoAssignment> Assignments { get; set; } = new List<CupoAssignment>();
}
