using AutoMapper;
using TravelApi.Application.DTOs;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tarea 2026-07-16: el desplegable de facturas del frontend necesita saber en que moneda esta
/// cada factura para armar el label (ej. "Factura C 0001-00000051 — $ 125.000,50" vs "US$ 500,00").
/// <c>Invoice.MonId</c> guarda el codigo ARCA ("PES"/"DOL"), no ISO, asi que <c>InvoiceDto.Currency</c>
/// lo traduce con <c>ArcaCurrencyMapper.ToIso</c> — el MISMO helper que <c>ReservaService</c> ya usa
/// para agrupar el extracto de cuenta por moneda (fuente unica, sin duplicar la tabla de mapeo).
///
/// <para>Tests PUROS de AutoMapper (sin DB): arman un <see cref="Invoice"/> en memoria y verifican
/// el <see cref="InvoiceDto.Currency"/> resultante.</para>
/// </summary>
public class InvoiceDtoCurrencyMappingTests
{
    private static readonly IMapper Mapper =
        new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

    private static Invoice InvoiceWithMonId(string? monId)
        => new()
        {
            TipoComprobante = 11, // Factura C
            PuntoDeVenta = 1,
            NumeroComprobante = 51,
            ImporteTotal = 125_000.50m,
            MonId = monId!,
        };

    [Fact]
    public void MonId_PES_MapeaAIsoArs()
    {
        var invoice = InvoiceWithMonId("PES");

        var dto = Mapper.Map<InvoiceDto>(invoice);

        Assert.Equal("ARS", dto.Currency);
    }

    [Fact]
    public void MonId_DOL_MapeaAIsoUsd()
    {
        var invoice = InvoiceWithMonId("DOL");

        var dto = Mapper.Map<InvoiceDto>(invoice);

        Assert.Equal("USD", dto.Currency);
    }

    [Fact]
    public void MonId_Null_CaeAlDefaultArs()
    {
        // Dato legacy raro: en teoria MonId no es nullable en C# (default "PES"), pero una fila
        // vieja de BD podria tener NULL. El criterio historico del sistema (mismo que el extracto
        // de cuenta) es que todo lo legacy/desconocido se factura en pesos.
        var invoice = InvoiceWithMonId(null);

        var dto = Mapper.Map<InvoiceDto>(invoice);

        Assert.Equal(Monedas.ARS, dto.Currency);
    }

    [Fact]
    public void MonId_CodigoNoReconocido_CaeAlDefaultArs()
    {
        var invoice = InvoiceWithMonId("EUR"); // ni ARCA ni ISO soportado hoy

        var dto = Mapper.Map<InvoiceDto>(invoice);

        Assert.Equal(Monedas.ARS, dto.Currency);
    }

    [Fact]
    public void MonId_MinusculaDol_EsToleranteAMayusculas()
    {
        // ArcaCurrencyMapper.ToIso es OrdinalIgnoreCase.
        var invoice = InvoiceWithMonId("dol");

        var dto = Mapper.Map<InvoiceDto>(invoice);

        Assert.Equal("USD", dto.Currency);
    }
}
