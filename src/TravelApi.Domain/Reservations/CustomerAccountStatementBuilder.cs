using System;
using System.Collections.Generic;
using System.Linq;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Tipo de movimiento en el extracto (libro mayor) de la CUENTA POR COBRAR de un cliente. El front lo usa
/// para pintar cada linea (icono/color) sin reclasificar.
/// </summary>
public static class CustomerAccountStatementLineKinds
{
    /// <summary>
    /// Venta CONFIRMADA de una reserva del cliente: SUMA a lo que el cliente le debe a la agencia (cargo).
    /// Es la base del saldo a cobrar (ConfirmedSale), NO la factura: por eso el extracto cierra con el
    /// receivable AUNQUE la venta todavia no este facturada ("facturar tarde").
    /// </summary>
    public const string Sale = "Sale";

    /// <summary>Factura fiscal aprobada: open item documentado que aumenta la cuenta por cobrar.</summary>
    public const string Invoice = "Invoice";

    /// <summary>Nota de debito fiscal aprobada: open item documentado que aumenta la cuenta por cobrar.</summary>
    public const string DebitNote = "DebitNote";

    /// <summary>Nota de credito fiscal aprobada: documento que reduce la cuenta por cobrar.</summary>
    public const string CreditNote = "CreditNote";

    /// <summary>Cobro imputado a la deuda del cliente: RESTA de lo que debe (abono).</summary>
    public const string Payment = "Payment";

    /// <summary>
    /// Compensacion explicita que reduce deuda sin ingreso de caja (por ejemplo, saldo a favor aplicado).
    /// Se separa de Payment para que "Cobrado" mida exclusivamente dinero real.
    /// </summary>
    public const string CreditApplication = "CreditApplication";
}

/// <summary>
/// Una linea YA clasificada del extracto del cliente. El builder NO clasifica ni filtra: recibe lineas
/// planas (solo las VIVAS) y solo las ordena, las agrupa por moneda y calcula el saldo corriente.
///
/// <para>Convencion de signo (estilo banco, lado VENTA): <see cref="Charge"/> es lo que SUMA a la deuda del
/// cliente (venta confirmada); <see cref="Credit"/> es lo que la RESTA (cobro). Una linea tiene Charge o
/// Credit, nunca ambos (el otro va en 0). El cobro puente de sobrepago llega con <see cref="Credit"/>
/// negativo (traslada el excedente al saldo a favor): el signo lo pone la columna, el builder solo acumula.</para>
///
/// <para>A diferencia del extracto de UNA reserva, el del cliente cruza VARIAS reservas; por eso cada linea
/// arrastra la identidad de su reserva (<see cref="ReservaPublicId"/> / <see cref="NumeroReserva"/>) para que
/// el front pueda enlazar cada movimiento a su expediente.</para>
/// </summary>
public readonly record struct CustomerAccountStatementInputLine(
    DateTime Date,
    string Kind,
    string Description,
    string? DocumentRef,
    Guid ReservaPublicId,
    string? NumeroReserva,
    string Currency,
    decimal Charge,
    decimal Credit,
    // PublicId del documento de origen (la reserva en una venta, el cobro en un abono). El builder no lo
    // interpreta: solo lo arrastra a la linea de resultado para que el front cuelgue acciones por renglon.
    Guid? SourcePublicId);

/// <summary>
/// Una linea del extracto del cliente YA con su saldo corriente calculado. <see cref="RunningBalance"/> es el
/// saldo acumulado de la moneda HASTA esta linea inclusive (positivo = el cliente debe; negativo = saldo a
/// favor sin trasladar todavia).
/// </summary>
public readonly record struct CustomerAccountStatementResultLine(
    DateTime Date,
    string Kind,
    string Description,
    string? DocumentRef,
    Guid ReservaPublicId,
    string? NumeroReserva,
    string Currency,
    decimal Charge,
    decimal Credit,
    decimal RunningBalance,
    Guid? SourcePublicId);

/// <summary>
/// Un bloque del extracto: todas las lineas de UNA moneda, en orden cronologico, con su saldo de cierre.
/// </summary>
public sealed class CustomerAccountStatementCurrencyBlock
{
    public string Currency { get; }
    public IReadOnlyList<CustomerAccountStatementResultLine> Lines { get; }

    /// <summary>
    /// Saldo de cierre de esta moneda = el <see cref="CustomerAccountStatementResultLine.RunningBalance"/> de
    /// la ultima linea (0 si no hay lineas).
    ///
    /// <para><b>INVARIANTE del diseño</b>: este saldo de cierre coincide con la suma de
    /// <c>ReservaMoneyByCurrency.Balance</c> (= ConfirmedSale - TotalPaid) de esta moneda sobre TODAS las
    /// reservas EN FIRME del cliente. Ambos parten de los mismos insumos (venta confirmada como cargo, cobros
    /// vivos imputados como abono) con la MISMA imputacion que <see cref="ReservaMoneyCalculator"/>. Como en el
    /// flujo normal el sobrepago se traslada al saldo a favor (dejando cada reserva en Balance &gt;= 0), ese
    /// cierre iguala ademas el "Debe" por moneda del header del cliente (el receivable, que suma solo saldos
    /// positivos). Hay un test invariante que lo verifica.</para>
    /// </summary>
    public decimal ClosingBalance { get; }

    /// <summary>
    /// Creditos que quedaron sin aplicar en esta moneda. Se calcula por reserva y se expone separado del
    /// <see cref="ClosingBalance"/>: nunca compensa automaticamente la deuda de otro expediente.
    /// </summary>
    public decimal UnappliedCredit { get; }

    public CustomerAccountStatementCurrencyBlock(
        string currency,
        IReadOnlyList<CustomerAccountStatementResultLine> lines,
        decimal closingBalance,
        decimal unappliedCredit)
    {
        Currency = currency;
        Lines = lines;
        ClosingBalance = closingBalance;
        UnappliedCredit = unappliedCredit;
    }
}

/// <summary>
/// Resultado completo del extracto del cliente: un bloque por cada moneda presente. Value object puro.
/// </summary>
public sealed class CustomerAccountStatement
{
    public IReadOnlyList<CustomerAccountStatementCurrencyBlock> Currencies { get; }

    public CustomerAccountStatement(IReadOnlyList<CustomerAccountStatementCurrencyBlock> currencies)
    {
        Currencies = currencies;
    }
}

/// <summary>
/// Constructor PURO del "Estado de Cuenta" de un CLIENTE (Cuenta por Cobrar) como LIBRO MAYOR con saldo
/// corriente, estilo extracto bancario. Espejo de <see cref="SupplierAccountStatementBuilder"/> (lado costo) y
/// de <see cref="ReservaAccountStatementBuilder"/> (una sola reserva), pero del lado VENTA y CRUZANDO todas las
/// reservas del cliente: "cuanto nos debe el cliente" con cada venta y cada cobro en una sola linea de tiempo.
///
/// <para><b>Que hace</b>: recibe una lista plana de lineas YA clasificadas (cada venta confirmada como cargo,
/// cada cobro imputado como abono — todas VIVAS, el filtro y la clasificacion los hace el llamador), las separa
/// por moneda, las ordena cronologicamente y calcula el saldo corriente. Cada bloque termina con su saldo de
/// cierre.</para>
///
/// <para><b>Que NO hace (a proposito)</b>: no toca EF ni base de datos, no clasifica, no filtra
/// canceladas/borradas. Todo eso es del llamador (capa de infraestructura), que arma las lineas reusando las
/// MISMAS primitivas de imputacion que el persister del saldo. Asi el builder se testea sin Postgres.</para>
///
/// <para><b>SEGURIDAD</b>: el extracto es venta/cobranza PURA. Las lineas NO transportan costo ni margen, asi
/// que este read-model NO se enmascara por <c>see_cost</c> (a diferencia del extracto del proveedor, que si es
/// lado costo). El gate de acceso vive en el controller, igual que el resto de la cuenta del cliente.</para>
/// </summary>
public static class CustomerAccountStatementBuilder
{
    /// <summary>
    /// Arma el extracto a partir de las lineas planas. Las agrupa por moneda (orden alfabetico estable entre
    /// bloques), ordena cada moneda por fecha (estable: respeta el orden de entrada ante empate de fecha) y
    /// acumula el saldo corriente. Saldo de apertura = 0 (es la cuenta viva del cliente, no hay "saldo anterior").
    ///
    /// <para>El llamador debe insertar las ventas ANTES que los cobros en la lista de entrada: asi, ante misma
    /// fecha, la venta (cargo) queda antes que el cobro (abono) y el saldo corriente se lee natural.</para>
    /// </summary>
    public static CustomerAccountStatement Build(IEnumerable<CustomerAccountStatementInputLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // Agrupamos por moneda preservando el orden de aparicion dentro de cada grupo (clave para el desempate
        // estable cuando dos movimientos comparten fecha: gana el que entro primero en la lista de origen).
        var byCurrency = new Dictionary<string, List<CustomerAccountStatementInputLine>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!byCurrency.TryGetValue(line.Currency, out var bucket))
            {
                bucket = new List<CustomerAccountStatementInputLine>();
                byCurrency[line.Currency] = bucket;
            }
            bucket.Add(line);
        }

        var blocks = new List<CustomerAccountStatementCurrencyBlock>();

        // Orden alfabetico estable entre bloques (ARS antes que USD), coherente con el resto de la cuenta.
        foreach (var currency in byCurrency.Keys.OrderBy(c => c, StringComparer.Ordinal))
        {
            blocks.Add(BuildBlock(currency, byCurrency[currency]));
        }

        return new CustomerAccountStatement(blocks);
    }

    /// <summary>
    /// Arma el bloque de UNA moneda: ordena por fecha (estable) y va acumulando el saldo corriente
    /// (cargo suma, abono resta). El saldo de cierre es el running balance de la ultima linea.
    /// </summary>
    private static CustomerAccountStatementCurrencyBlock BuildBlock(
        string currency,
        List<CustomerAccountStatementInputLine> inputLines)
    {
        // OrderBy de LINQ es ESTABLE: ante misma fecha mantiene el orden de insercion (ventas antes que cobros,
        // segun como el llamador arma la lista). Asi el saldo corriente es reproducible corrida a corrida.
        var ordered = inputLines.OrderBy(line => line.Date).ToList();

        var resultLines = new List<CustomerAccountStatementResultLine>(ordered.Count);
        // El saldo corriente es por reserva. Un abono de R-B no puede disminuir el saldo mostrado de R-A.
        var runningByReserva = new Dictionary<Guid, decimal>();

        foreach (var line in ordered)
        {
            // Cargo suma a la deuda del cliente; abono la resta. Una linea trae uno u otro (el otro en 0); el
            // cobro puente de sobrepago trae Credit negativo, lo que devuelve saldo (correcto: saco el excedente).
            var runningBalance = runningByReserva.GetValueOrDefault(line.ReservaPublicId);
            runningBalance += line.Charge;
            runningBalance -= line.Credit;
            runningByReserva[line.ReservaPublicId] = runningBalance;

            resultLines.Add(new CustomerAccountStatementResultLine(
                Date: line.Date,
                Kind: line.Kind,
                Description: line.Description,
                DocumentRef: line.DocumentRef,
                ReservaPublicId: line.ReservaPublicId,
                NumeroReserva: line.NumeroReserva,
                Currency: line.Currency,
                Charge: line.Charge,
                Credit: line.Credit,
                RunningBalance: runningBalance,
                SourcePublicId: line.SourcePublicId));
        }

        // Open-item principle: el Debe suma solo saldos positivos por reserva. Los negativos quedan visibles
        // como credito no aplicado y requieren una aplicacion explicita para cancelar otra deuda.
        decimal closingBalance = runningByReserva.Values.Where(balance => balance > 0m).Sum();
        decimal unappliedCredit = -runningByReserva.Values.Where(balance => balance < 0m).Sum();
        return new CustomerAccountStatementCurrencyBlock(currency, resultLines, closingBalance, unappliedCredit);
    }
}
