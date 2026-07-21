using System.Globalization;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// P1 "circuito proveedor" (2026-07-21): formatea montos de plata en el formato que usa Argentina —
/// punto para separar miles y coma para los decimales (ej. <c>$1.234,56</c>).
///
/// <para><b>Por que existe</b>: escribir <c>{monto:N2}</c> directo en un mensaje usa la cultura POR
/// DEFECTO del servidor (en la mayoria de los entornos .NET, "en-US"), que sale "gringa"
/// (<c>1,234.56</c>, coma y punto AL REVES de lo que espera un vendedor argentino). Un vendedor que
/// ve "1,800.00" puede leerlo como "1 con 800 centavos" en vez de "mil ochocientos" — confunde en un
/// mensaje de guard de plata. Este helper centraliza el formato correcto para que ningun mensaje
/// nuevo vuelva a salir mal.</para>
/// </summary>
public static class CurrencyDisplayFormat
{
    // CultureInfo.GetCultureInfo (a diferencia de "new CultureInfo") devuelve una instancia cacheada
    // de solo lectura — mas barato si este helper se llama muchas veces (ej. un PDF con varias lineas).
    private static readonly CultureInfo EsAr = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>
    /// Formatea un monto con 2 decimales en formato es-AR, SIN simbolo de moneda (el llamador antepone
    /// el "$" o el codigo ISO segun corresponda, porque eso varia por contexto — ej. "USD 1.234,56" vs
    /// "$ 1.234,56").
    /// </summary>
    public static string Amount(decimal value) => value.ToString("N2", EsAr);
}
