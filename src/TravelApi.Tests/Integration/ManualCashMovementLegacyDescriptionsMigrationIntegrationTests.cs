using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Obra "la ficha del operador no borra la historia" (2026-08-20, punto 5, ALTO RIESGO T-8): valida contra
/// Postgres real el SQL crudo de la migracion
/// <c>BackfillManualCashMovementLegacyDescriptions</c> (limpia los GUIDs internos fosilizados en
/// <c>ManualCashMovements.Description</c> — ver el XML-doc de la migracion para el detalle de los 2 bugs
/// que corrige).
///
/// <para><b>Por que Postgres real y no InMemory</b>: la migracion es SQL crudo (<c>regexp_replace</c>,
/// operador <c>~</c>, <c>UPDATE ... FROM ...</c>) — el provider InMemory ni siquiera lo ejecuta. El SQL de
/// abajo esta COPIADO tal cual de la migracion; si la migracion cambia, hay que actualizar esta constante
/// tambien (mismo patron que <c>ReservaNumberFormatMigrationIntegrationTests</c>).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ManualCashMovementLegacyDescriptionsMigrationIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public ManualCashMovementLegacyDescriptionsMigrationIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // SQL copiado tal cual de 20260820034212_BackfillManualCashMovementLegacyDescriptions.Up.
    private const string RefundFixSql = """
        UPDATE "ManualCashMovements"
        SET "Description" = regexp_replace(
            "Description",
            '^(Devolucion del operador .+) \([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\)$',
            '\1'
        )
        WHERE "Description" ~ '^Devolucion del operador .+ \([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\)$';
        """;

    private const string WithdrawalFixSql = """
        UPDATE "ManualCashMovements" AS mcm
        SET "Description" = CASE w."Kind"
            WHEN 1 THEN 'Retiro de saldo a favor ' ||
                (CASE WHEN c."FullName" IS NOT NULL AND btrim(c."FullName") <> ''
                      THEN 'de ' || c."FullName" ELSE 'del cliente' END) ||
                ' en efectivo'
            WHEN 2 THEN 'Retiro de saldo a favor ' ||
                (CASE WHEN c."FullName" IS NOT NULL AND btrim(c."FullName") <> ''
                      THEN 'de ' || c."FullName" ELSE 'del cliente' END) ||
                ' por transferencia'
            WHEN 4 THEN 'Devolucion al operador del saldo a favor ' ||
                (CASE WHEN c."FullName" IS NOT NULL AND btrim(c."FullName") <> ''
                      THEN 'de ' || c."FullName" ELSE 'del cliente' END)
            ELSE mcm."Description"
        END
        FROM "ClientCreditWithdrawals" AS w
        JOIN "ClientCreditEntries" AS e ON e."Id" = w."ClientCreditEntryId"
        JOIN "Customers" AS c ON c."Id" = e."CustomerId"
        WHERE mcm."ClientCreditWithdrawalId" = w."Id"
          AND mcm."Description" ~ '^Retiro credito cliente [0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12} \((PhysicalCash|Transfer|ReversedToOperator)\)$';
        """;

    // Mismo motivo que ReservaNumberFormatMigrationIntegrationTests: el regex trae cuantificadores como
    // {8}/{12}, que ExecuteSqlRawAsync interpretaria como huecos de parametro y explotaria con
    // FormatException. Se ejecuta el SQL tal cual contra la conexion Npgsql subyacente (lo mismo que hace
    // el migrador de EF al aplicar una migracion de verdad).
    private static async Task ExecuteMigrationSqlVerbatimAsync(AppDbContext context, string sql)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var connectionWasClosed = connection.State != ConnectionState.Open;
        if (connectionWasClosed)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            if (connectionWasClosed)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static ManualCashMovement NewMovement(string description) => new()
    {
        Direction = CashMovementDirections.Income,
        Amount = 1000m,
        Currency = Monedas.ARS,
        Method = "Transfer",
        Category = "OperatorRefund",
        Description = description,
        CreatedBy = "system-test",
    };

    // ===================== Fix 1: "Devolucion del operador {nombre} ({guid})" =====================

    [Fact]
    public async Task Up_StripsLeakedGuid_FromOperatorRefundDescription()
    {
        int movementId;
        var guid = Guid.NewGuid();
        await using (var setup = _fixture.CreateDbContext())
        {
            var movement = NewMovement($"Devolucion del operador SANTA CATALINA VIAJES S R L ({guid})");
            setup.ManualCashMovements.Add(movement);
            await setup.SaveChangesAsync();
            movementId = movement.Id;
        }

        await using (var migrate = _fixture.CreateDbContext())
        {
            await ExecuteMigrationSqlVerbatimAsync(migrate, RefundFixSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var repaired = await verify.ManualCashMovements.AsNoTracking().FirstAsync(m => m.Id == movementId);
        Assert.Equal("Devolucion del operador SANTA CATALINA VIAJES S R L", repaired.Description);
    }

    [Fact]
    public async Task Up_LeavesAlreadyCleanOperatorRefundDescription_Untouched()
    {
        // Fila creada con el codigo NUEVO (post 2026-07-25): ya nace limpia. La migracion no debe tocarla.
        int movementId;
        await using (var setup = _fixture.CreateDbContext())
        {
            var movement = NewMovement("Devolucion del operador AVIS TURISMO");
            setup.ManualCashMovements.Add(movement);
            await setup.SaveChangesAsync();
            movementId = movement.Id;
        }

        await using (var migrate = _fixture.CreateDbContext())
        {
            await ExecuteMigrationSqlVerbatimAsync(migrate, RefundFixSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var untouched = await verify.ManualCashMovements.AsNoTracking().FirstAsync(m => m.Id == movementId);
        Assert.Equal("Devolucion del operador AVIS TURISMO", untouched.Description);
    }

    [Fact]
    public async Task Up_DoesNotTouchUnrelatedDescriptions_WithParenthesesThatAreNotAGuid()
    {
        // Un movimiento manual cualquiera con parentesis en el texto (no relacionado al bug) no debe
        // matchear el patron: el WHERE exige especificamente un GUID entre parentesis al final.
        int movementId;
        await using (var setup = _fixture.CreateDbContext())
        {
            var movement = NewMovement("Ajuste manual de caja (diferencia de arqueo)");
            setup.ManualCashMovements.Add(movement);
            await setup.SaveChangesAsync();
            movementId = movement.Id;
        }

        await using (var migrate = _fixture.CreateDbContext())
        {
            await ExecuteMigrationSqlVerbatimAsync(migrate, RefundFixSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var untouched = await verify.ManualCashMovements.AsNoTracking().FirstAsync(m => m.Id == movementId);
        Assert.Equal("Ajuste manual de caja (diferencia de arqueo)", untouched.Description);
    }

    [Fact]
    public async Task Up_RunTwice_IsIdempotent_ForOperatorRefundDescription()
    {
        int movementId;
        var guid = Guid.NewGuid();
        await using (var setup = _fixture.CreateDbContext())
        {
            var movement = NewMovement($"Devolucion del operador AVIS TURISMO ({guid})");
            setup.ManualCashMovements.Add(movement);
            await setup.SaveChangesAsync();
            movementId = movement.Id;
        }

        await using (var migrate = _fixture.CreateDbContext())
        {
            await ExecuteMigrationSqlVerbatimAsync(migrate, RefundFixSql);
            // Segunda corrida: ya no queda ningun GUID entre parentesis, el WHERE no matchea nada.
            await ExecuteMigrationSqlVerbatimAsync(migrate, RefundFixSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var repaired = await verify.ManualCashMovements.AsNoTracking().FirstAsync(m => m.Id == movementId);
        Assert.Equal("Devolucion del operador AVIS TURISMO", repaired.Description);
    }

    // ===================== Fix 2: "Retiro credito cliente {guid} ({Kind})" =====================

    private async Task<(int MovementId, int WithdrawalId)> SeedLegacyWithdrawalMovementAsync(
        string kindLabel, string? customerFullName)
    {
        await using var setup = _fixture.CreateDbContext();

        var customer = new Customer { FullName = customerFullName ?? string.Empty };
        setup.Customers.Add(customer);
        await setup.SaveChangesAsync();

        var entry = new ClientCreditEntry
        {
            CustomerId = customer.Id,
            Currency = Monedas.ARS,
            CreditedAmount = 5000m,
            RemainingBalance = 0m,
        };
        setup.ClientCreditEntries.Add(entry);
        await setup.SaveChangesAsync();

        var kind = kindLabel switch
        {
            "PhysicalCash" => WithdrawalKind.PhysicalCash,
            "Transfer" => WithdrawalKind.Transfer,
            "ReversedToOperator" => WithdrawalKind.ReversedToOperator,
            _ => throw new ArgumentOutOfRangeException(nameof(kindLabel)),
        };
        var withdrawal = new ClientCreditWithdrawal
        {
            ClientCreditEntryId = entry.Id,
            Amount = 5000m,
            Kind = kind,
            ExecutedByUserId = "user-test",
            ExecutedByUserName = "Cajero de Prueba",
        };
        setup.ClientCreditWithdrawals.Add(withdrawal);
        await setup.SaveChangesAsync();

        var legacyGuid = Guid.NewGuid();
        var movement = NewMovement($"Retiro credito cliente {legacyGuid} ({kindLabel})");
        movement.ClientCreditWithdrawalId = withdrawal.Id;
        setup.ManualCashMovements.Add(movement);
        await setup.SaveChangesAsync();

        return (movement.Id, withdrawal.Id);
    }

    [Fact]
    public async Task Up_RewritesLegacyWithdrawalDescription_PhysicalCash_WithCustomerName()
    {
        var (movementId, _) = await SeedLegacyWithdrawalMovementAsync("PhysicalCash", "Juan Perez");

        await using (var migrate = _fixture.CreateDbContext())
        {
            await ExecuteMigrationSqlVerbatimAsync(migrate, WithdrawalFixSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var repaired = await verify.ManualCashMovements.AsNoTracking().FirstAsync(m => m.Id == movementId);
        Assert.Equal("Retiro de saldo a favor de Juan Perez en efectivo", repaired.Description);
    }

    [Fact]
    public async Task Up_RewritesLegacyWithdrawalDescription_Transfer_WithCustomerName()
    {
        var (movementId, _) = await SeedLegacyWithdrawalMovementAsync("Transfer", "Maria Gomez");

        await using (var migrate = _fixture.CreateDbContext())
        {
            await ExecuteMigrationSqlVerbatimAsync(migrate, WithdrawalFixSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var repaired = await verify.ManualCashMovements.AsNoTracking().FirstAsync(m => m.Id == movementId);
        Assert.Equal("Retiro de saldo a favor de Maria Gomez por transferencia", repaired.Description);
    }

    [Fact]
    public async Task Up_RewritesLegacyWithdrawalDescription_ReversedToOperator_WithCustomerName()
    {
        var (movementId, _) = await SeedLegacyWithdrawalMovementAsync("ReversedToOperator", "Carlos Diaz");

        await using (var migrate = _fixture.CreateDbContext())
        {
            await ExecuteMigrationSqlVerbatimAsync(migrate, WithdrawalFixSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var repaired = await verify.ManualCashMovements.AsNoTracking().FirstAsync(m => m.Id == movementId);
        Assert.Equal("Devolucion al operador del saldo a favor de Carlos Diaz", repaired.Description);
    }

    [Fact]
    public async Task Up_RewritesLegacyWithdrawalDescription_FallsBackToGenericLabel_WhenCustomerHasNoName()
    {
        var (movementId, _) = await SeedLegacyWithdrawalMovementAsync("PhysicalCash", customerFullName: "");

        await using (var migrate = _fixture.CreateDbContext())
        {
            await ExecuteMigrationSqlVerbatimAsync(migrate, WithdrawalFixSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var repaired = await verify.ManualCashMovements.AsNoTracking().FirstAsync(m => m.Id == movementId);
        Assert.Equal("Retiro de saldo a favor del cliente en efectivo", repaired.Description);
    }

    [Fact]
    public async Task Up_LeavesAlreadyCleanWithdrawalDescription_Untouched()
    {
        // Fila creada con el codigo NUEVO (post 2026-07-27): ya nace limpia. La migracion no debe tocarla,
        // ni siquiera si el withdrawal enlazado tiene un Kind DISTINTO al que el texto sugeriria (prueba de
        // que el WHERE filtra por el TEXTO viejo, no reescribe cualquier fila que tenga un withdrawal atado).
        var (movementId, withdrawalId) = await SeedLegacyWithdrawalMovementAsync("Transfer", "Ana Lopez");

        await using (var setup = _fixture.CreateDbContext())
        {
            var movement = await setup.ManualCashMovements.FirstAsync(m => m.Id == movementId);
            movement.Description = "Retiro de saldo a favor de Ana Lopez por transferencia";
            await setup.SaveChangesAsync();
        }

        await using (var migrate = _fixture.CreateDbContext())
        {
            await ExecuteMigrationSqlVerbatimAsync(migrate, WithdrawalFixSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var untouched = await verify.ManualCashMovements.AsNoTracking().FirstAsync(m => m.Id == movementId);
        Assert.Equal("Retiro de saldo a favor de Ana Lopez por transferencia", untouched.Description);
    }

    [Fact]
    public async Task Up_RunTwice_IsIdempotent_ForWithdrawalDescription()
    {
        var (movementId, _) = await SeedLegacyWithdrawalMovementAsync("PhysicalCash", "Sofia Ruiz");

        await using (var migrate = _fixture.CreateDbContext())
        {
            await ExecuteMigrationSqlVerbatimAsync(migrate, WithdrawalFixSql);
            // Segunda corrida: el texto ya quedo limpio, el WHERE no matchea el patron viejo.
            await ExecuteMigrationSqlVerbatimAsync(migrate, WithdrawalFixSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var repaired = await verify.ManualCashMovements.AsNoTracking().FirstAsync(m => m.Id == movementId);
        Assert.Equal("Retiro de saldo a favor de Sofia Ruiz en efectivo", repaired.Description);
    }

    [Fact]
    public async Task Up_DoesNotTouchWithdrawalMovement_WhenNoClientCreditWithdrawalIsLinked()
    {
        // Fila con el TEXTO viejo pero sin FK enlazada (dato corrupto/imposible en el flujo normal, pero
        // el UPDATE debe ser defensivo: mejor no tocar una fila de la que no puede reconstruir el nombre
        // ni el Kind con certeza, que adivinar).
        int movementId;
        var legacyGuid = Guid.NewGuid();
        await using (var setup = _fixture.CreateDbContext())
        {
            var movement = NewMovement($"Retiro credito cliente {legacyGuid} (PhysicalCash)");
            movement.ClientCreditWithdrawalId = null;
            setup.ManualCashMovements.Add(movement);
            await setup.SaveChangesAsync();
            movementId = movement.Id;
        }

        await using (var migrate = _fixture.CreateDbContext())
        {
            await ExecuteMigrationSqlVerbatimAsync(migrate, WithdrawalFixSql);
        }

        await using var verify = _fixture.CreateDbContext();
        var untouched = await verify.ManualCashMovements.AsNoTracking().FirstAsync(m => m.Id == movementId);
        Assert.Equal($"Retiro credito cliente {legacyGuid} (PhysicalCash)", untouched.Description);
    }
}
