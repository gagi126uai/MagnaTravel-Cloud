using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Obra "numero de reserva sin F-" (2026-08-11, decision del dueño): valida contra Postgres real el SQL
/// crudo de la migracion <c>ReservaNumberFormat_M1_DropFPrefixAndRepairName</c> (saca el prefijo "F-" de
/// <c>TravelFiles."FileNumber"</c> y repara <c>"Name"</c> cuando lo menciona).
///
/// <para><b>Por que Postgres real y no InMemory</b>: la migracion es SQL crudo (<c>regexp_replace</c>,
/// <c>REPLACE</c>) — el provider InMemory ni siquiera lo ejecuta. El SQL de abajo esta COPIADO tal cual de
/// la migracion; si la migracion cambia, hay que actualizar esta constante tambien (mismo patron que
/// <c>FiscalLiquidationBackfillIntegrationTests</c>).</para>
///
/// <para>La fixture arma el schema con <c>EnsureCreatedAsync</c> (no corre migraciones reales), por eso
/// este test ejecuta el SQL a mano en vez de <c>Database.MigrateAsync()</c>.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReservaNumberFormatMigrationIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public ReservaNumberFormatMigrationIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // SQL copiado tal cual de 20260811225336_ReservaNumberFormat_M1_DropFPrefixAndRepairName.Up/Down.
    private const string UpSql = """
        UPDATE "TravelFiles"
        SET
            "Name" = REPLACE("Name", "FileNumber", regexp_replace("FileNumber", '^F-', '')),
            "FileNumber" = regexp_replace("FileNumber", '^F-', '')
        WHERE "FileNumber" ~ '^F-[0-9]{4}-[0-9]+$';
        """;

    private const string DownSql = """
        UPDATE "TravelFiles"
        SET
            "Name" = REPLACE("Name", "FileNumber", 'F-' || "FileNumber"),
            "FileNumber" = 'F-' || "FileNumber"
        WHERE "FileNumber" ~ '^[0-9]{4}-[0-9]+$';
        """;

    [Fact]
    public async Task Up_RepairsPrefixedFileNumberAndNameThatMentionsIt()
    {
        int reservaId;
        await using (var setup = _fixture.CreateDbContext())
        {
            var reserva = new Reserva
            {
                // Nombre autogenerado tipico ($"Reserva {numeroReserva}") de CreateReservaAsync.
                NumeroReserva = "F-2026-1067",
                Name = "Reserva F-2026-1067",
                Status = EstadoReserva.Budget,
            };
            setup.Reservas.Add(reserva);
            await setup.SaveChangesAsync();
            reservaId = reserva.Id;
        }

        await using (var migrate = _fixture.CreateDbContext())
        {
            await migrate.Database.ExecuteSqlRawAsync(UpSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var repaired = await verify.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
        Assert.Equal("2026-1067", repaired.NumeroReserva);
        Assert.Equal("Reserva 2026-1067", repaired.Name);
    }

    [Fact]
    public async Task Up_LeavesCustomNameUntouched_WhenNameDoesNotMentionTheOldNumber()
    {
        // El usuario pudo haber sobreescrito el nombre autogenerado con uno propio que no
        // menciona el numero de reserva para nada. El REPLACE no debe inventarle nada.
        int reservaId;
        await using (var setup = _fixture.CreateDbContext())
        {
            var reserva = new Reserva
            {
                NumeroReserva = "F-2026-1068",
                Name = "Viaje a Bariloche familia Perez",
                Status = EstadoReserva.Budget,
            };
            setup.Reservas.Add(reserva);
            await setup.SaveChangesAsync();
            reservaId = reserva.Id;
        }

        await using (var migrate = _fixture.CreateDbContext())
        {
            await migrate.Database.ExecuteSqlRawAsync(UpSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var repaired = await verify.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
        Assert.Equal("2026-1068", repaired.NumeroReserva);
        Assert.Equal("Viaje a Bariloche familia Perez", repaired.Name);
    }

    [Theory]
    [InlineData("2026-2001")] // ya nacio con el formato nuevo (reserva creada despues del deploy del codigo).
    [InlineData("F-PPTO-ABC123")] // formato de seed de tests HTTP (F1FlightSegmentsControllerCreateTests) — el segmento "PPTO" no son 4 digitos.
    [InlineData("F-99-1")] // año de solo 2 digitos: no matchea el patron AAAA exacto.
    public async Task Up_DoesNotTouchRowsThatDoNotMatchTheExactLegacyPattern(string numeroReserva)
    {
        int reservaId;
        await using (var setup = _fixture.CreateDbContext())
        {
            var reserva = new Reserva
            {
                NumeroReserva = numeroReserva,
                Name = $"Reserva {numeroReserva}",
                Status = EstadoReserva.Budget,
            };
            setup.Reservas.Add(reserva);
            await setup.SaveChangesAsync();
            reservaId = reserva.Id;
        }

        await using (var migrate = _fixture.CreateDbContext())
        {
            await migrate.Database.ExecuteSqlRawAsync(UpSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var untouched = await verify.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
        Assert.Equal(numeroReserva, untouched.NumeroReserva);
        Assert.Equal($"Reserva {numeroReserva}", untouched.Name);
    }

    [Fact]
    public async Task Up_RunTwice_IsIdempotent()
    {
        int reservaId;
        await using (var setup = _fixture.CreateDbContext())
        {
            var reserva = new Reserva
            {
                NumeroReserva = "F-2026-1069",
                Name = "Reserva F-2026-1069",
                Status = EstadoReserva.Budget,
            };
            setup.Reservas.Add(reserva);
            await setup.SaveChangesAsync();
            reservaId = reserva.Id;
        }

        await using (var migrate = _fixture.CreateDbContext())
        {
            await migrate.Database.ExecuteSqlRawAsync(UpSql);
            // Segunda corrida: ya no queda ninguna fila con el prefijo "F-", asi que el WHERE
            // no matchea nada y no rompe el dato ya reparado (ej. no le vuelve a sacar un "F-").
            await migrate.Database.ExecuteSqlRawAsync(UpSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var repaired = await verify.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
        Assert.Equal("2026-1069", repaired.NumeroReserva);
        Assert.Equal("Reserva 2026-1069", repaired.Name);
    }

    [Fact]
    public async Task Down_ReAddsPrefixSymmetrically()
    {
        int reservaId;
        await using (var setup = _fixture.CreateDbContext())
        {
            // Simula el estado DESPUES de aplicar Up (o una reserva creada con el codigo nuevo).
            var reserva = new Reserva
            {
                NumeroReserva = "2026-1070",
                Name = "Reserva 2026-1070",
                Status = EstadoReserva.Budget,
            };
            setup.Reservas.Add(reserva);
            await setup.SaveChangesAsync();
            reservaId = reserva.Id;
        }

        await using (var migrate = _fixture.CreateDbContext())
        {
            await migrate.Database.ExecuteSqlRawAsync(DownSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var reverted = await verify.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
        Assert.Equal("F-2026-1070", reverted.NumeroReserva);
        Assert.Equal("Reserva F-2026-1070", reverted.Name);
    }
}
