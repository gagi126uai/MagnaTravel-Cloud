namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-040 (cuenta corriente del cliente, 2026-06-26): forma en la que un cliente paga sus reservas.
///
/// <para><b>Prepaid</b> (default, byte-identico a ADR-036 prepago puro): el cliente tiene que quedar SALDADO
/// antes de viajar y antes de cerrar. Es el unico modo que existia hasta ADR-040.</para>
///
/// <para><b>Account</b> (cuenta corriente): el cliente puede VIAJAR y CERRAR debiendo, siempre que su deuda
/// total se mantenga dentro del limite de credito que la agencia le asigna POR MONEDA (ver
/// <c>CustomerCreditLimitByCurrency</c>). Es la excepcion controlada al prepago puro.</para>
///
/// <para>Se guarda como entero (EF default enum-&gt;int). El orden de los valores NO debe cambiarse: ya hay
/// columnas persistidas que lo referencian (Customer.BillingMode, OperationalFinanceSettings.DefaultCustomerBillingMode).</para>
/// </summary>
public enum CustomerBillingMode
{
    /// <summary>Prepago: paga el 100% antes de viajar/cerrar (comportamiento clasico ADR-036).</summary>
    Prepaid = 0,

    /// <summary>Cuenta corriente: puede deber dentro de su limite por moneda.</summary>
    Account = 1
}
