using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Servicio de ASISTENCIA AL VIAJERO (seguro de viaje tipo Assist Card, Universal Assistance, etc.).
/// Es un tipo de servicio PROPIO de la reserva, espejo de <see cref="HotelBooking"/> /
/// <see cref="FlightSegment"/>: tiene su propio costo neto, precio de venta, comision oculta,
/// proveedor (la compania aseguradora) y trazabilidad de moneda.
///
/// POR QUE existe como entidad propia (y no como ServicioReserva generico): la agencia lo vende
/// como una linea mas que SUMA al saldo del cliente, necesita aparecer en el voucher, en la cuenta
/// corriente del proveedor y en los calculos de fechas/pasajeros igual que los otros 4 tipos. Si
/// quedara fuera de alguno de esos calculos, el saldo del cliente descuadraria EN SILENCIO.
///
/// Los pasajeros cubiertos por la poliza se marcan nominalmente via
/// <see cref="PassengerServiceAssignment"/> (ServiceType = "Assistance"), igual que se asignan
/// pasajeros a una habitacion de hotel.
/// </summary>
public class AssistanceBooking : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    // Relaciones
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    // El proveedor es la compania aseguradora (Assist Card, Universal Assistance, etc.).
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    // Tarifario - snapshot de precios al momento de crear (opcional, igual que los otros bookings).
    public int? RateId { get; set; }
    public Rate? Rate { get; set; }

    // === Datos de negocio del seguro ===

    // Numero de poliza emitido por la aseguradora. Nullable: al cargar todavia puede no estar emitida.
    [MaxLength(100)]
    public string? PolicyNumber { get; set; }

    // Plan contratado (ej. "Plan 60", "Premium 150K", "Schengen"). Texto libre porque cada
    // aseguradora nombra sus planes distinto.
    [MaxLength(100)]
    public string? PlanType { get; set; }

    // Tope de cobertura. Es TEXTO a proposito (NO decimal): suele venir con moneda y formato propio
    // del plan (ej. "USD 60.000", "EUR 30.000 + COVID"). No se usa en ningun calculo de saldo.
    [MaxLength(100)]
    public string? CoverageLimit { get; set; }

    // Zona / destinos cubiertos (ej. "Mundial", "Europa (Schengen)", "America"). Texto libre.
    [MaxLength(200)]
    public string? CoverageZone { get; set; }

    // === Vigencia de la poliza ===
    // ValidFrom/ValidTo son fechas SIN HORA (date-only), exactamente como Hotel CheckIn/CheckOut:
    // representan "el dia desde / hasta el que cubre la poliza", no un instante universal. Por eso
    // las manejamos con el mismo criterio de Hotel y NO les inventamos hora — asi evitamos el lio
    // de timezone que sufren los campos con hora (ver NormalizeAirportWallClock en BookingService).
    // Son columnas 'timestamp with time zone' en Postgres y Npgsql exige Kind=Utc al escribir; eso
    // lo resuelve el ScheduleCalculator/AsUtc igual que Hotel.
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }

    // Pasajeros cubiertos por la poliza, separados por categoria (capacidad declarada del servicio).
    public int Adults { get; set; } = 1;
    public int Children { get; set; } = 0;

    // Numero de confirmacion que entrega el proveedor. Distinto de PolicyNumber: puede haber
    // confirmacion antes de que emitan el numero de poliza definitivo. Nullable.
    [MaxLength(100)]
    public string? ConfirmationNumber { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "Solicitado";

    // === Financiero (copiado del tarifario al momento de crear - inmutable) ===
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetCost { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }

    // Comision de la agencia. Igual que Hotel/Flight: se persiste pero NUNCA se expone en el DTO.
    [Column(TypeName = "decimal(18,2)")]
    public decimal Commission { get; set; }

    /// <summary>
    /// Impuestos incluidos en el costo (mismo criterio que <see cref="FlightSegment.Tax"/> y
    /// <see cref="Rate.Tax"/>): NO suma al precio que paga el cliente (SalePrice ya es el total),
    /// es un componente del costo. La ganancia/Commission = SalePrice - NetCost - Tax.
    /// Default 0 = sin impuesto informado (las filas previas a este campo quedan en 0, lo que deja
    /// la Commission inalterada porque SalePrice - NetCost - 0 = SalePrice - NetCost).
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Tax { get; set; }

    /// <summary>
    /// Moneda en que se COTIZO el servicio (copiada del tarifario al crearlo).
    /// Es metadato de TRAZABILIDAD: NO se usa todavia en calculos de saldo, pagos ni factura.
    /// Null = legacy / no informado (se asume ARS por compatibilidad hacia atras).
    /// </summary>
    [MaxLength(3)]
    public string? Currency { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    /// <summary>
    /// Paridad con ADR-009 (FC1.3): lista JSON de conceptos no reintegrables que se imputan al
    /// cliente fuera del costo neto/venta del seguro. Persistido como <c>jsonb</c> (consistencia
    /// con <see cref="HotelBooking.NonRefundableConceptsJson"/>). Null o array vacio = sin conceptos.
    /// Hoy no se completa desde la UI; existe para que Asistencia entre a la maquinaria de
    /// cancelacion/NC parcial sin un cambio de esquema futuro.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? NonRefundableConceptsJson { get; set; }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (item 5): fecha limite de pago a la aseguradora/operador. Mismo
    /// criterio que <see cref="HotelBooking.OperatorPaymentDeadline"/>. La Asistencia no tenia el
    /// campo antes de ADR-019 (el mockup viejo no lo incluia), pero la auditoria pide cobertura de
    /// pago al operador en todo servicio con costo/proveedor. Opcional. Date-only "de pared" Kind=Utc.
    /// </summary>
    public DateTime? OperatorPaymentDeadline { get; set; }

    /// <summary>
    /// ADR-017 F1.1 (decision D7): marca "costo a confirmar" (default false, ortogonal al workflow).
    /// Mismo criterio que <see cref="HotelBooking.CostToConfirm"/>. En F1.1 nadie lo setea.
    /// </summary>
    public bool CostToConfirm { get; set; } = false;

    /// <summary>
    /// ADR-017 F1.1 (D7): razon de la marca ("NoKnownCost" | "StaleReference"). Null si no hay marca.
    /// </summary>
    [MaxLength(30)]
    public string? CostToConfirmReason { get; set; }

    // === ADR-020 (2026-06-07): trazabilidad de confirmacion del operador y de cancelacion del servicio ===

    /// <summary>
    /// ADR-020: fecha en que el operador CONFIRMO/emitio este servicio (la estampa el motor de
    /// estados). Null = nunca confirmado. NO se borra al des-confirmar. Gobierna borrar-vs-cancelar
    /// y penalidades. En asistencia, "confirmado" incluye "voucher emitido" (el mapeo trata emit* como confirmado).
    /// </summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>ADR-020: cuando se cancelo el servicio (Status -> Cancelado). Null = no cancelado.</summary>
    public DateTime? CancelledAt { get; set; }

    [MaxLength(200)]
    public string? CancelledByUserId { get; set; }

    [MaxLength(200)]
    public string? CancelledByUserName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Capacidad de pasajeros del servicio (mismo contrato que Hotel/Package).
    public int GetExpectedPaxCount() => Adults + Children;
}
