using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Time;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Cobranzas: las dos listas de deudores de la spec firmada el 2026-08-06 — "Viajan pronto y deben"
/// (§4.2 / M-6) y "Deuda por cliente" (§4.3 / M-7), mas el numero nuevo de Configuracion "el saldo tiene
/// que estar completo N dias antes de la salida" (§4.5 / M-8, default 21).
///
/// <para>Lo que se protege aca: que NUNCA se sumen pesos con dolares (P-3), que las reservas muertas
/// (anuladas, perdidas, presupuestos) no entren, y que el veredicto de "vencido" respete el numero
/// configurado, incluido su borde exacto.</para>
/// </summary>
public class CobranzasDeudoresTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// PaymentService sin HttpContext = "ve todo" (comportamiento legacy de los tests unitarios). El
    /// alcance por vendedor ya esta cubierto por los tests de la worklist de cobranza.
    /// </summary>
    private static PaymentService CreateService(AppDbContext context, int? fullPaymentDueDays = null)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        var entity = new OperationalFinanceSettings();
        if (fullPaymentDueDays.HasValue)
        {
            entity.FullPaymentDueDaysBeforeDeparture = fullPaymentDueDays.Value;
        }
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entity);

        var mapper = new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

        return new PaymentService(
            context,
            new EntityReferenceResolver(context),
            mapper,
            settings.Object,
            NullLogger<PaymentService>.Instance);
    }

    /// <summary>El "hoy" que usa el motor: fecha de pared de Argentina, igual que el resto del sistema.</summary>
    private static DateTime Today => AgencyTimezone.TodayWallClockUtc();

    private static Customer SeedCustomer(AppDbContext context, int id, string name)
    {
        var customer = new Customer { Id = id, FullName = name };
        context.Customers.Add(customer);
        return customer;
    }

    /// <summary>
    /// Siembra una reserva con su detalle de plata POR MONEDA (la tabla materializada que usa el sistema).
    /// El escalar Balance es solo el semaforo "¿debe si o no?".
    /// </summary>
    private static Reserva SeedReserva(
        AppDbContext context,
        int id,
        string numero,
        string status,
        DateTime? startDate,
        int? customerId,
        params (string Currency, decimal ConfirmedSale, decimal Balance)[] money)
    {
        var reserva = new Reserva
        {
            Id = id,
            NumeroReserva = numero,
            Name = $"Destino {id}",
            Status = status,
            StartDate = startDate,
            PayerId = customerId,
            Balance = money.Sum(line => line.Balance),
            ResponsibleUserName = "Vendedora Ana"
        };
        context.Reservas.Add(reserva);

        foreach (var line in money)
        {
            context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
            {
                ReservaId = id,
                Currency = line.Currency,
                ConfirmedSale = line.ConfirmedSale,
                TotalSale = line.ConfirmedSale,
                TotalPaid = line.ConfirmedSale - line.Balance,
                Balance = line.Balance
            });
        }

        return reserva;
    }

    /// <summary>
    /// PaymentService CON identidad: asi se puede probar el alcance real (que ve un vendedor vs. que ve
    /// alguien de back-office). <paramref name="canSeeAll"/> siembra el permiso <c>cobranzas.view_all</c>.
    /// </summary>
    private static PaymentService CreateServiceForUser(
        AppDbContext context, string userId, bool canSeeAll, bool isAdmin = false)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        var mapper = new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        if (isAdmin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };

        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> permissions = canSeeAll
            ? new HashSet<string> { Permissions.CobranzasView, Permissions.CobranzasViewAll }
            : new HashSet<string> { Permissions.CobranzasView };
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(permissions);

        return new PaymentService(
            context,
            new EntityReferenceResolver(context),
            mapper,
            settings.Object,
            NullLogger<PaymentService>.Instance,
            resolver.Object,
            accessor);
    }

    // =====================================================================================
    // Alcance: cada vendedor ve SUS reservas; el back-office (cobranzas.view_all) ve todas
    // =====================================================================================

    /// <summary>Siembra dos reservas con deuda, una de cada vendedor.</summary>
    private static void SeedDosVendedores(AppDbContext context)
    {
        var mia = SeedReserva(context, 1, "F-2026-MIA", EstadoReserva.Confirmed, Today.AddDays(40), null,
            ("ARS", 100000m, 40000m));
        mia.ResponsibleUserId = "vendedora-ana";

        var ajena = SeedReserva(context, 2, "F-2026-AJENA", EstadoReserva.Confirmed, Today.AddDays(50), null,
            ("ARS", 100000m, 60000m));
        ajena.ResponsibleUserId = "vendedor-bruno";
    }

    [Fact]
    public async Task Deudores_UnVendedorSoloVeSusReservas()
    {
        await using var context = CreateContext();
        SeedDosVendedores(context);
        await context.SaveChangesAsync();

        var result = await CreateServiceForUser(context, "vendedora-ana", canSeeAll: false)
            .GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("F-2026-MIA", item.NumeroReserva);
        // El total de la franja tambien es SOLO el suyo: no puede inferir la deuda del otro vendedor.
        Assert.Equal(40000m, result.TotalsPending.Single().Amount);
    }

    [Fact]
    public async Task Deudores_ConVerTodoSeVenLasDeTodos()
    {
        await using var context = CreateContext();
        SeedDosVendedores(context);
        await context.SaveChangesAsync();

        var result = await CreateServiceForUser(context, "jefa-cobranzas", canSeeAll: true)
            .GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(100000m, result.TotalsPending.Single().Amount);
    }

    [Fact]
    public async Task Deudores_ElAdminVeTodoSinPermisoExplicito()
    {
        await using var context = CreateContext();
        SeedDosVendedores(context);
        await context.SaveChangesAsync();

        var result = await CreateServiceForUser(context, "dueño", canSeeAll: false, isAdmin: true)
            .GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task DeudaPorCliente_UnVendedorSoloVeLaDeudaDeSusReservas()
    {
        await using var context = CreateContext();
        var cliente = SeedCustomer(context, 1, "Familia García");

        var mia = SeedReserva(context, 1, "F-2026-MIA", EstadoReserva.Confirmed, Today.AddDays(40), cliente.Id,
            ("ARS", 100000m, 40000m));
        mia.ResponsibleUserId = "vendedora-ana";

        // MISMO cliente, reserva de OTRO vendedor: su deuda no tiene que sumarse en la vista de Ana.
        var ajena = SeedReserva(context, 2, "F-2026-AJENA", EstadoReserva.Confirmed, Today.AddDays(50), cliente.Id,
            ("ARS", 100000m, 60000m));
        ajena.ResponsibleUserId = "vendedor-bruno";
        await context.SaveChangesAsync();

        var result = await CreateServiceForUser(context, "vendedora-ana", canSeeAll: false)
            .GetCustomerDebtsAsync(new DebtorsQuery(), CancellationToken.None);

        var row = Assert.Single(result.Items);
        Assert.Equal("Familia García", row.CustomerName);
        Assert.Equal(1, row.ReservationsWithDebt);
        Assert.Equal(40000m, row.Debt.Single().Amount);
    }

    [Fact]
    public async Task DeudaPorCliente_ConVerTodoSeSumanLasReservasDeTodosLosVendedores()
    {
        await using var context = CreateContext();
        var cliente = SeedCustomer(context, 1, "Familia García");

        var mia = SeedReserva(context, 1, "F-2026-MIA", EstadoReserva.Confirmed, Today.AddDays(40), cliente.Id,
            ("ARS", 100000m, 40000m));
        mia.ResponsibleUserId = "vendedora-ana";
        var ajena = SeedReserva(context, 2, "F-2026-AJENA", EstadoReserva.Confirmed, Today.AddDays(50), cliente.Id,
            ("ARS", 100000m, 60000m));
        ajena.ResponsibleUserId = "vendedor-bruno";
        await context.SaveChangesAsync();

        var result = await CreateServiceForUser(context, "jefa-cobranzas", canSeeAll: true)
            .GetCustomerDebtsAsync(new DebtorsQuery(), CancellationToken.None);

        var row = Assert.Single(result.Items);
        Assert.Equal(2, row.ReservationsWithDebt);
        Assert.Equal(100000m, row.Debt.Single().Amount);
    }

    // =====================================================================================
    // Detalle por moneda incoherente: NO se inventa una deuda en pesos
    // =====================================================================================

    /// <summary>
    /// Dos casos distintos que antes se trataban igual: una reserva VIEJA sin detalle por moneda usa el
    /// saldo escalar (unica forma de no esconder la plata); una reserva CON detalle pero sin ninguna moneda
    /// en positivo es una incoherencia, y ahi NO se inventa una deuda en pesos.
    /// </summary>
    [Fact]
    public async Task Deudores_ReservaLegacySinDetallePorMoneda_UsaElSaldoEscalarEnPesos()
    {
        await using var context = CreateContext();
        // Sin lineas de plata por moneda (nunca se recalculo): solo el escalar.
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-LEGACY",
            Name = "Reserva vieja",
            Status = EstadoReserva.Confirmed,
            StartDate = Today.AddDays(40),
            Balance = 75000m
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None);

        var pending = Assert.Single(result.Items[0].Pending);
        Assert.Equal("ARS", pending.Currency);
        Assert.Equal(75000m, pending.Amount);
    }

    [Fact]
    public async Task Deudores_DetalleSinMonedasEnPositivo_NoInventaDeudaEnPesos()
    {
        await using var context = CreateContext();
        // El escalar dice que debe, pero el detalle por moneda dice que no: incoherencia.
        var reserva = SeedReserva(context, 1, "F-2026-RARA", EstadoReserva.Confirmed, Today.AddDays(40), null,
            ("USD", 1000m, 0m));
        // El semaforo escalar dice que debe; el detalle por moneda dice que no. Eso es la incoherencia.
        reserva.Balance = 50000m;
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Empty(item.Pending);
        Assert.Empty(result.TotalsPending);
    }

    // =====================================================================================
    // "Viajan pronto y deben": quienes entran y como se separan las monedas
    // =====================================================================================

    [Fact]
    public async Task Deudores_DejaAfueraAnuladasPerdidasYPresupuestos()
    {
        await using var context = CreateContext();
        SeedReserva(context, 1, "F-2026-0001", EstadoReserva.Confirmed, Today.AddDays(30), null, ("ARS", 100000m, 40000m));
        SeedReserva(context, 2, "F-2026-0002", EstadoReserva.Cancelled, Today.AddDays(30), null, ("ARS", 100000m, 40000m));
        SeedReserva(context, 3, "F-2026-0003", EstadoReserva.Lost, Today.AddDays(30), null, ("ARS", 100000m, 40000m));
        SeedReserva(context, 4, "F-2026-0004", EstadoReserva.Budget, Today.AddDays(30), null, ("ARS", 100000m, 40000m));
        SeedReserva(context, 5, "F-2026-0005", EstadoReserva.PendingOperatorRefund, Today.AddDays(30), null, ("ARS", 100000m, 40000m));
        // Confirmada pero YA SALDADA: tampoco entra (no hay nada que cobrarle).
        SeedReserva(context, 6, "F-2026-0006", EstadoReserva.Confirmed, Today.AddDays(30), null, ("ARS", 100000m, 0m));
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("F-2026-0001", item.NumeroReserva);
    }

    [Fact]
    public async Task Deudores_SeparaLasMonedasYNuncaLasSuma()
    {
        await using var context = CreateContext();
        SeedReserva(context, 1, "F-2026-0001", EstadoReserva.Confirmed, Today.AddDays(40), null,
            ("ARS", 500000m, 95000m), ("USD", 2400m, 120m));
        SeedReserva(context, 2, "F-2026-0002", EstadoReserva.InManagement, Today.AddDays(50), null,
            ("USD", 1000m, 300m));
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None);

        var primera = result.Items[0];
        Assert.Equal(2, primera.Pending.Count);
        Assert.Equal(95000m, primera.Pending.Single(line => line.Currency == "ARS").Amount);
        Assert.Equal(120m, primera.Pending.Single(line => line.Currency == "USD").Amount);

        // Totales de la franja de arriba: una linea por moneda, jamas un numero mezclado.
        Assert.Equal(2, result.TotalsPending.Count);
        Assert.Equal(95000m, result.TotalsPending.Single(line => line.Currency == "ARS").Amount);
        Assert.Equal(420m, result.TotalsPending.Single(line => line.Currency == "USD").Amount);
    }

    /// <summary>
    /// Un saldo A FAVOR en una moneda no tapa la deuda de la otra (ADR-021 §2.4): solo se lista lo que
    /// realmente falta cobrar.
    /// </summary>
    [Fact]
    public async Task Deudores_ElSaldoAFavorDeUnaMonedaNoTapaLaDeudaDeLaOtra()
    {
        await using var context = CreateContext();
        SeedReserva(context, 1, "F-2026-0001", EstadoReserva.Confirmed, Today.AddDays(20), null,
            ("ARS", 500000m, 100000m), ("USD", 1000m, -200m));
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None);

        var pending = Assert.Single(result.Items[0].Pending);
        Assert.Equal("ARS", pending.Currency);
        Assert.Equal(100000m, pending.Amount);
    }

    [Fact]
    public async Task Deudores_OrdenaPorFechaDeSalidaYPoneAlFinalLasQueNoTienenFecha()
    {
        await using var context = CreateContext();
        SeedReserva(context, 1, "F-2026-0001", EstadoReserva.Confirmed, Today.AddDays(30), null, ("ARS", 10m, 10m));
        SeedReserva(context, 2, "F-2026-0002", EstadoReserva.Confirmed, Today.AddDays(3), null, ("ARS", 10m, 10m));
        SeedReserva(context, 3, "F-2026-0003", EstadoReserva.Confirmed, null, null, ("ARS", 10m, 10m));
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None);

        Assert.Equal(new[] { "F-2026-0002", "F-2026-0001", "F-2026-0003" },
            result.Items.Select(item => item.NumeroReserva).ToArray());
        Assert.Equal("en 3 días", result.Items[0].DepartureCountdownText);
        Assert.Equal(string.Empty, result.Items[2].DepartureCountdownText);
    }

    // =====================================================================================
    // "El saldo tiene que estar completo N dias antes de la salida" (§4.5)
    // =====================================================================================

    /// <summary>Sin tocar nada, el numero es 21 (firmado). Con 21 dias justos todavia NO esta vencida.</summary>
    [Fact]
    public async Task Vencido_PorDefectoSonVeintiunDias()
    {
        await using var context = CreateContext();
        SeedReserva(context, 1, "F-2026-0001", EstadoReserva.Confirmed, Today.AddDays(21), null, ("ARS", 100m, 50m));
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None);

        Assert.Equal(21, result.PaymentDueDaysBeforeDeparture);
        var item = Assert.Single(result.Items);
        Assert.Equal(Today, item.PaymentDueDate);
        Assert.False(item.IsPastDue);   // la fecha limite es HOY: todavia esta a tiempo
        Assert.Null(item.PastDueText);
    }

    /// <summary>Borde exacto: un dia menos que el numero configurado y ya esta vencida, con su frase lista.</summary>
    [Fact]
    public async Task Vencido_UnDiaDespuesDelBorde_QuedaVencidaConSuTexto()
    {
        await using var context = CreateContext();
        SeedReserva(context, 1, "F-2026-0001", EstadoReserva.Confirmed, Today.AddDays(20), null, ("ARS", 100m, 50m));
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.True(item.IsPastDue);
        Assert.Equal(1, item.DaysPastDue);
        Assert.Equal($"El saldo tenía que estar completo el {Today.AddDays(-1):dd/MM/yyyy}.", item.PastDueText);
    }

    /// <summary>Con el numero cambiado en Configuracion, el veredicto se mueve con el.</summary>
    [Fact]
    public async Task Vencido_ConOtroNumeroConfigurado_SeCorreLaFechaLimite()
    {
        await using var context = CreateContext();
        SeedReserva(context, 1, "F-2026-0001", EstadoReserva.Confirmed, Today.AddDays(20), null, ("ARS", 100m, 50m));
        await context.SaveChangesAsync();

        // Con 7 dias de anticipacion, una salida dentro de 20 dias NO esta vencida.
        var result = await CreateService(context, fullPaymentDueDays: 7)
            .GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None);

        Assert.Equal(7, result.PaymentDueDaysBeforeDeparture);
        var item = Assert.Single(result.Items);
        Assert.False(item.IsPastDue);
        Assert.Equal(Today.AddDays(13), item.PaymentDueDate);
    }

    /// <summary>Una reserva sin fecha de salida no puede estar vencida: no hay contra que comparar.</summary>
    [Fact]
    public async Task Vencido_SinFechaDeSalida_NoHayFechaLimiteNiVencimiento()
    {
        await using var context = CreateContext();
        SeedReserva(context, 1, "F-2026-0001", EstadoReserva.Confirmed, null, null, ("ARS", 100m, 50m));
        await context.SaveChangesAsync();

        var item = Assert.Single(
            (await CreateService(context).GetDebtorsByDepartureAsync(new DebtorsQuery(), CancellationToken.None)).Items);

        Assert.Null(item.PaymentDueDate);
        Assert.False(item.IsPastDue);
    }

    // =====================================================================================
    // "Deuda por cliente" (§4.3)
    // =====================================================================================

    [Fact]
    public async Task DeudaPorCliente_CruzaTodasSusReservasYSeparaMonedas()
    {
        await using var context = CreateContext();
        var garcia = SeedCustomer(context, 1, "Familia García");
        var perez = SeedCustomer(context, 2, "Pérez, Ana");

        SeedReserva(context, 1, "F-2026-0001", EstadoReserva.Confirmed, Today.AddDays(40), garcia.Id,
            ("USD", 2400m, 500m));
        SeedReserva(context, 2, "F-2026-0002", EstadoReserva.InManagement, Today.AddDays(10), garcia.Id,
            ("USD", 1000m, 400m), ("ARS", 200000m, 50000m));
        SeedReserva(context, 3, "F-2026-0003", EstadoReserva.Confirmed, Today.AddDays(60), perez.Id,
            ("ARS", 610000m, 210000m));
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerDebtsAsync(new DebtorsQuery(), CancellationToken.None);

        // Ordenados por la primera salida: García sale antes (dentro de 10 dias).
        Assert.Equal(new[] { "Familia García", "Pérez, Ana" },
            result.Items.Select(item => item.CustomerName).ToArray());

        var garciaRow = result.Items[0];
        Assert.Equal(2, garciaRow.ReservationsWithDebt);
        Assert.Equal(900m, garciaRow.Debt.Single(line => line.Currency == "USD").Amount);
        Assert.Equal(50000m, garciaRow.Debt.Single(line => line.Currency == "ARS").Amount);
        Assert.Equal(Today.AddDays(10), garciaRow.FirstDeparture);
        Assert.Equal("en 10 días", garciaRow.FirstDepartureCountdownText);
        Assert.True(garciaRow.HasPastDue); // la del dia 10 ya paso la fecha limite de 21 dias antes

        // Total general: una linea por moneda, jamas mezcladas.
        Assert.Equal(900m, result.TotalsDebt.Single(line => line.Currency == "USD").Amount);
        Assert.Equal(260000m, result.TotalsDebt.Single(line => line.Currency == "ARS").Amount);
    }

    [Fact]
    public async Task DeudaPorCliente_ElQueNoDebeNoAparece()
    {
        await using var context = CreateContext();
        var garcia = SeedCustomer(context, 1, "Familia García");
        var saldado = SeedCustomer(context, 2, "Cliente al día");
        SeedReserva(context, 1, "F-2026-0001", EstadoReserva.Confirmed, Today.AddDays(40), garcia.Id, ("ARS", 100m, 30m));
        SeedReserva(context, 2, "F-2026-0002", EstadoReserva.Confirmed, Today.AddDays(40), saldado.Id, ("ARS", 100m, 0m));
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerDebtsAsync(new DebtorsQuery(), CancellationToken.None);

        Assert.Equal("Familia García", Assert.Single(result.Items).CustomerName);
    }

    /// <summary>Las reservas sin cliente cargado no se esconden: van juntas como consumidor final.</summary>
    [Fact]
    public async Task DeudaPorCliente_ReservasSinClienteSeAgrupanComoConsumidorFinal()
    {
        await using var context = CreateContext();
        SeedReserva(context, 1, "F-2026-0001", EstadoReserva.Confirmed, Today.AddDays(40), null, ("ARS", 100m, 30m));
        SeedReserva(context, 2, "F-2026-0002", EstadoReserva.Confirmed, Today.AddDays(45), null, ("ARS", 100m, 20m));
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerDebtsAsync(new DebtorsQuery(), CancellationToken.None);

        var row = Assert.Single(result.Items);
        Assert.Null(row.CustomerPublicId);
        Assert.Equal("Consumidor Final", row.CustomerName);
        Assert.Equal(2, row.ReservationsWithDebt);
        Assert.Equal(50m, row.Debt.Single().Amount);
    }

    [Fact]
    public async Task DeudaPorCliente_BuscadorFiltraPorNombre()
    {
        await using var context = CreateContext();
        var garcia = SeedCustomer(context, 1, "Familia García");
        var perez = SeedCustomer(context, 2, "Pérez, Ana");
        SeedReserva(context, 1, "F-2026-0001", EstadoReserva.Confirmed, Today.AddDays(40), garcia.Id, ("ARS", 100m, 30m));
        SeedReserva(context, 2, "F-2026-0002", EstadoReserva.Confirmed, Today.AddDays(45), perez.Id, ("ARS", 100m, 20m));
        await context.SaveChangesAsync();

        var result = await CreateService(context)
            .GetCustomerDebtsAsync(new DebtorsQuery { Search = "pérez" }, CancellationToken.None);

        Assert.Equal("Pérez, Ana", Assert.Single(result.Items).CustomerName);
    }
}
