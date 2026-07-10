using System.ComponentModel.DataAnnotations;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.DTOs;

public class OperationalFinanceSettingsDto
{
    public bool RequireFullPaymentForOperativeStatus { get; set; } = true;
    public bool RequireFullPaymentForVoucher { get; set; } = true;
    public string AfipInvoiceControlMode { get; set; } = "AllowAgentOverrideWithReason";
    public bool EnableUpcomingUnpaidReservationNotifications { get; set; } = true;
    public int UpcomingUnpaidReservationAlertDays { get; set; } = 7;
    /// <summary>
    /// B1.15 Fase 2a: tope de descuento (% sobre precio de referencia) que un
    /// vendedor puede aplicar sin permiso <c>reservas.discount_above_threshold</c>.
    /// Rango valido 0..100. Default 10%.
    /// </summary>
    [Range(0, 100, ErrorMessage = "El porcentaje máximo de descuento debe estar entre 0 y 100.")]
    public decimal MaxDiscountPercentWithoutOverride { get; set; } = 10m;

    // ============================================================
    // FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.0, 2026-05-22):
    // 3 settings configurables desde el panel admin. Los 2 flags maestros
    // (EnablePartialCreditNoteRealEmission, EnableTotalPlusNewInvoiceAutoProcessing)
    // intencionalmente NO se exponen aca: se cambian via SQL/seed/migration igual
    // que los flags FC1.2/FC1.3 actuales (EnableNewCancellationFlow,
    // EnablePartialCreditNotes). El service valida las cross-field rules en runtime.
    //
    // El proyecto NO usa FluentValidation registrado (ver comentario en
    // Program.cs §FC1.3.2). Usamos DataAnnotations consistente con el resto del
    // DTO (MaxDiscountPercentWithoutOverride ya usa [Range]). El binding de
    // ASP.NET Core dispara ModelState invalido y devuelve HTTP 400 antes de
    // llegar al service — mismo patron que ya funciona en este DTO.
    //
    // B-002 fix (2026-05-26): los 3 campos son NULLABLE para evitar regresion
    // silenciosa via PUT full-replace. Mandar null o omitir el campo en el PUT
    // = no se modifica el valor actual. Solo se actualiza si viene con valor.
    // Razon: un cliente legacy o el frontend que olvide mandar uno de estos
    // 3 campos haria que el binder llene con el default del DTO y persista
    // sobre lo que el admin configuro. Cuando el contador conteste F1 round 3 y
    // un admin pase IvaProrrateoMode a PerItem, un PUT legacy lo revertiria
    // silenciosamente a ProportionalToNet. Con nullable, el campo omitido no
    // pisa nada. Los [Range] siguen aplicando — solo se evaluan si viene valor.
    // ============================================================

    /// <summary>
    /// FC1.3 Fase 2 (RH-005): modo de prorrateo de IVA en NC parcial. Configurable
    /// por admin segun respuesta del contador (pregunta F1 round 3). Default
    /// persistido en BD: <see cref="IvaProrrateoMode.ProportionalToNet"/>.
    /// Nullable en el DTO: enviar null o omitir en el PUT para no modificar el
    /// valor actual. Solo se actualiza si viene con valor.
    /// </summary>
    public IvaProrrateoMode? IvaProrrateoMode { get; set; }

    /// <summary>
    /// FC1.3 Fase 2: tolerancia maxima en la validacion defensiva pre-envio al
    /// ARCA (ImpNeto + ImpIVA + ImpTrib vs ImpTotal). Default persistido en BD:
    /// 0.01 (un centavo). Rango razonable 0..1 en la moneda original del comprobante.
    /// Nullable en el DTO: enviar null o omitir en el PUT para no modificar el
    /// valor actual. Solo se actualiza si viene con valor.
    /// </summary>
    [Range(0.0, 1.0, ErrorMessage = "La tolerancia de redondeo debe estar entre 0 y 1.")]
    public decimal? PartialCreditNoteRoundingTolerance { get; set; }

    /// <summary>
    /// FC1.3 Fase 2 (RH2-004): umbral en minutos para considerar huerfana una
    /// key en ArcaIdempotencyKeys. Default persistido en BD: 10. Rango razonable
    /// 1..60 (mas de 60 atrasa el recovery; menos de 1 declara "huerfanas" keys
    /// legitimas en vuelo).
    /// Nullable en el DTO: enviar null o omitir en el PUT para no modificar el
    /// valor actual. Solo se actualiza si viene con valor.
    /// </summary>
    [Range(1, 60, ErrorMessage = "La cantidad de minutos debe estar entre 1 y 60.")]
    public int? IdempotencyKeyStaleThresholdMinutes { get; set; }

    /// <summary>
    /// ADR-012 MVP (facturar en USD, 2026-05-29): feature flag maestro de facturacion
    /// multimoneda. Hasta hoy solo se prendia por SQL; ahora el admin lo puede prender/apagar
    /// desde Configuracion -> Facturacion. Con OFF (default) la facturacion sale en pesos
    /// exactamente como hoy; con ON el modal de emision habilita el selector de moneda.
    ///
    /// <para>Nullable y patch-like (mismo criterio B-002 que los 3 settings de arriba): enviar
    /// null u omitir el campo en el PUT = no se modifica el valor actual. Asi un cliente legacy
    /// o un PUT que olvide el campo no lo apaga silenciosamente. Solo se persiste si viene valor.</para>
    ///
    /// <para>OJO: el mismo valor se expone read-only en GET /afip/settings para que el modal de
    /// emision decida si muestra el selector de moneda. Ese endpoint NO se toca: sigue leyendo
    /// la entidad, asi que refleja automaticamente lo que se guarde por aca.</para>
    /// </summary>
    public bool? EnableMultiCurrencyInvoicing { get; set; }

    /// <summary>
    /// ADR-013 (Nota de Debito en cancelacion, 2026-06-01): feature flag de la emision de
    /// Nota de Debito por penalidad propia de la agencia en el flujo de cancelacion. Hasta
    /// hoy solo se prendia por SQL; ahora el admin lo prende/apaga desde el panel.
    ///
    /// <para><b>ZONA PELIGROSA — emision fiscal real</b>: con ON, una cancelacion puede emitir
    /// una ND C real contra ARCA. NO prender hasta tener signoff del contador + CAE de
    /// homologacion ARCA para ND C asociada a factura. El frontend deberia tratar este toggle
    /// como zona peligrosa (confirmacion explicita), igual que el de multimoneda.</para>
    ///
    /// <para><b>Validacion cruzada (server-side)</b>: prender este flag exige tener
    /// <c>EnableNewCancellationFlow=true</c>, porque la ND se dispara desde el callback de la
    /// NC total, que solo existe en el flujo de cancelacion nuevo (FC1.2). Hay un startup-check
    /// en Program.cs que rechaza el arranque si la combinacion es invalida; el service rechaza
    /// el mismo PUT con un 400 antes de persistir, para no dejar la app en un estado que no
    /// vuelve a arrancar. Mismo estilo que la validacion GR-002.</para>
    ///
    /// <para>Nullable y patch-like (mismo criterio B-002): enviar null u omitir el campo en
    /// el PUT = no se modifica el valor actual. Solo se persiste si viene con valor.</para>
    /// </summary>
    public bool? EnableCancellationDebitNote { get; set; }

    /// <summary>
    /// ADR-016 F0a (Base del copiloto de IA, 2026-06-03): feature flag maestro del copiloto.
    /// El admin lo prende/apaga desde el panel de Configuracion.
    ///
    /// <para>Es un flag de COMPORTAMIENTO: con OFF (default) el copiloto no existe y nada sale
    /// hacia la nube. NO tiene validacion cruzada en F0a porque el flag del piloto que lo
    /// consume (EnableAiUpcomingClientAlerts) todavia no existe; esa cruzada llega en F1.</para>
    ///
    /// <para>OJO: este flag NO incluye la API key ni el proveedor (eso va por variables de
    /// entorno <c>Ai__*</c>, fuera de la DB). Prender el flag sin esa config hace que el cerebro
    /// degrade elegante, no rompe nada.</para>
    ///
    /// <para>Nullable y patch-like (mismo criterio B-002 que el resto del DTO): enviar null
    /// u omitir el campo en el PUT = no se modifica el valor actual. Solo se persiste si viene
    /// con valor.</para>
    /// </summary>
    public bool? EnableAiCopilot { get; set; }

    /// <summary>
    /// ADR-017 F1.1 (catalogo find-or-create, 2026-06-05): feature flag maestro del catalogo
    /// find-or-create desde la venta. El admin lo prende/apaga desde el panel de Configuracion.
    ///
    /// <para>Es un flag de COMPORTAMIENTO puro: con OFF (default) todo es byte-identico a hoy y NO tiene
    /// validacion cruzada con otros flags. En F1.1 nadie lo lee todavia (el comportamiento que gobierna
    /// se construye en F1.2+); solo debe existir, persistir y poder togglearse.</para>
    ///
    /// <para>Nullable y patch-like (mismo criterio B-002 que el resto del DTO): enviar null u omitir el
    /// campo en el PUT = no se modifica el valor actual. Solo se persiste si viene con valor.</para>
    /// </summary>
    public bool? EnableCatalogFindOrCreate { get; set; }

    /// <summary>
    /// Feature flag de los avisos "Proximos inicios" (ADR-019; el nombre interno conserva el de
    /// ADR-017 a proposito — ver <c>OperationalFinanceSettings.EnableServiceDeadlineAlerts</c>).
    /// Independiente de <see cref="EnableCatalogFindOrCreate"/>. Flag de comportamiento puro,
    /// sin validacion cruzada.
    ///
    /// <para>Nullable y patch-like (criterio B-002): null u omitido = no se toca; solo persiste si viene.</para>
    /// </summary>
    public bool? EnableServiceDeadlineAlerts { get; set; }

    /// <summary>
    /// ADR-017 F1.1 (decision D7, 2026-06-05): umbral en DIAS para marcar "costo a confirmar" una
    /// referencia de costo vieja. Default persistido en BD: 60. En F1.1 nadie lo lee todavia (la cadena
    /// D7 es F1.3); solo debe existir y poder editarse desde el panel.
    ///
    /// <para>Nullable y patch-like (criterio B-002): enviar null u omitir = no se modifica el valor
    /// actual. Rango razonable 1..3650 (un dia a ~diez años); el [Range] solo se evalua si viene valor.</para>
    /// </summary>
    [Range(1, 3650, ErrorMessage = "La cantidad de días debe estar entre 1 y 3650.")]
    public int? StaleCostReferenceDays { get; set; }

    /// <summary>
    /// Ventana en DIAS del aviso "Proximos inicios" (UI: "Dias de anticipacion del aviso" — ADR-019).
    /// Editable desde el panel admin. Solo tiene efecto observable con <see cref="EnableServiceDeadlineAlerts"/> ON.
    ///
    /// <para>Nullable y patch-like (criterio B-002): enviar null u omitir = no se modifica el valor actual.
    /// Rango razonable 1..60 (mismo que <c>UpcomingUnpaidReservationAlertDays</c>); el [Range] solo se evalua
    /// si viene valor.</para>
    /// </summary>
    [Range(1, 60, ErrorMessage = "Los días de anticipación del aviso deben estar entre 1 y 60.")]
    public int? ServiceDeadlineAlertDays { get; set; }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (hallazgo #1): interruptor de la comision del vendedor. El admin lo
    /// prende/apaga desde el panel de Configuracion. Es un ajuste de NEGOCIO, sin validacion cruzada.
    ///
    /// <para>Con OFF (default) no se devenga ninguna comision (comportamiento byte-identico a antes). Con
    /// ON, las reservas totalmente cobradas devengan comision del vendedor responsable por moneda.</para>
    ///
    /// <para>Nullable y patch-like (mismo criterio B-002 que el resto del DTO): enviar null u omitir el
    /// campo en el PUT = no se modifica el valor actual. Solo se persiste si viene con valor. Se expone
    /// read-only en el GET para que el panel lo muestre como toggle.</para>
    /// </summary>
    public bool? EnableSellerCommissions { get; set; }

    /// <summary>
    /// Auditoria ERP 2026-06-13 (decision del dueño): UN SOLO porcentaje de comision parejo para TODAS las
    /// reservas (sin reglas por operador/tipo). El admin lo setea desde el panel; con
    /// <see cref="EnableSellerCommissions"/> ON y este valor &gt; 0, cada servicio confirmado devenga este % de
    /// su ganancia. Con 0 (default) no se devenga nada aunque el interruptor este prendido.
    ///
    /// <para>Rango 0..100 (validado por <c>[Range]</c>). Nullable y patch-like (criterio B-002): enviar null
    /// u omitir el campo en el PUT = no se modifica el valor actual; solo se persiste si viene con valor. Se
    /// expone read-only en el GET para que el panel lo muestre como input numerico.</para>
    /// </summary>
    [Range(0, 100, ErrorMessage = "El porcentaje de comisión debe estar entre 0 y 100.")]
    public decimal? SellerCommissionPercent { get; set; }

    /// <summary>
    /// G6 (caducidad de pre-venta, 2026-06-24): dias tras los cuales un Presupuesto (Budget) que no avanzo
    /// caduca y pasa SOLO a "Perdido". 0 = desactivado. El admin lo setea desde el panel (ej. 7).
    ///
    /// <para>Nullable y patch-like (criterio B-002): enviar null u omitir = no se modifica el valor actual.
    /// Rango 0..3650 (0 = desactivado; el [Range] solo se evalua si viene valor). Se expone read-only en el GET.</para>
    /// </summary>
    [Range(0, 3650, ErrorMessage = "Los días de caducidad del presupuesto deben estar entre 0 y 3650 (0 = desactivado).")]
    public int? BudgetExpirationDays { get; set; }

    /// <summary>
    /// G6 (caducidad de pre-venta, 2026-06-24): dias tras los cuales una Cotizacion (Quotation) que no avanzo
    /// caduca y pasa SOLO a "Perdido". 0 = desactivado. Eje SEPARADO del de Budget. El admin lo setea (ej. 20).
    ///
    /// <para>Nullable y patch-like (criterio B-002): enviar null u omitir = no se modifica el valor actual.
    /// Rango 0..3650 (0 = desactivado; el [Range] solo se evalua si viene valor). Se expone read-only en el GET.</para>
    /// </summary>
    [Range(0, 3650, ErrorMessage = "Los días de caducidad de la cotización deben estar entre 0 y 3650 (0 = desactivado).")]
    public int? QuotationExpirationDays { get; set; }

    /// <summary>
    /// ADR-044 T3a (2026-07-10): código de alícuota de IVA (catálogo ARCA: 3=0%, 4=10.5%, 5=21%, 6=27%, 8=5%,
    /// 9=2.5%) para la porción del cargo del operador que la agencia solo REPLICA al cliente (pass-through, sin
    /// agregarle nada), cuando la agencia emisora es Responsable Inscripto. Sin firma contable todavía: mientras
    /// quede en null, la Nota de Débito automática de un emisor Responsable Inscripto con este tipo de cargo NO
    /// se emite sola (queda para revisión manual). Setealo recién cuando el contador confirme el valor.
    ///
    /// <para>Nullable y patch-like (criterio B-002): enviar null u omitir el campo en el PUT = no se modifica el
    /// valor actual. El service valida que el código sea uno reconocido del catálogo ARCA (400 si no).</para>
    /// </summary>
    public int? CancellationDebitNoteRiPassThroughAlicuotaIvaId { get; set; }
}
