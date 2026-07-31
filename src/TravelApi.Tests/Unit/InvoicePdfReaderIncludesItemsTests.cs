using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Entities.Afip;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Hallazgo H1 (barrido E2E 2026-07-25): las facturas salian con el texto generico
/// "Servicios Turisticos - Res X" en vez del detalle real. La causa NO era la grabacion (los renglones
/// siempre se guardaron bien) sino la LECTURA: la consulta que arma el PDF no pedia
/// <c>.Include(i => i.Items)</c>. Sin lazy loading, una coleccion no incluida no viene vacia por error:
/// viene vacia SIEMPRE, y el generador del PDF caia a su texto de emergencia.
///
/// <para><b>Que fija este test y por que es unitario</b>: existe ya un test de integracion contra Postgres
/// real (<c>InvoicePdfItemsPersistenceIntegrationTests</c>) que reproduce el escenario multi-request
/// completo. Este de aca es la red BARATA que corre en cada build sin base: verifica que el generador de
/// PDF reciba la factura CON sus renglones. Para que sirva de verdad se limpia el ChangeTracker antes de
/// pedir el PDF — si no, el proveedor InMemory devolveria el grafo completo por fix-up de entidades ya
/// trackeadas y el test pasaria aunque falte el Include.</para>
///
/// <para>El PDF en si no se parsea (el proyecto no tiene libreria de extraccion de texto, mismo criterio
/// documentado en <c>InvoicePdfFiscalLegendTests</c>): se intercepta la Invoice que se le entrega al
/// generador, que es exactamente el dato del que depende el detalle impreso.</para>
/// </summary>
public class InvoicePdfReaderIncludesItemsTests
{
    [Fact]
    public async Task GetPdfAsync_EntregaAlGeneradorLaFacturaConSusRenglonesYTributos()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        await using var context = new AppDbContext(options);

        context.AfipSettings.Add(new AfipSettings { Id = 1, Cuit = 20111111112, PuntoDeVenta = 3, TaxCondition = "Monotributo" });
        context.AgencySettings.Add(new AgencySettings { Id = 1, AgencyName = "Magna Travel" });

        var customer = new Customer { Id = 1, FullName = "Cliente H1", TaxCondition = "Consumidor Final" };
        context.Customers.Add(customer);

        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva H1",
            Status = EstadoReserva.Confirmed,
            PayerId = customer.Id,
        };
        context.Reservas.Add(reserva);

        context.Invoices.Add(new Invoice
        {
            Id = 1,
            ReservaId = reserva.Id,
            TipoComprobante = 11, // Factura C
            PuntoDeVenta = 3,
            NumeroComprobante = 25,
            ImporteTotal = 150_000m,
            ImporteNeto = 150_000m,
            Resultado = "A",
            CAE = "75000000000001",
            Items = new List<InvoiceItem>
            {
                new() { Description = "Hotel Palace 4 noches", Quantity = 1, UnitPrice = 90_000m, Total = 90_000m, AlicuotaIvaId = 3 },
                new() { Description = "Traslado aeropuerto-hotel", Quantity = 1, UnitPrice = 20_000m, Total = 20_000m, AlicuotaIvaId = 3 },
                new() { Description = "Seguro de asistencia al viajero", Quantity = 1, UnitPrice = 40_000m, Total = 40_000m, AlicuotaIvaId = 3 },
            },
            Tributes = new List<InvoiceTribute>
            {
                new() { Id = 1, TributeId = 99, Description = "IIBB Córdoba", Importe = 1_500m, BaseImponible = 150_000m },
            },
        });

        await context.SaveChangesAsync();

        // CLAVE del test: sin esto, el proveedor InMemory completa las colecciones por fix-up de las
        // entidades que ya estan trackeadas y el Include faltante no se notaria (ver docstring).
        context.ChangeTracker.Clear();

        Invoice? invoiceEntregadaAlGenerador = null;
        var pdfService = new Mock<IInvoicePdfService>();
        pdfService
            .Setup(s => s.GenerateInvoicePdf(
                It.IsAny<Invoice>(), It.IsAny<Reserva>(), It.IsAny<AfipSettings>(), It.IsAny<AgencySettings>()))
            .Callback<Invoice, Reserva, AfipSettings, AgencySettings>((invoice, _, _, _) => invoiceEntregadaAlGenerador = invoice)
            .Returns(new byte[] { 0x25, 0x50, 0x44, 0x46 });

        var service = BuildInvoiceService(context, pdfService.Object);

        await service.GetPdfAsync(1, CancellationToken.None);

        Assert.NotNull(invoiceEntregadaAlGenerador);
        Assert.Equal(3, invoiceEntregadaAlGenerador!.Items.Count);
        Assert.Contains(invoiceEntregadaAlGenerador.Items, item => item.Description == "Hotel Palace 4 noches");
        // Los tributos provinciales tambien se imprimen en el total del PDF: mismo Include, misma red.
        Assert.Single(invoiceEntregadaAlGenerador.Tributes);
    }

    private static InvoiceService BuildInvoiceService(AppDbContext context, IInvoicePdfService pdfService)
    {
        var mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        return new InvoiceService(
            context,
            new EntityReferenceResolver(context),
            new AfipService(context, NullLogger<AfipService>.Instance, new HttpClient(), new NoopProtector()),
            pdfService,
            mapper,
            Mock.Of<IBackgroundJobClient>(),
            NullLogger<InvoiceService>.Instance,
            settingsMock.Object,
            BuildInertUserManager());
    }

    private static UserManager<ApplicationUser> BuildInertUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private sealed class NoopProtector : ISensitiveDataProtector
    {
        public string? ProtectString(string? value) => value;
        public string? UnprotectString(string? value) => value;
        public byte[]? ProtectBytes(byte[]? value) => value;
        public byte[]? UnprotectBytes(byte[]? value) => value;
    }
}
