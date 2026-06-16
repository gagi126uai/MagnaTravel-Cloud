namespace TravelApi.Application.DTOs;

/// <summary>
/// ADR-031 v2.1: "que pasajeros integran el SET de un servicio y que nombres le faltan" para que el
/// mini-form del front sepa a QUIEN pedirle los datos, exactamente como lo resuelve el gate del backend
/// (misma funcion <c>PassengerNominalRules.ResolveServiceSet</c> + misma regla por tipo). El front NO debe
/// reimplementar la regla del set ni la de los campos obligatorios: la pide aca.
/// </summary>
public class ServiceNominalCoverageDto
{
    /// <summary>Tipo de servicio (Hotel/Transfer/Package/Flight/Assistance/Generic), espejo del discriminator.</summary>
    public string ServiceType { get; set; } = string.Empty;

    /// <summary>Id interno del servicio resuelto (el del booking/segment).</summary>
    public int ServiceId { get; set; }

    /// <summary>PublicId del servicio, cuando se pudo resolver.</summary>
    public Guid? ServicePublicId { get; set; }

    /// <summary>
    /// true = el servicio tiene asignaciones explicitas (set = solo esos pasajeros). false = sin
    /// asignaciones, el set es TODA la reserva (default seguro). Sirve para el cartel "X de N".
    /// </summary>
    public bool HasExplicitAssignments { get; set; }

    /// <summary>Cantidad de pasajeros del SET (el "X" de "X de N").</summary>
    public int ServiceSetCount { get; set; }

    /// <summary>Cantidad total de pasajeros de la reserva (el "N" de "X de N").</summary>
    public int ReservaPassengerCount { get; set; }

    /// <summary>
    /// Pasajeros que integran el SET de este servicio (los que viajan en el). El front pide nombre/
    /// documento de estos, segun el tipo. NUNCA incluye el numero de documento (dato sensible): solo
    /// el publicId, el nombre y un flag de si ya tiene los datos obligatorios del tipo.
    /// </summary>
    public List<ServiceSetPassengerDto> ServiceSet { get; set; } = new();

    /// <summary>
    /// Mensaje accionable de lo que falta (mismo texto que lanzaria el gate al intentar resolver), o
    /// null si la cobertura nominal del set ya esta completa para este tipo. Sin numero de documento.
    /// </summary>
    public string? MissingMessage { get; set; }

    /// <summary>true si <see cref="MissingMessage"/> es null (cobertura completa). Atajo para el front.</summary>
    public bool IsComplete => MissingMessage is null;
}

/// <summary>
/// Pasajero del SET de un servicio, en su forma segura para el front. SIN numero de documento.
/// </summary>
public class ServiceSetPassengerDto
{
    public Guid PassengerPublicId { get; set; }
    public string FullName { get; set; } = string.Empty;

    /// <summary>true = es el TITULAR del set (primer pasajero por Id). Relevante para Hotel/Traslado.</summary>
    public bool IsLead { get; set; }

    /// <summary>
    /// true si este pasajero YA tiene los datos obligatorios para el tipo del servicio (asi el front
    /// resalta SOLO a los que faltan). No expone el documento; solo dice si esta completo.
    /// </summary>
    public bool HasRequiredDataForServiceType { get; set; }
}
