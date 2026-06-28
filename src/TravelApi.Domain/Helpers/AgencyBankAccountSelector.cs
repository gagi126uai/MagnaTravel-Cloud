using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// ADR-041 (2026-06-28): elige QUE cuenta(s) bancaria(s) de la AGENCIA mostrar en un comprobante (recibo de
/// cobro, presupuesto) para que el cliente sepa a donde transferir. Es una funcion PURA: recibe las cuentas y
/// la moneda del documento, y devuelve la lista a imprimir, sin tocar base de datos ni QuestPDF. Asi la regla
/// de seleccion se puede testear sola, separada de la generacion del PDF (que produce un binario no inspeccionable).
///
/// <para><b>Regla de seleccion</b> (de mas especifica a mas general):
/// <list type="number">
///   <item>La cuenta PRINCIPAL de la MONEDA del documento (camino feliz: una sola, la por defecto de esa moneda).</item>
///   <item>Si no hay principal en esa moneda, se muestran TODAS las principales activas (puede haber de varias
///         monedas), con las de la moneda del documento primero.</item>
///   <item>Red de seguridad: si hay cuentas activas pero NINGUNA principal (p.ej. la principal fue dada de baja),
///         se muestran todas las activas para no esconder el dato bancario.</item>
/// </list>
/// Si la agencia no tiene ninguna cuenta activa, devuelve lista vacia y el PDF OMITE la seccion (no se rompe).</para>
/// </summary>
public static class AgencyBankAccountSelector
{
    /// <summary>
    /// Devuelve las cuentas de la agencia a mostrar como destino de transferencia para un documento en
    /// <paramref name="documentCurrency"/>. Lista vacia = no mostrar la seccion. Ver reglas en la clase.
    /// </summary>
    /// <param name="agencyAccounts">
    /// Cuentas candidatas. Se filtran defensivamente a dueño Agencia + activas (el llamador deberia pasar ya
    /// las de la agencia, pero no confiamos: una cuenta de cliente/proveedor jamas se exhibe como destino de cobro).
    /// </param>
    /// <param name="documentCurrency">Moneda del documento (la de la reserva/cobro). null/vacio se lee como ARS.</param>
    public static IReadOnlyList<BankAccount> SelectForDocument(
        IEnumerable<BankAccount>? agencyAccounts,
        string? documentCurrency)
    {
        if (agencyAccounts is null)
            return Array.Empty<BankAccount>();

        // Solo cuentas de la AGENCIA y activas pueden ser destino de transferencia que mostramos al cliente.
        var activeAgencyAccounts = agencyAccounts
            .Where(account => account is not null
                           && account.OwnerType == BankAccountOwnerType.Agency
                           && account.IsActive)
            .ToList();

        if (activeAgencyAccounts.Count == 0)
            return Array.Empty<BankAccount>();

        var currency = Monedas.Normalizar(documentCurrency);

        // 1) Camino feliz: la principal de la MONEDA del documento. Es la cuenta por defecto para cobrar en
        //    esa moneda; si existe, alcanza con mostrar esa sola.
        var primaryOfCurrency = activeAgencyAccounts
            .Where(account => account.IsPrimary && CurrencyMatches(account.Currency, currency))
            .OrderBy(account => account.CreatedAt)
            .ThenBy(account => account.Id)
            .FirstOrDefault();

        if (primaryOfCurrency is not null)
            return new List<BankAccount> { primaryOfCurrency };

        // 2) No hay principal en la moneda del documento -> mostramos TODAS las principales activas (pueden ser
        //    de varias monedas). Las de la moneda del documento van primero por si el cliente igual puede usarlas.
        var activePrimaries = activeAgencyAccounts
            .Where(account => account.IsPrimary)
            .OrderByDescending(account => CurrencyMatches(account.Currency, currency))
            .ThenBy(account => account.Currency)
            .ThenBy(account => account.CreatedAt)
            .ThenBy(account => account.Id)
            .ToList();

        if (activePrimaries.Count > 0)
            return activePrimaries;

        // 3) Red de seguridad: hay cuentas activas pero ninguna marcada principal (la principal pudo darse de
        //    baja sin promover otra). No escondemos el dato bancario: mostramos todas las activas.
        return activeAgencyAccounts
            .OrderByDescending(account => CurrencyMatches(account.Currency, currency))
            .ThenBy(account => account.Currency)
            .ThenBy(account => account.CreatedAt)
            .ThenBy(account => account.Id)
            .ToList();
    }

    // Compara dos monedas tolerando capitalizacion/espacios (ambas pasan por Monedas.Normalizar).
    private static bool CurrencyMatches(string? accountCurrency, string normalizedDocumentCurrency) =>
        string.Equals(Monedas.Normalizar(accountCurrency), normalizedDocumentCurrency, StringComparison.Ordinal);
}
