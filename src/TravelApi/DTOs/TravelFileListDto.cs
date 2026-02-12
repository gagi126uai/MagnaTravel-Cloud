namespace TravelApi.DTOs;

public class TravelFileListDto
{
    public int Id { get; set; }
    public string FileNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Presupuesto";
    public string? CustomerName { get; set; }
    public decimal TotalSale { get; set; }
    public decimal Balance { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartDate { get; set; }
}
