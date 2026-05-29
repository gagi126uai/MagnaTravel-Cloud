using System;
using System.Collections.Generic;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.DTOs;

public class CreateInvoiceRequest
{
    public string ReservaId { get; set; } = string.Empty;
    public int CbteTipo { get; set; } // Optional: To override automatic detection
    public int Concepto { get; set; } = 3; // 1: Productos, 2: Servicios, 3: Ambos
    public int DocTipo { get; set; } = 99;
    public long DocNro { get; set; } = 0;

    public List<InvoiceItemDto> Items { get; set; } = new();
    public List<InvoiceTributeDto> Tributes { get; set; } = new();
    public string? OriginalInvoiceId { get; set; }
    public bool IsCreditNote { get; set; }
    public bool IsDebitNote { get; set; }
    public bool ForceIssue { get; set; }
    public string? ForceReason { get; set; }
    public string? ForcedByUserId { get; set; }
    public string? ForcedByUserName { get; set; }

    /// <summary>
    /// FC1.3.F2.5 (multimoneda, 2026-05-28): codigo de moneda del catalogo de ARCA
    /// ("PES" = pesos, "DOL" = dolar). Viaja hasta <c>Invoice.MonId</c> y termina en el
    /// XML SOAP que se manda a ARCA.
    ///
    /// <para><b>Default "PES" a proposito</b>: la facturacion normal de FC1.2 y la NC total
    /// NO setean este campo. Al quedar en "PES", el comportamiento es identico a antes de
    /// F2.5 (todo se factura en pesos). Solo la emision de NC parcial multimoneda lo setea
    /// a "DOL" cuando la factura origen fue en dolares.</para>
    /// </summary>
    public string MonId { get; set; } = "PES";

    /// <summary>
    /// FC1.3.F2.5 (multimoneda, 2026-05-28): cotizacion de la moneda contra el peso.
    /// Para "PES" siempre vale 1. Para "DOL" es el tipo de cambio del comprobante origen (T0).
    ///
    /// <para><b>Default 1 a proposito</b>: los callers FC1.2 que no lo setean mandan 1, que
    /// es lo correcto para pesos. Sin cambios de comportamiento para esos flujos.</para>
    /// </summary>
    public decimal MonCotiz { get; set; } = 1m;

    /// <summary>
    /// ADR-012 MVP (facturar en dolares, 2026-05-29): origen del tipo de cambio cuando
    /// la factura es en moneda extranjera (MVP = manual). Viaja hasta
    /// <c>Invoice.ExchangeRateSource</c>. NULL para facturas en pesos.
    ///
    /// <para>Con el flag <c>EnableMultiCurrencyInvoicing</c> ON y moneda extranjera, el
    /// service EXIGE este campo (no <c>Unset</c>). Con el flag OFF se ignora.</para>
    /// </summary>
    public ExchangeRateSource? ExchangeRateSource { get; set; }

    /// <summary>
    /// ADR-012 MVP: momento en que se tomo el TC. Viaja hasta
    /// <c>Invoice.ExchangeRateFetchedAt</c>. NULL para facturas en pesos. Exigido con el
    /// flag multimoneda ON + moneda extranjera.
    /// </summary>
    public DateTime? ExchangeRateFetchedAt { get; set; }

    /// <summary>
    /// ADR-012 MVP: justificacion escrita del TC manual (patron INV-120). Viaja hasta
    /// <c>Invoice.ExchangeRateJustification</c>. NULL para facturas en pesos. Exigido (no
    /// vacio) con el flag multimoneda ON + moneda extranjera.
    /// </summary>
    public string? ExchangeRateJustification { get; set; }

    /// <summary>
    /// FC1.3.F2.2 (fix fiscal B1, 2026-05-27): desglose de totales YA REDONDEADO que el
    /// caller calculo aparte (la NC parcial lo trae del <c>PartialCreditNoteIvaCalculator</c>).
    ///
    /// <para><b>Por que existe</b>: el pipeline compartido (<c>CreatePendingInvoice</c> +
    /// <c>ProcessInvoiceJob</c>) recalcula el IVA item por item SIN redondear y recien redondea
    /// al serializar. Con varias lineas de la misma alicuota, la suma de los redondeos por
    /// linea puede diferir en 1-2 centavos del redondeo del agregado, y el ARCA rebota el
    /// comprobante. Cuando el caller ya tiene el cuadre exacto (mismos numeros que valida
    /// antes de POSTear), lo pasa por aca y el pipeline usa ESTOS numeros tal cual, sin
    /// recalcular.</para>
    ///
    /// <para><b>null = comportamiento FC1.2 actual</b>: la facturacion normal y la NC total
    /// NO pueblan este campo. Si es <c>null</c>, el pipeline calcula los totales exactamente
    /// como hasta hoy (no cambia una sola linea de comportamiento para esos flujos).</para>
    /// </summary>
    public InvoiceTotalsOverride? TotalsOverride { get; set; }
}

/// <summary>
/// FC1.3.F2.2 (fix fiscal B1): totales del comprobante YA REDONDEADOS a 2 decimales por el
/// caller, listos para viajar al ARCA sin recalculo.
///
/// <para>El invariante que garantiza este override es el que exige el ARCA:
/// <c>ImpTotal == ImpNeto + ImpIVA + ImpTrib</c> y <c>ImpIVA == Σ AlicIvas.Importe</c>,
/// con <c>ImpNeto == Σ AlicIvas.BaseImp</c>. El caller es responsable de que estos numeros
/// cierren EXACTO a 2 decimales antes de armar el override.</para>
/// </summary>
/// <param name="AlicIvas">Desglose de IVA por alicuota, con Importe ya redondeado por grupo.</param>
/// <param name="ImpNeto">Neto total (suma de las bases imponibles), ya redondeado.</param>
/// <param name="ImpIVA">IVA total (suma de los Importe por grupo), ya redondeado.</param>
/// <param name="ImpTrib">Total de tributos, ya redondeado. 0 cuando no hay tributos.</param>
/// <param name="ImpTotal">Total del comprobante (neto + IVA + tributos), ya redondeado.</param>
public record InvoiceTotalsOverride(
    IReadOnlyList<AlicIvaOverride> AlicIvas,
    decimal ImpNeto,
    decimal ImpIVA,
    decimal ImpTrib,
    decimal ImpTotal);

/// <summary>
/// FC1.3.F2.2 (fix fiscal B1): una alicuota del desglose de IVA, con el importe YA redondeado
/// por grupo. Equivale a un nodo <c>AlicIva</c> del XML del ARCA.
/// </summary>
/// <param name="Id">Codigo de alicuota ARCA (3=0%, 4=10.5%, 5=21%, 6=27%, 8=5%, 9=2.5%).</param>
/// <param name="BaseImp">Base imponible acreditada con esta alicuota (suma del Total de las lineas del grupo).</param>
/// <param name="Importe">IVA de este grupo, ya redondeado a 2 decimales (round(BaseImp * tasa, 2)).</param>
public record AlicIvaOverride(
    int Id,
    decimal BaseImp,
    decimal Importe);
