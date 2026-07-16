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
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tarea 2026-07-16 (trazabilidad linea de factura -&gt; servicio de origen): objetivo de negocio
/// es que, al cancelar UN servicio de una reserva con varias facturas, el sistema pueda decir en
/// que factura esta ese servicio. Esto se logra grabando, al crear la factura, de que servicio
/// salio cada renglon (<c>InvoiceItem.SourceServiceTable</c> + <c>SourceServicePublicId</c>).
///
/// <para>Dos bloques de tests:
/// <list type="bullet">
/// <item><b>Resolver puro</b> (<see cref="AfipService.ResolveInvoiceItemServiceSource"/>): sin DB,
/// cubre la regla defensiva "o vienen los dos campos completos y validos, o se ignoran los dos".</item>
/// <item><b>CreatePendingInvoice de punta a punta</b> (InMemory, sin ARCA): verifica que lo que
/// resuelve el helper realmente QUEDA GRABADO en <c>InvoiceItem</c>, y que un dato de trazabilidad
/// invalido NUNCA rompe la creacion de la factura (es metadata, no plata).</item>
/// </list></para>
/// </summary>
public class AfipServiceInvoiceItemServiceSourceTests
{
    // ============================================================
    // Bloque 1: el resolver puro (sin DB)
    // ============================================================

    private static InvoiceItemDto LineWithSource(string? table, Guid? publicId)
        => new()
        {
            Description = "Item",
            Quantity = 1m,
            UnitPrice = 100m,
            Total = 100m,
            AlicuotaIvaId = 3,
            SourceServiceTable = table,
            SourceServicePublicId = publicId,
        };

    [Fact]
    public void ResolveInvoiceItemServiceSource_TablaYPublicIdValidos_DevuelveAmbosResueltos()
    {
        var publicId = Guid.NewGuid();
        var linea = LineWithSource("Hotel", publicId);

        var (table, resolvedPublicId) = AfipService.ResolveInvoiceItemServiceSource(linea);

        Assert.Equal(CancellableServiceTable.Hotel, table);
        Assert.Equal(publicId, resolvedPublicId);
    }

    [Fact]
    public void ResolveInvoiceItemServiceSource_NombreDeTablaMinuscula_IgnoraMayusculas()
    {
        // El front puede mandar "hotel" en vez de "Hotel"; el parseo del enum es case-insensitive
        // (mismo criterio que BookingCancellationService.CancelServiceAsync).
        var publicId = Guid.NewGuid();
        var linea = LineWithSource("hotel", publicId);

        var (table, resolvedPublicId) = AfipService.ResolveInvoiceItemServiceSource(linea);

        Assert.Equal(CancellableServiceTable.Hotel, table);
        Assert.Equal(publicId, resolvedPublicId);
    }

    [Fact]
    public void ResolveInvoiceItemServiceSource_SoloTablaSinPublicId_DevuelveAmbosNull()
    {
        var linea = LineWithSource("Hotel", null);

        var (table, publicId) = AfipService.ResolveInvoiceItemServiceSource(linea);

        Assert.Null(table);
        Assert.Null(publicId);
    }

    [Fact]
    public void ResolveInvoiceItemServiceSource_SoloPublicIdSinTabla_DevuelveAmbosNull()
    {
        var linea = LineWithSource(null, Guid.NewGuid());

        var (table, publicId) = AfipService.ResolveInvoiceItemServiceSource(linea);

        Assert.Null(table);
        Assert.Null(publicId);
    }

    [Fact]
    public void ResolveInvoiceItemServiceSource_NombreDeTablaInvalido_DevuelveAmbosNull_NoTira()
    {
        // Metadata, no plata: un texto que no matchea el enum NUNCA rompe la factura, solo se
        // pierde la trazabilidad de esa linea.
        var linea = LineWithSource("TablaInventada", Guid.NewGuid());

        var (table, publicId) = AfipService.ResolveInvoiceItemServiceSource(linea);

        Assert.Null(table);
        Assert.Null(publicId);
    }

    [Fact]
    public void ResolveInvoiceItemServiceSource_NingunoSeteado_DevuelveAmbosNull()
    {
        var linea = LineWithSource(null, null);

        var (table, publicId) = AfipService.ResolveInvoiceItemServiceSource(linea);

        Assert.Null(table);
        Assert.Null(publicId);
    }

    // ============================================================
    // Bloque 2: CreatePendingInvoice de punta a punta (InMemory, sin ARCA)
    // ============================================================

    private static AfipService BuildAfipService(AppDbContext context)
        => new(
            context,
            NullLogger<AfipService>.Instance,
            new HttpClient(),
            new NoopProtector());

    // Protector inerte: CreatePendingInvoice arma la Invoice PENDING, nunca llama a ARCA ni usa
    // certificados. Mismo patron que MultiCurrencyInvoicingTests.
    private sealed class NoopProtector : ISensitiveDataProtector
    {
        public string? ProtectString(string? value) => value;
        public string? UnprotectString(string? value) => value;
        public byte[]? ProtectBytes(byte[]? value) => value;
        public byte[]? UnprotectBytes(byte[]? value) => value;
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Siembra AFIP settings (Monotributo, factura C) + un cliente Consumidor Final + su reserva.</summary>
    private static async Task SeedAfipScenarioAsync(AppDbContext context)
    {
        context.AfipSettings.Add(new AfipSettings
        {
            Id = 1,
            PuntoDeVenta = 7,
            TaxCondition = "Monotributo",
        });

        var customer = new Customer
        {
            Id = 10,
            FullName = "Cliente Test",
            TaxCondition = "Consumidor Final",
        };
        context.Customers.Add(customer);

        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-TRAZA-1",
            Name = "Reserva trazabilidad",
            Status = EstadoReserva.Confirmed,
            TotalSale = 100m,
            Balance = 0m,
            TotalPaid = 100m,
            PayerId = 10,
            Payer = customer,
        });

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task CreatePendingInvoice_ItemsConIdentidadCompleta_GrabaLasColumnasDeTrazabilidad()
    {
        await using var context = CreateContext();
        await SeedAfipScenarioAsync(context);

        var hotelPublicId = Guid.NewGuid();
        var flightPublicId = Guid.NewGuid();

        var request = new CreateInvoiceRequest
        {
            ReservaId = "1",
            Items = new List<InvoiceItemDto>
            {
                new()
                {
                    Description = "Hotel", Quantity = 1, UnitPrice = 60_000m, Total = 60_000m, AlicuotaIvaId = 3,
                    SourceServiceTable = "Hotel", SourceServicePublicId = hotelPublicId,
                },
                new()
                {
                    Description = "Aereo", Quantity = 1, UnitPrice = 20_000m, Total = 20_000m, AlicuotaIvaId = 3,
                    SourceServiceTable = "Flight", SourceServicePublicId = flightPublicId,
                },
            },
        };

        var afip = BuildAfipService(context);
        var invoice = await afip.CreatePendingInvoice(1, request);

        var persistedItems = await context.Set<InvoiceItem>()
            .Where(i => i.InvoiceId == invoice.Id)
            .ToListAsync();

        Assert.Equal(2, persistedItems.Count);

        var hotelItem = persistedItems.Single(i => i.Description == "Hotel");
        Assert.Equal(CancellableServiceTable.Hotel, hotelItem.SourceServiceTable);
        Assert.Equal(hotelPublicId, hotelItem.SourceServicePublicId);

        var flightItem = persistedItems.Single(i => i.Description == "Aereo");
        Assert.Equal(CancellableServiceTable.Flight, flightItem.SourceServiceTable);
        Assert.Equal(flightPublicId, flightItem.SourceServicePublicId);
    }

    [Fact]
    public async Task CreatePendingInvoice_ItemConIdentidadAMedias_GrabaAmbosNull_YLaFacturaSaleIgual()
    {
        await using var context = CreateContext();
        await SeedAfipScenarioAsync(context);

        var request = new CreateInvoiceRequest
        {
            ReservaId = "1",
            Items = new List<InvoiceItemDto>
            {
                new()
                {
                    Description = "Cargo suelto", Quantity = 1, UnitPrice = 5_000m, Total = 5_000m, AlicuotaIvaId = 3,
                    SourceServiceTable = "Hotel", SourceServicePublicId = null, // falta el PublicId
                },
            },
        };

        var afip = BuildAfipService(context);
        var invoice = await afip.CreatePendingInvoice(1, request);

        var item = await context.Set<InvoiceItem>().SingleAsync(i => i.InvoiceId == invoice.Id);
        Assert.Null(item.SourceServiceTable);
        Assert.Null(item.SourceServicePublicId);
        // La factura se creo igual: el dato de trazabilidad es informativo, nunca bloquea la emision.
        Assert.Equal("PENDING", invoice.Resultado);
    }

    [Fact]
    public async Task CreatePendingInvoice_ItemConNombreDeTablaInvalido_GrabaAmbosNull_YLaFacturaSaleIgual()
    {
        await using var context = CreateContext();
        await SeedAfipScenarioAsync(context);

        var request = new CreateInvoiceRequest
        {
            ReservaId = "1",
            Items = new List<InvoiceItemDto>
            {
                new()
                {
                    Description = "Servicio", Quantity = 1, UnitPrice = 10_000m, Total = 10_000m, AlicuotaIvaId = 3,
                    SourceServiceTable = "NoExiste", SourceServicePublicId = Guid.NewGuid(),
                },
            },
        };

        var afip = BuildAfipService(context);
        var invoice = await afip.CreatePendingInvoice(1, request);

        var item = await context.Set<InvoiceItem>().SingleAsync(i => i.InvoiceId == invoice.Id);
        Assert.Null(item.SourceServiceTable);
        Assert.Null(item.SourceServicePublicId);
        Assert.Equal("PENDING", invoice.Resultado);
    }

    [Fact]
    public async Task CreatePendingInvoice_ItemsSinIdentidad_QuedaComoLegacy_AmbosNull()
    {
        // Compatibilidad hacia atras: un caller que no manda los campos nuevos (ej. facturacion
        // manual desde el modal viejo) sigue funcionando exactamente igual que antes.
        await using var context = CreateContext();
        await SeedAfipScenarioAsync(context);

        var request = new CreateInvoiceRequest
        {
            ReservaId = "1",
            Items = new List<InvoiceItemDto>
            {
                new() { Description = "Servicios Turisticos", Quantity = 1, UnitPrice = 100m, Total = 100m, AlicuotaIvaId = 3 },
            },
        };

        var afip = BuildAfipService(context);
        var invoice = await afip.CreatePendingInvoice(1, request);

        var item = await context.Set<InvoiceItem>().SingleAsync(i => i.InvoiceId == invoice.Id);
        Assert.Null(item.SourceServiceTable);
        Assert.Null(item.SourceServicePublicId);
    }

    // ============================================================
    // Bloque 3: GET suggested-items manda la identidad completa (InvoiceService.GetSuggestedItemsAsync)
    // ============================================================

    [Fact]
    public async Task GetSuggestedItemsAsync_ServicioHotelConfirmado_ItemSugeridoTraeTablaYPublicId()
    {
        await using var context = CreateContext();

        var reserva = new Reserva { Id = 1, NumeroReserva = "R-1", Name = "R1", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);
        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id,
            Status = "Confirmado",
            HotelName = "Sheraton",
            Currency = "ARS",
            SalePrice = 200_000m,
        };
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();

        var mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        var service = new InvoiceService(
            context,
            new EntityReferenceResolver(context),
            Mock.Of<IAfipService>(),
            Mock.Of<IInvoicePdfService>(),
            mapper,
            Mock.Of<IBackgroundJobClient>(),
            NullLogger<InvoiceService>.Instance,
            settingsMock.Object,
            BuildUserManagerForSuggestedItemsTest());

        var response = await service.GetSuggestedItemsAsync(reserva.Id, default);

        var group = Assert.Single(response.Groups);
        var item = Assert.Single(group.Items);
        Assert.Equal("Hotel", item.SourceServiceTable);
        Assert.Equal(hotel.PublicId, item.SourceServicePublicId);
    }

    private static UserManager<ApplicationUser> BuildUserManagerForSuggestedItemsTest()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }
}
