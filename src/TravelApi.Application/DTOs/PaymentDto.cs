namespace TravelApi.Application.DTOs;

public class PaymentDto
{
    public Guid PublicId { get; set; }
    public decimal Amount { get; set; }
    public DateTime PaidAt { get; set; }
    public string Method { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string Status { get; set; } = "Paid";
    public Guid? ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public bool AffectsCash { get; set; }
    public Guid? RelatedInvoicePublicId { get; set; }
    public Guid? OriginalPaymentPublicId { get; set; }
    public PaymentReceiptDto? Receipt { get; set; }

    // ====================================================================================
    // ADR-021 Capa 7 (multimoneda + cobro cruzado). Aditivos: un pago ARS no cruzado
    // (todo lo legacy) sale Currency="ARS", ImputedCurrency=null y el resto en null =
    // identico a lo que el front viejo ya interpretaba. El front los usa para: mostrar la
    // moneda en el historial; mostrar "imputado a US$ X" en un cobro cruzado; y DETECTAR
    // que un cobro es cruzado (ImputedCurrency != null && != Currency) para BLOQUEAR su
    // edicion (decision C). Sin estos campos un cobro cruzado se editaria como uno normal.
    // ====================================================================================

    /// <summary>ADR-021: moneda REAL del cobro (lo que entro a caja). Normalizada (nunca null), default ARS.</summary>
    public string Currency { get; set; } = "ARS";

    /// <summary>ADR-021: moneda del SALDO al que se imputo. null = no cruzado (se imputo a su propia moneda).</summary>
    public string? ImputedCurrency { get; set; }

    /// <summary>ADR-021: tipo de cambio aplicado (ARS por 1 USD). null si no hubo conversion.</summary>
    public decimal? ExchangeRate { get; set; }

    /// <summary>ADR-021: origen del tipo de cambio (enum <c>ExchangeRateSource</c> serializado como int). null si no hubo conversion.</summary>
    public int? ExchangeRateSource { get; set; }

    /// <summary>ADR-021: fecha del tipo de cambio aplicado. null si no hubo conversion.</summary>
    public DateTime? ExchangeRateAt { get; set; }

    /// <summary>ADR-021: monto equivalente que bajo del saldo imputado tras aplicar el TC. null si no hubo conversion.</summary>
    public decimal? ImputedAmount { get; set; }

    // ====================================================================================
    // Tanda 6 (plan de remediacion "contrato pantalla-motor", 2026-07-20). Antes la ficha
    // solo miraba la capacidad de la RESERVA entera (CanEditOrDeletePayment, por estado):
    // un cobro puntual con recibo YA EMITIDO o atado a una factura con CAE vivo seguia
    // mostrando "Editar"/"Eliminar" activos, y el rechazo llegaba recien despues de que el
    // usuario llenaba el formulario entero. Estos dos campos dejan que el front apague el
    // boton de ESTE pago puntual, con el motivo al lado, ANTES de abrir el formulario.
    //
    // Fuente unica de la regla: PaymentCapabilityPolicy (Domain), la MISMA que usan los
    // guards reales de escritura (MutationGuards/DeleteGuards) que rechazan el PUT/DELETE.
    // Se calculan en los DOS endpoints que la ficha usa de verdad para pintar la lista de
    // cobros: ReservaService.GetReservaByIdAsync (GET /api/reservas/{id}, el detalle
    // completo) y PaymentService.GetPaymentsForReservaAsync (GET /api/payments/reserva/{id},
    // el que consume useReservaDetail.js/EstadoCuentaExtracto para armar reserva.payments[]
    // en la PRACTICA). Cada uno junta sus hechos con UNA sola consulta extra (nunca por-pago)
    // y delega el mapeo a PaymentCapabilityDtoMapper (Infrastructure) para que los dos
    // caminos nunca puedan construir el CapabilityDto de forma distinta.
    //
    // NULO vs Allowed=false (decision explicita, 2026-07-20): el default es NULL, no un
    // CapabilityDto con Allowed=false. Un endpoint que TODAVIA no calcula estos campos (hoy:
    // GET /api/reservas/{id}/payments via GetReservaPaymentsAsync(int), sin consumidor en el
    // front actual) deja canEdit/canDelete en null. El front (paymentRowGuard.js,
    // resolverBloqueoFilaCobro) ya trata null/undefined como "no hay bloqueo nuevo que
    // mostrar" (degradacion elegante al comportamiento de ANTES de esta tanda). Si en cambio
    // el default fuera {Allowed:false, Reason:null}, un endpoint que se olvida de poblarlo
    // mostraria el boton APAGADO SIN MOTIVO — peor que no calcularlo: el usuario ve un
    // candado mudo. null es la forma correcta de decir "este dato no vino", Allowed=false es
    // la forma correcta de decir "se evaluo y esta bloqueado".
    // ====================================================================================

    /// <summary>
    /// Si ESTE pago puntual admite "Editar", y el motivo cuando no. <c>null</c> = el endpoint que
    /// armo esta fila todavia no calcula esta capacidad (ver nota arriba); NO significa bloqueado.
    /// </summary>
    public CapabilityDto? CanEdit { get; set; }

    /// <summary>
    /// Si ESTE pago puntual admite "Eliminar", y el motivo cuando no. <c>null</c> = el endpoint que
    /// armo esta fila todavia no calcula esta capacidad (ver nota arriba); NO significa bloqueado.
    /// </summary>
    public CapabilityDto? CanDelete { get; set; }
}
