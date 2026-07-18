namespace TravelApi.Application.DTOs;

public class HotelBookingDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public Guid? RatePublicId { get; set; }
    public string HotelName { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public DateTime CheckIn { get; set; }
    public DateTime CheckOut { get; set; }
    public int Nights { get; set; }
    public int Rooms { get; set; } = 1;
    public int Adults { get; set; } = 2;
    public int Children { get; set; } = 0;
    public string RoomType { get; set; } = "Standard";
    public string? MealPlan { get; set; }
    public string Status { get; set; } = "Pendiente";
    public string? ConfirmationNumber { get; set; }
    // Auditoria ERP 2026-06-12 (item 5): fecha limite de pago al operador. Se expone para que la ficha
    // la muestre/edite (round-trip) y para la alarma de pago. Aditivo, null = no informada. NO es dato
    // de costo (es solo una fecha): no se enmascara. Ver HotelBooking.OperatorPaymentDeadline.
    public DateTime? OperatorPaymentDeadline { get; set; }
    // Direccion del hotel (campo de "Mas detalles"). Se expone para que la ficha de edicion
    // la recupere y no la pise al guardar (round-trip). No es dato de costo.
    public string? Address { get; set; }
    public decimal SalePrice { get; set; }
    public decimal NetCost { get; set; }
    // Impuestos incluidos en el costo (mismo criterio que FlightSegmentDto.Tax). No suma al precio
    // que paga el cliente; se expone para el detalle. Default 0 en filas legacy.
    public decimal Tax { get; set; }
    /// <summary>
    /// Moneda en que se cotizo el servicio (trazabilidad, copiada del tarifario).
    /// Null = legacy / no informado. NO se usa todavia en calculos de saldo.
    /// </summary>
    public string? Currency { get; set; }
    /// <summary>
    /// ADR-017 (pill ambar "costo a confirmar", D7): el costo de este servicio quedo pendiente de
    /// confirmacion (producto nuevo sin costo conocido o costo de referencia viejo). SEGURIDAD: es una
    /// MARCA de costo — para callers sin cobranzas.see_cost se enmascara a false (guia UX linea 81:
    /// quien no ve costos no ve montos NI marcas de costo). CostToConfirmReason NO se expone (interno).
    /// Con flag EnableCatalogFindOrCreate OFF nadie la setea -> false (patron aditivo: el campo viaja
    /// siempre, el valor es neutro con flag OFF).
    /// </summary>
    public bool CostToConfirm { get; set; }
    /// <summary>
    /// ADR-017 (pill violeta "creado en esta venta"): true si el producto del tarifario vinculado a este
    /// servicio nacio inline durante una venta (Rate.CreatedInSale). NO es dato de costo: lo ven todos,
    /// no se enmascara. Con flag OFF no se crean rates asi -> false. Falso si el servicio no tiene Rate.
    /// </summary>
    public bool ProductCreatedInSale { get; set; }
    /// <summary>"TariffAtBookingTime" if a Rate was applied; "Manual" otherwise.</summary>
    public string SnapshotSource { get; set; } = "Manual";
    public string SourceKind { get; set; } = "Hotel";
    public string WorkflowStatus { get; set; } = "Solicitado";
    public string? RoomingAssignments { get; set; }
    // Auditoria de cancelacion (ADR-020): cuando se cancelo el servicio y quien lo cancelo. El front
    // los muestra como "Cancelado por X el DD/MM/YYYY". Null = servicio no cancelado. NO son datos de
    // costo ni fiscales (trazabilidad operativa): no se enmascaran. Mapean por convencion (mismo nombre
    // que la entidad HotelBooking). No hay motivo de cancelacion a nivel servicio (solo va al audit log).
    public DateTime? CancelledAt { get; set; }
    public string? CancelledByUserName { get; set; }
    // ADR-048 T4 (2026-07-17, spec "etiqueta Con multa"): si ESTE servicio anulado tiene multa del
    // operador en juego (confirmada o todavia en tramite), dice en que paso esta: "Pending" (en tramite,
    // o confirmada pero todavia sin cobrar del todo -> etiqueta ambar "Con multa") o "Collected"
    // (confirmada Y cobrada por completo -> etiqueta gris "Multa cobrada"). Null = este servicio no tiene
    // multa (no se muestra ninguna etiqueta). Se calcula reusando la MISMA fuente que ya alimenta el chip
    // Pago y el paso de multa de la ficha (BookingCancellationLine + OperatorPenaltySituations) — no es un
    // dato nuevo, es su proyeccion por servicio. Ver ReservaService.StampCancellationPenaltyPerServiceAsync.
    public string? CancellationPenaltyState { get; set; }
}

