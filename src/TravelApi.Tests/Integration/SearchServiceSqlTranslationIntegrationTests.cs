using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Hotfix 2026-07-25: el buscador global (Ctrl+K) tiraba 500 en PROD para CUALQUIER consulta.
/// Causa: <c>p.Status.ToString()</c> dentro del Where de Payments — Payment.Status ya ES string,
/// el ToString() era un no-op que Npgsql no puede traducir ("Translation of method 'object.ToString'
/// failed"). El proveedor InMemory ejecuta la expresion como C# directo y la tolera, por eso la
/// suite unit dio verde con el bug vivo (misma trampa que el hallazgo #47 de la Tanda 5).
///
/// <para>Este test es la red REAL: ejecuta <see cref="SearchService.SearchAsync"/> contra Postgres,
/// lo que obliga a EF a TRADUCIR las tres consultas (customers, reservas, payments) a SQL. No hace
/// falta sembrar datos: si alguna expresion no es traducible, ToListAsync explota aunque las tablas
/// esten vacias — que es exactamente como se rompio PROD.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SearchServiceSqlTranslationIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public SearchServiceSqlTranslationIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SearchAsync_ComoAdmin_TraduceLasTresConsultasASqlSinExplotar()
    {
        await using var ctx = _fixture.CreateDbContext();

        // Un user Admin bypassea todos los recortes de permisos, asi que las TRES ramas
        // (customers, reservas y payments) se compilan y ejecutan contra Postgres.
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "admin-integration-test"),
                    new Claim(ClaimTypes.Role, "Admin"),
                },
                authenticationType: "Test")),
        };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var service = new SearchService(ctx, permissionResolver: null, httpContextAccessor: accessor);

        // Si alguna de las tres consultas tiene una expresion no traducible, esto tira
        // InvalidOperationException aca mismo (el bug de PROD). Con tablas vacias el
        // resultado esperado es simplemente "sin resultados", nunca una excepcion.
        var result = await service.SearchAsync("prueba", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Customers);
        Assert.Empty(result.Reservas);
        Assert.Empty(result.Payments);
    }
}
