using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Fase D del rediseño de la maquina de estados. Verifica que los CONJUNTOS de estados
/// "activos/operativos/cobrables" de los servicios transversales tomen los estados correctos.
///
/// <para>ADR-036 (2026-06-21, prepago puro): el estado ToSettle MURIO. Los tests que validaban su
/// membresia se reescribieron: ahora fijan la membresia de los estados vigentes (InManagement, Confirmed,
/// Traveling, Closed segun corresponda) y verifican que un literal "ToSettle" residual NO es tomado por
/// ningun conjunto. Las reglas ADR-033 (Closed con deuda cuenta como cobrable/AR) se conservan.</para>
///
/// NOTA: InMemory + Moq. NO se corren local (se cuelgan). Los corre el reviewer/QA en VPS.
/// </summary>
public class FaseDStateSetTests
{
    private static DbContextOptions<AppDbContext> NewDbOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static Mock<IOperationalFinanceSettingsService> SettingsMock()
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        // Devolvemos una entidad con defaults; los tests de Fase D NO dependen del flag
        // (validan membresia de conjuntos, no transiciones). El flag solo controla si llegan
        // a EXISTIR filas Sold/ToSettle; aca las sembramos a mano para verificar la membresia.
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                UpcomingUnpaidReservationAlertDays = 30
            });
        return mock;
    }

    /// <summary>Reserva minima con saldo pendiente y viaje a 5 dias (entra en ventana de alertas).</summary>
    private static Reserva ReservaWithBalance(int id, string status, decimal balance = 100m) => new()
    {
        Id = id,
        Name = $"Reserva {id}",
        NumeroReserva = $"R-{id}",
        Status = status,
        Balance = balance,
        TotalSale = 1000m,
        StartDate = DateTime.UtcNow.Date.AddDays(5)
    };

    /// <summary>
    /// Reserva saldada (Balance 0) con venta sin facturar (TotalSale 1000). Si su estado es
    /// "facturable" cae en la bandeja de facturacion como "lista para emitir".
    /// </summary>
    private static Reserva ReservaReadyToInvoice(int id, string status) => new()
    {
        Id = id,
        Name = $"Reserva {id}",
        NumeroReserva = $"R-{id}",
        Status = status,
        Balance = 0m,
        TotalSale = 1000m,
        StartDate = DateTime.UtcNow.Date.AddDays(5)
    };

    private static HotelBooking HotelFor(int id, int reservaId, int supplierId) => new()
    {
        Id = id,
        ReservaId = reservaId,
        SupplierId = supplierId,
        HotelName = "Hotel Test",
        City = "Bariloche",
        CheckIn = DateTime.UtcNow.Date.AddDays(5),
        CheckOut = DateTime.UtcNow.Date.AddDays(8),
        Nights = 3,
        NetCost = 100m,
        SalePrice = 150m,
        Status = "Confirmado"
    };

    // =====================================================================
    // (a) Membresia de conjuntos POSITIVOS. ADR-036: ToSettle ya no es tomado por ninguno.
    // =====================================================================

    [Fact]
    public async Task PaymentService_CollectionsSummary_TakesFirmStates_NotResidualToSettle()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ReservaWithBalance(1, EstadoReserva.InManagement));
        // ADR-036: un literal "ToSettle" residual (estado eliminado) NO debe sumar a la cobranza.
        context.Reservas.Add(ReservaWithBalance(2, "ToSettle"));
        await context.SaveChangesAsync();

        var service = new PaymentService(
            context,
            Mock.Of<IEntityReferenceResolver>(),
            Mock.Of<AutoMapper.IMapper>(),
            SettingsMock().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentService>.Instance);

        var summary = await service.GetCollectionsSummaryAsync(CancellationToken.None);

        // Solo InManagement (100) suma; el residual ToSettle queda afuera.
        Assert.Equal(100m, summary.PendingAmount);
    }

    [Fact]
    public async Task AlertService_UrgentTrips_TakesInManagement_NotResidualToSettle()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ReservaWithBalance(1, EstadoReserva.InManagement));
        // ADR-036: un literal "ToSettle" residual NO debe figurar como "viaje proximo".
        context.Reservas.Add(ReservaWithBalance(2, "ToSettle"));
        await context.SaveChangesAsync();

        var service = new AlertService(context, SettingsMock().Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertService>.Instance);

        // Fuga 2 (ADR-017 F1b): GetAlertsAsync ahora recibe la identidad del caller.
        // Los buckets financieros son solo-admin, asi que este test pasa un admin.
        dynamic result = await service.GetAlertsAsync(new AlertCallerContext("admin-test", IsAdmin: true), CancellationToken.None);

        var urgentStatuses = EnumerateStatuses(result.UrgentTrips);
        Assert.Contains(EstadoReserva.InManagement, urgentStatuses);
        Assert.DoesNotContain("ToSettle", urgentStatuses);
    }

    [Fact]
    public async Task SupplierService_AccountServices_TakesTraveling_NotResidualToSettle()
    {
        using var context = new AppDbContext(NewDbOptions());
        const int supplierId = 7;
        context.Reservas.Add(ReservaWithBalance(1, EstadoReserva.InManagement));
        // ADR-036: Traveling sigue contando para la cuenta del proveedor; el residual ToSettle NO.
        context.Reservas.Add(ReservaWithBalance(2, EstadoReserva.Traveling));
        context.Reservas.Add(ReservaWithBalance(3, "ToSettle"));
        context.HotelBookings.Add(HotelFor(10, 1, supplierId));
        context.HotelBookings.Add(HotelFor(11, 2, supplierId));
        context.HotelBookings.Add(HotelFor(12, 3, supplierId));
        await context.SaveChangesAsync();

        var service = new SupplierService(context);

        var page = await service.GetSupplierAccountServicesAsync(
            supplierId,
            new SupplierAccountServicesQuery(),
            CancellationToken.None);

        // InManagement + Traveling cuentan (2); el residual ToSettle queda afuera.
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task TreasuryService_AccountsReceivable_TakesFirmStates_NotResidualToSettle()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ReservaWithBalance(1, EstadoReserva.InManagement, balance: 300m));
        // ADR-036: un literal "ToSettle" residual NO suma a AR.
        context.Reservas.Add(ReservaWithBalance(2, "ToSettle", balance: 200m));
        await context.SaveChangesAsync();

        var service = new TreasuryService(context, Mock.Of<IEntityReferenceResolver>());

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        // Solo InManagement (300) suma a AR; el residual ToSettle afuera.
        Assert.Equal(300m, summary.AccountsReceivable);
    }

    [Fact]
    public async Task InvoicingWorklist_OnlyConfirmed_NotTravelingNorResidualToSettle()
    {
        using var context = new AppDbContext(NewDbOptions());
        // ADR-036: la factura de venta es SOLO en Confirmed. Traveling ya NO factura (en viaje no se factura);
        // un literal "ToSettle" residual tampoco. Las tres estan saldadas con venta sin facturar.
        context.Reservas.Add(ReservaReadyToInvoice(1, EstadoReserva.Confirmed));
        context.Reservas.Add(ReservaReadyToInvoice(2, EstadoReserva.Traveling));
        context.Reservas.Add(ReservaReadyToInvoice(3, "ToSettle"));
        await context.SaveChangesAsync();

        var service = BuildInvoiceService(context);

        var page = await service.GetInvoicingWorklistAsync(new InvoicingWorklistQuery(), CancellationToken.None);
        var numerosEnBandeja = page.Items.Select(item => item.NumeroReserva).ToList();

        // Solo Confirmed aparece; Traveling y el residual ToSettle quedan afuera.
        Assert.Contains("R-1", numerosEnBandeja);
        Assert.DoesNotContain("R-2", numerosEnBandeja);
        Assert.DoesNotContain("R-3", numerosEnBandeja);
    }

    [Fact]
    public async Task InvoicingWorklist_HistoricStates_OnlyConfirmedFacturable_Adr036()
    {
        using var context = new AppDbContext(NewDbOptions());
        // ADR-036: facturable SOLO Confirmed. NO facturables: Traveling (en viaje no se factura), Budget,
        // Closed, Cancelled.
        context.Reservas.Add(ReservaReadyToInvoice(1, EstadoReserva.Confirmed));
        context.Reservas.Add(ReservaReadyToInvoice(2, EstadoReserva.Traveling));
        context.Reservas.Add(ReservaReadyToInvoice(3, EstadoReserva.Budget));
        context.Reservas.Add(ReservaReadyToInvoice(4, EstadoReserva.Closed));
        context.Reservas.Add(ReservaReadyToInvoice(5, EstadoReserva.Cancelled));
        await context.SaveChangesAsync();

        var service = BuildInvoiceService(context);

        var page = await service.GetInvoicingWorklistAsync(new InvoicingWorklistQuery(), CancellationToken.None);
        var numerosEnBandeja = page.Items.Select(item => item.NumeroReserva).ToList();

        // Solo Confirmed en la bandeja. El resto afuera (incluido Traveling).
        Assert.Contains("R-1", numerosEnBandeja);
        Assert.DoesNotContain("R-2", numerosEnBandeja);
        Assert.DoesNotContain("R-3", numerosEnBandeja);
        Assert.DoesNotContain("R-4", numerosEnBandeja);
        Assert.DoesNotContain("R-5", numerosEnBandeja);
    }

    // =====================================================================
    // (b) Sold y ToSettle SI cuentan en revenue/AR (patron NEGATIVO)
    //     Fijamos la decision de negocio para que no se rompa por accidente.
    // =====================================================================

    [Fact]
    public async Task ReservaSummary_RevenueNegativePattern_CountsFirmStates_NotClosedOrCancelled()
    {
        using var context = new AppDbContext(NewDbOptions());
        // ADR-036: venta activa = InManagement/Confirmed/Traveling (patron != Closed && != Cancelled &&
        // != Archived). Closed y Cancelled NO cuentan. ToSettle murio.
        context.Reservas.Add(new Reserva { Id = 1, Name = "S", NumeroReserva = "R-1", Status = EstadoReserva.InManagement, TotalSale = 1000m });
        context.Reservas.Add(new Reserva { Id = 2, Name = "T", NumeroReserva = "R-2", Status = EstadoReserva.Traveling, TotalSale = 1000m });
        context.Reservas.Add(new Reserva { Id = 3, Name = "C", NumeroReserva = "R-3", Status = EstadoReserva.Closed, TotalSale = 1000m });
        context.Reservas.Add(new Reserva { Id = 4, Name = "X", NumeroReserva = "R-4", Status = EstadoReserva.Cancelled, TotalSale = 1000m });
        await context.SaveChangesAsync();

        var service = BuildReservaService(context);

        var page = await service.GetReservasAsync(new ReservaListQuery(), CancellationToken.None);
        var summary = page.Summary;

        // Solo InManagement + Traveling aportan a TotalSaleActive (2 x 1000). Closed/Cancelled afuera.
        Assert.Equal(2000m, summary.TotalSaleActive);
        // Y ambos cuentan como "activas".
        Assert.Equal(2, summary.ActiveCount);
    }

    [Fact]
    public async Task OperationalFinanceMonitor_MonitorsLiveStates_NotCancelledOrClosed()
    {
        using var context = new AppDbContext(NewDbOptions());
        // Las 4 tienen saldo pendiente y viaje proximo. El predicado NEGATIVO del monitor
        // (Status != Cancelled && != Closed) toma InManagement y Traveling, descarta Cancelled/Closed.
        // ADR-036: ToSettle murio.
        context.Reservas.Add(ReservaWithBalance(1, EstadoReserva.InManagement));
        context.Reservas.Add(ReservaWithBalance(2, EstadoReserva.Traveling));
        context.Reservas.Add(ReservaWithBalance(3, EstadoReserva.Cancelled));
        context.Reservas.Add(ReservaWithBalance(4, EstadoReserva.Closed));
        await context.SaveChangesAsync();

        // Capturamos el Id de reserva de cada notificacion emitida: es la prueba de que el monitor
        // "tomo" esa reserva como activa (le genero el aviso de "sale pronto sin pagar").
        var notifiedReservaIds = new List<int>();
        var notificationMock = new Mock<INotificationService>();
        notificationMock
            .Setup(n => n.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Callback<Notification, CancellationToken>((notification, _) =>
            {
                // El monitor siempre setea RelatedEntityId con el Id de la reserva (nunca null en este flujo).
                if (notification.RelatedEntityId.HasValue)
                    notifiedReservaIds.Add(notification.RelatedEntityId.Value);
            })
            .ReturnsAsync((Notification notification, CancellationToken _) => notification);

        var service = BuildMonitorService(context, notificationMock.Object);

        await service.GenerateUpcomingUnpaidReservationNotificationsAsync();

        // InManagement y Traveling caen del lado "monitoreado" del predicado.
        Assert.Contains(1, notifiedReservaIds);
        Assert.Contains(2, notifiedReservaIds);
        // Cancelled y Closed quedan afuera (no se les genera aviso).
        Assert.DoesNotContain(3, notifiedReservaIds);
        Assert.DoesNotContain(4, notifiedReservaIds);
    }

    // =====================================================================
    // (c) Regresion: con los 7 estados historicos el comportamiento NO cambia
    // =====================================================================

    [Fact]
    public async Task PaymentService_ClosedWithDebt_NowCollectable_Adr033()
    {
        using var context = new AppDbContext(NewDbOptions());
        // ADR-033 (A1/A3): cobrables = venta firme con deuda, INCLUIDO Closed. Budget (pre-venta) y Cancelled
        // (terminal-no-firme) fuera. ADR-036 (2026-06-21): Traveling SALE de la cobranza (en viaje no se
        // cobra; en prepago puro no deberia llegar a viajar debiendo). Cobrables = {Confirmed, Closed}.
        context.Reservas.Add(ReservaWithBalance(1, EstadoReserva.Confirmed, 100m));
        context.Reservas.Add(ReservaWithBalance(2, EstadoReserva.Traveling, 100m));
        context.Reservas.Add(ReservaWithBalance(3, EstadoReserva.Budget, 100m));
        context.Reservas.Add(ReservaWithBalance(4, EstadoReserva.Closed, 100m));
        context.Reservas.Add(ReservaWithBalance(5, EstadoReserva.Cancelled, 100m));
        await context.SaveChangesAsync();

        var service = new PaymentService(
            context,
            Mock.Of<IEntityReferenceResolver>(),
            Mock.Of<AutoMapper.IMapper>(),
            SettingsMock().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentService>.Instance);

        var summary = await service.GetCollectionsSummaryAsync(CancellationToken.None);

        // ADR-036: Confirmed + Closed = 200. Traveling/Budget/Cancelled afuera.
        Assert.Equal(200m, summary.PendingAmount);
    }

    [Fact]
    public async Task AlertService_ClosedWithDebt_NowVisible_Adr033()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ReservaWithBalance(1, EstadoReserva.Confirmed));
        context.Reservas.Add(ReservaWithBalance(2, EstadoReserva.Traveling));
        context.Reservas.Add(ReservaWithBalance(3, EstadoReserva.Budget));
        context.Reservas.Add(ReservaWithBalance(4, EstadoReserva.Closed));
        await context.SaveChangesAsync();

        var service = new AlertService(context, SettingsMock().Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertService>.Instance);

        // Fuga 2 (ADR-017 F1b): buckets financieros solo-admin -> caller admin.
        dynamic result = await service.GetAlertsAsync(new AlertCallerContext("admin-test", IsAdmin: true), CancellationToken.None);

        var urgentStatuses = EnumerateStatuses(result.UrgentTrips);
        Assert.Contains(EstadoReserva.Confirmed, urgentStatuses);
        Assert.Contains(EstadoReserva.Traveling, urgentStatuses);
        Assert.DoesNotContain(EstadoReserva.Budget, urgentStatuses);
        // ADR-033 (A3/F6): la deuda de una Finalizada AHORA aparece en alertas ("terminado y debe").
        Assert.Contains(EstadoReserva.Closed, urgentStatuses);
    }

    [Fact]
    public async Task TreasuryService_ClosedWithDebt_NowCountsAR_Adr033()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ReservaWithBalance(1, EstadoReserva.Confirmed, 100m));
        context.Reservas.Add(ReservaWithBalance(2, EstadoReserva.Traveling, 100m));
        // ADR-033 (A3/E2): Closed con deuda suma a AR (deuda real cobrable). Budget fuera.
        // ADR-036 (2026-06-21): Traveling SALE de AR (en viaje no es firme cobrable). AR = {Confirmed, Closed}.
        context.Reservas.Add(ReservaWithBalance(3, EstadoReserva.Budget, 100m));
        context.Reservas.Add(ReservaWithBalance(4, EstadoReserva.Closed, 100m));
        await context.SaveChangesAsync();

        var service = new TreasuryService(context, Mock.Of<IEntityReferenceResolver>());

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        // ADR-036: Confirmed + Closed = 200. Traveling/Budget afuera.
        Assert.Equal(200m, summary.AccountsReceivable);
    }

    [Fact]
    public async Task SupplierService_HistoricStates_Unchanged()
    {
        using var context = new AppDbContext(NewDbOptions());
        const int supplierId = 9;
        // ValidReservationStatuses historico = Confirmed, Traveling, Closed. Budget/Cancelled afuera.
        context.Reservas.Add(ReservaWithBalance(1, EstadoReserva.Confirmed));
        context.Reservas.Add(ReservaWithBalance(2, EstadoReserva.Traveling));
        context.Reservas.Add(ReservaWithBalance(3, EstadoReserva.Closed));
        context.Reservas.Add(ReservaWithBalance(4, EstadoReserva.Budget));
        context.Reservas.Add(ReservaWithBalance(5, EstadoReserva.Cancelled));
        context.HotelBookings.Add(HotelFor(10, 1, supplierId));
        context.HotelBookings.Add(HotelFor(11, 2, supplierId));
        context.HotelBookings.Add(HotelFor(12, 3, supplierId));
        context.HotelBookings.Add(HotelFor(13, 4, supplierId));
        context.HotelBookings.Add(HotelFor(14, 5, supplierId));
        await context.SaveChangesAsync();

        var service = new SupplierService(context);

        var page = await service.GetSupplierAccountServicesAsync(
            supplierId,
            new SupplierAccountServicesQuery(),
            CancellationToken.None);

        // Confirmed + Traveling + Closed = 3. Budget y Cancelled NO cuentan (igual que siempre).
        Assert.Equal(3, page.TotalCount);
    }

    // ---- Helpers ----

    private static ReservaService BuildReservaService(AppDbContext context)
    {
        var settings = SettingsMock();
        return new ReservaService(
            context,
            Mock.Of<AutoMapper.IMapper>(),
            settings.Object,
            BuildUserManager(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReservaService>.Instance);
    }

    private static InvoiceService BuildInvoiceService(AppDbContext context)
    {
        // InvoiceService tiene muchos colaboradores, pero la bandeja/summary de facturacion solo
        // toca _context y _operationalFinanceSettingsService. El resto se pasa como Mock.Of<>:
        // no se invoca en este camino. Sin IHttpContextAccessor, el filtro "filter mine" queda en
        // null (sin scoping), asi que la bandeja devuelve todas las reservas facturables.
        return new InvoiceService(
            context,
            Mock.Of<IEntityReferenceResolver>(),
            Mock.Of<IAfipService>(),
            Mock.Of<IInvoicePdfService>(),
            Mock.Of<AutoMapper.IMapper>(),
            Mock.Of<Hangfire.IBackgroundJobClient>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InvoiceService>.Instance,
            SettingsMock().Object,
            BuildUserManager());
    }

    private static OperationalFinanceMonitorService BuildMonitorService(
        AppDbContext context,
        INotificationService notificationService)
    {
        return new OperationalFinanceMonitorService(
            context,
            SettingsMock().Object,
            notificationService,
            BuildUserManagerWithAdmin());
    }

    /// <summary>
    /// UserManager mockeado cuyo <c>GetUsersInRoleAsync("Admin")</c> devuelve un admin. El monitor
    /// necesita al menos un destinatario para emitir la notificacion que usamos como senal de
    /// "esta reserva fue monitoreada". GetUsersInRoleAsync es virtual -> Moq lo intercepta sin
    /// tocar el store (mismo truco que los tests de FC1.3).
    /// </summary>
    private static Microsoft.AspNetCore.Identity.UserManager<TravelApi.Infrastructure.Identity.ApplicationUser> BuildUserManagerWithAdmin()
    {
        var store = new Mock<Microsoft.AspNetCore.Identity.IUserStore<TravelApi.Infrastructure.Identity.ApplicationUser>>();
        var userManagerMock = new Mock<Microsoft.AspNetCore.Identity.UserManager<TravelApi.Infrastructure.Identity.ApplicationUser>>(
            store.Object, null!, null!,
            Array.Empty<Microsoft.AspNetCore.Identity.IUserValidator<TravelApi.Infrastructure.Identity.ApplicationUser>>(),
            Array.Empty<Microsoft.AspNetCore.Identity.IPasswordValidator<TravelApi.Infrastructure.Identity.ApplicationUser>>(),
            null!, null!, null!, null!);
        userManagerMock
            .Setup(u => u.GetUsersInRoleAsync("Admin"))
            .ReturnsAsync(new List<TravelApi.Infrastructure.Identity.ApplicationUser>
            {
                new() { Id = "admin-1" }
            });
        return userManagerMock.Object;
    }

    private static Microsoft.AspNetCore.Identity.UserManager<TravelApi.Infrastructure.Identity.ApplicationUser> BuildUserManager()
    {
        var store = new Mock<Microsoft.AspNetCore.Identity.IUserStore<TravelApi.Infrastructure.Identity.ApplicationUser>>();
        return new Microsoft.AspNetCore.Identity.UserManager<TravelApi.Infrastructure.Identity.ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<Microsoft.AspNetCore.Identity.IUserValidator<TravelApi.Infrastructure.Identity.ApplicationUser>>(),
            Array.Empty<Microsoft.AspNetCore.Identity.IPasswordValidator<TravelApi.Infrastructure.Identity.ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    /// <summary>
    /// Recorre la lista anonima de "UrgentTrips" del AlertService y extrae el valor de la
    /// propiedad Status de cada item. Usamos reflexion porque el tipo es anonimo (no exportado).
    /// </summary>
    private static List<string> EnumerateStatuses(object urgentTrips)
    {
        var result = new List<string>();
        foreach (var item in (IEnumerable)urgentTrips)
        {
            var statusProp = item.GetType().GetProperty("Status");
            var value = statusProp?.GetValue(item) as string;
            if (value is not null)
                result.Add(value);
        }
        return result;
    }
}
