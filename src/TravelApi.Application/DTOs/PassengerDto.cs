namespace TravelApi.Application.DTOs;

public class PassengerDto
{
    public Guid PublicId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? DocumentType { get; set; }
    public string? DocumentNumber { get; set; }
    public DateTime? BirthDate { get; set; }
    // Auditoria ERP 2026-06-12 (item 8): vencimiento del pasaporte. Se expone para que el front lo
    // muestre/edite y para la alarma de vigencia. Aditivo (null = no informado). Ver Passenger.PassportExpiry.
    public DateTime? PassportExpiry { get; set; }
    public string? Nationality { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Gender { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// Obra "cada campo acepta solo lo que va en ese campo" (2026-07-31, TANDA 2): aviso NO bloqueante
    /// para mostrarle al vendedor despues de guardar (mismo patron que
    /// <see cref="ReservaDto.Warning"/>, el aviso de fechas de la reserva).
    ///
    /// <para>Hoy lo llena el alta/edicion de pasajero cuando el pasaporte cargado esta VENCIDO: la
    /// operacion sale bien igual (decision firmada del dueño: en la agencia se carga al pasajero antes de
    /// que renueve el pasaporte), pero la pantalla avisa. Null = sin aviso; tambien null en las lecturas
    /// (el listado de pasajeros de la reserva no recalcula avisos).</para>
    /// </summary>
    public string? Warning { get; set; }

    /// <summary>
    /// D2 (2026-07-31 tarde): semaforo de vencimiento de pasaporte CONTRA LAS FECHAS DEL VIAJE, calculado
    /// SIEMPRE en el backend (T-13: el front nunca deduce esto solo). A diferencia de <see cref="Warning"/>
    /// (que solo se llena al guardar, para el toast), estos dos campos se recalculan tambien en cada
    /// LECTURA del listado de pasajeros, para que la pantalla pinte el chip fijo apenas carga la fila
    /// (mockup firmado F11). Valores: "Expired" (rojo) / "Tight" (ambar) / null (sin aviso).
    /// </summary>
    public string? PassportAlertLevel { get; set; }

    /// <summary>Texto exacto del aviso de <see cref="PassportAlertLevel"/> (mismo texto en toast y chip).</summary>
    public string? PassportAlertText { get; set; }
}
