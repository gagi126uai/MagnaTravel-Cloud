using System;
using System.Threading;
using System.Threading.Tasks;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Hallazgo #47 (barrido T5, 2026-07-24): en la cuenta del operador, la descripcion de una Asistencia
/// sin <see cref="AssistanceBooking.CoverageZone"/> mostraba parentesis vacios "()" (ej. "Seguro ()").
/// El fix en <c>SupplierService.BuildSupplierServicesQuery</c> usa <c>string.IsNullOrWhiteSpace</c>
/// DENTRO de un <c>Select()</c> de EF Core (LINQ-to-SQL, no LINQ-to-Objects).
///
/// <para><b>Por que este test necesita Postgres real (no InMemory)</b>: el proveedor InMemory de EF Core
/// no traduce la expresion a SQL — la ejecuta como delegado C# directo, asi que un <c>Select</c> roto
/// que InMemory acepta puede tirar <c>InvalidOperationException</c> ("could not be translated") recien
/// contra un motor SQL real como Postgres (igual que produccion). Los tests InMemory de
/// <c>SupplierServiceAssistanceDescriptionTests</c> ya prueban el RESULTADO (que arma bien el texto);
/// este prueba que la EXPRESION es traducible por Npgsql, la red que falta.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SupplierServiceAssistanceDescriptionIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public SupplierServiceAssistanceDescriptionIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetSupplierAccountServicesAsync_AsistenciaSinCoverageZone_TraduceASqlSinParentesisVacios()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Assist Integration SA" };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva
        {
            NumeroReserva = $"F-ASIST-{Guid.NewGuid():N}"[..14],
            Name = "Reserva asistencia sin zona",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.AssistanceBookings.Add(new AssistanceBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            PlanType = "Full Cobertura",
            CoverageZone = null, // sin dato: antes esto generaba "Full Cobertura ()"
            Status = "Solicitado",
            ValidFrom = DateTime.UtcNow,
            ValidTo = DateTime.UtcNow.AddDays(10),
        });
        await ctx.SaveChangesAsync();

        var service = new SupplierService(ctx);

        // ACT: si string.IsNullOrWhiteSpace(assistance.CoverageZone) dentro del Select() no fuera
        // traducible por Npgsql, esta linea tiraria InvalidOperationException ANTES de llegar al
        // assert de contenido — esa es la red que un test InMemory no puede tender.
        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Full Cobertura", item.Description);
        Assert.DoesNotContain("(", item.Description);
        Assert.DoesNotContain(")", item.Description);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_AsistenciaConCoverageZone_TraduceASqlConElParentesisYElDato()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Assist Integration SA 2" };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva
        {
            NumeroReserva = $"F-ASIST-{Guid.NewGuid():N}"[..14],
            Name = "Reserva asistencia con zona",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.AssistanceBookings.Add(new AssistanceBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            PlanType = "Full Cobertura",
            CoverageZone = "Mundial",
            Status = "Solicitado",
            ValidFrom = DateTime.UtcNow,
            ValidTo = DateTime.UtcNow.AddDays(10),
        });
        await ctx.SaveChangesAsync();

        var service = new SupplierService(ctx);

        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Full Cobertura (Mundial)", item.Description);
    }
}
