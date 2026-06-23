using System;
using System.Collections.Generic;
using System.Linq;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Tipo de movimiento en el extracto (libro mayor) de la reserva. Sirve para que el front pinte cada
/// linea (icono/color) sin tener que reclasificar comprobantes. Espejo legible de la categoria fiscal.
/// </summary>
public static class AccountStatementLineKinds
{
    public const string Invoice = "Invoice";
    public const string CreditNote = "CreditNote";
    public const string DebitNote = "DebitNote";
    public const string Payment = "Payment";
}

/// <summary>
/// Una linea YA clasificada del extracto, tal como la arma el llamador desde las Invoices/Payments de la
/// reserva. El builder NO clasifica ni filtra: recibe lineas planas (solo las VIVAS) y solo las ordena,
/// las agrupa por moneda y calcula el saldo corriente.
///
/// <para>Convencion de signo (estilo banco): <see cref="Charge"/> es lo que SUMA a la deuda del cliente
/// (factura, nota de debito); <see cref="Credit"/> es lo que la RESTA (cobro, nota de credito). Una linea
/// tiene Charge o Credit, nunca ambos a la vez (el otro va en 0). Ambos se esperan en POSITIVO; el signo lo
/// pone la columna, no el valor.</para>
/// </summary>
public readonly record struct AccountStatementInputLine(
    DateTime Date,
    string Kind,
    string Description,
    string? DocumentRef,
    string Currency,
    decimal Charge,
    decimal Credit,
    // PublicId del documento (Invoice o Payment) que origino la linea. El builder no lo interpreta: solo lo
    // arrastra hasta la linea de resultado para que el front cuelgue acciones por renglon. Ver el DTO de App.
    Guid? SourcePublicId);

/// <summary>
/// Una linea del extracto YA con su saldo corriente calculado. Es lo que el builder devuelve (espejo del
/// DTO de la capa de aplicacion). <see cref="RunningBalance"/> es el saldo acumulado de la moneda HASTA esta
/// linea inclusive (positivo = el cliente debe; negativo = saldo a favor).
/// </summary>
public readonly record struct AccountStatementResultLine(
    DateTime Date,
    string Kind,
    string Description,
    string? DocumentRef,
    string Currency,
    decimal Charge,
    decimal Credit,
    decimal RunningBalance,
    // PublicId del documento de origen (Invoice/Payment), arrastrado tal cual desde la linea de entrada.
    Guid? SourcePublicId);

/// <summary>
/// Un bloque del extracto: todas las lineas de UNA moneda, en orden cronologico, con su saldo de cierre.
/// </summary>
public sealed class AccountStatementCurrencyBlock
{
    public string Currency { get; }
    public IReadOnlyList<AccountStatementResultLine> Lines { get; }

    /// <summary>
    /// Saldo de cierre de esta moneda = el <see cref="AccountStatementResultLine.RunningBalance"/> de la
    /// ultima linea (0 si no hay lineas). INVARIANTE del diseño: debe coincidir con el
    /// <c>PorMoneda[moneda].Balance</c> de <see cref="ReservaMoneyCalculator"/> para esta misma reserva,
    /// porque ambos parten de los mismos comprobantes/cobros vivos. (Ojo: el extracto suma facturado-neto,
    /// mientras que Balance usa ConfirmedSale; ver nota en el builder de la capa de aplicacion.)
    /// </summary>
    public decimal ClosingBalance { get; }

    public AccountStatementCurrencyBlock(string currency, IReadOnlyList<AccountStatementResultLine> lines, decimal closingBalance)
    {
        Currency = currency;
        Lines = lines;
        ClosingBalance = closingBalance;
    }
}

/// <summary>
/// Resultado completo del extracto: un bloque por cada moneda presente. Es un value object puro.
/// </summary>
public sealed class ReservaAccountStatement
{
    public IReadOnlyList<AccountStatementCurrencyBlock> Currencies { get; }

    public ReservaAccountStatement(IReadOnlyList<AccountStatementCurrencyBlock> currencies)
    {
        Currencies = currencies;
    }
}

/// <summary>
/// Constructor PURO del "Estado de Cuenta" de una reserva como LIBRO MAYOR con saldo corriente, estilo
/// extracto bancario.
///
/// <para><b>Que hace</b>: recibe una lista plana de lineas YA clasificadas (cada factura/ND como cargo,
/// cada cobro/NC como abono — todas VIVAS, el filtro lo hace el llamador), las separa por moneda, las ordena
/// cronologicamente y calcula el saldo corriente (running balance) de cada moneda. Cada bloque termina con su
/// saldo de cierre.</para>
///
/// <para><b>Que NO hace (a proposito)</b>: no toca EF ni base de datos, no clasifica comprobantes AFIP, no
/// filtra anulados/borrados, no mira costos. Todo eso es responsabilidad del llamador (capa de aplicacion),
/// que arma las lineas reusando los clasificadores canonicos. Asi el builder se testea sin Postgres, igual
/// que <see cref="ReservaInvoicingCuadreCalculator"/>.</para>
///
/// <para><b>SEGURIDAD</b>: el extracto es venta/cobranza PURA. Las lineas no transportan ningun campo de
/// costo ni margen, asi que este read-model NO necesita pasar por el enmascarado de costo. Ver la nota en el
/// armado del DTO (ReservaService).</para>
/// </summary>
public static class ReservaAccountStatementBuilder
{
    /// <summary>
    /// Arma el extracto a partir de las lineas planas. Las agrupa por moneda (orden alfabetico estable entre
    /// bloques), ordena cada moneda por fecha (estable: respeta el orden de entrada ante empate de fecha) y
    /// acumula el saldo corriente. Saldo de apertura = 0 (es por reserva: no hay "saldo anterior").
    /// </summary>
    public static ReservaAccountStatement Build(IEnumerable<AccountStatementInputLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // Agrupamos por moneda preservando el orden de aparicion dentro de cada grupo (clave para el desempate
        // estable cuando dos movimientos comparten fecha: gana el que entro primero en la lista de origen).
        var byCurrency = new Dictionary<string, List<AccountStatementInputLine>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!byCurrency.TryGetValue(line.Currency, out var bucket))
            {
                bucket = new List<AccountStatementInputLine>();
                byCurrency[line.Currency] = bucket;
            }
            bucket.Add(line);
        }

        var blocks = new List<AccountStatementCurrencyBlock>();

        // Orden alfabetico estable entre bloques (ARS antes que USD), coherente con como el detalle ordena PorMoneda.
        foreach (var currency in byCurrency.Keys.OrderBy(c => c, StringComparer.Ordinal))
        {
            var block = BuildBlock(currency, byCurrency[currency]);
            blocks.Add(block);
        }

        return new ReservaAccountStatement(blocks);
    }

    /// <summary>
    /// Arma el bloque de UNA moneda: ordena por fecha (estable) y va acumulando el saldo corriente
    /// (cargo suma, abono resta). El saldo de cierre es el running balance de la ultima linea.
    /// </summary>
    private static AccountStatementCurrencyBlock BuildBlock(string currency, List<AccountStatementInputLine> inputLines)
    {
        // OrderBy de LINQ es ESTABLE: ante misma fecha, mantiene el orden de insercion (que ya viene del orden
        // de la lista de origen). Asi una factura y su cobro del mismo dia quedan en un orden deterministico.
        var ordered = inputLines.OrderBy(line => line.Date).ToList();

        var resultLines = new List<AccountStatementResultLine>(ordered.Count);
        decimal runningBalance = 0m; // saldo de apertura por reserva = 0 (no hay saldo anterior)

        foreach (var line in ordered)
        {
            // Cargo suma a la deuda; abono la resta. Una linea trae uno u otro (el otro en 0).
            runningBalance += line.Charge;
            runningBalance -= line.Credit;

            resultLines.Add(new AccountStatementResultLine(
                Date: line.Date,
                Kind: line.Kind,
                Description: line.Description,
                DocumentRef: line.DocumentRef,
                Currency: line.Currency,
                Charge: line.Charge,
                Credit: line.Credit,
                RunningBalance: runningBalance,
                SourcePublicId: line.SourcePublicId));
        }

        // El saldo de cierre es el ultimo running balance (0 si el bloque quedo vacio, caso que no deberia
        // pasar porque solo creamos bloques para monedas con al menos una linea).
        decimal closingBalance = resultLines.Count > 0 ? resultLines[^1].RunningBalance : 0m;

        return new AccountStatementCurrencyBlock(currency, resultLines, closingBalance);
    }
}
