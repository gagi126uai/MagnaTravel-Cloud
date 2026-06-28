using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class Supplier : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? ContactName { get; set; }

    [MaxLength(100)]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(20)]
    public string? TaxId { get; set; } // CUIT

    [MaxLength(50)]
    public string? TaxCondition { get; set; } // IVA_RESP_INSCRIPTO, MONOTRIBUTISTA, IVA_EXENTO

    [MaxLength(200)]
    public string? Address { get; set; }

    /// <summary>
    /// Rediseño alta de operador (2026-06-28): moneda por defecto del operador en codigo ISO
    /// ("ARS"/"USD"). Es la moneda que el alta propone al cargar compras/pagos de este operador
    /// (la mayoria opera siempre en la misma). NO bloquea operar en otra moneda: la plata real de
    /// cada compra/pago sigue resolviendose por su propia moneda; esto es solo un valor sugerido.
    ///
    /// <para>NULLABLE con default "ARS" (<see cref="Monedas.ARS"/>): las filas viejas quedan en ARS
    /// (la moneda por defecto del sistema) sin backfill destructivo. La validacion server-side
    /// (debe ser una de <see cref="Monedas.Soportadas"/>) la hace <c>SupplierService</c>.</para>
    /// </summary>
    [MaxLength(3)]
    public string? DefaultCurrency { get; set; } = Monedas.ARS;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// ADR-041 TANDA 5 (2026-06-27): plazo de pago por defecto (en dias) acordado con este operador.
    /// OPCIONAL: <c>null</c> = el operador no tiene plazo configurado = comportamiento actual (la deuda
    /// no muestra vencimiento sugerido).
    ///
    /// <para>Seguimos siendo PREPAGO: este plazo NO bloquea nada. Solo sirve para DERIVAR un vencimiento
    /// sugerido por cada compra/servicio (= fecha de la compra + estos dias), para priorizar pagos y avisar.
    /// La deuda al operador hoy es derivada de los servicios (no hay "factura del operador" con vencimiento
    /// propio), por eso el plazo vive en el maestro del proveedor y el vencimiento se deriva por linea.</para>
    /// </summary>
    public int? DefaultPaymentTermDays { get; set; }

    // Financials (what we owe them) - calculated, not editable on create
    public decimal CurrentBalance { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ============================================================
    // FC1.3 (ADR-009 §2.3.2, 2026-05-21): operador como input fiscal
    // del modulo NC parcial. Estos dos campos NO cambian el comportamiento
    // de FC1.2 — solo se leen cuando la cancelacion entra al flujo
    // FC1.3 con el flag EnablePartialCreditNotes en true.
    // ============================================================

    /// <summary>
    /// FC1.3 (ADR-009): modelo de facturacion al cliente para este operador.
    /// <see cref="SupplierInvoicingMode.TotalToCustomer"/> = reseller (factura el total del servicio);
    /// <see cref="SupplierInvoicingMode.CommissionOnly"/> = intermediario (factura solo la comision).
    /// Default <see cref="SupplierInvoicingMode.TotalToCustomer"/> (conservador, comportamiento legacy).
    ///
    /// <para>Al momento de emitir factura, este valor se copia a
    /// <c>FiscalSnapshot.InvoicingModeAtEvent</c> para que la cancelacion futura use
    /// el modo vigente AL MOMENTO de la facturacion (no el actual, que pudo cambiar).</para>
    ///
    /// <para>Fase 1 (GR-003): CommissionOnly NO se procesa automatico, se deriva a
    /// revision manual obligatoria hasta respuesta del contador (pregunta F2 round 3).</para>
    /// </summary>
    public SupplierInvoicingMode InvoicingMode { get; set; } = SupplierInvoicingMode.TotalToCustomer;

    /// <summary>
    /// FC1.3 (ADR-009 §2.5): tabla de penalidades por antelacion en formato JSON,
    /// persistida como columna <c>jsonb</c> de Postgres (RH-014).
    ///
    /// <para>Schema esperado:
    /// <c>{ "tiers": [{"minDaysBefore": int, "penaltyPercent": decimal}, ...], "currency": "USD"|"ARS" }</c>
    /// </para>
    ///
    /// <para>Tiers ordenados DESC por <c>minDaysBefore</c>. El vendedor puede override
    /// manual al confirmar (D2 2026-05-21). Si es null, el operador no tiene tabla
    /// configurada y el vendedor debe ingresar el monto manual cada vez.</para>
    ///
    /// <para>El CHECK constraint <c>chk_Suppliers_penaltypolicy_object</c> de Postgres
    /// rechaza valores que no sean objetos top-level (array, string, etc.) — esto evita
    /// que un bug del API guarde un JSON con shape equivocado y reviente el calculator.</para>
    ///
    /// <para>Validacion de schema interno (tiers ordenados, porcentajes 0..100) se hace
    /// en <c>SupplierService</c> con FluentValidation antes de persistir. El CHECK
    /// SQL solo valida que sea objeto, no la forma interna.</para>
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? PenaltyPolicyJson { get; set; }

    /// <summary>
    /// ADR-013 §3.7 (2026-06-01): "quien se queda la penalidad" de cancelacion para
    /// este operador. <b>Ortogonal a <see cref="InvoicingMode"/></b> (ese es
    /// reseller-vs-intermediario, otro eje).
    ///
    /// <para><see cref="PenaltyOwnership.Operator"/> = pass-through (la plata es del
    /// operador, la agencia NO emite ND propia, comportamiento de hoy).
    /// <see cref="PenaltyOwnership.Agency"/> = la penalidad es ingreso propio de la
    /// agencia (habilita emitir la ND gravada).</para>
    ///
    /// <para>Default <see cref="PenaltyOwnership.Operator"/> (conservador): mientras
    /// nadie lo cambie, el sistema sigue haciendo SOLO la NC total, igual que hoy. Al
    /// momento del evento de cancelacion este valor se CONGELA en el snapshot fiscal
    /// (mismo patron que <c>InvoicingModeAtEvent</c>) para usar el acuerdo vigente AL
    /// MOMENTO, no el actual.</para>
    /// </summary>
    public PenaltyOwnership PenaltyOwnership { get; set; } = PenaltyOwnership.Operator;
}

public static class TaxConditions
{
    public const string IvaResponsableInscripto = "IVA_RESP_INSCRIPTO";
    public const string Monotributista = "MONOTRIBUTISTA";
    public const string IvaExento = "IVA_EXENTO";
    public const string ConsumidorFinal = "CONSUMIDOR_FINAL";
}
