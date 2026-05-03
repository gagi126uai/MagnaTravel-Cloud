namespace TravelApi.Application.DTOs;

public class TransitionReadinessDto
{
    public bool Allowed { get; set; }
    public string TargetStatus { get; set; } = string.Empty;

    // Pasajeros
    public int ExpectedPassengerCount { get; set; }
    public int CurrentPassengerCount { get; set; }
    public int MissingPassengers { get; set; }

    // Razones por las cuales NO se puede transicionar (mensajes accionables al usuario)
    public List<string> BlockingReasons { get; set; } = new();
}
