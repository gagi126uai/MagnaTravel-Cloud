using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class HotelBooking : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    
    // Relaciones
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }
    
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    
    // Tarifario - snapshot de precios al momento de crear
    public int? RateId { get; set; }
    public Rate? Rate { get; set; }

    // Datos del Hotel
    [Required]
    [MaxLength(200)]
    public string HotelName { get; set; } = string.Empty;
    
    public int? StarRating { get; set; } // 1-5
    
    [MaxLength(200)]
    public string? Address { get; set; }
    
    [Required]
    [MaxLength(100)]
    public string City { get; set; } = string.Empty;
    
    [MaxLength(100)]
    public string? Country { get; set; }
    
    // Fechas
    public DateTime CheckIn { get; set; }
    public DateTime CheckOut { get; set; }
    public int Nights { get; set; }
    
    // Habitación
    [MaxLength(50)]
    public string RoomType { get; set; } = "Doble"; // Single, Doble, Triple, Suite
    
    [MaxLength(50)]
    public string MealPlan { get; set; } = "Desayuno"; // Solo Aloj., Desayuno, Media Pensión, All Inclusive

    /// <summary>
    /// Nombre FINO de la habitacion, tal como lo escribe el vendedor: "Superior", "Vista al mar",
    /// "Deluxe" (spec firmada 2026-08-07, §5.2). Es texto libre CON MEMORIA: la primera vez se escribe,
    /// despues el sistema lo ofrece.
    ///
    /// <para><b>Por que hacia falta</b>: sin este dato, una "Doble Superior" y una "Doble" comun
    /// compartian la misma memoria de precio y una pisaba a la otra. Junto con la capacidad
    /// (<see cref="RoomType"/>) y el regimen (<see cref="MealPlan"/>) forman la VARIANTE del producto.</para>
    ///
    /// <para>Opcional: la mayoria de las ventas no lo usa y queda null (la habitacion es "Doble" a secas).</para>
    /// </summary>
    [MaxLength(100)]
    public string? RoomCategory { get; set; }
    
    public int Rooms { get; set; } = 1;
    public int Adults { get; set; } = 2;
    public int Children { get; set; } = 0;
    
    // Confirmación
    [MaxLength(100)]
    public string? ConfirmationNumber { get; set; }
    
    [MaxLength(50)]
    public string Status { get; set; } = "Solicitado";
    
    // Financiero (copiado del tarifario al momento de crear - inmutable)
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetCost { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }
    
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
    /// Auditoria ERP 2026-06-12 (item 5, decision del dueño): VUELVE la fecha limite de pago al
    /// operador. La carga el operador por servicio: es la fecha tope para pagarle al proveedor sin
    /// perder el cupo/tarifa. Opcional (null = no informada).
    ///
    /// <para><b>Por que vuelve tras ADR-019</b>: ADR-019 (D7) no dropeo este campo porque la fecha en
    /// si fuera invalida, sino porque la VIEJA campanita de "fechas limite manuales" (ADR-017 F1.4) fue
    /// reemplazada por el aviso automatico "Proximos inicios" (que mira el INICIO del viaje, no el pago
    /// al operador). El concepto "pago al operador" NUNCA fue cubierto por ese aviso. La auditoria de
    /// negocio lo identifico como el vencimiento que mas plata cuesta y el dueño lo reintrodujo. Ahora
    /// alimenta una ALARMA PROPIA (AlertService.ComputeOperatorPaymentDeadlinesAsync), NO la pill vieja.</para>
    ///
    /// <para>Date-only "de pared" con Kind=Utc, igual criterio que CheckIn (sin hora, sin conversion de
    /// zona — ver NormalizeCalendarDate en BookingService).</para>
    /// </summary>
    public DateTime? OperatorPaymentDeadline { get; set; }

    /// <summary>
    /// ADR-017 F1.1 (decision D7, 2026-06-05): marca "costo a confirmar". Aditivo, default false: las
    /// filas existentes no cambian. ORTOGONAL al workflow (Status no se toca): un servicio "a confirmar"
    /// se confirma/factura/viaja igual; lo unico que bloquea es el upsert de RateSupplierSale hasta que
    /// alguien con permiso confirme el costo (boton "Confirmar costo", F1.3). En F1.1 nadie lo setea.
    /// </summary>
    public bool CostToConfirm { get; set; } = false;

    /// <summary>
    /// ADR-017 F1.1 (D7): por que quedo marcado: "NoKnownCost" (producto nuevo sin costo) o
    /// "StaleReference" (costo de referencia mas viejo que el umbral). Null si no hay marca.
    /// </summary>
    [MaxLength(30)]
    public string? CostToConfirmReason { get; set; }

    // Planner de habitaciones: JSON con asignación de pasajeros por habitación
    public string? RoomingAssignmentsJson { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009 §2.3.2, 2026-05-21): lista JSON de conceptos no reintegrables
    /// que se imputan al cliente fuera del costo neto/venta del hotel
    /// (ej. cargo gestion $5.000, seguro cancelacion $20.000).
    /// Persistido como <c>jsonb</c> (consistencia con
    /// <c>Supplier.PenaltyPolicyJson</c>).
    ///
    /// <para>Schema:
    /// <c>[{"description": string, "amount": decimal, "category": InvoiceItemCategory}, ...]</c>
    /// Null o array vacio significa "sin conceptos adicionales".</para>
    ///
    /// <para>Cada concepto se traduce a un <c>InvoiceItem</c> con
    /// <c>IsRefundable=false</c> al momento de facturar, asi se mantiene la
    /// trazabilidad fiscal del concepto retenido en la NC parcial.</para>
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? NonRefundableConceptsJson { get; set; }

    // === ADR-020 (2026-06-07): trazabilidad de confirmacion del operador y de cancelacion del servicio ===

    /// <summary>
    /// ADR-020: fecha en que el operador CONFIRMO este servicio (la estampa el motor de estados
    /// al detectar que el Status paso a confirmado). Null = nunca confirmado. NO se borra si el
    /// servicio se des-confirma: queda como historia y se re-estampa si se vuelve a confirmar.
    /// Gobierna borrar-vs-cancelar (un servicio con ConfirmedAt no se borra, solo se cancela) y
    /// el inicio de las penalidades.
    /// </summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>ADR-020: cuando se cancelo el servicio (Status -> Cancelado). Null = no cancelado.</summary>
    public DateTime? CancelledAt { get; set; }

    [MaxLength(200)]
    public string? CancelledByUserId { get; set; }

    [MaxLength(200)]
    public string? CancelledByUserName { get; set; }

    /// <summary>
    /// ADR-050 (2026-07-24): el <c>Status</c> que tenía el servicio JUSTO ANTES de cancelarse. Se estampa cada
    /// vez que el servicio pasa a cancelado (por cualquiera de los caminos: anular la reserva entera, cancelar
    /// este servicio suelto, o que el operador cambie su estado). Permite a "Volver atrás" restaurar el estado
    /// EXACTO en vez de adivinar uno genérico. Null en servicios legacy nunca cancelados o cancelados antes de
    /// esta obra (el revivir hace fallback a "Solicitado" en ese caso).
    /// </summary>
    [MaxLength(50)]
    public string? StatusBeforeCancellation { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Opciones A/B/C (decisión #1 firmada del dueño, 2026-08-11/12): ver el XML-doc completo en
    // TravelApi.Domain.Reservations.OptionGroupRules. Replicado igual en los 5 servicios tipados.
    /// <summary>Nombre corto del grupo de opciones (ej. "hoteles"). Null = no es una alternativa de nada.</summary>
    [MaxLength(60)]
    public string? OptionGroup { get; set; }

    /// <summary>Etiqueta visible dentro del grupo ("A"/"B"/"C").</summary>
    [MaxLength(5)]
    public string? OptionLabel { get; set; }

    /// <summary>
    /// Obra "PDF completo" (decisión firmada del dueño, 2026-08-13): cantidad de cuotas del plan de pago
    /// que el vendedor anota para este hotel (ej. "6 CUOTAS DE 280 USD" en el presupuesto). Es un dato
    /// PURAMENTE INFORMATIVO para el papel — no genera pagos ni imputaciones reales, no toca el saldo del
    /// cliente ni el circuito de cobranzas. Opcional: null = no informado, el PDF omite la línea entera
    /// (regla espejo, decisión #8). Va siempre junto con <see cref="InstallmentAmount"/>: el PDF solo
    /// imprime la línea si AMBOS campos están cargados (una cantidad de cuotas sin monto, o un monto sin
    /// cantidad, no dice nada útil).
    /// </summary>
    public int? InstallmentsCount { get; set; }

    /// <summary>
    /// Monto de CADA cuota del plan (ver <see cref="InstallmentsCount"/>). Mismo criterio informativo: no
    /// es un componente de <see cref="SalePrice"/> ni se valida contra el total (el vendedor puede anotar
    /// cualquier combinación, es texto de presupuesto, no un plan de cobranza real todavía).
    /// </summary>
    [Column(TypeName = "decimal(12,2)")]
    public decimal? InstallmentAmount { get; set; }

    public int GetExpectedPaxCount() => Adults + Children;
}
