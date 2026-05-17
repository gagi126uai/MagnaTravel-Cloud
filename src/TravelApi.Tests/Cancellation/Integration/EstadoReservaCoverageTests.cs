using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1 (ADR-002 §2.3.3 / INV-100, 2026-05-14): valida que cada valor literal de
/// la "convencion estado" (clase <see cref="EstadoReserva"/>) ES aceptado por el
/// CHECK <c>chk_TravelFiles_status_valid</c>, mas el legacy "Archived" que NO
/// pertenece al enum pero sigue siendo legal por compatibilidad de datos
/// historicos.
///
/// Por que reflexion sobre <see cref="EstadoReserva"/>:
///  - Si un dev agrega un nuevo estado a la clase y olvida actualizar la
///    migracion del CHECK, este test lo detecta inmediatamente — sin
///    necesidad de mantener un enum-list duplicado en el test.
///  - <see cref="EstadoReserva"/> es <c>static class</c> con campos <c>const string</c>,
///    no un enum CLR. Por eso usamos <c>GetFields(BindingFlags.Public | Static)</c>.
///
/// Test 10.b cubre "Archived": esta en la lista del CHECK pero NO en
/// <see cref="EstadoReserva"/> por diseño (es un legacy de soft-delete).
/// </summary>
[Trait("Category", "Integration")]
public sealed class EstadoReservaCoverageTests : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public EstadoReservaCoverageTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EstadoReserva_AllEnumLiteralValues_PassCheckConstraint()
    {
        // ARRANGE: reflexion sobre EstadoReserva para obtener TODOS los literales
        // declarados. Si alguien agrega "Suspended" en codigo pero olvida agregarlo
        // al CHECK SQL, este test lo detecta cuando intente insertarlo.
        var statusValues = typeof(EstadoReserva)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToArray();

        // Sanity check: el enum literal tiene al menos los 6 valores documentados.
        Assert.True(statusValues.Length >= 6,
            $"Se esperaban >= 6 valores en EstadoReserva, se obtuvieron {statusValues.Length}.");

        // ACT: probamos cada valor en una iteracion independiente — un valor invalido
        // dejaria a la reserva tracked con error y rompera el siguiente intento.
        foreach (var status in statusValues)
        {
            await _fixture.ResetDatabaseAsync();
            await using var ctx = _fixture.CreateDbContext();

            var customer = new Customer
            {
                FullName = $"Cliente {status}",
                TaxCondition = "Consumidor Final",
                IsActive = true,
            };
            ctx.Customers.Add(customer);
            await ctx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = $"F-{status}-{Guid.NewGuid().ToString("N")[..6]}",
                Name = $"Reserva {status}",
                Status = status,
                PayerId = customer.Id,
            };
            ctx.Reservas.Add(reserva);

            // ASSERT: cada literal del enum debe ser aceptado por el CHECK.
            // Si Postgres rechaza, el test falla con la fila del estado problematico.
            var savedRows = await ctx.SaveChangesAsync();
            Assert.True(savedRows > 0,
                $"El status '{status}' fue rechazado por chk_TravelFiles_status_valid.");
        }
    }

    [Fact]
    public async Task EstadoReserva_ArchivedLegacy_PassesCheckConstraint()
    {
        // ARRANGE: "Archived" NO esta en EstadoReserva (es legacy soft-delete),
        // pero la migracion del CHECK lo lista explicitamente para preservar
        // datos historicos. Hardcodeo el string a proposito — el test
        // documenta esa decision intencional.
        await using var ctx = _fixture.CreateDbContext();
        var (custId, _, _, _) = await CancellationTestData.SeedBaseAsync(ctx);

        var reserva = await ctx.Reservas.FirstAsync();
        reserva.Status = "Archived";

        // ACT + ASSERT: debe persistirse sin BusinessInvariantViolationException.
        // Si en algun momento se decide eliminar "Archived" del CHECK, este test
        // fallara y obligara a revisar el plan de migracion de los datos legacy.
        var savedRows = await ctx.SaveChangesAsync();
        Assert.True(savedRows > 0,
            "El status 'Archived' (legacy) debe seguir siendo aceptado por chk_TravelFiles_status_valid.");
    }
}
