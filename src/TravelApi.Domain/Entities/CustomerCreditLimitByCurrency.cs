using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-040 (cuenta corriente del cliente, 2026-06-26): limite de credito de un cliente SEPARADO por moneda.
/// Espejo estructural de <see cref="SupplierBalanceByCurrency"/> / <see cref="ReservaMoneyByCurrency"/>: una
/// fila por (cliente, moneda). El conjunto de monedas es ABIERTO (no hay columnas fijas ARS/USD).
///
/// <para><b>Por que una tabla y no el campo <c>Customer.CreditLimit</c></b> (review B3): <c>CreditLimit</c> es
/// un unico decimal SIN moneda — comparar la deuda del cliente (que es por moneda) contra un escalar sin moneda
/// mezcla peras con manzanas. El limite tiene que vivir por moneda igual que la deuda. <c>Customer.CreditLimit</c>
/// queda MUERTO (zombie historico, no se lee ni se escribe desde ADR-040); este es el dato vivo.</para>
///
/// <para><b>Semantica de AUSENCIA</b> (decision del dueño): si NO hay fila para una moneda, esa moneda es
/// PREPAGO para el cliente — debe quedar saldado en esa moneda para viajar/cerrar. Es decir, "sin limite
/// definido" NO significa "credito infinito"; significa "credito cero en esa moneda". Por eso el evaluador de
/// credito (<c>ClientCreditPolicy</c>) bloquea si el cliente debe en una moneda que no tiene fila aca.</para>
///
/// <para>El monto es dato de PLATA: donde se exponga, respeta el enmascarado de costo (see_cost), igual que
/// los saldos por moneda.</para>
/// </summary>
public class CustomerCreditLimitByCurrency
{
    public int Id { get; set; }

    /// <summary>FK a <see cref="Customer"/>. Indexada unica junto con <see cref="Currency"/>.</summary>
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>Moneda de esta linea: "ARS" o "USD" (<c>Monedas.Soportadas</c>). Conjunto abierto.</summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>
    /// Limite de credito que el cliente puede deber en ESTA moneda. La deuda del cliente en esta moneda
    /// (exposicion) puede llegar hasta este valor; pasarse frena el viaje/cierre (o solo avisa, segun la
    /// llave <c>BlockTravelWhenCreditExceeded</c>). Debe ser &gt;= 0.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Limit { get; set; }
}
