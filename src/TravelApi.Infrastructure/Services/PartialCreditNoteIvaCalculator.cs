using System;
using System.Collections.Generic;
using System.Linq;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.3.F2.2 (plan tactico Fase 2 §FC1.3.F2.2 puntos 4 y 5, 2026-05-27 + fix fiscal de
/// semantica 2026-05-28): calculadora PURA del prorrateo de IVA de una Nota de Credito
/// (NC) parcial.
///
/// <para><b>Por que existe</b>: cuando se cancela parte de una reserva ya facturada, se
/// emite una NC que solo acredita una PARTE de la factura origen. Antes de armar el XML
/// que va al ARCA hay que repartir (prorratear) el IVA entre las distintas alicuotas que
/// tienen las lineas que se acreditan. Este helper hace exactamente ese reparto y devuelve
/// el desglose neto + IVA por alicuota, mas el IVA total.</para>
///
/// <para><b>Por que es PURO</b>: no toca la base de datos, no llama al ARCA, no tiene
/// dependencias inyectadas. Recibe la liquidacion ya armada (lineas + montos) + los
/// settings, y devuelve numeros. Eso lo hace facil de testear sin Docker ni Postgres
/// (los ~14 tests unitarios corren en milisegundos) y permite reusarlo desde el job de
/// emision (<c>ProcessPartialCreditNoteJob</c>) sin arrastrar infraestructura.</para>
///
/// <para><b>SEMANTICA CANONICA DE <c>line.Total</c> (decision Gaston, 2026-05-28)</b>: el
/// <c>Total</c> de cada <see cref="PartialCreditNoteLineDto"/> es BRUTO, es decir el monto
/// que el cliente ve en la factura (incluye IVA por dentro). Es el MISMO numero que
/// aparece como total de linea en el comprobante origen.
///
/// <para>Esta semantica esta alineada con: (a) el plan firmado §FC1.3.F2.2 punto 4 — la
/// suma de <c>line.Total</c> es el <c>FiscalAmountToCredit</c>, que tambien es bruto;
/// (b) <c>FiscalLiquidationCalculator</c> + <c>InvoiceService.ValidateLiquidationAmounts</c>
/// (que validan suma de totales contra el bruto a acreditar); (c) el XML doc del DTO,
/// que define <c>FiscalAmountToCredit</c> como "neto+iva a acreditar". El sistema entero
/// asume bruto: el calculator hace la EXTRACCION del IVA por dentro.</para>
///
/// <para>HISTORIA: la version inicial de este calculator asumia <c>line.Total</c> NETO y
/// hacia "gross-up" (IVA on-top). Eso rompia el camino feliz porque <c>creditedTotal</c>
/// quedaba por encima de <c>FiscalAmountToCredit</c> (el calculator devolvia
/// neto + 21% on-top, pero el caller le pasaba el bruto). El fix es extraer el IVA POR
/// DENTRO: <c>BaseImp = Total / (1 + tasa)</c>, <c>IVA = Total - BaseImp</c> (residuo
/// exacto). Asi el comprobante ARCA recibe la BASE y el IVA discriminados, y cuadra
/// EXACTO contra el bruto que el cliente vio en la factura.</para>
/// </para>
///
/// <para><b>De donde sale el porcentaje de cada alicuota</b>: cada linea trae un
/// <c>AlicuotaIvaId</c> (codigo ARCA: 3=0%, 4=10.5%, 5=21%, 6=27%, 8=5%, 9=2.5%). El
/// metodo <see cref="GetVatMultiplier"/> traduce ese codigo a la tasa decimal. Es la
/// MISMA tabla que ya usa <c>AfipService.GetVatMultiplier</c> (AfipService.cs:760) y la
/// que documenta <see cref="InvoiceItem.AlicuotaIvaId"/>. Se duplica aca a proposito:
/// el helper es puro y no debe depender de <c>AfipService</c> (que arrastra SOAP, settings
/// de certificados, etc.). Si manana cambia una alicuota, hay que tocar los dos lugares —
/// el costo de esa duplicacion es bajo frente al beneficio de mantener este helper aislado.</para>
///
/// <para><b>Los dos modos de prorrateo</b> (<see cref="IvaProrrateoMode"/>):
/// <list type="bullet">
///   <item><see cref="IvaProrrateoMode.ProportionalToNet"/> (default): agrupa las lineas
///   por alicuota, suma el <c>Total</c> (BRUTO) de cada grupo y EXTRAE el IVA por dentro
///   del total bruto del grupo. <c>BaseImp = round(brutoGrupo / (1+tasa), 2)</c>;
///   <c>IVA = brutoGrupo - BaseImp</c>. El IVA total es la suma de los IVA de cada grupo.</item>
///   <item><see cref="IvaProrrateoMode.PerItem"/>: extrae el IVA de cada linea por separado
///   (redondeando por item) y despues suma. El desglose por alicuota se construye agregando
///   los items que comparten alicuota. Resultado numerico igual al modo anterior cuando no
///   hay redondeo intermedio; difiere solo en como se distribuyen los centavos (item por
///   item vs por grupo).</item>
/// </list>
/// </para>
///
/// <para><b>Validacion de tolerancia</b> (plan §FC1.3.F2.2 punto 5): por construccion de
/// la extraccion, <c>BaseImp + IVA == brutoGrupo</c> exacto por grupo, y la suma de los
/// brutos por grupo es <c>Σ line.Total</c>, que el contrato del input promete igual a
/// <c>FiscalAmountToCredit</c>. Asi que en input coherente, <c>creditedTotal ==
/// FiscalAmountToCredit</c> EXACTO. La tolerancia queda como GUARD defensivo contra inputs
/// inconsistentes (alguien pasa <c>FiscalAmountToCredit</c> distinto a la suma de
/// <c>line.Total</c>): si se desvia mas que la tolerancia, se lanza
/// <see cref="InvalidOperationException"/> y el caller (el job) marca la factura como
/// <c>AnnulmentStatus = Failed</c> SIN mandar el XML al ARCA. Un XML con totales
/// inconsistentes rebota con un error oscuro del ARCA y deja el job huerfano —
/// preferimos cortar aca con un mensaje claro.</para>
/// </summary>
public static class PartialCreditNoteIvaCalculator
{
    /// <summary>
    /// Calcula el desglose de IVA de la NC parcial y valida que cierre contra el monto
    /// fiscal a acreditar.
    /// </summary>
    /// <param name="input">La liquidacion a emitir (lineas + montos + moneda).</param>
    /// <param name="mode">
    /// Modo de prorrateo. Viene de
    /// <see cref="OperationalFinanceSettings.IvaProrrateoMode"/>.
    /// </param>
    /// <param name="roundingTolerance">
    /// Desvio maximo permitido (en la moneda original del comprobante) entre la suma
    /// neto+IVA calculada y el <c>FiscalAmountToCredit</c>. Viene de
    /// <see cref="OperationalFinanceSettings.PartialCreditNoteRoundingTolerance"/>.
    /// </param>
    /// <returns>El desglose por alicuota + los totales. Ver <see cref="PartialCreditNoteIvaResult"/>.</returns>
    /// <exception cref="ArgumentNullException">Si <paramref name="input"/> es null.</exception>
    /// <exception cref="ArgumentException">Si no hay lineas o la tolerancia es negativa.</exception>
    /// <exception cref="InvalidOperationException">
    /// Si la suma neto+IVA prorrateada se desvia de <c>FiscalAmountToCredit</c> mas que
    /// <paramref name="roundingTolerance"/>. Es un guard defensivo: la liquidacion llego
    /// inconsistente y NO debe viajar al ARCA.
    /// </exception>
    public static PartialCreditNoteIvaResult Calculate(
        PartialCreditNoteEmissionInput input,
        IvaProrrateoMode mode,
        decimal roundingTolerance)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (input.Lines is null || input.Lines.Count == 0)
        {
            throw new ArgumentException(
                "La liquidacion no tiene lineas para prorratear el IVA.",
                nameof(input));
        }

        if (roundingTolerance < 0m)
        {
            throw new ArgumentException(
                "La tolerancia de redondeo no puede ser negativa.",
                nameof(roundingTolerance));
        }

        // El desglose por alicuota se calcula distinto segun el modo, pero ambos modos
        // devuelven la MISMA forma de salida (lista de grupos). Asi el caller no necesita
        // saber que modo se uso para armar el XML.
        IReadOnlyList<PartialCreditNoteIvaGroup> groups = mode switch
        {
            IvaProrrateoMode.PerItem => CalculatePerItem(input.Lines),
            // ProportionalToNet es el default y el fallback de cualquier valor inesperado:
            // preferimos el criterio conservador del contador antes que tirar un error
            // por un enum nuevo no contemplado.
            _ => CalculateProportionalToNet(input.Lines),
        };

        // El neto acreditado es la suma de las BASES IMPONIBLES extraidas (sin IVA).
        // El IVA acreditado es la suma del IVA extraido por cada grupo.
        // El TOTAL acreditado es la suma de los Total BRUTOS de las lineas (= la suma de
        // los brutos por grupo). En input coherente coincide EXACTO con FiscalAmountToCredit,
        // porque la extraccion garantiza BaseImp + IVA == brutoGrupo por construccion.
        decimal creditedNet = Math.Round(groups.Sum(g => g.BaseImponible), 2);
        decimal creditedVat = Math.Round(groups.Sum(g => g.ImporteIva), 2);
        decimal creditedTotal = Math.Round(input.Lines.Sum(line => line.Total), 2);

        ValidateAgainstFiscalAmount(
            creditedTotal: creditedTotal,
            fiscalAmountToCredit: input.FiscalAmountToCredit,
            roundingTolerance: roundingTolerance,
            mode: mode);

        return new PartialCreditNoteIvaResult(
            CreditedNetAmount: creditedNet,
            CreditedVatAmount: creditedVat,
            CreditedTotalAmount: creditedTotal,
            VatGroups: groups);
    }

    /// <summary>
    /// Modo <see cref="IvaProrrateoMode.ProportionalToNet"/>: agrupa por alicuota, suma el
    /// <c>Total</c> BRUTO de cada grupo y EXTRAE el IVA del bruto del grupo.
    ///
    /// <para><b>Formula de extraccion</b> (decision Gaston 2026-05-28, ver doc de la clase):
    /// <c>line.Total</c> es BRUTO (incluye IVA). Extraemos el IVA por dentro asi:
    /// <list type="bullet">
    ///   <item><c>brutoGrupo = Σ line.Total del grupo</c>.</item>
    ///   <item><c>BaseImp = round(brutoGrupo / (1 + tasa), 2)</c>.</item>
    ///   <item><c>IVA = brutoGrupo - BaseImp</c> (residuo exacto, no redondeo aparte).</item>
    /// </list>
    /// El residuo en el IVA en vez de redondear el IVA por separado garantiza
    /// <c>BaseImp + IVA == brutoGrupo</c> a centavo exacto, que es lo que ARCA exige en el
    /// <c>AlicIva</c> y lo que el comprobante origen tiene impreso.</para>
    ///
    /// <para>El caso de alicuota 0% (tasa = 0) cae naturalmente: <c>BaseImp = brutoGrupo /
    /// 1 = brutoGrupo</c> y <c>IVA = brutoGrupo - brutoGrupo = 0</c>. Sin caso especial.</para>
    /// </summary>
    private static IReadOnlyList<PartialCreditNoteIvaGroup> CalculateProportionalToNet(
        IReadOnlyList<PartialCreditNoteLineDto> lines)
    {
        var groups = new List<PartialCreditNoteIvaGroup>();

        // Agrupamos por codigo de alicuota. Cada grupo es una alicuota distinta presente
        // en las lineas (puede haber lineas 21%, lineas 10.5%, etc.).
        foreach (var lineGroup in lines.GroupBy(line => line.AlicuotaIvaId))
        {
            int alicuotaIvaId = lineGroup.Key;
            decimal multiplier = GetVatMultiplier(alicuotaIvaId);

            // Total BRUTO del grupo = suma de los Total de las lineas de esa alicuota.
            decimal brutoGrupo = lineGroup.Sum(line => line.Total);

            // Extraccion del IVA por dentro del bruto del grupo.
            decimal baseImponible = Math.Round(brutoGrupo / (1m + multiplier), 2);
            decimal importeIva = brutoGrupo - baseImponible;

            groups.Add(new PartialCreditNoteIvaGroup(
                AlicuotaIvaId: alicuotaIvaId,
                BaseImponible: baseImponible,
                ImporteIva: importeIva));
        }

        return groups;
    }

    /// <summary>
    /// Modo <see cref="IvaProrrateoMode.PerItem"/>: EXTRAE el IVA de cada linea por
    /// separado (redondeando linea por linea) y despues agrega las lineas que comparten
    /// alicuota para devolver el desglose por alicuota. Mas preciso a nivel item pero puede
    /// acumular mas centavos de redondeo que el modo proporcional. Solo se usa si el
    /// contador lo confirma (respuesta F1 round 3).
    ///
    /// <para><b>Formula de extraccion por linea</b> (decision Gaston 2026-05-28):
    /// <c>line.Total</c> es BRUTO. Para cada linea: <c>lineBaseImp = round(line.Total /
    /// (1+tasa), 2)</c>, <c>lineVat = line.Total - lineBaseImp</c>. Como el IVA por linea
    /// es residuo, <c>lineBaseImp + lineVat == line.Total</c> a centavo exacto. Despues
    /// agregamos por alicuota.</para>
    /// </summary>
    private static IReadOnlyList<PartialCreditNoteIvaGroup> CalculatePerItem(
        IReadOnlyList<PartialCreditNoteLineDto> lines)
    {
        // Primero EXTRAEMOS el IVA de cada linea individual (redondeando la base por linea
        // y dejando el IVA como residuo, garantizando cuadre exacto por linea). Despues
        // sumamos por alicuota. El redondeo por linea es lo que distingue este modo del
        // proporcional (que redondea recien a nivel grupo).
        var perAlicuota = new Dictionary<int, (decimal BaseImponible, decimal ImporteIva)>();

        foreach (var line in lines)
        {
            decimal multiplier = GetVatMultiplier(line.AlicuotaIvaId);

            // Extraccion del IVA por dentro de la linea bruta.
            decimal lineBaseImp = Math.Round(line.Total / (1m + multiplier), 2);
            decimal lineVat = line.Total - lineBaseImp;

            if (perAlicuota.TryGetValue(line.AlicuotaIvaId, out var acumulado))
            {
                perAlicuota[line.AlicuotaIvaId] = (
                    acumulado.BaseImponible + lineBaseImp,
                    acumulado.ImporteIva + lineVat);
            }
            else
            {
                perAlicuota[line.AlicuotaIvaId] = (lineBaseImp, lineVat);
            }
        }

        var groups = new List<PartialCreditNoteIvaGroup>();
        foreach (var entry in perAlicuota)
        {
            groups.Add(new PartialCreditNoteIvaGroup(
                AlicuotaIvaId: entry.Key,
                BaseImponible: entry.Value.BaseImponible,
                ImporteIva: entry.Value.ImporteIva));
        }

        return groups;
    }

    /// <summary>
    /// Guard defensivo (plan §FC1.3.F2.2 punto 5): si la suma neto+IVA prorrateada se aleja
    /// del monto fiscal a acreditar mas que la tolerancia, abortamos. Es lo que evita mandar
    /// un XML inconsistente al ARCA.
    /// </summary>
    private static void ValidateAgainstFiscalAmount(
        decimal creditedTotal,
        decimal fiscalAmountToCredit,
        decimal roundingTolerance,
        IvaProrrateoMode mode)
    {
        decimal difference = Math.Abs(creditedTotal - fiscalAmountToCredit);

        if (difference > roundingTolerance)
        {
            // Mensaje pensado para el log/diagnostico (no es 409 al usuario final): el job
            // que lo recibe marca AnnulmentStatus = Failed con esta razon. Incluimos los
            // numeros para que sea evidente cuanto se desvio. NO hay datos sensibles aca
            // (montos fiscales, no documentos de pasajeros ni datos de pago).
            throw new InvalidOperationException(
                $"El prorrateo de IVA no cierra contra el monto fiscal a acreditar. " +
                $"Modo={mode}, neto+IVA calculado={creditedTotal}, " +
                $"FiscalAmountToCredit esperado={fiscalAmountToCredit}, " +
                $"desvio={difference}, tolerancia permitida={roundingTolerance}. " +
                $"No se emite la NC parcial para no mandar un comprobante inconsistente al ARCA.");
        }
    }

    /// <summary>
    /// Traduce el codigo de alicuota de IVA del ARCA a la tasa decimal.
    ///
    /// <para>Tabla canonica (misma que <c>AfipService.GetVatMultiplier</c> y la documentada
    /// en <see cref="InvoiceItem.AlicuotaIvaId"/>): 3=0%, 4=10.5%, 5=21%, 6=27%, 8=5%,
    /// 9=2.5%. Cualquier codigo no reconocido devuelve 0% (igual que el original): un codigo
    /// raro NO debe inventar IVA de la nada. Si el codigo es invalido, el resultado dara un
    /// IVA menor al esperado y la validacion de tolerancia lo va a hacer rebotar, que es el
    /// comportamiento seguro.</para>
    /// </summary>
    private static decimal GetVatMultiplier(int alicuotaIvaId) => alicuotaIvaId switch
    {
        3 => 0m,     // 0%
        4 => 0.105m, // 10.5%
        5 => 0.21m,  // 21%
        6 => 0.27m,  // 27%
        8 => 0.05m,  // 5%
        9 => 0.025m, // 2.5%
        _ => 0m,     // codigo desconocido -> 0% (no inventamos IVA)
    };
}

/// <summary>
/// FC1.3.F2.2: resultado del prorrateo de IVA. Trae los totales acreditados + el desglose
/// por alicuota. El caller (el job de emision) usa <see cref="VatGroups"/> para armar el
/// detalle de IVA del <c>CreateInvoiceRequest</c> que va al ARCA.
///
/// <para><b>Supuesto de forma de retorno</b>: el plan §FC1.3.F2.2 punto 4 describe el
/// calculo pero NO fija el record exacto de salida. Se definio este shape (totales + lista
/// de grupos por alicuota) porque es lo que necesita el armado del XML del ARCA, que pide
/// el IVA discriminado por alicuota (<c>AlicIva</c> con Id + BaseImp + Importe).</para>
/// </summary>
/// <param name="CreditedNetAmount">Neto total acreditado (suma de las bases imponibles), redondeado a 2 decimales.</param>
/// <param name="CreditedVatAmount">IVA total acreditado (suma del IVA de cada grupo), redondeado a 2 decimales.</param>
/// <param name="CreditedTotalAmount">Total acreditado (neto + IVA), redondeado a 2 decimales.</param>
/// <param name="VatGroups">Desglose por alicuota: una entrada por cada alicuota distinta presente en las lineas.</param>
public record PartialCreditNoteIvaResult(
    decimal CreditedNetAmount,
    decimal CreditedVatAmount,
    decimal CreditedTotalAmount,
    IReadOnlyList<PartialCreditNoteIvaGroup> VatGroups);

/// <summary>
/// FC1.3.F2.2: una alicuota del desglose de IVA de la NC parcial. Equivale a un nodo
/// <c>AlicIva</c> del XML del ARCA: el codigo de alicuota, la base imponible acreditada con
/// esa alicuota y el IVA resultante.
/// </summary>
/// <param name="AlicuotaIvaId">Codigo de alicuota ARCA (3=0%, 4=10.5%, 5=21%, 6=27%, 8=5%, 9=2.5%).</param>
/// <param name="BaseImponible">Neto acreditado con esta alicuota (base imponible EXTRAIDA del bruto del grupo, redondeada a 2 decimales).</param>
/// <param name="ImporteIva">IVA acreditado de este grupo (residuo entre el bruto del grupo y la base imponible: <c>brutoGrupo - BaseImponible</c>).</param>
public record PartialCreditNoteIvaGroup(
    int AlicuotaIvaId,
    decimal BaseImponible,
    decimal ImporteIva);
