namespace TravelApi.Application.DTOs;

public class TransitionReadinessDto
{
    public bool Allowed { get; set; }
    public string TargetStatus { get; set; } = string.Empty;

    // Pasajeros
    public int ExpectedPassengerCount { get; set; }
    public int CurrentPassengerCount { get; set; }
    public int MissingPassengers { get; set; }

    // Composicion derivada de los servicios cargados (para construir slots del modal de
    // confirmar reserva). Si los servicios coinciden en composicion, esto refleja una
    // sola fuente de verdad. Si no, AmbiguousComposition=true y se toma el servicio de
    // mayor total como "anchor". El usuario puede ajustar manualmente en el modal.
    public int ExpectedAdults { get; set; }
    public int ExpectedChildren { get; set; }
    public int ExpectedInfants { get; set; }
    public bool AmbiguousComposition { get; set; }

    // Razones por las cuales NO se puede transicionar (mensajes accionables al usuario)
    public List<string> BlockingReasons { get; set; } = new();
}
