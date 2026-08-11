using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tanda 1 rediseño del listado de Reservas (2026-08-04, plan A1/A2/A3, `docs/ux/maquetas/
/// 2026-08-03-reservas-rediseno.html`). Tres cosas nuevas del backend, cada una con su propio
/// grupo de tests:
///
/// <para>A1 — el resumen del listado (KPIs) deja de mezclar pesos y dolares en un escalar unico
/// (violaba P-3⭐) y pasa a un desglose <c>VendidoPorMoneda</c>/<c>PorCobrarPorMoneda</c>.</para>
///
/// <para>A2 — cada fila trae <c>Destino</c>, derivado de los servicios cargados (vuelo/hotel/
/// paquete/generico), sin repetidos y en el orden en que se cargaron.</para>
///
/// <para>A3 — con <c>GlobalSearch=true</c> Y texto en el buscador, la pestaña (view) y el periodo
/// (mes/fechas) dejan de filtrar: tanto en las filas como en el resumen/contadores. El flag es
/// EXPLICITO (fix B1 de review, T-8): sin el, el texto de busqueda solo no alcanza — sigue
/// aplicando pestaña y periodo como siempre (lo necesita PaymentsByReservaPage.jsx, que manda
/// view+search+periodo juntos y espera que la pestaña filtre).</para>
/// </summary>
public class ReservaListTanda1Tests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static ReservaService CreateService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        const string userId = "tester";
        var accessor = BuildHttpContextAccessor(userId, "Admin");
        var resolver = BuildResolver(userId, Permissions.CobranzasSeeCost, Permissions.ReservasViewAll);

        return new ReservaService(
            context,
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            settings.Object,
            BuildUserManager(),
            NullLogger<ReservaService>.Instance,
            resolver,
            accessor);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

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

    // =====================================================================
    // A1 — resumen por moneda, nunca mezclado
    // =====================================================================

    [Fact]
    public async Task Summary_SeparatesArsAndUsd_NeverMixesCurrencies()
    {
        using var context = CreateContext();
        // Reserva 1 vende en ARS, Reserva 2 vende en USD. Las dos CONFIRMADAS: asi cuentan a la vez
        // en el "vendido" (venta firme, decision del dueño 2026-08-11) y en el "por cobrar" (cartera
        // activa) — el foco de este test es que las monedas no se mezclen, no el alcance de estados.
        context.Reservas.Add(new Reserva { Id = 1, Name = "R1", NumeroReserva = "R-1", Status = EstadoReserva.Confirmed, TotalSale = 1000m });
        context.Reservas.Add(new Reserva { Id = 2, Name = "R2", NumeroReserva = "R-2", Status = EstadoReserva.Confirmed, TotalSale = 500m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", TotalSale = 1000m, ConfirmedSale = 1000m, Balance = 300m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 2, Currency = "USD", TotalSale = 500m, ConfirmedSale = 500m, Balance = 200m });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var page = await service.GetReservasAsync(new ReservaListQuery(), CancellationToken.None);
        var summary = page.Summary;

        Assert.Equal(2, summary.VendidoPorMoneda.Count);
        Assert.Equal(1000m, summary.VendidoPorMoneda.Single(l => l.Currency == "ARS").Amount);
        Assert.Equal(500m, summary.VendidoPorMoneda.Single(l => l.Currency == "USD").Amount);

        Assert.Equal(2, summary.PorCobrarPorMoneda.Count);
        Assert.Equal(300m, summary.PorCobrarPorMoneda.Single(l => l.Currency == "ARS").Amount);
        Assert.Equal(200m, summary.PorCobrarPorMoneda.Single(l => l.Currency == "USD").Amount);
    }

    [Fact]
    public async Task Summary_PorCobrar_ExcludesClosed_ButVendidoIncludesIt()
    {
        using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, Name = "Confirmada", NumeroReserva = "R-1", Status = EstadoReserva.Confirmed, TotalSale = 1000m });
        context.Reservas.Add(new Reserva { Id = 2, Name = "Finalizada", NumeroReserva = "R-2", Status = EstadoReserva.Closed, TotalSale = 1000m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", TotalSale = 1000m, Balance = 100m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 2, Currency = "ARS", TotalSale = 1000m, Balance = 100m });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var page = await service.GetReservasAsync(new ReservaListQuery(), CancellationToken.None);
        var summary = page.Summary;

        // VENDIDO (decision del dueño 2026-08-11): las dos vendieron, la Finalizada tambien —
        // el viaje ya paso pero la venta existio. 1000 + 1000.
        var vendidoArs = Assert.Single(summary.VendidoPorMoneda);
        Assert.Equal(2000m, vendidoArs.Amount);

        // POR COBRAR: sigue siendo cartera ACTIVA, y ahi la Finalizada no entra (esa deuda vive en la
        // cuenta corriente del cliente). Solo los 100 de la Confirmada.
        var porCobrarArs = Assert.Single(summary.PorCobrarPorMoneda);
        Assert.Equal(100m, porCobrarArs.Amount);
    }

    [Fact]
    public async Task Summary_Vendido_IgnoresBudgetsAndInManagement()
    {
        // Bug encontrado en la prueba con navegador contra PROD (2026-08-11): un PRESUPUESTO sin
        // confirmar sumaba al "vendido" del listado — la pantalla decia que se habia vendido plata
        // que nadie compro todavia. Decision del dueño: vendido = Confirmada, En viaje o Finalizada.
        using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, Name = "Presupuesto", NumeroReserva = "R-1", Status = EstadoReserva.Budget, TotalSale = 900m });
        context.Reservas.Add(new Reserva { Id = 2, Name = "Cotizacion", NumeroReserva = "R-2", Status = EstadoReserva.Quotation, TotalSale = 800m });
        context.Reservas.Add(new Reserva { Id = 3, Name = "En gestion", NumeroReserva = "R-3", Status = EstadoReserva.InManagement, TotalSale = 700m });
        context.Reservas.Add(new Reserva { Id = 4, Name = "Anulada", NumeroReserva = "R-4", Status = EstadoReserva.Cancelled, TotalSale = 600m });
        context.Reservas.Add(new Reserva { Id = 5, Name = "Perdida", NumeroReserva = "R-5", Status = EstadoReserva.Lost, TotalSale = 500m });
        context.Reservas.Add(new Reserva { Id = 6, Name = "Confirmada", NumeroReserva = "R-6", Status = EstadoReserva.Confirmed, TotalSale = 400m });
        context.Reservas.Add(new Reserva { Id = 7, Name = "En viaje", NumeroReserva = "R-7", Status = EstadoReserva.Traveling, TotalSale = 300m });
        // Cada una con su fila de plata por moneda (el camino normal del resumen).
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", TotalSale = 900m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 2, Currency = "ARS", TotalSale = 800m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 3, Currency = "ARS", TotalSale = 700m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 4, Currency = "ARS", TotalSale = 600m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 5, Currency = "ARS", TotalSale = 500m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 6, Currency = "ARS", TotalSale = 400m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 7, Currency = "ARS", TotalSale = 300m });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var page = await service.GetReservasAsync(new ReservaListQuery(), CancellationToken.None);

        // Solo la Confirmada (400) y la En viaje (300).
        var vendidoArs = Assert.Single(page.Summary.VendidoPorMoneda);
        Assert.Equal(700m, vendidoArs.Amount);
    }

    [Fact]
    public async Task Summary_LegacyReservaWithoutMoneyRows_FallsBackToScalarInArs_WithoutDoubleCounting()
    {
        using var context = CreateContext();
        // Reserva 1: legacy, SIN ninguna fila en ReservaMoneyByCurrency (nunca paso por el
        // persister) — debe entrar por el fallback de escalar asumiendo ARS.
        // Confirmadas las dos: asi entran a la vez al vendido (venta firme) y al por cobrar (activas).
        context.Reservas.Add(new Reserva { Id = 1, Name = "Legacy sin filas", NumeroReserva = "R-1", Status = EstadoReserva.Confirmed, TotalSale = 800m, Balance = 300m });
        // Reserva 2: CON su fila ARS — debe entrar por el camino normal (ReservaMoneyByCurrency).
        context.Reservas.Add(new Reserva { Id = 2, Name = "Con fila ARS", NumeroReserva = "R-2", Status = EstadoReserva.Confirmed, TotalSale = 1000m, Balance = 100m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 2, Currency = "ARS", TotalSale = 1000m, Balance = 100m });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var page = await service.GetReservasAsync(new ReservaListQuery(), CancellationToken.None);
        var summary = page.Summary;

        // 800 (fallback legacy) + 1000 (fila real) = 1800, todo en UNA sola linea ARS (sin doble conteo).
        var vendidoArs = Assert.Single(summary.VendidoPorMoneda);
        Assert.Equal("ARS", vendidoArs.Currency);
        Assert.Equal(1800m, vendidoArs.Amount);

        // 300 (fallback legacy) + 100 (fila real) = 400.
        var porCobrarArs = Assert.Single(summary.PorCobrarPorMoneda);
        Assert.Equal("ARS", porCobrarArs.Currency);
        Assert.Equal(400m, porCobrarArs.Amount);
    }

    [Fact]
    public async Task Summary_WithNothingSoldAndNothingToCollect_ReturnsEmptyLists_NotAZeroLine()
    {
        using var context = CreateContext();
        // Un presupuesto sin confirmar y sin saldo: no es venta (no va al vendido) y no debe nada
        // (no va al por cobrar). Antes este test usaba una Cerrada, pero desde el 2026-08-11 una
        // Finalizada SI cuenta como vendida.
        context.Reservas.Add(new Reserva { Id = 1, Name = "Presupuesto", NumeroReserva = "R-1", Status = EstadoReserva.Budget, TotalSale = 1000m });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var page = await service.GetReservasAsync(new ReservaListQuery(), CancellationToken.None);

        // "Monedas con 0 no viajan": sin nada vendido ni por cobrar, las listas vienen VACIAS.
        Assert.Empty(page.Summary.VendidoPorMoneda);
        Assert.Empty(page.Summary.PorCobrarPorMoneda);
    }

    // =====================================================================
    // A2 — Destino derivado de los servicios
    // =====================================================================

    [Fact]
    public async Task Destino_JoinsMultipleServices_InLoadOrder_WithoutDuplicates()
    {
        using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, Name = "R1", NumeroReserva = "R-1", Status = EstadoReserva.Budget });
        await context.SaveChangesAsync();

        var baseTime = DateTime.UtcNow;
        // Se cargan en este orden: hotel primero (Cancún), despues vuelo con el MISMO destino
        // (no debe duplicarse), despues un paquete con destino distinto (Riviera Maya).
        context.HotelBookings.Add(new HotelBooking { ReservaId = 1, SupplierId = 1, HotelName = "Hotel X", City = "Cancún", CheckIn = baseTime, CheckOut = baseTime.AddDays(3), CreatedAt = baseTime });
        context.FlightSegments.Add(new FlightSegment { ReservaId = 1, SupplierId = 1, DestinationCity = "cancún", DepartureTime = baseTime, CreatedAt = baseTime.AddMinutes(5) });
        context.PackageBookings.Add(new PackageBooking { ReservaId = 1, SupplierId = 1, PackageName = "Paquete Y", Destination = "Riviera Maya", StartDate = baseTime, CreatedAt = baseTime.AddMinutes(10) });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        // View "all": la reserva de prueba esta en Presupuesto (Budget), que el view "active" por
        // defecto NO incluye — este test verifica el calculo de Destino, no el filtro de pestaña.
        var page = await service.GetReservasAsync(new ReservaListQuery { View = "all" }, CancellationToken.None);
        var fila = Assert.Single(page.Items);

        // "cancún" (vuelo) es el MISMO destino que "Cancún" (hotel) sin distinguir mayus/minus: no se repite.
        Assert.Equal("Cancún · Riviera Maya", fila.Destino);
    }

    [Fact]
    public async Task Destino_ReservaSinServicios_QuedaNull()
    {
        using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, Name = "Sin servicios", NumeroReserva = "R-1", Status = EstadoReserva.Budget });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var page = await service.GetReservasAsync(new ReservaListQuery { View = "all" }, CancellationToken.None);
        var fila = Assert.Single(page.Items);

        Assert.Null(fila.Destino);
    }

    [Fact]
    public async Task Destino_ServicioGenerico_UsaDestinoDeLaTarifaVinculada()
    {
        using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, Name = "R1", NumeroReserva = "R-1", Status = EstadoReserva.Budget });
        var tarifa = new Rate { Id = 1, ServiceType = "Aereo", ProductName = "Vuelo tarifario", Destination = "Bariloche" };
        context.Rates.Add(tarifa);
        context.Servicios.Add(new ServicioReserva { ReservaId = 1, RateId = 1, ServiceType = "Aereo", DepartureDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var page = await service.GetReservasAsync(new ReservaListQuery { View = "all" }, CancellationToken.None);
        var fila = Assert.Single(page.Items);

        Assert.Equal("Bariloche", fila.Destino);
    }

    // =====================================================================
    // A2 (fix N4) — servicios anulados no aportan destino
    // =====================================================================

    [Fact]
    public async Task Destino_ExcludesCancelledServices()
    {
        using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, Name = "R1", NumeroReserva = "R-1", Status = EstadoReserva.Budget });
        await context.SaveChangesAsync();

        var baseTime = DateTime.UtcNow;
        // El hotel en Cancún se ANULO (Status=Cancelado); el vuelo a Mendoza sigue vivo.
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = 1,
            SupplierId = 1,
            HotelName = "Hotel Cancún",
            City = "Cancún",
            CheckIn = baseTime,
            CheckOut = baseTime.AddDays(3),
            CreatedAt = baseTime,
            Status = WorkflowStatuses.Cancelado
        });
        context.FlightSegments.Add(new FlightSegment
        {
            ReservaId = 1,
            SupplierId = 1,
            DestinationCity = "Mendoza",
            DepartureTime = baseTime,
            CreatedAt = baseTime.AddMinutes(5),
            Status = "NN"
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var page = await service.GetReservasAsync(new ReservaListQuery { View = "all" }, CancellationToken.None);
        var fila = Assert.Single(page.Items);

        // El hotel anulado NO aporta destino: solo queda el vuelo vivo (Mendoza).
        Assert.Equal("Mendoza", fila.Destino);
    }

    [Fact]
    public async Task Destino_CapsAtFiveDistinctDestinations()
    {
        using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, Name = "R1", NumeroReserva = "R-1", Status = EstadoReserva.Budget });
        await context.SaveChangesAsync();

        var baseTime = DateTime.UtcNow;
        // 6 vuelos con destinos distintos, cargados en orden: solo los primeros 5 deben viajar.
        var ciudades = new[] { "Ciudad A", "Ciudad B", "Ciudad C", "Ciudad D", "Ciudad E", "Ciudad F" };
        for (var i = 0; i < ciudades.Length; i++)
        {
            context.FlightSegments.Add(new FlightSegment
            {
                ReservaId = 1,
                SupplierId = 1,
                DestinationCity = ciudades[i],
                DepartureTime = baseTime,
                CreatedAt = baseTime.AddMinutes(i)
            });
        }
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var page = await service.GetReservasAsync(new ReservaListQuery { View = "all" }, CancellationToken.None);
        var fila = Assert.Single(page.Items);

        Assert.Equal("Ciudad A · Ciudad B · Ciudad C · Ciudad D · Ciudad E", fila.Destino);
    }

    // =====================================================================
    // A3 (fix B1) — GlobalSearch es una señal EXPLICITA, no se deduce del texto
    // =====================================================================

    [Fact]
    public async Task GlobalSearch_True_IgnoresViewFilter_MatchesAcrossAllTabs()
    {
        using var context = CreateContext();
        // Dos reservas del MISMO cliente en pestañas distintas: una En gestion (view "in-management"
        // por defecto de este test) y otra Cerrada (fuera de esa pestaña).
        context.Reservas.Add(new Reserva { Id = 1, Name = "Viaje a Roma", NumeroReserva = "R-1", Status = EstadoReserva.InManagement });
        context.Reservas.Add(new Reserva { Id = 2, Name = "Viaje a Roma bis", NumeroReserva = "R-2", Status = EstadoReserva.Closed });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var query = new ReservaListQuery { View = "in-management", Search = "Roma", GlobalSearch = true };
        var page = await service.GetReservasAsync(query, CancellationToken.None);

        // Con GlobalSearch=true, la pestaña "in-management" queda ignorada: aparecen las DOS reservas.
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task GlobalSearch_True_IgnoresPeriodFilter_MatchesOutsideCreatedRange()
    {
        using var context = CreateContext();
        // Creada hace 2 años: fuera de cualquier filtro de "este mes".
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            Name = "Viaje viejo a Bariloche",
            NumeroReserva = "R-1",
            Status = EstadoReserva.Budget,
            CreatedAt = DateTime.UtcNow.AddYears(-2)
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var query = new ReservaListQuery
        {
            Search = "Bariloche",
            GlobalSearch = true,
            CreatedFrom = DateTime.UtcNow.AddDays(-30),
            CreatedTo = DateTime.UtcNow
        };
        var page = await service.GetReservasAsync(query, CancellationToken.None);

        // Con GlobalSearch=true, el rango de fechas queda ignorado: la reserva vieja SI aparece.
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task GlobalSearch_True_AlsoMakesSummaryCountersIgnoreViewAndSearchFilter()
    {
        using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, Name = "Roma en gestion", NumeroReserva = "R-1", Status = EstadoReserva.InManagement });
        context.Reservas.Add(new Reserva { Id = 2, Name = "Roma cerrada", NumeroReserva = "R-2", Status = EstadoReserva.Closed });
        // Cerrada que NO matchea la busqueda: si el resumen ignorara el texto de busqueda (bug), el
        // contador de "Cerradas" contaria esta tambien y darian 2 en vez de 1.
        context.Reservas.Add(new Reserva { Id = 3, Name = "Praga cerrada", NumeroReserva = "R-3", Status = EstadoReserva.Closed });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var query = new ReservaListQuery { View = "in-management", Search = "Roma", GlobalSearch = true };
        var page = await service.GetReservasAsync(query, CancellationToken.None);

        // Las filas: los DOS resultados de "Roma" (ignora la pestaña "in-management").
        Assert.Equal(2, page.Items.Count);
        // El CONTADOR de la pestaña "Cerradas" del resumen tambien cuenta SOLO los resultados de la
        // busqueda (1 Cerrada que matchea "Roma"), no las 2 Cerradas que hay en toda la base.
        Assert.Equal(1, page.Summary.ClosedCount);
    }

    [Fact]
    public async Task GlobalSearch_True_AlsoMakesVendidoPorMoneda_IgnorePeriod()
    {
        using var context = CreateContext();
        // Creada hace 2 años (fuera de "este mes"), confirmada y con venta.
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            Name = "Viaje viejo a Bariloche",
            NumeroReserva = "R-1",
            Status = EstadoReserva.Confirmed,
            TotalSale = 1000m,
            CreatedAt = DateTime.UtcNow.AddYears(-2)
        });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", TotalSale = 1000m });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var query = new ReservaListQuery
        {
            Search = "Bariloche",
            GlobalSearch = true,
            CreatedFrom = DateTime.UtcNow.AddDays(-30),
            CreatedTo = DateTime.UtcNow
        };
        var page = await service.GetReservasAsync(query, CancellationToken.None);

        // Si el KPI aplicara el periodo (bug), esta linea vendria vacia.
        var vendidoArs = Assert.Single(page.Summary.VendidoPorMoneda);
        Assert.Equal(1000m, vendidoArs.Amount);
    }

    [Fact]
    public async Task NoSearch_StillAppliesViewAndPeriod_SameAsBefore()
    {
        using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, Name = "En gestion", NumeroReserva = "R-1", Status = EstadoReserva.InManagement });
        context.Reservas.Add(new Reserva { Id = 2, Name = "Cerrada", NumeroReserva = "R-2", Status = EstadoReserva.Closed });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        // Sin texto de busqueda: el comportamiento de siempre, la pestaña SI filtra.
        var query = new ReservaListQuery { View = "in-management" };
        var page = await service.GetReservasAsync(query, CancellationToken.None);

        var fila = Assert.Single(page.Items);
        Assert.Equal("R-1", fila.NumeroReserva);
    }

    [Fact]
    public async Task Search_WithoutGlobalFlag_ViewStillFilters_EvenWithSearchText()
    {
        using var context = CreateContext();
        // Fix B1 de review: las DOS reservas matchean el texto de busqueda ("Roma"), pero SOLO la
        // primera esta "Saldado" (view=settled). Sin GlobalSearch=true, la pestaña sigue mandando —
        // este es el caso de PaymentsByReservaPage.jsx (manda view+search+periodo juntos).
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            Name = "Viaje a Roma saldado",
            NumeroReserva = "R-1",
            Status = EstadoReserva.InManagement,
            DerivedCollectionStatus = ReservaCollectionStatus.Settled
        });
        context.Reservas.Add(new Reserva
        {
            Id = 2,
            Name = "Viaje a Roma con deuda",
            NumeroReserva = "R-2",
            Status = EstadoReserva.InManagement,
            DerivedCollectionStatus = ReservaCollectionStatus.NoCharges
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        // GlobalSearch NO se manda (default false).
        var query = new ReservaListQuery { View = "settled", Search = "Roma" };
        var page = await service.GetReservasAsync(query, CancellationToken.None);

        var fila = Assert.Single(page.Items);
        Assert.Equal("R-1", fila.NumeroReserva);
    }
}
