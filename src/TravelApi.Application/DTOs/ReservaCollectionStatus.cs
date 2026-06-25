namespace TravelApi.Application.DTOs;

/// <summary>
/// Una linea de actividad de cobro por moneda, usada para DERIVAR el estado de cobro distinguiendo
/// "sin movimientos" de "saldado de verdad". Lleva el saldo de la moneda mas dos senales de actividad:
/// si hubo CARGOS (algo que cobrar) y si hubo COBROS (plata recibida).
///
/// <para>Por que un tipo aparte y no pasar la fila de plata completa: <c>Derive</c> es una regla pura del
/// dominio de cobro; no debe depender de los DTOs de presentacion (margen, facturacion por moneda, costo).
/// Este struct expone solo lo que la regla necesita.</para>
/// </summary>
public readonly struct ReservaCollectionLine
{
    /// <summary>Saldo de la moneda. &gt; 0 = deuda del cliente; &lt; 0 = saldo a favor; 0 = sin saldo.</summary>
    public decimal Balance { get; }

    /// <summary>True si en esta moneda hubo algo que cobrar (venta &gt; 0). Distingue "sin cargos" de "saldado".</summary>
    public bool HasCharges { get; }

    /// <summary>True si en esta moneda se recibio algun cobro (pagado &gt; 0).</summary>
    public bool HasPayments { get; }

    public ReservaCollectionLine(decimal balance, bool hasCharges, bool hasPayments)
    {
        Balance = balance;
        HasCharges = hasCharges;
        HasPayments = hasPayments;
    }
}

/// <summary>
/// ADR-033 (E7/A5, 2026-06-16): ESTADO DE COBRO de una reserva, DERIVADO del saldo POR MONEDA. NO se persiste
/// (no hay columna en BD): es la lectura del saldo que ya vive por moneda en <c>ReservaMoneyByCurrency</c>.
///
/// <para>Es un eje INDEPENDIENTE del estado operativo (<c>Reserva.Status</c>) y de la facturacion (existencia
/// de factura con CAE). Una reserva Finalizada puede estar "ConDeuda"; una En gestion puede estar "Saldada".</para>
///
/// <para>Por que por moneda y no por el escalar: el <c>Balance</c> escalar suma ARS + USD, lo que es
/// semanticamente impuro. Una reserva que debe USD y tiene saldo a favor ARS daria escalar ~0 ("Saldada"),
/// cuando en realidad esta "ConDeuda" en USD. La verdad es por moneda.</para>
///
/// <para>H1 (2026-06-24): se separa "SIN MOVIMIENTOS" de "SALDADO de verdad". Antes una reserva NUEVA sin
/// cargos ni cobros caia en "Saldado" (todas las monedas en 0 / sin lineas), y el front lo mostraba como
/// "Pagada" -> daba a entender que se habia cobrado algo. Ahora ese caso devuelve <see cref="NoCharges"/>.</para>
/// </summary>
public static class ReservaCollectionStatus
{
    /// <summary>Alguna moneda tiene deuda (Balance &gt; 0). Gana sobre SaldoAFavor cuando hay ambas.</summary>
    public const string WithDebt = "ConDeuda";

    /// <summary>No hay deuda en ninguna moneda y alguna tiene sobrepago (Balance &lt; 0).</summary>
    public const string CreditBalance = "SaldoAFavor";

    /// <summary>Hubo cargos y se cobro todo: saldo 0 con actividad real. Es el "pagada" legitimo.</summary>
    public const string Settled = "Saldado";

    /// <summary>
    /// H1 (2026-06-24): la reserva NO tiene movimientos de plata (ni cargos para cobrar ni cobros recibidos).
    /// Tipico de una reserva nueva en armado/gestion. NO es "pagada": no hay nada cobrado todavia.
    /// </summary>
    public const string NoCharges = "SinMovimientos";

    // Umbral por debajo del centavo: cualquier resto menor se considera "saldado" en esa moneda.
    // Los importes ya vienen redondeados a 2 decimales desde el calculador de plata.
    private const decimal Epsilon = 0.005m;

    /// <summary>
    /// Deriva el estado de cobro a partir SOLO de los balances por moneda. NO puede distinguir "sin
    /// movimientos" de "saldado" (no recibe la senal de actividad), asi que TODO saldo 0 cae en "Saldado".
    ///
    /// <para>Se mantiene por compatibilidad para callers que solo tienen el balance (p. ej. el listado cuando
    /// no hay filas hijas materializadas). Para distinguir "sin movimientos" usar el overload que recibe
    /// <see cref="ReservaCollectionLine"/>.</para>
    /// </summary>
    public static string Derive(IEnumerable<decimal> balancesByCurrency)
    {
        bool anyDebt = false;
        bool anyCredit = false;

        foreach (var balance in balancesByCurrency)
        {
            if (balance > Epsilon)
                anyDebt = true;
            else if (balance < -Epsilon)
                anyCredit = true;
        }

        if (anyDebt)
            return WithDebt;

        if (anyCredit)
            return CreditBalance;

        return Settled;
    }

    /// <summary>
    /// Deriva el estado de cobro distinguiendo "sin movimientos" de "saldado". Regla (ADR-033 A5 + H1):
    ///   - si ALGUNA moneda tiene Balance &gt; 0          -&gt; "ConDeuda" (gana sobre el saldo a favor);
    ///   - si no hay deuda y ALGUNA moneda &lt; 0          -&gt; "SaldoAFavor";
    ///   - si saldo 0 en todo y NO hubo cargos ni cobros  -&gt; "SinMovimientos" (reserva nueva, nada cobrado);
    ///   - si saldo 0 en todo PERO hubo actividad          -&gt; "Saldado" (se cobro todo de verdad).
    /// </summary>
    public static string Derive(IEnumerable<ReservaCollectionLine> linesByCurrency)
    {
        bool anyDebt = false;
        bool anyCredit = false;
        bool anyActivity = false;

        foreach (var line in linesByCurrency)
        {
            if (line.Balance > Epsilon)
                anyDebt = true;
            else if (line.Balance < -Epsilon)
                anyCredit = true;

            // Hubo movimiento si hay algo facturable/cobrable (cargos) o si entro plata (cobros).
            if (line.HasCharges || line.HasPayments)
                anyActivity = true;
        }

        if (anyDebt)
            return WithDebt;

        if (anyCredit)
            return CreditBalance;

        // Saldo 0 en todas las monedas: depende de si hubo actividad o no.
        return anyActivity ? Settled : NoCharges;
    }
}
