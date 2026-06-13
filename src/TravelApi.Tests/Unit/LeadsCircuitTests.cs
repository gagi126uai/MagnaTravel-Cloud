using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Cierre del circuito CRM de leads (2026-06-12): reserva nacida de un lead, conversion a cliente
/// mas rica, dedup del formulario publico de paquetes y normalizador unico de telefono.
/// </summary>
public class LeadsCircuitTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

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

    private static ReservaService NewReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        // El mapper solo se usa en GetReservaByIdAsync (que CreateReservaAsync invoca al final).
        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<ReservaDto>(It.IsAny<Reserva>()))
              .Returns((Reserva r) => new ReservaDto
              {
                  PublicId = r.PublicId, NumeroReserva = r.NumeroReserva, Name = r.Name, Status = r.Status
              });

        return new ReservaService(context, mapper.Object, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    private static LeadService NewLeadService(AppDbContext context)
    {
        // El resolver solo se usa en ConvertToCustomerAsync para devolver el PublicId del cliente.
        var resolver = new Mock<IEntityReferenceResolver>();
        resolver.Setup(r => r.ResolvePublicIdAsync<Customer>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
        return new LeadService(context, resolver.Object);
    }

    // ===================== Item 1: reserva nacida de un lead =====================

    [Fact]
    public async Task CreateReserva_WithSourceLead_LinksButDoesNotMarkWonYet()
    {
        // Decision del dueño (auditoria ERP 2026-06-13): crear la reserva desde un lead solo LINKEA. El lead
        // se gana recien cuando la reserva linkeada llega a un estado EN FIRME (el cliente acepta el
        // presupuesto). Al nacer en Cotizacion, el lead debe conservar su estado actual.
        await using var ctx = NewContext();
        var lead = new Lead { FullName = "Ana", Phone = "1122334455", Status = LeadStatus.Contacted };
        ctx.Leads.Add(lead);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);
        var request = new CreateReservaRequest { Name = "Viaje Ana", SourceLeadPublicId = lead.PublicId.ToString() };

        var dto = await service.CreateReservaAsync(request, createdByUserId: null, CancellationToken.None);

        var reserva = await ctx.Reservas.FirstAsync(r => r.PublicId == dto.PublicId);
        Assert.Equal(lead.Id, reserva.SourceLeadId);     // se linkea
        Assert.Equal(EstadoReserva.Quotation, reserva.Status); // nace en Cotizacion (no en firme)

        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Contacted, refreshedLead!.Status); // NO se marca Ganado al crear
        Assert.Null(refreshedLead.ClosedAt);
    }

    [Fact]
    public async Task UpdateStatus_ReservaFromLeadReachesFirmStatus_MarksLeadWon()
    {
        // El lead se gana cuando la reserva linkeada transiciona a un estado EN FIRME. La unica entrada
        // manual al set en firme es Budget -> InManagement (el cliente acepto el presupuesto).
        await using var ctx = NewContext();
        var lead = new Lead { FullName = "Ana", Phone = "1122334455", Status = LeadStatus.Contacted };
        ctx.Leads.Add(lead);

        // Reserva linkeada al lead, ya en Presupuesto y con un servicio cargado (gate de readiness).
        var reserva = new Reserva
        {
            Name = "Viaje Ana",
            NumeroReserva = "RES-00001",
            Status = EstadoReserva.Budget,
            SourceLeadId = lead.Id,
            AdultCount = 1, // el gate de readiness exige >= 1 pasajero declarado
        };
        reserva.HotelBookings.Add(new HotelBooking
        {
            Status = ReservationStatuses.Requested,
            Currency = "ARS",
            SalePrice = 1000m,
        });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // El gate de readiness pide nominales cargados por la cantidad declarada (1).
        ctx.Passengers.Add(new Passenger { ReservaId = reserva.Id, FullName = "Ana Lopez" });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);
        await service.UpdateStatusAsync(reserva.Id, EstadoReserva.InManagement, actorUserId: "u1");

        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Won, refreshedLead!.Status);
        Assert.NotNull(refreshedLead.ClosedAt);
    }

    [Fact]
    public async Task UpdateStatus_ReservaFromLostLeadReachesFirmStatus_DoesNotReopenLead()
    {
        // Idempotencia/seguridad: un lead Perdido NO se reabre aunque la reserva linkeada avance a en firme.
        await using var ctx = NewContext();
        var closedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lead = new Lead { FullName = "Beto", Phone = "1100000000", Status = LeadStatus.Lost, ClosedAt = closedAt };
        ctx.Leads.Add(lead);

        var reserva = new Reserva
        {
            Name = "Viaje Beto",
            NumeroReserva = "RES-00002",
            Status = EstadoReserva.Budget,
            SourceLeadId = lead.Id,
            AdultCount = 1,
        };
        reserva.HotelBookings.Add(new HotelBooking
        {
            Status = ReservationStatuses.Requested,
            Currency = "ARS",
            SalePrice = 1000m,
        });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.Passengers.Add(new Passenger { ReservaId = reserva.Id, FullName = "Beto Diaz" });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);
        await service.UpdateStatusAsync(reserva.Id, EstadoReserva.InManagement, actorUserId: "u1");

        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Lost, refreshedLead!.Status); // no se reabre
        Assert.Equal(closedAt, refreshedLead.ClosedAt);       // no se toca
    }

    [Fact]
    public async Task CreateReserva_WithUnknownSourceLead_ThrowsArgumentException()
    {
        await using var ctx = NewContext();
        var service = NewReservaService(ctx);
        var request = new CreateReservaRequest
        {
            Name = "Viaje",
            SourceLeadPublicId = Guid.NewGuid().ToString() // no existe ningun lead con este PublicId
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateReservaAsync(request, createdByUserId: null, CancellationToken.None));
    }

    [Fact]
    public async Task CreateReserva_WithLostSourceLead_LinksButDoesNotChangeStatus()
    {
        await using var ctx = NewContext();
        var closedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lead = new Lead { FullName = "Beto", Phone = "1100000000", Status = LeadStatus.Lost, ClosedAt = closedAt };
        ctx.Leads.Add(lead);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);
        var request = new CreateReservaRequest { Name = "Viaje Beto", SourceLeadPublicId = lead.PublicId.ToString() };

        var dto = await service.CreateReservaAsync(request, createdByUserId: null, CancellationToken.None);

        var reserva = await ctx.Reservas.FirstAsync(r => r.PublicId == dto.PublicId);
        Assert.Equal(lead.Id, reserva.SourceLeadId); // se linkea igual

        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Lost, refreshedLead!.Status); // no se reabre
        Assert.Equal(closedAt, refreshedLead.ClosedAt);       // no se toca
    }

    [Fact]
    public async Task CreateReserva_WithoutSourceLead_WorksUnchanged()
    {
        await using var ctx = NewContext();
        var service = NewReservaService(ctx);
        var request = new CreateReservaRequest { Name = "Reserva suelta" };

        var dto = await service.CreateReservaAsync(request, createdByUserId: null, CancellationToken.None);

        var reserva = await ctx.Reservas.FirstAsync(r => r.PublicId == dto.PublicId);
        Assert.Null(reserva.SourceLeadId);
    }

    // ===================== Item 2: conversion a cliente mas rica =====================

    [Fact]
    public async Task ConvertToCustomer_NewCustomer_CopiesTravelDataToNotes()
    {
        await using var ctx = NewContext();
        var lead = new Lead
        {
            FullName = "Carla",
            Phone = "1199887766",
            Status = LeadStatus.New,
            InterestedIn = "Caribe",
            TravelDates = "Marzo 2026",
            Travelers = "2 adultos",
            EstimatedBudget = 500000m,
            Notes = "Prefiere all inclusive"
        };
        ctx.Leads.Add(lead);
        await ctx.SaveChangesAsync();

        var service = NewLeadService(ctx);
        await service.ConvertToCustomerAsync(lead.Id, CancellationToken.None);

        var customer = await ctx.Customers.FirstAsync();
        Assert.Contains("Interes: Caribe", customer.Notes);
        Assert.Contains("Fechas: Marzo 2026", customer.Notes);
        Assert.Contains("Viajeros: 2 adultos", customer.Notes);
        Assert.Contains("Presupuesto est.: 500000", customer.Notes);
        Assert.Contains("Prefiere all inclusive", customer.Notes);
    }

    [Fact]
    public async Task ConvertToCustomer_ExistingCustomerByPhone_DoesNotOverwriteNotes()
    {
        await using var ctx = NewContext();
        var existing = new Customer
        {
            FullName = "Diego",
            Phone = "54-11-2233-4455",
            Notes = "Cliente VIP, no tocar",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Customers.Add(existing);
        // mismo telefono escrito distinto (mismos digitos) -> debe deduplicar por normalizacion fuerte
        var lead = new Lead
        {
            FullName = "Diego",
            Phone = "+54 11 2233 4455",
            Status = LeadStatus.Contacted,
            InterestedIn = "Brasil",
            Notes = "consulta nueva"
        };
        ctx.Leads.Add(lead);
        await ctx.SaveChangesAsync();

        var service = NewLeadService(ctx);
        await service.ConvertToCustomerAsync(lead.Id, CancellationToken.None);

        // No se creo un cliente nuevo y no se pisaron las notas del existente.
        Assert.Single(ctx.Customers);
        var refreshed = await ctx.Customers.FindAsync(existing.Id);
        Assert.Equal("Cliente VIP, no tocar", refreshed!.Notes);

        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(existing.Id, refreshedLead!.ConvertedCustomerId);
    }

    [Fact]
    public async Task ConvertToCustomer_LeadNew_BecomesContacted()
    {
        await using var ctx = NewContext();
        var lead = new Lead { FullName = "Elena", Phone = "1133221100", Status = LeadStatus.New };
        ctx.Leads.Add(lead);
        await ctx.SaveChangesAsync();

        var service = NewLeadService(ctx);
        await service.ConvertToCustomerAsync(lead.Id, CancellationToken.None);

        var refreshed = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Contacted, refreshed!.Status);
    }

    [Fact]
    public async Task ConvertToCustomer_LeadQuoted_StaysQuoted()
    {
        await using var ctx = NewContext();
        var lead = new Lead { FullName = "Fede", Phone = "1144556677", Status = LeadStatus.Quoted };
        ctx.Leads.Add(lead);
        await ctx.SaveChangesAsync();

        var service = NewLeadService(ctx);
        await service.ConvertToCustomerAsync(lead.Id, CancellationToken.None);

        var refreshed = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Quoted, refreshed!.Status); // conversion nunca marca Ganado ni revierte
    }

    // ===================== Item 4: PhoneNormalizer =====================

    [Theory]
    [InlineData("+54 9 11 1234-5678", "5491112345678")]
    [InlineData("(011) 4321-9876", "01143219876")]
    [InlineData("11 2233 4455", "1122334455")]
    [InlineData("+54-11-2233-4455", "541122334455")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("abc", "")]
    public void PhoneNormalizer_StripsEverythingButDigits(string? raw, string expected)
    {
        Assert.Equal(expected, PhoneNormalizer.Normalize(raw));
    }

    [Fact]
    public void PhoneNormalizer_OldLaxMatches_StillMatchUnderStrongNormalization()
    {
        // Compat: el webhook viejo sacaba solo '+'. "+54911..." vs "54911..." matcheaban.
        // Con la normalizacion fuerte tienen que seguir dando IGUAL.
        Assert.Equal(
            PhoneNormalizer.Normalize("+5491112345678"),
            PhoneNormalizer.Normalize("5491112345678"));
    }
}
