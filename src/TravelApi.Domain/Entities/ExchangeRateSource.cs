namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1 (ADR-002 §2.3 / §2.7, 2026-05-13): origen del tipo de cambio capturado
/// en <see cref="FiscalSnapshot"/>. Soporta multimoneda con auditoria fiscal:
/// los 3 momentos (T0/T2/T3) registran <c>ExchangeRateSource</c> + <c>FetchedAt</c>
/// para que el contador pueda reconstruir como se calculo cada conversion.
///
/// Si el cashier elige <see cref="Manual"/>, el flow exige
/// <c>FiscalSnapshot.ManualJustification</c> (INV-120) — el sistema no permite
/// guardar un TC ingresado a mano sin razon escrita.
/// </summary>
public enum ExchangeRateSource
{
    /// <summary>
    /// Valor centinela aplicado por default a un <see cref="FiscalSnapshot"/> recien
    /// instanciado. Significa "todavia no se eligio fuente" — el sistema rechaza
    /// persistir un BC en estado &gt;= <c>AwaitingFiscalConfirmation</c> con este
    /// valor (CHECK <c>chk_BookingCancellations_fiscalsnapshot_consistent</c>,
    /// INV-118 / ADR-002 §2.7). Solo es legal en estado <c>Drafted</c>.
    /// </summary>
    Unset = 0,

    /// <summary>BCRA Comunicacion A 3500. TC mayorista, suele usarse para asientos.</summary>
    BCRA_A3500 = 1,

    /// <summary>Banco Nacion - mayorista.</summary>
    BNA_Mayorista = 2,

    /// <summary>Banco Nacion - minorista (publico general).</summary>
    BNA_Minorista = 3,

    /// <summary>TC oficial publicado por AFIP/ARCA para liquidaciones.</summary>
    AfipOficial = 4,

    /// <summary>Cargado a mano por el cashier. Requiere <c>ManualJustification</c> + audit (INV-120).</summary>
    Manual = 5,

    /// <summary>
    /// ADR-012 MVP (facturar en dolares, 2026-05-29): dolar VENDEDOR DIVISA del Banco
    /// Nacion del dia habil anterior. Es el tipo de cambio que —segun la lectura de la
    /// RG 5616— corresponde aplicar para valuar una factura en dolares.
    ///
    /// <para><b>Numero explicito 6 a proposito</b>: se agrega AL FINAL del enum sin
    /// renumerar los valores existentes (0..5 ya estan persistidos como int en la BD,
    /// reordenarlos corromperia datos historicos). Cualquier valor nuevo va aca abajo.</para>
    ///
    /// <para><b>Confirmacion normativa pendiente del contador</b>: que ESTE TC (vendedor
    /// divisa BNA dia habil anterior) sea el fiscalmente correcto para la factura en USD
    /// es la lectura actual de la RG 5616, pero la validacion final es del contador
    /// matriculado antes de prender el flag en produccion. El enum solo ofrece la opcion;
    /// no afirma que sea obligatoria.</para>
    /// </summary>
    BNA_VendedorDivisa = 6,
}
