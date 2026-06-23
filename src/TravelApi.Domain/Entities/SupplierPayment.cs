using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Pago realizado a un proveedor (egreso)
/// </summary>
public class SupplierPayment : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    // Optional links
    public int? ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    public int? ServicioReservaId { get; set; }
    public ServicioReserva? ServicioReserva { get; set; }

    // ====================================================================================
    // ADR-036 punto 4c (2026-06-23): imputacion del pago a UN servicio concreto de la reserva.
    // Un servicio de la reserva puede vivir en 6 tablas distintas (vuelo/hotel/traslado/paquete/
    // asistencia + el generico ServicioReserva). Por eso NO alcanza con una sola FK como
    // ServicioReservaId (que solo apunta al generico): se necesita una referencia POLIMORFICA.
    // Usamos el MISMO identificador que ya usa el front: (recordKind, publicId). Asi el estado
    // "pagado al operador" por servicio se deriva sumando los pagos vivos imputados a ese par.
    //
    // Es aditivo y opcional: un pago sin servicio (anticipo o pago a nivel reserva) deja ambos en
    // null = identico al comportamiento previo. NO reemplaza a ServicioReservaId legacy (que sigue
    // intacto para no romper el camino del servicio generico ya existente).
    // ====================================================================================

    /// <summary>
    /// ADR-036 4c: tipo de registro del servicio al que se imputa el pago, en el vocabulario del
    /// front (<c>ServicePaymentRecordKinds</c>): flight/hotel/transfer/package/assistance/generic.
    /// <c>null</c> = el pago no se imputa a un servicio puntual (anticipo o pago a nivel reserva).
    /// Va junto con <see cref="ServicePublicId"/>: o ambos seteados o ambos null.
    /// </summary>
    [MaxLength(20)]
    public string? ServiceRecordKind { get; set; }

    /// <summary>
    /// ADR-036 4c: <c>PublicId</c> del servicio concreto al que se imputa el pago. Es polimorfico
    /// (no es una FK de base de datos, porque el servicio puede estar en cualquiera de las 6 tablas);
    /// la integridad se valida en el servicio de aplicacion al registrar el pago. <c>null</c> si el
    /// pago no se imputa a un servicio puntual.
    /// </summary>
    public Guid? ServicePublicId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    // ====================================================================================
    // ADR-021 (multimoneda + pago saliente cruzado, 2026-06-08, §15.3). Espejo EXACTO del
    // bloque de Payment (eje cliente): la deuda con el operador tambien se separa por moneda.
    // En Capa 1 son SOLO modelo+columna; el default deja todo en ARS no cruzado = identico a hoy.
    // ====================================================================================

    /// <summary>
    /// ADR-021: moneda REAL del egreso, lo que efectivamente salio de caja. Sagrada (no se
    /// convierte). NOT NULL, default ARS a nivel BD. Valores: <c>Monedas.Soportadas</c>.
    /// </summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>
    /// ADR-021: moneda de la DEUDA al operador a la que se imputa el pago. <c>null</c> = se imputa
    /// a su propia moneda (pago NO cruzado). Si difiere de <see cref="Currency"/>, es cruzado y
    /// el bloque de TC pasa a obligatorio (validacion Capa 2, §15.7).
    /// </summary>
    [MaxLength(3)]
    public string? ImputedCurrency { get; set; }

    /// <summary>
    /// ADR-021: tipo de cambio aplicado. Convencion FIJA (§2.2bis): ARS por 1 USD. Precision
    /// (18,6) alineada con <c>Invoice.MonCotiz</c> y con <c>Payment.ExchangeRate</c>. <c>null</c> si no cruza.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal? ExchangeRate { get; set; }

    /// <summary>
    /// ADR-021: origen del TC (reusa el enum de ADR-012/ADR-002), persistido como <c>int</c>.
    /// <c>null</c> si no cruza; en un pago cruzado nunca <c>null</c> ni <c>Unset</c> (validacion Capa 2).
    /// </summary>
    public ExchangeRateSource? ExchangeRateSource { get; set; }

    /// <summary>ADR-021: fecha del TC aplicado. <c>null</c> si no cruza.</summary>
    public DateTime? ExchangeRateAt { get; set; }

    /// <summary>
    /// ADR-021: monto EQUIVALENTE que baja de la deuda en <see cref="ImputedCurrency"/> tras
    /// aplicar el TC. <c>null</c> si no cruza (se imputa <see cref="Amount"/> sobre <see cref="Currency"/>).
    /// Precision (18,2).
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ImputedAmount { get; set; }

    public DateTime PaidAt { get; set; } = DateTime.UtcNow;

    public string Method { get; set; } = "Transfer"; // Cash, Transfer, Card, Check

    public string? Reference { get; set; } // Nro de transferencia, cheque, etc

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // B1.15 Fase 0' (CODE-10 / INV-2): soft-delete. Antes era hard-delete, lo
    // que perdia auditoria — un pago a proveedor borrado dejaba al CurrentBalance
    // restaurado pero sin registro de quien/cuando/por que. AuditLog en el
    // delete handler captura el motivo y el actor.
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    public string? DeletedByUserId { get; set; }
}
