using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-021 §15.3 (multimoneda, eje proveedor, 2026-06-08): tabla hija MATERIALIZADA con la deuda
/// de la agencia HACIA un proveedor/operador (cuentas por pagar) SEPARADA por moneda. Espejo exacto
/// de <see cref="ReservaMoneyByCurrency"/> del lado cliente; una fila por moneda presente.
///
/// <para><b>Por que existe</b>: igual razon que la hija del cliente (B1). <c>AlertService</c> ordena
/// el top-N de proveedores deudores en SQL y <c>ReportService</c> suma deuda de proveedor en SQL;
/// no pueden leer un diccionario en memoria. El escalar <c>Supplier.CurrentBalance</c> queda de
/// semaforo (¿debe si/no?); el monto real por moneda vive aca.</para>
///
/// <para><b>Es una PROYECCION del calculo de deuda</b> (<c>SupplierService.CalculateSupplierDebt</c>),
/// reescrita en cada recalculo en la misma transaccion que el escalar. En Capa 1 se crea vacia y se
/// backfillea por recalculo (Capa 2).</para>
/// </summary>
public class SupplierBalanceByCurrency
{
    public int Id { get; set; }

    /// <summary>FK a <see cref="Supplier"/> (tabla "Suppliers"). Indexada unica junto con <see cref="Currency"/>.</summary>
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>Moneda de esta linea: "ARS" o "USD" (<c>Monedas.Soportadas</c>).</summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>Compras CONFIRMADAS (NetCost de servicios que cuentan como deuda) en esta moneda.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal ConfirmedPurchases { get; set; }

    /// <summary>Pagado al proveedor imputado a ESTA moneda (suma de <c>ImputedAmount</c>, o <c>Amount</c> si no cruzo).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalPaid { get; set; }

    /// <summary>
    /// Deuda de esta moneda = <see cref="ConfirmedPurchases"/> - <see cref="TotalPaid"/>. Puede ser
    /// negativa (sobrepago al operador en esta moneda). El sobrepago de una moneda NO compensa la
    /// deuda de otra. Indexado por (Currency, Balance) para el top-N de proveedores deudores.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Balance { get; set; }
}
