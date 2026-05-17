using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Authorization;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.2.0 v3 — MR-04 (2026-05-17): valida que <see cref="OwnershipResolver"/>
/// resuelve correctamente las entidades nuevas del modulo de cancelacion
/// (<c>BookingCancellation</c> y <c>ClientCreditEntry</c>) cuando corre contra
/// un Postgres real, no InMemory.
///
/// **Por que vale la pena un test con Postgres real**:
///  - Los <c>OwnershipResolverTests</c> unit usan EF InMemory, que NO traduce
///    los joins LINQ a SQL — solo navega objetos en memoria. Eso pasa si el
///    resolver tiene un bug en como arma el Select de la navigation chain
///    (ej. <c>.BookingCancellation.Reserva.ResponsibleUserId</c>): InMemory
///    sigue las references in-process y devuelve true aunque el SQL real
///    fallaria con NullReferenceException o devolveria null.
///  - Postgres aplica las FK constraints reales, los CHECK constraints del
///    modulo, y la traduccion completa LINQ -> SQL. Si la query del resolver
///    se rompe (ej. una nav property opcional), Postgres lo expone.
///
/// **Categoria Integration**: solo corre si Docker esta arriba. La fixture
/// reusa la imagen <c>postgres:16</c> compartida con el resto de los
/// integration tests del modulo (un solo container para toda la clase).
/// </summary>
[Trait("Category", "Integration")]
public sealed class OwnershipResolverPostgresTests : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public OwnershipResolverPostgresTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Inserta filas minimas en <c>AspNetUsers</c> para satisfacer la FK
    /// <c>FK_TravelFiles_AspNetUsers_ResponsibleUserId</c>.
    ///
    /// Por que SQL crudo en vez de UserManager:
    ///  - La fixture compartida no levanta el stack de Identity (UserManager,
    ///    SignInManager, etc.). Pedir esos services rompe el aislamiento de la
    ///    fixture y obligaria a cargar TravelApi/Program.cs completo.
    ///  - Solo necesitamos que la FILA exista — el resolver compara strings, no
    ///    autentica. <c>NormalizedUserName</c> y <c>SecurityStamp</c> son
    ///    columnas NOT NULL del esquema Identity, asi que las completamos con
    ///    valores deterministicos para que el insert pase.
    ///  - <c>ON CONFLICT DO NOTHING</c> hace el helper idempotente: si dos tests
    ///    de la misma clase reusan el mismo userId, no se rompe.
    /// </summary>
    private static async Task SeedAspNetUserAsync(AppDbContext ctx, string userId)
    {
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetUsers"
              ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
               "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp",
               "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled",
               "AccessFailedCount", "FullName", "IsActive")
            VALUES
              ({userId}, {userId}, {userId.ToUpperInvariant()},
               {userId + "@test.local"}, {(userId + "@test.local").ToUpperInvariant()},
               true, 'test-hash', {Guid.NewGuid().ToString()}, {Guid.NewGuid().ToString()},
               false, false, false,
               0, {"Test User " + userId}, true)
            ON CONFLICT ("Id") DO NOTHING;
            """);
    }

    // ============== BookingCancellation -> Reserva.ResponsibleUserId ==============

    [Fact]
    public async Task BookingCancellation_OwnershipResolver_resolvesViaPostgresJoinToReserva()
    {
        // ARRANGE: creo customer + supplier + reserva CON ResponsibleUserId
        // (la fixture base no asigna responsable, asi que armo el seed aca).
        const string ownerUserId = "user-123";

        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, ownerUserId);
        var (custId, supId, _, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        // Actualizo la reserva creada por SeedBaseAsync para setearle el owner.
        // Hago un UPDATE directo en vez de modificar SeedBaseAsync para no
        // tocar todos los tests de integracion existentes.
        var reserva = await ctx.Reservas.FindAsync(1); // SeedBaseAsync resetea identity
        Assert.NotNull(reserva);
        reserva!.ResponsibleUserId = ownerUserId;
        await ctx.SaveChangesAsync();

        // Creo el BookingCancellation linkeado a la reserva.
        var bc = CancellationTestData.NewCancellation(custId, supId, reserva.Id, invId);
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        // El resolver requiere SU PROPIO context (mismo container, distinta
        // session) para imitar el escenario request real: cada request crea
        // un AppDbContext scoped, el resolver corre con ese context, no con
        // el que sembro datos.
        await using var resolverCtx = _fixture.CreateDbContext();
        var resolver = new OwnershipResolver(resolverCtx);

        // ACT + ASSERT: owner exacto -> true.
        var isOwner = await resolver.IsOwnerAsync(
            ownerUserId,
            OwnedEntity.BookingCancellation,
            bc.PublicId.ToString(),
            CancellationToken.None);
        Assert.True(isOwner);

        // Otro user -> false (sin importar permisos globales — eso lo decide el authz pipeline).
        var isOther = await resolver.IsOwnerAsync(
            "user-OTRO",
            OwnedEntity.BookingCancellation,
            bc.PublicId.ToString(),
            CancellationToken.None);
        Assert.False(isOther);
    }

    // ============== ClientCreditEntry -> BookingCancellation -> Reserva.ResponsibleUserId ==============

    [Fact]
    public async Task ClientCreditEntry_OwnershipResolver_resolvesViaPostgresJoinChain()
    {
        // ARRANGE: armo la cadena completa porque ClientCreditEntry requiere
        // FK a OperatorRefundAllocation (NOT NULL en BD). El resolver navega
        // solo BookingCancellation.Reserva, pero el insert necesita toda la
        // cadena de FKs para no violar integridad referencial.
        const string ownerUserId = "user-456";

        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, ownerUserId);
        var (custId, supId, _, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.NotNull(reserva);
        reserva!.ResponsibleUserId = ownerUserId;
        await ctx.SaveChangesAsync();

        // BookingCancellation (Drafted: FiscalSnapshot completo, pero el status
        // Drafted no obliga al CHECK chk_BC_fiscalsnapshot_consistent).
        var bc = CancellationTestData.NewCancellation(custId, supId, reserva.Id, invId);
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        // OperatorRefundReceived + Allocation que originan el credito.
        var refund = new OperatorRefundReceived
        {
            SupplierId = supId,
            ReceivedAt = DateTime.UtcNow,
            ReceivedAmount = 800m,
            AllocatedAmount = 800m,
            Method = "Transfer",
            Currency = "ARS",
            ExchangeRateAtReceipt = 1m,
            ReceivedByUserId = "tester",
            ReceivedByUserName = "Tester",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        var allocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id,
            BookingCancellationId = bc.Id,
            GrossAmount = 800m,
            NetAmount = 800m,
            CreatedByUserId = "tester",
        };
        ctx.OperatorRefundAllocations.Add(allocation);
        await ctx.SaveChangesAsync();

        // ClientCreditEntry: este es el target del test.
        var entry = new ClientCreditEntry
        {
            CustomerId = custId,
            OperatorRefundAllocationId = allocation.Id,
            BookingCancellationId = bc.Id,
            CreditedAmount = 800m,
            RemainingBalance = 800m,
        };
        ctx.ClientCreditEntries.Add(entry);
        await ctx.SaveChangesAsync();

        // ACT: resolver con un context nuevo (request-like).
        await using var resolverCtx = _fixture.CreateDbContext();
        var resolver = new OwnershipResolver(resolverCtx);

        var isOwner = await resolver.IsOwnerAsync(
            ownerUserId,
            OwnedEntity.ClientCreditEntry,
            entry.PublicId.ToString(),
            CancellationToken.None);
        Assert.True(isOwner);

        var isOther = await resolver.IsOwnerAsync(
            "user-OTRO",
            OwnedEntity.ClientCreditEntry,
            entry.PublicId.ToString(),
            CancellationToken.None);
        Assert.False(isOther);
    }
}
