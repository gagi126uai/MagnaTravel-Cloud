using System;
using System.Collections.Generic;
using System.Linq;
using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Tipo de movimiento en el extracto (libro mayor) de la CUENTA POR PAGAR de un proveedor. El front lo usa
/// para pintar cada linea (icono/color) sin reclasificar.
/// </summary>
public static class SupplierAccountStatementLineKinds
{
    /// <summary>Compra confirmada al operador: SUMA a lo que la agencia le debe (cargo).</summary>
    public const string Purchase = "Purchase";

    /// <summary>Pago al operador: RESTA de lo que la agencia le debe (abono).</summary>
    public const string Payment = "Payment";

    // ====================================================================================================
    // Pasos B/C cuenta del operador (2026-06-29): movimientos del "Circuito de cancelacion". NO entran al
    // running balance de CAJA (Purchase/Payment) — viven en un bloque APARTE y derivan-en-lectura del estado
    // de las cancelaciones del operador. Son la CONTRAPARTIDA de visualizacion del pago negativo que deja una
    // anulacion: explican por que el saldo de caja quedo negativo y hacia donde se va (multa retenida +
    // reembolso recibido), de modo que la cuenta del operador cierre cuando el operador devuelve / retiene multa.
    // ====================================================================================================

    /// <summary>
    /// Circuito de cancelacion: MULTA que el operador RETUVO de una cancelacion (cargo +). Es plata que la
    /// agencia ya no espera de vuelta porque el operador se la quedo como penalidad pass-through. Reduce el
    /// "me tiene que devolver", NO infla el "le debo".
    /// </summary>
    public const string PenaltyRetained = "PenaltyRetained";

    /// <summary>
    /// Circuito de cancelacion: REEMBOLSO efectivamente recibido del operador por una cancelacion (cargo +).
    /// Neutraliza la parte del pago negativo que el operador ya devolvio.
    /// </summary>
    public const string RefundReceived = "RefundReceived";

    /// <summary>
    /// Bug de lectura (2026-07-03): SALDO A FAVOR consumido con el operador APLICADO a la deuda de una reserva
    /// (cargo +). Cuando la agencia usa su saldo a favor (sobrepago) para cubrir la deuda de otra reserva del
    /// mismo operador, el saldo a favor disponible BAJA. Esta linea es la contrapartida de visualizacion: mueve
    /// el saldo economico hacia 0 (reduce el sobrepago) para que el saldo a favor MOSTRADO en el header coincida
    /// con lo que realmente queda para gastar (el pool). NO mueve la CAJA (no hubo movimiento de efectivo), igual
    /// que las lineas de circuito: vive solo en el saldo economico intercalado, no en el saldo de caja.
    /// </summary>
    public const string CreditApplied = "CreditApplied";

    /// <summary>
    /// ADR-044 T2 Addendum (2026-07-10): cargo del operador FACTURADO APARTE (<c>PenaltyCollectionMode.FacturadaAparte</c>):
    /// el operador devuelve el reembolso INTEGRO (no retiene nada) pero factura este cargo con su propio
    /// documento. Es una DEUDA NUEVA de la agencia hacia el operador (cargo +), NUNCA una retencion — no se
    /// confunde con <see cref="PenaltyRetained"/> (que es plata que el operador SI se quedo de la caja).
    ///
    /// <para><b>(2026-07-15) Ya suma al saldo OFICIAL</b>: este cargo vive en el bloque "circuito" del extracto
    /// (economico, igual que <see cref="PenaltyRetained"/>/<see cref="RefundReceived"/>) Y ADEMAS suma a
    /// <c>Supplier.CurrentBalance</c>/<c>SupplierBalanceByCurrency</c> (la cuenta por pagar REAL): lo agrega
    /// <c>SupplierDebtPersister</c> via <c>OperatorChargeInvoicedReader</c>, en su propia columna
    /// <c>SupplierBalanceByCurrency.OperatorChargesInvoiced</c>. Antes de esta fecha vivia SOLO en el circuito y
    /// el "Saldo (deuda)" del listado de proveedores lo ignoraba (mostraba menos deuda que la real); ese gap
    /// quedo cerrado. A diferencia de <see cref="PenaltyRetained"/> (que NO se suma al saldo oficial porque ya
    /// esta neteado en el reembolso esperado del operador), este cargo SI es deuda nueva.</para>
    /// </summary>
    public const string OperatorChargeInvoiced = "OperatorChargeInvoiced";

    /// <summary>
    /// ADR-044 T3b Decision 3 (2026-07-10): "Diferencia de cambio" de tesoreria de un cargo del operador
    /// liquidado con conversion (la ND salio con un TC, la liquidacion real fue a otro). A diferencia de
    /// <see cref="PenaltyRetained"/>/<see cref="RefundReceived"/>/<see cref="OperatorChargeInvoiced"/>, esta
    /// linea es la UNICA del bloque circuito cuyo <see cref="SupplierCircuitLine.Amount"/> puede ser NEGATIVO
    /// (a favor de la agencia = positivo; en contra = negativo) — es un ajuste de valuacion, no un cargo. Solo
    /// se muestran las filas VIGENTES (<c>IsSuperseded = false</c>).
    /// </summary>
    public const string TreasuryFxAdjustment = "TreasuryFxAdjustment";

    /// <summary>
    /// Obra "la ficha del operador no borra la historia" (2026-08-20, F-6/F-2): contra-linea de una
    /// compra que quedo SIN EFECTO porque la reserva se anulo DESPUES de haberse confirmado con el
    /// operador. Es un ABONO (resta) por el MISMO monto que la compra original (que sigue viva en el
    /// extracto, tachada) — juntas netean CERO, asi el saldo de la moneda no cambia un centavo por
    /// mostrar el rastro. NUNCA se borra la compra original: se agrega esta contra-linea al lado (F-6,
    /// "una reversa es un contra-asiento, no un borrado").
    /// </summary>
    public const string PurchaseReversal = "PurchaseReversal";
}

/// <summary>
/// Una linea YA clasificada del extracto del proveedor. El builder NO clasifica ni filtra: recibe lineas
/// planas (solo las VIVAS) y solo las ordena, las agrupa por moneda y calcula el saldo corriente.
///
/// <para>Convencion de signo (estilo banco, lado COSTO): <see cref="Charge"/> es lo que SUMA a la deuda con
/// el operador (compra confirmada); <see cref="Credit"/> es lo que la RESTA (pago al operador). Una linea
/// tiene Charge o Credit, nunca ambos (el otro va en 0). Ambos se esperan en POSITIVO; el signo lo pone la
/// columna, no el valor.</para>
/// </summary>
public readonly record struct SupplierAccountStatementInputLine(
    DateTime Date,
    string Kind,
    string Description,
    string? DocumentRef,
    string Currency,
    decimal Charge,
    decimal Credit,
    // PublicId del documento de origen (el servicio comprado o el pago) que origino la linea. El builder no
    // lo interpreta: solo lo arrastra hasta la linea de resultado para que el front cuelgue acciones por
    // renglon (ver el servicio, ver/anular el pago).
    Guid? SourcePublicId,
    // ================================================================================================
    // Tanda backend "Registrar pago" (2026-07-20): a que reserva/servicio bajo la plata de un ABONO
    // (pago al operador). Van AL FINAL y son OPCIONALES (default null) a proposito: son campos nuevos
    // que solo poblamos para las lineas de PAGO (ver PaymentLine); las de COMPRA los dejan en null porque
    // ya llevan la reserva en DocumentRef. Ponerlos al final evita romper los callers existentes que
    // arman la linea con argumentos posicionales (los tests del builder, por ejemplo).
    // ================================================================================================
    /// <summary>Numero de la reserva a la que se imputo el pago. Null si es un anticipo "a cuenta" o si la linea es una compra.</summary>
    string? ReservaNumero = null,
    /// <summary>Descripcion del servicio puntual imputado (si el pago se imputo a un servicio, no a toda la reserva). Null en el resto de los casos.</summary>
    string? ServicioDescripcion = null,
    /// <summary>
    /// (2026-08-20) true si la RESERVA duena de esta linea esta anulada (Cancelled/PendingOperatorRefund).
    /// Es un HECHO sobre la reserva, no una instruccion de que chip pintar: el front decide el chip rojo
    /// "Anulada" mirando el <see cref="Kind"/> de la linea (solo la compra lo lleva), no este campo solo.
    /// Default false para no romper a los callers/tests existentes que arman lineas de reservas vivas.
    /// </summary>
    bool ReservaIsVoided = false);

/// <summary>
/// Una linea del extracto del proveedor YA con su saldo corriente calculado. <see cref="RunningBalance"/>
/// es el saldo acumulado de la moneda HASTA esta linea inclusive (positivo = la agencia le debe al operador;
/// negativo = saldo a favor de la agencia, es decir le pago de mas en esa moneda).
/// </summary>
public readonly record struct SupplierAccountStatementResultLine(
    DateTime Date,
    string Kind,
    string Description,
    string? DocumentRef,
    string Currency,
    decimal Charge,
    decimal Credit,
    decimal RunningBalance,
    Guid? SourcePublicId,
    /// <summary>Numero de la reserva a la que se imputo el pago (ver <see cref="SupplierAccountStatementInputLine.ReservaNumero"/>).</summary>
    string? ReservaNumero = null,
    /// <summary>Descripcion del servicio puntual imputado (ver <see cref="SupplierAccountStatementInputLine.ServicioDescripcion"/>).</summary>
    string? ServicioDescripcion = null,
    /// <summary>Ver <see cref="SupplierAccountStatementInputLine.ReservaIsVoided"/>.</summary>
    bool ReservaIsVoided = false);

/// <summary>
/// Un bloque del extracto: todas las lineas de UNA moneda, en orden cronologico, con su saldo de cierre.
/// </summary>
public sealed class SupplierAccountStatementCurrencyBlock
{
    public string Currency { get; }
    public IReadOnlyList<SupplierAccountStatementResultLine> Lines { get; }

    /// <summary>
    /// Saldo de cierre de esta moneda = el <see cref="SupplierAccountStatementResultLine.RunningBalance"/> de
    /// la ultima linea (0 si no hay lineas).
    ///
    /// <para><b>INVARIANTE del diseño</b>: este saldo de cierre DEBE coincidir EXACTAMENTE con el
    /// <c>SupplierBalanceByCurrency.Balance</c> de esta misma moneda para este proveedor. Ambos parten de los
    /// MISMOS insumos (compras confirmadas que cuentan como deuda + pagos vivos imputados) y usan la MISMA
    /// primitiva de imputacion (<see cref="SupplierDebtCalculator.ImputedCurrencyOf"/> /
    /// <see cref="SupplierDebtCalculator.ImputedAmountOf"/>). El persister materializa el Balance corriendo
    /// <see cref="SupplierDebtCalculator.Calculate"/> sobre esos mismos insumos, asi que extracto y proyeccion
    /// no pueden divergir. Hay un test invariante que lo verifica.</para>
    /// </summary>
    public decimal ClosingBalance { get; }

    public SupplierAccountStatementCurrencyBlock(
        string currency,
        IReadOnlyList<SupplierAccountStatementResultLine> lines,
        decimal closingBalance)
    {
        Currency = currency;
        Lines = lines;
        ClosingBalance = closingBalance;
    }
}

/// <summary>
/// Resultado completo del extracto del proveedor: un bloque por cada moneda presente. Value object puro.
/// </summary>
public sealed class SupplierAccountStatement
{
    public IReadOnlyList<SupplierAccountStatementCurrencyBlock> Currencies { get; }

    public SupplierAccountStatement(IReadOnlyList<SupplierAccountStatementCurrencyBlock> currencies)
    {
        Currencies = currencies;
    }
}

/// <summary>
/// Constructor PURO del "Estado de Cuenta" de un PROVEEDOR (Cuenta por Pagar) como LIBRO MAYOR con saldo
/// corriente, estilo extracto bancario. Espejo de <see cref="ReservaAccountStatementBuilder"/>, pero del lado
/// COSTO: en vez de "cuanto nos debe el cliente" muestra "cuanto le debemos al operador".
///
/// <para><b>Que hace</b>: recibe una lista plana de lineas YA clasificadas (cada compra confirmada como
/// cargo, cada pago al operador como abono — todas VIVAS, el filtro lo hace el llamador), las separa por
/// moneda, las ordena cronologicamente y calcula el saldo corriente de cada moneda. Cada bloque termina con
/// su saldo de cierre.</para>
///
/// <para><b>Que NO hace (a proposito)</b>: no toca EF ni base de datos, no clasifica que cuenta como deuda,
/// no filtra anulados/borrados ni proveedores CommissionOnly. Todo eso es responsabilidad del llamador (capa
/// de infraestructura), que arma las lineas reusando exactamente los mismos clasificadores y el mismo
/// universo de servicios/pagos que el persister de la deuda. Asi el builder se testea sin Postgres.</para>
///
/// <para><b>SEGURIDAD</b>: a diferencia del extracto de la RESERVA (venta/cobranza, no se enmascara), este
/// extracto es del lado COSTO (deuda con el operador). El llamador DEBE enmascarar los montos cuando el caller
/// no tiene <c>cobranzas.see_cost</c>, igual que el resto de la cuenta del proveedor.</para>
/// </summary>
public static class SupplierAccountStatementBuilder
{
    /// <summary>
    /// Construye la linea de un CARGO (compra confirmada al operador). La moneda se normaliza (null -> ARS)
    /// igual que en <see cref="SupplierDebtCalculator"/>, para que el cargo caiga en el mismo bucket de moneda
    /// que su compra confirmada en el calculo de deuda.
    /// </summary>
    public static SupplierAccountStatementInputLine PurchaseLine(
        DateTime date,
        string description,
        string? documentRef,
        string? currency,
        decimal netCost,
        Guid? sourcePublicId,
        // (2026-08-20) Ver el XML-doc de SupplierAccountStatementInputLine.ReservaIsVoided. Default false:
        // la inmensa mayoria de las compras son de reservas vivas.
        bool reservaIsVoided = false)
        => new(
            Date: date,
            Kind: SupplierAccountStatementLineKinds.Purchase,
            Description: description,
            DocumentRef: documentRef,
            Currency: Monedas.Normalizar(currency),
            Charge: netCost,
            Credit: 0m,
            SourcePublicId: sourcePublicId,
            ReservaIsVoided: reservaIsVoided);

    /// <summary>
    /// Obra "la ficha del operador no borra la historia" (2026-08-20): construye la contra-linea
    /// "Anulacion de compra" que acompaña a una <see cref="PurchaseLine"/> cuya reserva se anulo DESPUES
    /// de haberse confirmado con el operador. Es un ABONO (resta) por el MISMO monto que la compra
    /// original, fechado el dia de la anulacion (no el dia de la compra) — juntas netean CERO.
    /// </summary>
    /// <param name="date">Fecha en que se confirmo la anulacion de la reserva (no la fecha de la compra).</param>
    /// <param name="reservaNumero">Numero de la reserva anulada, para el texto "Anulacion de compra · Reserva {numero} ({servicio})".</param>
    /// <param name="servicioDescripcion">Descripcion corta del servicio anulado (sin el prefijo "Tipo:").</param>
    /// <param name="netCost">MISMO monto que tenia la compra original en <see cref="PurchaseLine"/> (neteo exacto).</param>
    /// <param name="sourcePublicId">PublicId del servicio anulado (mismo que la compra original): la fila no lleva
    /// acciones propias, pero conserva la trazabilidad hacia el servicio de origen.</param>
    public static SupplierAccountStatementInputLine PurchaseReversalLine(
        DateTime date,
        string reservaNumero,
        string servicioDescripcion,
        string? currency,
        decimal netCost,
        Guid? sourcePublicId)
        => new(
            Date: date,
            Kind: SupplierAccountStatementLineKinds.PurchaseReversal,
            Description: $"Anulación de compra · Reserva {reservaNumero} ({servicioDescripcion})",
            // Sin comprobante propio: es un contra-asiento, no un documento nuevo (el front lo muestra como "—").
            DocumentRef: null,
            Currency: Monedas.Normalizar(currency),
            Charge: 0m,
            Credit: netCost,
            SourcePublicId: sourcePublicId,
            // (2026-08-20) true: la reserva duena de esta linea TAMBIEN esta anulada (es la misma reserva que la
            // compra que revierte). El front NUNCA pinta el chip rojo "Anulada" aca porque decide por Kind
            // (PurchaseReversal siempre lleva el chip ambar "Anulacion", nunca el rojo) — ver §1.4 de la spec.
            ReservaIsVoided: true);

    /// <summary>
    /// Construye la linea de un ABONO (pago al operador). La moneda y el monto del abono se derivan con las
    /// MISMAS primitivas de imputacion que usa el calculo de deuda
    /// (<see cref="SupplierDebtCalculator.ImputedCurrencyOf"/> / <see cref="SupplierDebtCalculator.ImputedAmountOf"/>):
    /// un pago cruzado abona en la moneda IMPUTADA por su equivalente imputado. Esto garantiza que el saldo de
    /// cierre del extracto cierre exactamente con la deuda materializada (no hay segunda formula).
    /// </summary>
    public static SupplierAccountStatementInputLine PaymentLine(
        DateTime date,
        string description,
        string? documentRef,
        SupplierDebtCalculator.SupplierPaymentInput payment,
        Guid? sourcePublicId,
        // Tanda backend "Registrar pago" (2026-07-20): a que reserva/servicio bajo esta plata. Opcionales
        // (null = anticipo "a cuenta", sin reserva imputada) para no romper a los callers/tests que ya
        // arman una linea de pago sin estos dos datos nuevos.
        string? reservaNumero = null,
        string? servicioDescripcion = null)
        => new(
            Date: date,
            Kind: SupplierAccountStatementLineKinds.Payment,
            Description: description,
            DocumentRef: documentRef,
            Currency: SupplierDebtCalculator.ImputedCurrencyOf(payment),
            Charge: 0m,
            Credit: SupplierDebtCalculator.ImputedAmountOf(payment),
            SourcePublicId: sourcePublicId,
            ReservaNumero: reservaNumero,
            ServicioDescripcion: servicioDescripcion);

    /// <summary>
    /// Arma el extracto a partir de las lineas planas. Las agrupa por moneda (orden alfabetico estable entre
    /// bloques), ordena cada moneda por fecha (estable: respeta el orden de entrada ante empate de fecha) y
    /// acumula el saldo corriente. Saldo de apertura = 0 (es por proveedor: no hay "saldo anterior").
    /// </summary>
    public static SupplierAccountStatement Build(IEnumerable<SupplierAccountStatementInputLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // Agrupamos por moneda preservando el orden de aparicion dentro de cada grupo (clave para el
        // desempate estable cuando dos movimientos comparten fecha: gana el que entro primero).
        var byCurrency = new Dictionary<string, List<SupplierAccountStatementInputLine>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!byCurrency.TryGetValue(line.Currency, out var bucket))
            {
                bucket = new List<SupplierAccountStatementInputLine>();
                byCurrency[line.Currency] = bucket;
            }
            bucket.Add(line);
        }

        var blocks = new List<SupplierAccountStatementCurrencyBlock>();

        // Orden alfabetico estable entre bloques (ARS antes que USD), coherente con el resto de la cuenta.
        foreach (var currency in byCurrency.Keys.OrderBy(c => c, StringComparer.Ordinal))
        {
            blocks.Add(BuildBlock(currency, byCurrency[currency]));
        }

        return new SupplierAccountStatement(blocks);
    }

    /// <summary>
    /// Arma el bloque de UNA moneda: ordena por fecha (estable) y va acumulando el saldo corriente
    /// (cargo suma, abono resta). El saldo de cierre es el running balance de la ultima linea.
    /// </summary>
    private static SupplierAccountStatementCurrencyBlock BuildBlock(
        string currency,
        List<SupplierAccountStatementInputLine> inputLines)
    {
        // OrderBy de LINQ es ESTABLE: ante misma fecha, mantiene el orden de insercion. Asi una compra y su
        // pago del mismo dia quedan en un orden deterministico.
        var ordered = inputLines.OrderBy(line => line.Date).ToList();

        var resultLines = new List<SupplierAccountStatementResultLine>(ordered.Count);
        decimal runningBalance = 0m; // saldo de apertura por proveedor = 0

        foreach (var line in ordered)
        {
            // Cargo suma a la deuda con el operador; abono la resta. Una linea trae uno u otro (el otro en 0).
            runningBalance += line.Charge;
            runningBalance -= line.Credit;

            resultLines.Add(new SupplierAccountStatementResultLine(
                Date: line.Date,
                Kind: line.Kind,
                Description: line.Description,
                DocumentRef: line.DocumentRef,
                Currency: line.Currency,
                Charge: line.Charge,
                Credit: line.Credit,
                RunningBalance: runningBalance,
                SourcePublicId: line.SourcePublicId,
                ReservaNumero: line.ReservaNumero,
                ServicioDescripcion: line.ServicioDescripcion,
                ReservaIsVoided: line.ReservaIsVoided));
        }

        decimal closingBalance = resultLines.Count > 0 ? resultLines[^1].RunningBalance : 0m;
        return new SupplierAccountStatementCurrencyBlock(currency, resultLines, closingBalance);
    }
}
