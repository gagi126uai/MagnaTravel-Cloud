using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class FlightSegment : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    
    // Relaciones
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }
    
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    
    /// <summary>
    /// ADR-018 (2026-06-06): nombre del producto tal como lo VIO/tipeo el vendedor en la ficha
    /// "producto-primero" (ej. "AEP-IGR LATAM"). Es la IDENTIDAD visible del segmento. Snapshot:
    /// se copia al crear y NO se re-deriva del Rate despues (preserva el principio de ADR-017 §6,
    /// igual que <see cref="HotelBooking.HotelName"/>). Null = carga por el modal viejo (la fila
    /// se sigue mostrando con la derivacion estructurada AirlineCode/FlightNumber/ruta).
    /// </summary>
    [MaxLength(200)]
    public string? ProductName { get; set; }

    // Datos del vuelo. ADR-018: estos 4 campos estructurados dejan de ser obligatorios. La ficha
    // "producto-primero" identifica el vuelo con un solo texto (ProductName) y NO pide aerolinea/
    // nro/origen/destino por separado; el modal viejo los sigue mandando. Null = no informado.
    [MaxLength(3)]
    public string? AirlineCode { get; set; } // AA, AR, LA

    [MaxLength(100)]
    public string? AirlineName { get; set; } // American Airlines

    [MaxLength(10)]
    public string? FlightNumber { get; set; } // 900

    [MaxLength(3)]
    public string? Origin { get; set; } // MIA

    [MaxLength(100)]
    public string? OriginCity { get; set; } // Miami

    [MaxLength(3)]
    public string? Destination { get; set; } // EZE
    
    [MaxLength(100)]
    public string? DestinationCity { get; set; } // Buenos Aires
    
    public DateTime DepartureTime { get; set; }

    // BUG 2 (2026-06-08): ArrivalTime es NULLABLE. Existen vuelos solo de ida: un segmento puede no
    // tener hora de llegada. El modelo es POR SEGMENTO (ida y vuelta = 2 segmentos distintos), asi que
    // esto NO modela "vuelta" — solo permite que un segmento quede sin hora de llegada. DepartureTime
    // sigue siendo obligatorio. Los consumidores (ReservaScheduleCalculator, validaciones) toleran null.
    public DateTime? ArrivalTime { get; set; }
    
    // Clase y equipaje. ADR-018 Ronda 7 (2026-06-06): la cabina deja de ser obligatoria —
    // null = "Sin especificar" (antes era NOT NULL con default "Economy"; la columna se
    // relaja en la migracion Adr017_M6).
    [MaxLength(20)]
    public string? CabinClass { get; set; } // Economy, Premium Economy, Business, First
    
    [MaxLength(50)]
    public string? Baggage { get; set; } // "23kg" o "2PC"
    
    // Ticket
    [MaxLength(50)]
    public string? TicketNumber { get; set; }
    
    [MaxLength(20)]
    public string? FareBase { get; set; } // Base tarifaria
    
    [MaxLength(20)]
    public string? PNR { get; set; } // Record locator
    
    // ADR-020 (2026-06-07): el default pasa de "HK" (confirmado) a "NN" (solicitado/"need").
    // El ciclo nuevo exige que un vuelo NUEVO nazca SIN confirmar (sino nace no-borrable y con
    // ConfirmedAt). "NN" cabe en MaxLength(2) y MapFlightStatus lo mapea a "Solicitado" por la
    // rama default. Las filas historicas conservan su Status real (el default de C# no las toca).
    [MaxLength(2)]
    public string Status { get; set; } = "NN"; // NN=solicitado, HK/TK/KK/KL=confirmado, UN/UC/HX/NO=cancelado

    // Numero de confirmacion que devuelve la aerolinea/operador para este segmento.
    // Es distinto del PNR/localizador (que sirve para gestionar la reserva en la GDS):
    // este campo guarda el comprobante de confirmacion que entrega el proveedor.
    // Nullable porque hay segmentos todavia "Solicitados" sin confirmar.
    [MaxLength(100)]
    public string? ConfirmationNumber { get; set; }

    // Cantidad de pasajeros que viajan EN ESTE segmento concreto.
    // No siempre coincide con el total de pasajeros de la reserva: en un mismo file
    // puede haber tramos con distinta cantidad de gente (ej. uno viaja solo la ida).
    // Nullable: los segmentos cargados antes de este campo quedan en null (no informado).
    public int? PassengerCount { get; set; }

    // Financiero (copiado del tarifario - inmutable)
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetCost { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Commission { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Tax { get; set; } // Impuestos

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
    /// Auditoria ERP 2026-06-12 (item 5, decision del dueño): VUELVE la fecha limite de EMISION del
    /// aereo (time-limit). Es la fecha tope que da la aerolinea/consolidador para emitir el ticket: si
    /// se pasa, se cae la reserva del vuelo. La carga el operador. Opcional (null = no informada).
    ///
    /// <para><b>Por que vuelve tras ADR-019</b>: igual que <see cref="OperatorPaymentDeadline"/>, ADR-019
    /// la dropeo porque murio la pill manual vieja (ADR-017 F1.4), no porque el time-limit sea invalido.
    /// El aviso "Proximos inicios" mira la SALIDA del vuelo, NO el time-limit de emision — son fechas
    /// distintas (el time-limit suele ser mucho antes de la salida). Ahora alimenta una ALARMA PROPIA
    /// (AlertService.ComputeTicketingDeadlinesAsync). Es ESPECIFICA del aereo: solo el vuelo tiene emision.</para>
    ///
    /// <para>Date-only "de pared" Kind=Utc (sin hora, sin conversion de zona — ver NormalizeCalendarDate).</para>
    /// </summary>
    public DateTime? TicketingDeadline { get; set; }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (item 5): fecha limite de pago al operador/consolidador del segmento,
    /// por paridad con los demas servicios. Mismo criterio que
    /// <see cref="HotelBooking.OperatorPaymentDeadline"/>. Distinta del time-limit de emision
    /// (<see cref="TicketingDeadline"/>): una es PAGAR, la otra EMITIR. Opcional. Date-only Kind=Utc.
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

    // === ADR-020 (2026-06-07): confirmacion del operador vs emision del ticket (DOS hechos distintos) ===
    // En el aereo la CONFIRMACION (PNR HK/TK/KK/KL) y la EMISION del ticket son hechos separados:
    //  - ConfirmedAt: el operador se comprometio (PNR confirmado). Desde aca el segmento NO se borra,
    //    solo se cancela, y corren penalidades / deuda al consolidador.
    //  - TicketIssuedAt: el ticket esta EMITIDO. Esto es lo que RESUELVE el segmento para que el file
    //    pase a Confirmada y para que su venta entre al saldo del cliente (ConfirmedSale).
    // Un PNR confirmado (HK) SIN ticket es confirmado-pero-NO-resuelto: no se borra, pero no resuelve.

    /// <summary>ADR-020: cuando el operador confirmo el PNR (Status -> HK/TK/KK/KL). Null = no confirmado.</summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>ADR-020: cuando se emitio el ticket (accion "Marcar emitido"). Null = no emitido. RESUELVE el segmento.</summary>
    public DateTime? TicketIssuedAt { get; set; }

    [MaxLength(200)]
    public string? TicketIssuedByUserId { get; set; }

    [MaxLength(200)]
    public string? TicketIssuedByUserName { get; set; }

    /// <summary>ADR-020: cuando se cancelo el segmento (Status -> HX). Null = no cancelado.</summary>
    public DateTime? CancelledAt { get; set; }

    [MaxLength(200)]
    public string? CancelledByUserId { get; set; }

    [MaxLength(200)]
    public string? CancelledByUserName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Tarifario - snapshot de precios al momento de crear
    public int? RateId { get; set; }
    public Rate? Rate { get; set; }

    // Legacy - mantener compatibilidad temporal
    public int? ServicioReservaId { get; set; }
    public ServicioReserva? ServicioReserva { get; set; }
}
