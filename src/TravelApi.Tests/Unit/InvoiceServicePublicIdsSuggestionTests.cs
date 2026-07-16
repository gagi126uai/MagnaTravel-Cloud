using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
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
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tarea chica (2026-07-16): sugerir automaticamente en que factura esta un servicio al cancelarlo,
/// en vez de que el usuario adivine en un desplegable con varias facturas activas.
///
/// <para>Estos tests recorren el path real (<c>ReservaService.GetReservaByIdAsync</c>) para probar
/// <c>InvoiceDto.ServicePublicIds</c>: la trazabilidad <c>InvoiceItem.SourceServicioReservaId</c>
/// (FC1.3/ADR-009) traducida a los <c>PublicId</c> publicos de los servicios de origen.</para>
///
/// <para><b>Hallazgo importante para quien lea esto despues</b>: hoy (2026-07-16) NINGUN flujo de
/// emision de factura en produccion completa <c>SourceServicioReservaId</c> al crear el
/// <c>InvoiceItem</c> (se confirmo grepeando el codigo: el unico lugar que lo asigna es la
/// siembra manual de estos tests y de otros tests del modulo de cancelacion). Estos tests sirven
/// para blindar el CABLEADO de la proyeccion (que funcione bien EL DIA que algo empiece a poblar el
/// dato), no para probar que hoy haya sugerencias reales en produccion.</para>
/// </summary>
public class InvoiceServicePublicIdsSuggestionTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static ReservaService CreateService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        const string userId = "tester";
        var accessor = BuildHttpContextAccessor(userId, "Admin");
        var resolver = BuildResolver(userId, Permissions.CobranzasSeeCost);

        return new ReservaService(
            context,
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            settings.Object,
            BuildUserManager(),
            NullLogger<ReservaService>.Instance,
            resolver,
            accessor);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ServicioReserva GenericService(int id, int reservaId, decimal salePrice)
        => new()
        {
            Id = id,
            ReservaId = reservaId,
            ServiceType = "Excursion",
            ProductType = "Excursion",
            Description = "Servicio test",
            Status = "Confirmado",
            Currency = "ARS",
            DepartureDate = DateTime.UtcNow.AddDays(10),
            SalePrice = salePrice,
            NetCost = 0m,
            CreatedAt = DateTime.UtcNow
        };

    private static Invoice LiveInvoice(int reservaId, decimal importe, int numero)
        => new()
        {
            ReservaId = reservaId,
            TipoComprobante = 11, // Factura C
            ImporteTotal = importe,
            MonId = "PES",
            Resultado = "A",
            AnnulmentStatus = AnnulmentStatus.None,
            NumeroComprobante = numero,
            CreatedAt = DateTime.UtcNow,
        };

    private static InvoiceDto InvoiceDtoOf(ReservaDto dto, Guid invoicePublicId)
        => dto.Invoices.Single(i => i.PublicId == invoicePublicId);

    // ============================================================================================

    [Fact]
    public async Task FacturaConItemsTrazadosADosServicios_DevuelveLosDosPublicIds()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-1", Name = "R1", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);

        var hotelService = GenericService(10, reserva.Id, salePrice: 60_000m);
        var excursionService = GenericService(11, reserva.Id, salePrice: 20_000m);
        context.Servicios.AddRange(hotelService, excursionService);

        var invoice = LiveInvoice(reserva.Id, importe: 80_000m, numero: 1);
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();

        context.Set<InvoiceItem>().AddRange(
            new InvoiceItem
            {
                InvoiceId = invoice.Id,
                Description = "Hotel",
                Quantity = 1,
                UnitPrice = 60_000m,
                Total = 60_000m,
                AlicuotaIvaId = 3,
                SourceServicioReservaId = hotelService.Id,
            },
            new InvoiceItem
            {
                InvoiceId = invoice.Id,
                Description = "Excursion",
                Quantity = 1,
                UnitPrice = 20_000m,
                Total = 20_000m,
                AlicuotaIvaId = 3,
                SourceServicioReservaId = excursionService.Id,
            });
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var invoiceDto = InvoiceDtoOf(dto, invoice.PublicId);
        Assert.Equal(2, invoiceDto.ServicePublicIds.Count);
        Assert.Contains(hotelService.PublicId, invoiceDto.ServicePublicIds);
        Assert.Contains(excursionService.PublicId, invoiceDto.ServicePublicIds);
    }

    [Fact]
    public async Task FacturaLegacySinTrazabilidad_DevuelveListaVaciaNuncaNull()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-1", Name = "R1", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);
        context.Servicios.Add(GenericService(10, reserva.Id, salePrice: 50_000m));

        var invoice = LiveInvoice(reserva.Id, importe: 50_000m, numero: 1);
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();

        // Item legacy: SourceServicioReservaId se deja en su default (null), como en cualquier
        // factura emitida hoy en produccion (ver el hallazgo en el XML-doc de la clase).
        context.Set<InvoiceItem>().Add(new InvoiceItem
        {
            InvoiceId = invoice.Id,
            Description = "Paquete completo",
            Quantity = 1,
            UnitPrice = 50_000m,
            Total = 50_000m,
            AlicuotaIvaId = 3,
        });
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var invoiceDto = InvoiceDtoOf(dto, invoice.PublicId);
        Assert.NotNull(invoiceDto.ServicePublicIds);
        Assert.Empty(invoiceDto.ServicePublicIds);
    }

    [Fact]
    public async Task ItemsConSourceNullMezcladosConTrazados_IgnoraLosNull()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-1", Name = "R1", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);

        var trazado = GenericService(10, reserva.Id, salePrice: 30_000m);
        context.Servicios.Add(trazado);

        var invoice = LiveInvoice(reserva.Id, importe: 45_000m, numero: 1);
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();

        context.Set<InvoiceItem>().AddRange(
            new InvoiceItem
            {
                InvoiceId = invoice.Id,
                Description = "Servicio con trazabilidad",
                Quantity = 1,
                UnitPrice = 30_000m,
                Total = 30_000m,
                AlicuotaIvaId = 3,
                SourceServicioReservaId = trazado.Id,
            },
            new InvoiceItem
            {
                InvoiceId = invoice.Id,
                Description = "Cargo de gestion suelto (sin servicio de origen)",
                Quantity = 1,
                UnitPrice = 15_000m,
                Total = 15_000m,
                AlicuotaIvaId = 3,
                SourceServicioReservaId = null,
            });
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var invoiceDto = InvoiceDtoOf(dto, invoice.PublicId);
        var solo = Assert.Single(invoiceDto.ServicePublicIds);
        Assert.Equal(trazado.PublicId, solo);
    }

    // ============================================================================================
    // Tarea 2026-07-16: la trazabilidad polimorfica nueva (SourceServiceTable/SourceServicePublicId,
    // cubre los 6 tipos de servicio) se UNE con la legacy (SourceServicioReservaId, solo generico).
    // ============================================================================================

    [Fact]
    public async Task FacturaConUnItemLegacyYOtroConTrazabilidadNueva_DevuelveLaUnionDeAmbas()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-1", Name = "R1", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);

        var servicioGenericoLegacy = GenericService(10, reserva.Id, salePrice: 30_000m);
        context.Servicios.Add(servicioGenericoLegacy);

        var invoice = LiveInvoice(reserva.Id, importe: 80_000m, numero: 1);
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();

        var hotelPublicIdNuevo = Guid.NewGuid();

        context.Set<InvoiceItem>().AddRange(
            new InvoiceItem
            {
                InvoiceId = invoice.Id,
                Description = "Servicio con trazabilidad LEGACY",
                Quantity = 1,
                UnitPrice = 30_000m,
                Total = 30_000m,
                AlicuotaIvaId = 3,
                SourceServicioReservaId = servicioGenericoLegacy.Id,
            },
            new InvoiceItem
            {
                InvoiceId = invoice.Id,
                Description = "Hotel con trazabilidad NUEVA (polimorfica)",
                Quantity = 1,
                UnitPrice = 50_000m,
                Total = 50_000m,
                AlicuotaIvaId = 3,
                SourceServiceTable = CancellableServiceTable.Hotel,
                SourceServicePublicId = hotelPublicIdNuevo,
            });
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var invoiceDto = InvoiceDtoOf(dto, invoice.PublicId);
        Assert.Equal(2, invoiceDto.ServicePublicIds.Count);
        Assert.Contains(servicioGenericoLegacy.PublicId, invoiceDto.ServicePublicIds);
        Assert.Contains(hotelPublicIdNuevo, invoiceDto.ServicePublicIds);
    }

    [Fact]
    public async Task FacturaSoloConTrazabilidadNueva_SinNingunItemLegacy_FuncionaIgual()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-1", Name = "R1", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);

        var invoice = LiveInvoice(reserva.Id, importe: 20_000m, numero: 1);
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();

        var flightPublicId = Guid.NewGuid();
        context.Set<InvoiceItem>().Add(new InvoiceItem
        {
            InvoiceId = invoice.Id,
            Description = "Aereo",
            Quantity = 1,
            UnitPrice = 20_000m,
            Total = 20_000m,
            AlicuotaIvaId = 3,
            SourceServiceTable = CancellableServiceTable.Flight,
            SourceServicePublicId = flightPublicId,
        });
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var invoiceDto = InvoiceDtoOf(dto, invoice.PublicId);
        var solo = Assert.Single(invoiceDto.ServicePublicIds);
        Assert.Equal(flightPublicId, solo);
    }
}
