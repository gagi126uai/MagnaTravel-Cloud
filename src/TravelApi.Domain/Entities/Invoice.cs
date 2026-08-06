using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class Invoice : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // AFIP Data
    public int TipoComprobante { get; set; } // 1 (A), 6 (B), 11 (C)
    public int PuntoDeVenta { get; set; }
    public long NumeroComprobante { get; set; }
    
    public string? CAE { get; set; }
    public DateTime? VencimientoCAE { get; set; }

    /// <summary>
    /// Fix B1 (revision reviewer, barrido fechas 2026-07-2x): dia calendario ARGENTINO EXACTO que
    /// viaja como <c>&lt;CbteFch&gt;</c> en el envelope a ARCA (la fecha de emision oficial que
    /// ARCA registra para este comprobante). Es la fuente unica de verdad para "Fecha de Emisión"
    /// en el PDF y para el campo <c>fecha</c> del QR — ambos tienen que coincidir byte a byte con
    /// lo que ARCA tiene registrado, y antes de este campo se DERIVABAN de <c>IssuedAt</c>, lo que
    /// podia divergir (ver el bug documentado abajo).
    ///
    /// <para><b>Por que es <c>DateTime?</c> y NO pasa nunca por <see cref="TravelApi.Domain.Helpers.ArgentinaTime"/></b>:
    /// este campo NO es un instante (no representa "cuando paso algo"), es una FECHA PURA — mismo
    /// patron que <see cref="VencimientoCAE"/>. Se guarda como medianoche marcada Kind=Utc (Postgres
    /// timestamptz exige Kind=Utc al escribir con Npgsql), pero esa medianoche representa el DIA,
    /// no un momento real en UTC. Convertirlo con <c>ArgentinaTime.ToArgentinaTime</c> le restaria 3
    /// horas y lo correria a el DIA ANTERIOR — exactamente el bug que este campo vino a cerrar (ver
    /// <c>InvoicePdfService.GetEmissionDateArgentina</c>).</para>
    ///
    /// <para><b>Bug que motivo este campo (B1)</b>: en el camino de recuperacion anti-doble-CAE
    /// (<c>ProcessInvoiceJob</c>, idempotencia), <c>IssuedAt</c> se seteaba con el <c>CbteFch</c>
    /// que devuelve ARCA parseado como fecha-a-medianoche y re-etiquetado Kind=Utc. El PDF/QR
    /// pasaban ese valor por <c>ArgentinaTime.ToArgentinaTime</c> (pensado para instantes reales) y
    /// mostraban un dia ANTES del <c>CbteFch</c> real, de forma deterministica. Este campo se llena
    /// con el mismo dato crudo (sin la conversion que rompia todo) en AMBOS caminos de emision
    /// (normal y recovery), asi PDF/QR coinciden siempre con ARCA.</para>
    ///
    /// <para><b>Nullable, SIN backfill</b>: las facturas emitidas ANTES de esta columna quedan en
    /// null (no hay forma confiable de reconstruir el dia exacto sin volver a consultar ARCA
    /// factura por factura). Para esas, <c>InvoicePdfService.GetEmissionDateArgentina</c> cae al
    /// fallback historico (derivar de <c>IssuedAt</c>/<c>CreatedAt</c> vía <c>ArgentinaTime</c>),
    /// que puede seguir mostrando el dia corrido para facturas viejas recuperadas por idempotencia
    /// — riesgo residual conocido y documentado, no bloqueante para esta migracion aditiva.</para>
    /// </summary>
    public DateTime? CbteFchArgentina { get; set; }

    public string? Resultado { get; set; } // A (Aprobado), R (Rechazado), P (Parcial)
    public string? Observaciones { get; set; } // Error messages from AFIP

    /// <summary>
    /// Obra "Empezar de cero" (2026-07-27): candado fiscal para el borrado masivo de datos. Guarda, AL MOMENTO
    /// DE EMITIR (CAE aprobado), si <c>AfipSettings.IsProduction</c> era <c>true</c>. Es la mejora que
    /// <c>InvoicePdfService</c> ya venia anticipando por escrito (persistir el ambiente POR COMPROBANTE en vez
    /// de leer la config actual): un comprobante real (ambiente productivo de ARCA) NUNCA se puede borrar del
    /// sistema aunque despues el dueño reconfigure AFIP a homologacion.
    ///
    /// <para><b>Nullable, SIN backfill</b> (T-8, tabla "Invoices" con datos reales en PROD): las facturas
    /// emitidas ANTES de esta columna quedan en <c>NULL</c>. El candado de "Empezar de cero" NO confia solo en
    /// esta columna para esos historicos — usa ADEMAS un chequeo cinturon-y-tiradores
    /// (<c>AfipSettings.IsProduction == true</c> y existe alguna factura con CAE) para cubrir el hueco. Ver
    /// <c>SystemDataWipeService.EvaluateFiscalLockAsync</c>.</para>
    /// </summary>
    public bool? WasIssuedInProduction { get; set; }

    /// <summary>
    /// Leyenda fiscal obligatoria del comprobante (campo Obs del WSFEv1 / texto del PDF).
    ///
    /// <para><b>Por que es un campo aparte de <see cref="Observaciones"/></b>: Observaciones
    /// guarda los mensajes de ERROR que devuelve ARCA y se sobreescribe en cada intento. La
    /// leyenda, en cambio, forma parte del CONTENIDO del comprobante (lo que se manda a ARCA y
    /// se imprime) y se decide al crear la factura PENDING. Mezclarlas pisaria la leyenda con un
    /// mensaje de error o viceversa.</para>
    ///
    /// <para>Hoy el unico uso es la leyenda de la Ley 27.618 que debe llevar una Factura A
    /// emitida por un Responsable Inscripto a un Monotributista (ver
    /// <c>InvoiceTypeResolver.LeyendaFacturaAMonotributista</c>). NULL para todos los demas
    /// comprobantes -> envelope byte-identico al historico (no se emite el nodo Obs).</para>
    /// </summary>
    [MaxLength(1000)]
    public string? FiscalLegend { get; set; }

    // Financial Data
    [Column(TypeName = "decimal(18,2)")]
    public decimal ImporteTotal { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal ImporteNeto { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal ImporteIva { get; set; }

    // FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.5, 2026-05-27): moneda del
    // comprobante segun el catalogo de ARCA. Hoy (FC1.2) todo se factura en pesos,
    // asi que el default deja la estructura lista sin cambiar comportamiento.
    //
    // IMPORTANTE: en esta sub-fase (Etapa 0) estas dos columnas son INERTES. El XML
    // SOAP que se manda a ARCA en AfipService sigue hardcoded en 'PES'/1 por ahora;
    // el uso real (multimoneda) lo conecta F2.5, NO esta etapa. Solo creamos la
    // estructura para que la migracion F2.5 no tenga que tocar el schema.

    /// <summary>
    /// Codigo de moneda de ARCA. "PES" = pesos argentinos, "DOL" = dolar, etc.
    /// Default "PES" para back-compat con FC1.2: las facturas que ya existen y los
    /// callers que no setean este campo quedan en pesos sin cambios.
    /// </summary>
    public string MonId { get; set; } = "PES";

    /// <summary>
    /// Cotizacion de la moneda contra el peso. Para "PES" siempre vale 1. Para una
    /// moneda extranjera seria el tipo de cambio del comprobante. Default 1 (pesos).
    /// </summary>
    public decimal MonCotiz { get; set; } = 1m;

    // ============================================================
    // ADR-012 MVP (facturar en dolares, 2026-05-29): trazabilidad del tipo de
    // cambio cuando la factura es en moneda extranjera. Las columnas son
    // NULLABLE y aditivas: las facturas en pesos (todo lo que existe hoy + todo
    // lo que sale con el flag EnableMultiCurrencyInvoicing OFF) las dejan en NULL.
    //
    // ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): agrega ExchangeRateQuoteId +
    // ExchangeRateFchCotiz (mas abajo), que apuntan a la libreta de cotizaciones
    // (ExchangeRateQuotes) cuando el TC vino de ahi. Siguen NULL cuando el TC es manual —
    // el operador todavia puede cargarlo a mano y el modelo lo sigue soportando igual.
    // ============================================================

    /// <summary>
    /// ADR-012 MVP: de donde salio el tipo de cambio que se aplico a esta factura en
    /// moneda extranjera (p. ej. <see cref="ExchangeRateSource.BNA_VendedorDivisa"/> o
    /// <see cref="ExchangeRateSource.Manual"/>). NULL cuando la factura es en pesos.
    ///
    /// <para>Se persiste como <c>int</c> (mismo patron que <c>FiscalSnapshot.Source</c>,
    /// que usa <c>HasConversion&lt;int&gt;()</c>) para que la auditoria fiscal pueda
    /// reconstruir como se valuo el comprobante.</para>
    /// </summary>
    public ExchangeRateSource? ExchangeRateSource { get; set; }

    /// <summary>
    /// ADR-012 MVP: momento exacto en que se tomo/cargo el tipo de cambio. Sirve para
    /// que el contador chequee que el TC corresponde al dia habil anterior (RG 5616).
    /// NULL cuando la factura es en pesos.
    /// </summary>
    public DateTime? ExchangeRateFetchedAt { get; set; }

    /// <summary>
    /// ADR-012 MVP: razon escrita por el operador al cargar el TC a mano (patron INV-120,
    /// mismo criterio que <c>FiscalSnapshot.ManualJustification</c>: no se permite un TC
    /// manual sin justificacion). Maximo 500 chars. NULL cuando la factura es en pesos.
    /// </summary>
    [MaxLength(500)]
    public string? ExchangeRateJustification { get; set; }

    /// <summary>
    /// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): puntero de PROCEDENCIA — "este
    /// comprobante uso exactamente esta fila de <c>ExchangeRateQuotes</c>". Lo llena el servidor en
    /// DOS casos, nunca el request (bindeable desde HTTP bloqueado con <c>[JsonIgnore]</c> en
    /// <c>CreateInvoiceRequest</c> — fix BLOQUEANTE F-4, revision post-implementacion 2026-08-05):
    /// <list type="bullet">
    ///   <item><b>Factura de venta genuina</b>: cuando <c>MonCotiz</c> coincidio EXACTO con la
    ///   sugerencia oficial que le ofrecio el resolver (<see cref="ExchangeRateSource.AfipOficial"/>
    ///   u otra fuente de la libreta).</item>
    ///   <item><b>NC/ND</b>: HEREDADO del comprobante que corrige (§6.2, "nunca se recotiza") — mismo
    ///   criterio que <c>MonId</c>/<c>MonCotiz</c>/<c>ExchangeRateSource</c>. Ver
    ///   <c>BookingCancellationService</c> (los 3 builders de NC/ND) y el camino legacy F2.2 de
    ///   <c>InvoiceService</c>.</item>
    /// </list>
    /// NULL cuando el TC fue manual (el usuario piso el numero sugerido), el comprobante original
    /// nunca tuvo este dato (legacy anterior a esta obra), o el comprobante es en pesos. La FK es
    /// <c>ON DELETE RESTRICT</c>: una fila citada por un comprobante con CAE no se puede borrar
    /// jamas (regla F-6, "nada se borra, se tacha").
    /// </summary>
    public int? ExchangeRateQuoteId { get; set; }

    /// <summary>
    /// ADR-011: el <c>FchCotiz</c> que efectivamente devolvio ARCA para la cotizacion usada (viaja
    /// junto con <see cref="ExchangeRateQuoteId"/>, poblado por el servidor en los mismos DOS casos
    /// — venta genuina con match exacto, o NC/ND heredando del original). Es el dato que defiende
    /// el numero ante una inspeccion: puede diferir de la fecha de emision del comprobante (ej. un
    /// comprobante emitido en fin de semana usa el TC del ultimo dia habil). NULL cuando el TC fue
    /// manual o el comprobante/original es anterior a esta obra.
    /// </summary>
    public DateOnly? ExchangeRateFchCotiz { get; set; }

    /// <summary>
    /// "Ayuda invisible del tipo de cambio" (spec firmada 2026-08-06, A4 + Parte B): COMO llego el
    /// sistema al tipo de cambio de este comprobante (lo acepto el usuario, lo escribio el, se lo
    /// acomodo el sistema al techo del dia, o lo completo el sistema solo). Ver
    /// <see cref="InvoiceExchangeRateOrigin"/> para el por que de tener esto ADEMAS de
    /// <see cref="ExchangeRateSource"/>.
    ///
    /// <para>NULL para comprobantes en pesos y para todo lo emitido antes de esta obra (sin backfill).
    /// Lo llena SIEMPRE el servidor, nunca el request.</para>
    /// </summary>
    public InvoiceExchangeRateOrigin? ExchangeRateOrigin { get; set; }

    /// <summary>
    /// "Ayuda invisible del tipo de cambio" (spec A4): el tipo de cambio que el usuario QUISO poner,
    /// guardado tal cual cuando el sistema tuvo que acomodarlo al techo del dia
    /// (<see cref="InvoiceExchangeRateOrigin.ClampedToDailyCeiling"/>). Es el rastro que permite
    /// explicar, meses despues, por que la factura salio a un dolar y el cobro entro a otro.
    ///
    /// <para><b>Rastro INTERNO</b>: no viaja en ninguna respuesta que vea el que opera el sistema
    /// (spec A4: "no se muestra en ningun lado del formulario"). NULL en todos los demas casos.</para>
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal? RequestedExchangeRate { get; set; }

    /// <summary>
    /// ADR-042 §3.3.1 (2026-07-01): valor fiscal <c>CanMisMonExt</c> ("Cancela en Misma Moneda
    /// Extranjera", RG 5616/2024) CONGELADO al emitir este comprobante. <c>null</c> = pesos/no aplica
    /// (no se emite el nodo, byte-identico al historico); <c>'S'</c>/<c>'N'</c> para divisa. Hoy toda
    /// divisa emite <c>'N'</c> (factura USD, cobra pesos); el camino <c>'S'</c> no se construye aun.
    ///
    /// <para><b>Espejado</b>: la NC/ND ESPEJA este valor de su comprobante original (igual que hereda
    /// <c>MonId</c>/<c>MonCotiz</c>), nunca lo redecide. Un par NC/original con distinto valor rompe el
    /// libro IVA y puede rebotar el CAE. Ver <see cref="TravelApi.Domain.Reservations.CanMisMonExtResolver"/>.</para>
    /// </summary>
    [MaxLength(1)]
    public string? CanMisMonExt { get; set; }

    public bool WasForced { get; set; }
    public string? ForceReason { get; set; }
    public string? ForcedByUserId { get; set; }
    public string? ForcedByUserName { get; set; }
    public DateTime? ForcedAt { get; set; }

    // B1.15 Fase 1: trazabilidad de quien y cuando se emitio la factura.
    // Nullable para soportar backfill de historicos (Name="(legacy)", Id=null).
    public string? IssuedByUserId { get; set; }
    public string? IssuedByUserName { get; set; }
    public DateTime? IssuedAt { get; set; }

    // B1.15 Fase 2a (FIX 6): trazabilidad de la anulacion + estado del flujo.
    // - AnnulledByUserId/Name: quien solicito el annul (back-office, no Vendedor).
    // - AnnulledAt: timestamp del momento en que la NC quedo aprobada por AFIP.
    // - AnnulmentReason: motivo declarado en el request (auditoria fiscal).
    // - AnnulmentStatus: None/Pending/Succeeded/Failed. Solo Succeeded levanta el
    //   bloqueo fiscal de cancelacion de reserva (ver FIX 7).
    public string? AnnulledByUserId { get; set; }
    public string? AnnulledByUserName { get; set; }
    public DateTime? AnnulledAt { get; set; }
    [MaxLength(500)]
    public string? AnnulmentReason { get; set; }
    public AnnulmentStatus AnnulmentStatus { get; set; } = AnnulmentStatus.None;

    /// <summary>
    /// FC1 (ADR-002 §2.6, 2026-05-13): timestamp del ultimo intento de
    /// consulta/anulacion contra ARCA. Lo usa el job recurrente
    /// <c>ArcaAnnulmentReconciliationJob</c> para detectar facturas en
    /// <see cref="AnnulmentStatus.Pending"/> que pasaron del umbral
    /// configurado (<c>ArcaStaleAnnulmentThresholdMinutes</c>) sin respuesta
    /// y reintentar la consulta de estado a AFIP. Null = aun no se intento.
    /// </summary>
    public DateTime? LastArcaAttemptAt { get; set; }

    /// <summary>
    /// FC1.2.0 v3 §10.1 (BR-V2-03, 2026-05-17): cross-reference fiscal del
    /// approval que autorizo la anulacion.
    ///
    /// **Por que existe**: cuando <c>BookingCancellationService.ConfirmAsync</c>
    /// dispara la NC con <c>requesterIsAdmin: true</c>, el approval normal de
    /// tipo <c>InvoiceAnnulment</c> se omite — el <c>InvariantOverride</c>
    /// aprobado para el BC cubre el caso. Para que la auditoria fiscal pueda
    /// trazar "quien aprobo esta annulacion", guardamos aca el FK al
    /// <see cref="ApprovalRequest"/> que valido la operacion.
    ///
    /// **Null = annulacion legacy o flujo back-office sin BC** (no cubierto por
    /// FC1.2): el campo es opcional para no romper datos historicos. La
    /// trazabilidad alternativa esta en <see cref="AnnulmentReason"/> (prefijo
    /// "BC override [publicId]:" cuando aplica) y en el AuditLog.
    ///
    /// **OnDelete: Restrict** — si alguien intenta borrar el ApprovalRequest
    /// vinculado, la BD rechaza (preserva trazabilidad).
    /// Ver §13 OPEN QUESTION OPS-FISCAL-001 del plan tactico.
    /// </summary>
    public int? AnnulmentApprovalRequestId { get; set; }
    public ApprovalRequest? AnnulmentApprovalRequest { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OutstandingBalanceAtIssuance { get; set; }

    // Snapshots (JSON) for Immutability
    public string? AgencySnapshot { get; set; }
    public string? CustomerSnapshot { get; set; }
    
    // Relationships
    public int? ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    // Navigation for Items/Tributes
    public ICollection<InvoiceItem> Items { get; set; } = new List<InvoiceItem>();
    public ICollection<InvoiceTribute> Tributes { get; set; } = new List<InvoiceTribute>();

    // Self-Referencing for Credit/Debit Notes
    public int? OriginalInvoiceId { get; set; }
    [ForeignKey("OriginalInvoiceId")]
    public Invoice? OriginalInvoice { get; set; }

    /// <summary>
    /// FC1.3 Fase 2 (Fase2_M2, 2026-05-28): huella REAL de idempotencia de la NC
    /// parcial. Guarda el MISMO string que se inserto en <c>ArcaIdempotencyKeys.Key</c>
    /// cuando esta NC se emitio (un hash SHA256 deterministico del request de emision).
    ///
    /// <para><b>Por que existe</b>: el job de reconciliacion ("el barrendero") y el
    /// arbitro de idempotencia necesitan correlacionar una NC con su fila en
    /// <c>ArcaIdempotencyKeys</c>. Antes esa correlacion se RE-DERIVABA recalculando el
    /// hash desde datos persistidos (factura origen + approval + monto + moneda). Eso
    /// asume que <c>ImporteTotal == FiscalAmountToCredit</c> a 1 centavo exacto, cosa que
    /// hoy se cumple pero NO esta garantizada por igualdad estricta (el calculator valida
    /// con tolerancia). Persistir la huella real elimina esa fragilidad: el lookup es
    /// directo y exacto.</para>
    ///
    /// <para><b>Por que es nullable</b>: las NC emitidas ANTES de esta columna no la
    /// tienen. Para esas (legacy), el codigo de reconciliacion cae al camino de
    /// re-derivacion historico (compatibilidad). Solo las NC parciales emitidas de aca
    /// en adelante quedan con la huella real grabada. NO se hace backfill (no podemos
    /// reconstruir con certeza la key de una NC vieja sin riesgo de error fiscal).</para>
    /// </summary>
    [MaxLength(64)]
    public string? IdempotencyKey { get; set; }
}
