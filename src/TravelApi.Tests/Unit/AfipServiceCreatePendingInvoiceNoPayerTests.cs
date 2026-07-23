using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Regresion 2026-07-23 (bug real confirmado en PROD): facturar una reserva SIN cliente asignado
/// (Consumidor Final) tiraba 500 SIEMPRE. <c>AfipService.CreatePendingInvoice</c> hacia
/// <c>if (customer == null) throw new Exception(...)</c> aunque el RESTO del pipeline (ArcaReceptorResolver,
/// ProcessInvoiceJob, InvoiceTypeResolver) ya sabia manejar el caso perfectamente — era el UNICO lugar
/// roto. Este test pinea que ahora NO tira y persiste una Invoice valida a Consumidor Final.
///
/// <para>Corre con InMemory (sin Docker): <c>CreatePendingInvoice</c> es una escritura EF comun, sin
/// SQL crudo ni CHECK constraints de por medio — no hace falta Postgres real para este caso.</para>
/// </summary>
public class AfipServiceCreatePendingInvoiceNoPayerTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static AfipService BuildAfipService(AppDbContext context)
    {
        var protector = new Mock<ISensitiveDataProtector>();
        protector.Setup(p => p.UnprotectString(It.IsAny<string?>())).Returns((string? v) => v);
        protector.Setup(p => p.UnprotectBytes(It.IsAny<byte[]?>())).Returns((byte[]? v) => v);

        return new AfipService(context, NullLogger<AfipService>.Instance, new HttpClient(), protector.Object);
    }

    private static CreateInvoiceRequest BuildRequestWithOneLine()
        => new()
        {
            Items = new List<InvoiceItemDto>
            {
                new()
                {
                    Description = "Paquete turistico",
                    Quantity = 1,
                    UnitPrice = 1000m,
                    Total = 1000m,
                    AlicuotaIvaId = 5, // 21%
                },
            },
        };

    /// <summary>
    /// CASO (a) del brief: reserva con PayerId=null. Emisor Responsable Inscripto -> sin CUIT del
    /// receptor la matriz fiscal degrada a Factura B (nunca A, que exige CUIT identificado). La
    /// Invoice se persiste igual, sin tirar, y CustomerSnapshot queda null (no el string "null").
    /// </summary>
    [Fact]
    public async Task CreatePendingInvoice_ReservaWithoutPayer_DoesNotThrow_PersistsInvoiceAsConsumidorFinal()
    {
        await using var ctx = CreateContext();

        ctx.AfipSettings.Add(new AfipSettings { Cuit = 20111111112, TaxCondition = "Responsable Inscripto" });
        var reserva = new Reserva
        {
            NumeroReserva = "F-NOPAY-1",
            Name = "Reserva sin cliente",
            Status = EstadoReserva.Confirmed,
            PayerId = null, // <-- el caso que rompia
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var afip = BuildAfipService(ctx);

        var invoice = await afip.CreatePendingInvoice(reserva.Id, BuildRequestWithOneLine());

        Assert.NotNull(invoice);
        Assert.Equal(InvoiceTypeResolver.FacturaB, invoice.TipoComprobante);
        Assert.Null(invoice.CustomerSnapshot);
        Assert.Equal("PENDING", invoice.Resultado);

        // Se persistio de verdad (no solo el objeto en memoria).
        var reloaded = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.Null(reloaded.CustomerSnapshot);
    }

    /// <summary>
    /// Mismo caso sin cliente, pero con emisor Monotributo: la matriz fiscal siempre da Factura C
    /// para ese emisor, independientemente del receptor. Confirma que la letra sigue derivandose
    /// del EMISOR (no hardcodeada a B) cuando no hay cliente.
    /// </summary>
    [Fact]
    public async Task CreatePendingInvoice_ReservaWithoutPayer_MonotributoEmisor_EmitsFacturaC()
    {
        await using var ctx = CreateContext();

        ctx.AfipSettings.Add(new AfipSettings { Cuit = 20111111112, TaxCondition = "Monotributo" });
        var reserva = new Reserva
        {
            NumeroReserva = "F-NOPAY-2",
            Name = "Reserva sin cliente (emisor mono)",
            Status = EstadoReserva.Confirmed,
            PayerId = null,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var afip = BuildAfipService(ctx);

        var invoice = await afip.CreatePendingInvoice(reserva.Id, BuildRequestWithOneLine());

        Assert.Equal(InvoiceTypeResolver.FacturaC, invoice.TipoComprobante);
        Assert.Null(invoice.CustomerSnapshot);
    }
}
