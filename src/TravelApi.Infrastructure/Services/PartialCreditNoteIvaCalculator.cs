using System;
using System.Collections.Generic;
using System.Linq;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.3.F2.2 (plan tactico Fase 2 §FC1.3.F2.2 puntos 4 y 5, 2026-05-27): calculadora
/// PURA del prorrateo de IVA de una Nota de Credito (NC) parcial.
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
/// (los ~12 tests unitarios corren en milisegundos) y permite reusarlo desde el job de
/// emision (<c>ProcessPartialCreditNoteJob</c>) sin arrastrar infraestructura.</para>
///
/// <para><b>De donde sale el porcentaje de cada alicuota</b>: cada linea trae un
/// <c>AlicuotaIvaId</c> (codigo ARCA: 3=0%, 4=10.5%, 5=21%, 6=27%, 8=5%, 9=2.5%). El
/// metodo <see cref="GetVatMultiplier"/> traduce ese codigo a la tasa decimal. Es la
/// MISMA tabla que ya usa <c>AfipService.GetVatMultiplier</c> (AfipService.cs:717) y la
/// que documenta <see cref="InvoiceItem.AlicuotaIvaId"/>. Se duplica aca a proposito:
/// el helper es puro y no debe depender de <c>AfipService</c> (que arrastra SOAP, settings
/// de certificados, etc.). Si manana cambia una alicuota, hay que tocar los dos lugares —
/// el costo de esa duplicacion es bajo frente al beneficio de mantener este helper aislado.</para>
///
/// <para><b>Los dos modos de prorrateo</b> (<see cref="IvaProrrateoMode"/>):
/// <list type="bullet">
///   <item><see cref="IvaProrrateoMode.ProportionalToNet"/> (default): agrupa las lineas
///   por alicuota, suma el <c>Total</c> de cada grupo como base imponible y le aplica la
///   tasa del grupo. El IVA total es la suma de los IVA de cada grupo.</item>
///   <item><see cref="IvaProrrateoMode.PerItem"/>: calcula el IVA de cada linea por separado
///   y despues lo suma. El desglose por alicuota se construye agregando los items que
///   comparten alicuota. Resultado numerico igual al modo anterior cuando no hay redondeo
///   intermedio; difiere solo en como se redondea (item por item vs por grupo).</item>
/// </list>
/// </para>
///
/// <para><b>Validacion de tolerancia</b> (plan §FC1.3.F2.2 punto 5): la suma neto+IVA del
/// resultado tiene que coincidir con el <c>FiscalAmountToCredit</c> del input dentro de
/// <see cref="OperationalFinanceSettings.PartialCreditNoteRoundingTolerance"/> (default
/// 0.01). Si se desvia mas que eso, se lanza <see cref="InvalidOperationException"/> y el
/// caller (el job) marca la factura como <c>AnnulmentStatus = Failed</c> SIN mandar el XML
/// al ARCA. Un XML con totales inconsistentes rebota con un error oscuro del ARCA y deja
/// el job huerfano — preferimos cortar aca con un mensaje claro.</para>
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

        // El neto acreditado es la suma de las bases imponibles (Total de las lineas).
        // El IVA acreditado es la suma del IVA de cada grupo.
        decimal creditedNet = Math.Round(groups.Sum(g => g.BaseImponible), 2);
        decimal creditedVat = Math.Round(groups.Sum(g => g.ImporteIva), 2);
        decimal creditedTotal = Math.Round(creditedNet + creditedVat, 2);

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
    /// <c>Total</c> de cada grupo como base imponible y aplica la tasa una sola vez sobre
    /// esa base. Es el mismo patron que <c>AfipService</c> usa al armar la factura
    /// (AfipService.cs:645-653): <c>BaseImp = sum(Total)</c>, <c>Importe = BaseImp * tasa</c>.
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

            // Base imponible del grupo = suma de los Total de las lineas de esa alicuota.
            decimal baseImponible = lineGroup.Sum(line => line.Total);

            // IVA del grupo = base imponible * tasa, redondeado a 2 decimales una sola vez
            // (a nivel grupo). Esto difiere del modo PerItem, que redondea linea por linea.
            decimal importeIva = Math.Round(baseImponible * multiplier, 2);

            groups.Add(new PartialCreditNoteIvaGroup(
                AlicuotaIvaId: alicuotaIvaId,
                BaseImponible: baseImponible,
                ImporteIva: importeIva));
        }

        return groups;
    }

    /// <summary>
    /// Modo <see cref="IvaProrrateoMode.PerItem"/>: calcula el IVA de cada linea por
    /// separado (redondeando linea por linea) y despues agrega las lineas que comparten
    /// alicuota para devolver el desglose por alicuota. Mas preciso a nivel item pero puede
    /// acumular mas centavos de redondeo que el modo proporcional. Solo se usa si el
    /// contador lo confirma (respuesta F1 round 3).
    /// </summary>
    private static IReadOnlyList<PartialCreditNoteIvaGroup> CalculatePerItem(
        IReadOnlyList<PartialCreditNoteLineDto> lines)
    {
        // Primero calculamos el IVA de cada linea individual y lo redondeamos. Despues
        // sumamos por alicuota. El redondeo por linea es lo que distingue este modo del
        // proporcional (que redondea recien a nivel grupo).
        var perAlicuota = new Dictionary<int, (decimal BaseImponible, decimal ImporteIva)>();

        foreach (var line in lines)
        {
            decimal multiplier = GetVatMultiplier(line.AlicuotaIvaId);
            decimal lineVat = Math.Round(line.Total * multiplier, 2);

            if (perAlicuota.TryGetValue(line.AlicuotaIvaId, out var acumulado))
            {
                perAlicuota[line.AlicuotaIvaId] = (
                    acumulado.BaseImponible + line.Total,
                    acumulado.ImporteIva + lineVat);
            }
            else
            {
                perAlicuota[line.AlicuotaIvaId] = (line.Total, lineVat);
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
/// <param name="BaseImponible">Neto acreditado con esta alicuota (suma del Total de las lineas del grupo).</param>
/// <param name="ImporteIva">IVA acreditado de este grupo (base imponible * tasa).</param>
public record PartialCreditNoteIvaGroup(
    int AlicuotaIvaId,
    decimal BaseImponible,
    decimal ImporteIva);
