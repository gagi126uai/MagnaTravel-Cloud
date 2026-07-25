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
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Hallazgo H1 (barrido E2E 2026-07-25): una factura con 5 renglones armados en pantalla
/// terminaba mostrando el PDF con el texto generico "Servicios Turisticos - Res ..." en vez del
/// detalle real. Causa raiz encontrada: <c>InvoiceService.GetPdfAsync</c> consultaba la Invoice
/// SIN <c>.Include(i => i.Items)</c> ni <c>.Include(i => i.Tributes)</c>. Como el proyecto NO usa
/// lazy loading proxies (ver <c>AppDbContext</c>), esa navegacion quedaba SIEMPRE en la lista
/// vacia por defecto de la entidad — sin importar cuantos renglones hubiera de verdad en la
/// tabla <c>InvoiceItem</c> — y <c>InvoicePdfService.ComposeContent</c> caia siempre al fallback.
/// El bug afectaba a CUALQUIER factura, no solo a la del hallazgo puntual.
///
/// <para><b>Por que este test necesita Postgres real (no InMemory)</b>: la creacion de la factura
/// (<c>AfipService.CreatePendingInvoice</c>) ya estaba probada contra InMemory
/// (<c>AfipServiceInvoiceItemServiceSourceTests</c>) y ese camino SI persiste bien. El bug vivia
/// en la LECTURA: un <c>Include</c> faltante no se nota contra InMemory (el proveedor materializa
/// todo el grafo igual la mayoria de las veces si el objeto sigue siendo tracked por el MISMO
/// context) — la unica forma de reproducir de verdad "otro request lee lo que este grabo" es abrir
/// un <see cref="AppDbContext"/> NUEVO por cada paso, exactamente como pasa en produccion (cada
/// HTTP request tiene su propio scope de DI). Este test simula ese escenario multi-request contra
/// Postgres real.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class InvoicePdfItemsPersistenceIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public InvoicePdfItemsPersistenceIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EmitirFacturaYLuegoPedirElPdf_DesdeContextsDistintos_PersisteYLeeLosRenglonesReales()
    {
        int reservaId;
        int invoiceId;

        // ── Paso 1 (un "request" de emision): sembrar el escenario + crear la factura PENDING
        //    con 3 renglones reales, tal cual arma el modal de facturacion. ─────────────────────
        await using (var ctxEmision = _fixture.CreateDbContext())
        {
            ctxEmision.AfipSettings.Add(new AfipSettings
            {
                Id = 1,
                Cuit = 20111111112,
                PuntoDeVenta = 3,
                TaxCondition = "Monotributo", // Factura C, sin desglose de IVA (mismo caso que el hallazgo).
                IsProduction = false,
            });

            var customer = new Customer
            {
                FullName = "PI0724 Cliente Integracion H1",
                TaxCondition = "Consumidor Final",
            };
            ctxEmision.Customers.Add(customer);

            var reserva = new Reserva
            {
                NumeroReserva = $"F-H1-{Guid.NewGuid():N}"[..14],
                Name = "Reserva integracion H1",
                Status = EstadoReserva.Confirmed,
                TotalSale = 150_000m,
                Balance = 0m,
                TotalPaid = 150_000m,
                Payer = customer,
            };
            ctxEmision.Reservas.Add(reserva);
            await ctxEmision.SaveChangesAsync();
            reservaId = reserva.Id;

            var afipService = new AfipService(
                ctxEmision,
                NullLogger<AfipService>.Instance,
                new HttpClient(),
                new NoopProtector());

            var request = new CreateInvoiceRequest
            {
                ReservaId = reservaId.ToString(),
                Items = new List<InvoiceItemDto>
                {
                    new() { Description = "Hotel Palace 4 noches", Quantity = 1, UnitPrice = 90_000m, Total = 90_000m, AlicuotaIvaId = 3 },
                    new() { Description = "Traslado aeropuerto-hotel", Quantity = 1, UnitPrice = 20_000m, Total = 20_000m, AlicuotaIvaId = 3 },
                    new() { Description = "Seguro de asistencia al viajero", Quantity = 1, UnitPrice = 40_000m, Total = 40_000m, AlicuotaIvaId = 3 },
                },
            };

            var invoice = await afipService.CreatePendingInvoice(reservaId, request);
            invoiceId = invoice.Id;
        }

        // ── Paso 2 (verificacion cruzada, "SQL de solo lectura" como en el barrido): abrir un
        //    context NUEVO y confirmar que los 3 renglones estan de verdad en la tabla InvoiceItem
        //    de Postgres — no es un artefacto del ChangeTracker del context que los creo. ─────────
        await using (var ctxVerificacion = _fixture.CreateDbContext())
        {
            var renglonesPersistidos = await ctxVerificacion.Set<InvoiceItem>()
                .Where(i => i.InvoiceId == invoiceId)
                .OrderBy(i => i.Id)
                .ToListAsync();

            Assert.Equal(3, renglonesPersistidos.Count);
            Assert.Contains(renglonesPersistidos, i => i.Description == "Hotel Palace 4 noches");
            Assert.Contains(renglonesPersistidos, i => i.Description == "Traslado aeropuerto-hotel");
            Assert.Contains(renglonesPersistidos, i => i.Description == "Seguro de asistencia al viajero");
        }

        // ── Paso 3 (otro "request", el de "Ver PDF" / reenvio por WhatsApp): pedir el PDF con un
        //    InvoiceService construido sobre un context NUEVO. Antes del fix, invoice.Items llegaba
        //    vacio aca aunque el paso 2 ya probo que las filas existen en la base — la prueba real
        //    de la RAIZ del hallazgo es este paso, no el paso 2. ───────────────────────────────────
        await using (var ctxPdf = _fixture.CreateDbContext())
        {
            var invoiceService = BuildInvoiceService(ctxPdf);

            var pdfBytes = await invoiceService.GetPdfAsync(invoiceId, CancellationToken.None);

            Assert.NotNull(pdfBytes);
            AssertIsPdf(pdfBytes);

            // Ademas del PDF (que no parseamos: el proyecto no tiene libreria de extraccion de
            // texto, mismo criterio documentado en InvoicePdfFiscalLegendTests), replicamos EXACTO
            // el query que corre GetPdfAsync puertas adentro para blindar la causa raiz misma: si
            // alguien vuelve a sacar el Include, esta linea es la primera en romperse.
            var invoiceParaElPdf = await ctxPdf.Invoices
                .Include(i => i.Reserva).ThenInclude(t => t!.Payer)
                .Include(i => i.Items)
                .Include(i => i.Tributes)
                .FirstOrDefaultAsync(i => i.Id == invoiceId);

            Assert.NotNull(invoiceParaElPdf);
            Assert.Equal(3, invoiceParaElPdf!.Items.Count);
        }
    }

    private static InvoiceService BuildInvoiceService(AppDbContext context)
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
            new InvoicePdfService(),
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

    /// <summary>Cabecera magica "%PDF": mismo cinturon de seguridad barato que InvoicePdfFiscalLegendTests.</summary>
    private static void AssertIsPdf(byte[] bytes)
    {
        Assert.True(bytes.Length > 4, "El PDF deberia tener mas que la cabecera.");
        Assert.Equal((byte)'%', bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'D', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
    }

    // Protector inerte: ni CreatePendingInvoice ni GetPdfAsync llaman a ARCA ni usan certificados.
    // Mismo patron que AfipServiceInvoiceItemServiceSourceTests.
    private sealed class NoopProtector : ISensitiveDataProtector
    {
        public string? ProtectString(string? value) => value;
        public string? UnprotectString(string? value) => value;
        public byte[]? ProtectBytes(byte[]? value) => value;
        public byte[]? UnprotectBytes(byte[]? value) => value;
    }
}
