using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Obra "numero de reserva sin F-" (2026-08-11): bug de atomicidad corregido en
/// <c>ReservaService.GenerateNumeroReservaAsync</c>. La version vieja hacia SELECT -&gt; sumar en memoria
/// -&gt; SaveChanges sin ningun candado; dos altas casi simultaneas podian leer el mismo <c>LastValue</c> de
/// <c>BusinessSequences</c> y terminar generando el MISMO numero de reserva.
///
/// <para><b>Por que Postgres real</b>: el fix es un <c>INSERT ... ON CONFLICT ... DO UPDATE ... RETURNING</c>
/// (SQL crudo). El provider InMemory no lo ejecuta — solo corre el camino de candado en memoria, que NO
/// prueba la colision real que este test viene a cubrir.</para>
///
/// <para><c>GenerateNumeroReservaAsync</c> es <c>internal</c> (no <c>private</c>) puntualmente para que este
/// test pueda llamarlo directo, sin tener que levantar todo <c>CreateReservaAsync</c> (que ademas pide
/// Customer/PayerId). <c>InternalsVisibleTo("TravelApi.Tests")</c> ya esta configurado en el csproj.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReservaNumberSequenceConcurrencyIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;
    private readonly Mock<IMapper> _mapperMock = new();
    private readonly Mock<IOperationalFinanceSettingsService> _settingsMock = new();

    public ReservaNumberSequenceConcurrencyIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
        _settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetDatabaseAsync();

        // La fixture no trunca "BusinessSequences" (ningun otro test del modulo de cancelacion la usa).
        // La limpiamos aca para que este test parta siempre de "sin secuencia previa para el año actual".
        await using var ctx = _fixture.CreateDbContext();
        await ctx.Database.ExecuteSqlRawAsync("""TRUNCATE TABLE "BusinessSequences" RESTART IDENTITY;""");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        // No lo usa GenerateNumeroReservaAsync — solo lo pide el constructor de ReservaService.
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private ReservaService BuildService(AppDbContext context)
        => new(context, _mapperMock.Object, _settingsMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

    [Fact]
    public async Task GenerateNumeroReservaAsync_TwoConcurrentCalls_NeverProduceTheSameNumber()
    {
        // ACT: dos "altas" casi simultaneas, cada una con su PROPIO AppDbContext (dos conexiones
        // distintas a la misma base, igual que dos usuarios creando una reserva al mismo tiempo).
        await using var ctxA = _fixture.CreateDbContext();
        await using var ctxB = _fixture.CreateDbContext();

        var (numeroA, numeroB) = await WhenAllAsync(
            BuildService(ctxA).GenerateNumeroReservaAsync(CancellationToken.None),
            BuildService(ctxB).GenerateNumeroReservaAsync(CancellationToken.None));

        // ASSERT: si el UPDATE no fuera atomico, las dos conexiones podrian haber leido el mismo
        // LastValue y generar el mismo numero -> una de las dos altas reales chocaria contra el
        // indice unico de FileNumber con un error crudo de base de datos.
        Assert.NotEqual(numeroA, numeroB);
    }

    [Fact]
    public async Task GenerateNumeroReservaAsync_TenConcurrentCalls_ProduceTenDistinctConsecutiveNumbers()
    {
        const int concurrentCalls = 10;

        var contexts = Enumerable.Range(0, concurrentCalls)
            .Select(_ => _fixture.CreateDbContext())
            .ToArray();

        try
        {
            var tasks = contexts
                .Select(ctx => BuildService(ctx).GenerateNumeroReservaAsync(CancellationToken.None))
                .ToArray();

            var numeros = await Task.WhenAll(tasks);

            // Distintos entre si (la garantia critica: nunca dos altas se llevan el mismo numero).
            Assert.Equal(concurrentCalls, numeros.Distinct().Count());

            // Ademas, cubren exactamente el rango 1000..1009 sin huecos ni saltos: el UPDATE
            // atomico reparte el correlativo uno por uno, no importa el orden en que cada
            // conexion termine de esperar su turno.
            var year = ArgentinaTime.GetArgentinaNow().Year;
            var valoresEsperados = Enumerable.Range(1000, concurrentCalls).Select(n => $"{year}-{n}");
            Assert.Equal(valoresEsperados.OrderBy(v => v), numeros.OrderBy(v => v));
        }
        finally
        {
            foreach (var ctx in contexts)
            {
                await ctx.DisposeAsync();
            }
        }
    }

    private static async Task<(T First, T Second)> WhenAllAsync<T>(Task<T> first, Task<T> second)
    {
        await Task.WhenAll(first, second);
        return (first.Result, second.Result);
    }
}
