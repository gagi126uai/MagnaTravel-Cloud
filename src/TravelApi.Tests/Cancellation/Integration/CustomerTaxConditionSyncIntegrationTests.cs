using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// (2026-07-17) Reproduce, contra Postgres real, el bug real reportado por el dueño: edito la condicion
/// fiscal de un cliente desde la ficha y la emision de una devolucion seguia diciendo "completa la
/// condicion fiscal en la ficha del cliente".
///
/// <para><b>El escenario exacto</b>: un cliente creado ANTES del fix, con el codigo AFIP nunca cargado
/// (<c>TaxConditionId=null</c>) y el texto en el default de siempre (<c>TaxCondition="Consumidor
/// Final"</c>). El vendedor abre la ficha, elige "Responsable Inscripto" en el desplegable y guarda. El
/// PUT que dispara <c>CustomerFormModal.jsx</c> SOLO manda <c>taxConditionId</c> — nunca el texto — asi
/// que este test arma el mismo <c>Customer</c> de entrada que <c>CustomersController.MapCustomer</c>
/// construiria a partir de ESE payload exacto (texto <c>null</c>, codigo 1).</para>
///
/// <para><b>Por que Postgres real y no InMemory</b>: lo que hay que probar es el ROUND-TRIP completo
/// (guardar -&gt; releer de la base) y que el segundo consumidor del dato
/// (<see cref="BookingCancellationService.ResolveServerSideTaxIdentity"/>, el mismo camino que usa una
/// anulacion real) ya no se bloquee con INV-118 despues de la edicion. Un test InMemory no demuestra que
/// el dato quedo bien escrito en la columna real.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CustomerTaxConditionSyncIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public CustomerTaxConditionSyncIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpdateCustomerAsync_PutSoloConTaxConditionId_DerivaElTextoYDestrabaResolveServerSideTaxIdentity()
    {
        await using var ctx = _fixture.CreateDbContext();

        // Estado previo al fix: el codigo nunca se cargo, el texto quedo en el default de siempre.
        var customer = new Customer
        {
            FullName = "Cliente sin condicion cargada",
            TaxConditionId = null,
            TaxCondition = "Consumidor Final",
            IsActive = true,
        };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();
        var customerId = customer.Id;
        ctx.ChangeTracker.Clear();

        var service = new CustomerService(ctx, new FinancePositionService(ctx));

        // Payload IDENTICO al que arma CustomersController.MapCustomer a partir del PUT real de
        // CustomerFormModal.jsx: solo taxConditionId (1 = Responsable Inscripto, elegido en el
        // desplegable). El campo taxCondition (texto) NUNCA viaja desde ese formulario.
        var incoming = new Customer
        {
            Id = customerId,
            FullName = customer.FullName,
            TaxId = null,
            TaxCondition = null,
            TaxConditionId = 1,
            IsActive = true,
        };

        await service.UpdateCustomerAsync(customerId, incoming, CancellationToken.None);

        // 1) El round-trip contra la base real deja los DOS campos coherentes (antes del fix, el codigo
        // quedaba en 1 pero el texto seguia en "Consumidor Final" para siempre).
        await using var verifyCtx = _fixture.CreateDbContext();
        var reloaded = await verifyCtx.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.Equal(1, reloaded.TaxConditionId);
        Assert.Equal("Responsable Inscripto", reloaded.TaxCondition);

        // 2) La devolucion ya no se bloquea: mismo helper que usa una anulacion real
        // (BookingCancellationService.ConfirmAsync) para resolver la identidad fiscal server-side.
        var afipSettings = new AfipSettings { Cuit = 20111111112, TaxCondition = "Monotributo" };
        var supplier = new Supplier { Name = "Operador Test", TaxCondition = "IVA_RESP_INSCRIPTO" };

        var identity = BookingCancellationService.ResolveServerSideTaxIdentity(afipSettings, supplier, reloaded);

        Assert.Equal("RESPONSABLE_INSCRIPTO", identity.CustomerTaxCondition);
    }
}
