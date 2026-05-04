namespace TravelApi.Application.DTOs;

public record RevertStatusRequest(
    string TargetStatus,                       // "Reservado", "Presupuesto", "Operativo"
    string? AuthorizedBySuperiorUserId,        // requerido si el actor no es Admin
    string? Reason);                           // requerido si el actor no es Admin

public class RevertOptionsDto
{
    public string CurrentStatus { get; set; } = string.Empty;
    public List<string> AllowedTargets { get; set; } = new(); // estados validos a los que puede revertir
    public bool ActorIsAdmin { get; set; }
    public bool RequiresAuthorization { get; set; }            // !ActorIsAdmin
    public List<SupervisorOptionDto> Supervisors { get; set; } = new();
    public List<string> HardBlockers { get; set; } = new();    // razones que NO se pueden saltear ni siendo admin (ej: factura con CAE)
}

public class SupervisorOptionDto
{
    public string UserId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
}
