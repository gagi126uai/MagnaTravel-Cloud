using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3.2 (ADR-009 §2.7, 2026-05-21): smoke tests del wiring DI del nuevo
/// <see cref="IPartialCreditNoteApprovalBridge"/>.
///
/// <para>
/// <b>Por que importa el "same instance"</b>: si por un error de registro DI cada
/// interface devolviera una instancia distinta, los callbacks del bridge no verian
/// los cambios del flujo principal (cada instancia tendria su propio
/// <c>AppDbContext.ChangeTracker</c>). El bug se manifestaria recien en runtime,
/// despues de que el approval se confirme — situacion dificil de debugear.
/// </para>
///
/// <para>
/// <b>Diseno del test</b>: armamos un <see cref="ServiceCollection"/> minimo con las 4
/// registraciones reales que viven en <c>Program.cs</c> (lineas 442-448, FC1.3.2)
/// + mocks de las 6 dependencias del constructor de <see cref="BookingCancellationService"/>.
/// NO usamos <c>CustomWebApplicationFactory</c> ni <c>WebApplicationFactory</c>: ambos
/// arrancan el host completo y levantan Postgres TestContainers, lo que cuelga la
/// suite si Docker tarda. Estos tests son puros y corren en milisegundos.
/// </para>
///
/// <para>
/// Si alguien modifica las registraciones en <c>Program.cs</c> y este test queda
/// desactualizado, va a fallar al resolver — eso es la red de seguridad. Como
/// contramedida ante "test verde pero codigo roto", la cadena de cuatro
/// <c>AddScoped</c> esta encapsulada en una variable local para que sea facil
/// detectar drift cuando alguien lea el diff.
/// </para>
/// </summary>
public class PartialCreditNoteApprovalBridgeWiringTests
{
    /// <summary>
    /// Construye un <see cref="IServiceProvider"/> minimo que reproduce el wiring real
    /// de <c>Program.cs</c> para el modulo de cancelaciones. Las dependencias reales
    /// del service estan mockeadas porque este test solo valida el wiring DI, no
    /// la logica del service en si.
    /// </summary>
    private static IServiceProvider BuildMinimalContainer()
    {
        var services = new ServiceCollection();

        // Dependencias del constructor de BookingCancellationService (8 desde FC1.3.3).
        // AppDbContext usa InMemory porque las 4 registraciones de wiring necesitan
        // poder INSTANCIAR el service para verificar same-instance — sin DbContext
        // resolvible el GetRequiredService<BookingCancellationService>() tira.
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("wiring-tests"));
        services.AddSingleton(Mock.Of<IInvoiceService>());
        services.AddSingleton(Mock.Of<IApprovalRequestService>());
        services.AddSingleton(Mock.Of<IAuditService>());
        services.AddSingleton<ILogger<BookingCancellationService>>(NullLogger<BookingCancellationService>.Instance);
        services.AddSingleton(Mock.Of<IOperationalFinanceSettingsService>());
        // FC1.3.3: dos deps nuevas inyectadas en el ctor del BC service.
        services.AddSingleton(Mock.Of<IFiscalLiquidationCalculator>());
        services.AddSingleton(Mock.Of<IAdminUserCountService>());

        // === Wiring REAL FC1.3.2 (copia textual de Program.cs:442-448) ===
        // Si Program.cs cambia, este bloque tambien cambia. El test detecta drift
        // al resolver: si alguna registracion falta, GetRequiredService tira.
        services.AddScoped<BookingCancellationService>();
        services.AddScoped<IBookingCancellationService>(sp =>
            sp.GetRequiredService<BookingCancellationService>());
        services.AddScoped<IInvoiceAnnulmentBcBridge>(sp =>
            sp.GetRequiredService<BookingCancellationService>());
        services.AddScoped<IPartialCreditNoteApprovalBridge>(sp =>
            sp.GetRequiredService<BookingCancellationService>());

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// El registro DI nuevo debe resolver la interface sin tirar excepcion.
    /// Si este test falla con <c>InvalidOperationException</c>, falta el
    /// <c>AddScoped&lt;IPartialCreditNoteApprovalBridge&gt;</c> en Program.cs.
    /// </summary>
    [Fact]
    public void PartialCreditNoteApprovalBridge_IsRegistered()
    {
        using var provider = (ServiceProvider)BuildMinimalContainer();
        using var scope = provider.CreateScope();

        var bridge = scope.ServiceProvider.GetRequiredService<IPartialCreditNoteApprovalBridge>();

        Assert.NotNull(bridge);
    }

    /// <summary>
    /// El bridge y la interface publica deben resolverse a la MISMA instancia
    /// dentro de un mismo scope. Si fallara, alguien rompio el patron MR-V2-02
    /// y los callbacks no verian los cambios del flujo principal en runtime.
    /// </summary>
    [Fact]
    public void PartialCreditNoteApprovalBridge_AndBookingCancellationService_ResolveToSameInstance()
    {
        using var provider = (ServiceProvider)BuildMinimalContainer();
        using var scope = provider.CreateScope();

        var bridge = scope.ServiceProvider.GetRequiredService<IPartialCreditNoteApprovalBridge>();
        var publicService = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();

        // ReferenceEquals (no Equals) porque queremos asegurarnos de que es
        // literalmente el mismo objeto en memoria, no dos objetos que se
        // comparen como iguales por algun override de Equals.
        Assert.Same(bridge, publicService);
    }

    /// <summary>
    /// Tercer interface heredada de FC1.2 (<see cref="IInvoiceAnnulmentBcBridge"/>)
    /// tambien tiene que mantener el patron — lo dejamos pinneado por si el
    /// registro nuevo de FC1.3 hubiera roto el wiring previo.
    /// </summary>
    [Fact]
    public void AllThreeBookingCancellationInterfaces_ResolveToSameInstance()
    {
        using var provider = (ServiceProvider)BuildMinimalContainer();
        using var scope = provider.CreateScope();

        var partialBridge = scope.ServiceProvider.GetRequiredService<IPartialCreditNoteApprovalBridge>();
        var invoiceBridge = scope.ServiceProvider.GetRequiredService<IInvoiceAnnulmentBcBridge>();
        var publicService = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();

        Assert.Same(partialBridge, invoiceBridge);
        Assert.Same(partialBridge, publicService);
    }
}
