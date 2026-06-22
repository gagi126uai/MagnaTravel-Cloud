using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Time;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-019 (avisos "Proximos inicios"): bucket <c>UpcomingStarts</c> de /alerts — UN aviso POR RESERVA
/// vendida/confirmada cuyo primer servicio NO cancelado empieza dentro de [hoy ... hoy + ventana].
/// Reemplaza al bucket <c>ServiceDeadlines</c> de ADR-017 F1.4 (fechas limite manuales, nunca prendido).
///
/// Tambien conserva (migrados del archivo viejo AlertServiceDeadlineBucketsTests) los tests del bucket
/// <c>CostsToConfirm</c> y los de blindaje del contrato (flag OFF byte-identico + casing camelCase).
/// </summary>
public class AlertServiceUpcomingStartsTests
{
    private static DbContextOptions<AppDbContext> NewDbOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(System.Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static AlertService BuildService(AppDbContext context, bool upcomingStarts, bool catalogFlag, int alertDays = 7)
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                UpcomingUnpaidReservationAlertDays = 30,
                EnableServiceDeadlineAlerts = upcomingStarts,
                EnableCatalogFindOrCreate = catalogFlag,
                ServiceDeadlineAlertDays = alertDays
            });
        return new AlertService(context, mock.Object, NullLogger<AlertService>.Instance);
    }

    // "Hoy" con el MISMO reloj que usa el service (pared Argentina, medianoche Kind=Utc). Usar
    // DateTime.UtcNow.Date aca desincronizaria los offsets entre las 21:00 y las 00:00 ART.
    private static readonly System.DateTime Today = AgencyTimezone.TodayWallClockUtc();

    private static Reserva BuildReserva(int id, string status = EstadoReserva.Confirmed, string? responsible = "vendedor-A")
        => new()
        {
            Id = id,
            NumeroReserva = $"R-{id}",
            Name = $"Reserva {id}",
            Status = status,
            // StartDate persistido a proposito DISTINTO de los servicios: el bucket NO debe mirarlo (B1-bis).
            StartDate = Today.AddDays(20),
            ResponsibleUserId = responsible
        };

    private static HotelBooking Hotel(int id, int reservaId, System.DateTime checkIn, string status = "Solicitado")
        => new() { Id = id, ReservaId = reservaId, HotelName = $"Hotel {id}", City = "C", CheckIn = checkIn, CheckOut = checkIn.AddDays(2), Status = status };

    // El payload puede ser el objeto anonimo historico (path OFF) o el DTO AlertsResponse (path ON). Ambos se
    // leen por reflexion sobre el nombre C# (PascalCase); el casing camelCase es solo del JSON serializado.
    private static List<object> Bucket(object payload, string key)
    {
        var value = payload.GetType().GetProperty(key)?.GetValue(payload);
        return value is System.Collections.IEnumerable items
            ? items.Cast<object>().ToList()
            : new List<object>();
    }

    private static bool HasKey(object payload, string key)
    {
        var prop = payload.GetType().GetProperty(key);
        return prop != null && prop.GetValue(payload) != null;
    }

    // Serializa igual que la API real (Web defaults => PropertyNamingPolicy camelCase), para ver el casing del
    // CONTRATO y no el de las propiedades C#.
    private static string SerializeAsApi(object payload)
        => System.Text.Json.JsonSerializer.Serialize(
            payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    private static T Prop<T>(object item, string name)
        => (T)item.GetType().GetProperty(name)!.GetValue(item)!;

    private static T? PropOrNull<T>(object item, string name) where T : class
        => item.GetType().GetProperty(name)!.GetValue(item) as T;

    private static readonly AlertCallerContext Admin = new("admin", IsAdmin: true);

    // ===================== contrato: flag OFF byte-identico + casing =====================

    [Fact]
    public async Task FlagOff_PayloadIsHistoricAnonymousObject_NoNewKeys()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: false, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("admin", IsAdmin: true, CanSeeCost: true), CancellationToken.None);

        // Con ambos flags OFF el payload es el objeto anonimo historico: exactamente 3 propiedades.
        var names = payload.GetType().GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "SupplierDebts", "TotalCount", "UrgentTrips" }, names);

        // Y el JSON serializado no contiene ninguna clave nueva.
        var json = SerializeAsApi(payload);
        Assert.DoesNotContain("upcomingStarts", json);
        Assert.DoesNotContain("upcomingStartsWindowDays", json);
        Assert.DoesNotContain("costsToConfirm", json);
        Assert.Contains("\"urgentTrips\"", json);
        Assert.Contains("\"supplierDebts\"", json);
        Assert.Contains("\"totalCount\"", json);
    }

    [Fact]
    public async Task FlagOn_SerializedKeysAreCamelCase_AndIncludeWindowDays()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var json = SerializeAsApi(payload);
        // Claves nuevas en camelCase; historicas conservan su casing (no PascalCase).
        Assert.Contains("\"upcomingStarts\"", json);
        Assert.Contains("\"upcomingStartsWindowDays\":7", json);
        Assert.Contains("\"urgentTrips\"", json);
        Assert.Contains("\"supplierDebts\"", json);
        Assert.Contains("\"totalCount\"", json);
        Assert.DoesNotContain("\"UpcomingStarts\"", json);
        Assert.DoesNotContain("\"UrgentTrips\"", json);
        // El bucket viejo NO existe mas, ni siquiera con flag ON (contrato limpio, D1).
        Assert.DoesNotContain("serviceDeadlines", json);
        Assert.DoesNotContain("ServiceDeadlines", json);
    }

    [Fact]
    public async Task FlagOn_WindowDaysReflectsSetting()
    {
        await using var context = new AppDbContext(NewDbOptions());
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false, alertDays: 12);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Equal(12, Prop<int?>(payload, "UpcomingStartsWindowDays"));
    }

    // ===================== MIN entre tipos + un aviso por reserva =====================

    [Fact]
    public async Task MinAcrossSixTypes_EarliestWins_SingleAlertPerReserva()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        // Los 6 tipos en la misma reserva, cada uno con fecha distinta: gana el generico (dia +1).
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(6)));
        context.PackageBookings.Add(new PackageBooking { Id = 1, ReservaId = 1, PackageName = "P", StartDate = Today.AddDays(5) });
        context.FlightSegments.Add(new FlightSegment { Id = 1, ReservaId = 1, DepartureTime = Today.AddDays(4), Status = "HK" });
        context.TransferBookings.Add(new TransferBooking { Id = 1, ReservaId = 1, PickupDateTime = Today.AddDays(3) });
        context.AssistanceBookings.Add(new AssistanceBooking { Id = 1, ReservaId = 1, ValidFrom = Today.AddDays(2), ValidTo = Today.AddDays(9) });
        context.Servicios.Add(new ServicioReserva { Id = 1, ReservaId = 1, DepartureDate = Today.AddDays(1), Status = "Confirmado" });
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        // UN solo aviso para la reserva (no uno por servicio), con el inicio mas temprano.
        var item = Assert.Single(Bucket(payload, "UpcomingStarts"));
        Assert.Equal(Today.AddDays(1), Prop<System.DateTime>(item, "FirstStartDate"));
        Assert.Equal(1, Prop<int>(item, "DaysLeft"));
        Assert.Equal("R-1", Prop<string>(item, "NumeroReserva"));
    }

    [Fact]
    public async Task CancelledServices_ExcludedFromMin_AcrossAllTypes()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        // Servicios CANCELADOS mas tempranos en los 6 tipos (vuelo: los 4 estados cancelados) — ninguno
        // debe ganar el MIN. El unico activo es un hotel a dia +5.
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(1), status: "Cancelado"));
        context.PackageBookings.Add(new PackageBooking { Id = 1, ReservaId = 1, PackageName = "P", StartDate = Today.AddDays(1), Status = "Cancelado" });
        context.TransferBookings.Add(new TransferBooking { Id = 1, ReservaId = 1, PickupDateTime = Today.AddDays(1), Status = "Cancelado" });
        context.AssistanceBookings.Add(new AssistanceBooking { Id = 1, ReservaId = 1, ValidFrom = Today.AddDays(1), ValidTo = Today.AddDays(9), Status = "Cancelado" });
        context.Servicios.Add(new ServicioReserva { Id = 1, ReservaId = 1, DepartureDate = Today.AddDays(1), Status = "Cancelado" });
        context.FlightSegments.Add(new FlightSegment { Id = 1, ReservaId = 1, DepartureTime = Today.AddDays(1), Status = "UN" });
        context.FlightSegments.Add(new FlightSegment { Id = 2, ReservaId = 1, DepartureTime = Today.AddDays(1), Status = "UC" });
        context.FlightSegments.Add(new FlightSegment { Id = 3, ReservaId = 1, DepartureTime = Today.AddDays(1), Status = "HX" });
        context.FlightSegments.Add(new FlightSegment { Id = 4, ReservaId = 1, DepartureTime = Today.AddDays(1), Status = "NO" });
        context.HotelBookings.Add(Hotel(2, 1, Today.AddDays(5)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "UpcomingStarts"));
        Assert.Equal(Today.AddDays(5), Prop<System.DateTime>(item, "FirstStartDate"));
    }

    [Fact]
    public async Task AllServicesCancelled_NoAlert()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3), status: "Cancelado"));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload, "UpcomingStarts"));
    }

    // ===================== elegibilidad por Status (Q2 + B2-nuevo) =====================

    [Theory]
    // Presupuesto NO avisa: decision del dueño (Q2) — CAMBIO deliberado vs el mecanismo viejo, que incluia Budget.
    [InlineData(EstadoReserva.Quotation, false)]
    [InlineData(EstadoReserva.Budget, false)]
    [InlineData(EstadoReserva.InManagement, true)]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.Closed, false)]
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    public async Task Eligibility_OnlyInManagementConfirmedTraveling(string status, bool expectsAlert)
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, status));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Equal(expectsAlert ? 1 : 0, Bucket(payload, "UpcomingStarts").Count);
    }

    [Fact]
    public async Task Traveling_FirstStartToday_AlertsAsToday()
    {
        // B2-nuevo (round 3): el job de lifecycle promueve Confirmed -> Traveling a las 00:00 ART del dia
        // de inicio; el aviso rojo "Empieza HOY" debe verse igual durante TODO ese dia.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, EstadoReserva.Traveling));
        context.HotelBookings.Add(Hotel(1, 1, Today));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "UpcomingStarts"));
        Assert.Equal(0, Prop<int>(item, "DaysLeft"));
    }

    [Fact]
    public async Task Traveling_FirstStartInThePast_DoesNotAlert()
    {
        // Reserva genuinamente en viaje: el primer inicio quedo atras -> la ventana la deja afuera sola
        // (eso lo cubre urgentTrips, no este bucket).
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, EstadoReserva.Traveling));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(-1)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload, "UpcomingStarts"));
    }

    // ===================== B1-bis: NADIE debe reintroducir un prefiltro sobre Reserva.StartDate =====================

    [Fact]
    public async Task ReservaStartDateNull_ServiceInWindow_StillAlerts()
    {
        // Reserva.StartDate es editable/borrable a mano (UpdateDatesAsync): un prefiltro de fecha sobre
        // ese campo SILENCIARIA este aviso. Este test protege que el prefiltro sea solo Status+ownership.
        await using var context = new AppDbContext(NewDbOptions());
        var reserva = BuildReserva(1);
        reserva.StartDate = null;
        context.Reservas.Add(reserva);
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Single(Bucket(payload, "UpcomingStarts"));
    }

    [Fact]
    public async Task ReservaStartDateManuallyDesynced_OutOfWindow_ServiceInWindow_StillAlerts()
    {
        // StartDate persistido corrido a mano FUERA de la ventana mientras el servicio real cae adentro:
        // la verdad la dan los servicios, no el campo editable.
        await using var context = new AppDbContext(NewDbOptions());
        var reserva = BuildReserva(1);
        reserva.StartDate = Today.AddDays(40);
        context.Reservas.Add(reserva);
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "UpcomingStarts"));
        Assert.Equal(Today.AddDays(3), Prop<System.DateTime>(item, "FirstStartDate"));
    }

    // ===================== titular (Q3): Payer -> primer pasajero -> null =====================

    [Fact]
    public async Task HolderName_UsesPayerFullName_WhenPayerExists()
    {
        await using var context = new AppDbContext(NewDbOptions());
        var customer = new Customer { Id = 10, FullName = "Clara Pagadora" };
        context.Customers.Add(customer);
        var reserva = BuildReserva(1);
        reserva.PayerId = customer.Id;
        context.Reservas.Add(reserva);
        // Hay pasajeros, pero el Payer tiene prioridad.
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pasajero Uno" });
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "UpcomingStarts"));
        Assert.Equal("Clara Pagadora", PropOrNull<string>(item, "HolderName"));
    }

    [Fact]
    public async Task HolderName_FallsBackToFirstPassengerById_WhenNoPayer()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1)); // sin Payer
        // El "primer" pasajero es el de menor Id (orden de carga), no el orden de insercion.
        context.Passengers.Add(new Passenger { Id = 7, ReservaId = 1, FullName = "Pasajero Siete" });
        context.Passengers.Add(new Passenger { Id = 3, ReservaId = 1, FullName = "Pasajero Tres" });
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "UpcomingStarts"));
        Assert.Equal("Pasajero Tres", PropOrNull<string>(item, "HolderName"));
    }

    [Fact]
    public async Task HolderName_Null_WhenNoPayerAndNoPassengers()
    {
        // Contrato con el front: holderName null => la linea 2 cae al nombre de la reserva (name),
        // que SI viaja en el item. Nunca una linea rota.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "UpcomingStarts"));
        Assert.Null(PropOrNull<string>(item, "HolderName"));
        Assert.Equal("Reserva 1", Prop<string>(item, "Name"));
    }

    // ===================== ventana: bordes inclusivos + borde timezone =====================

    [Theory]
    [InlineData(0, true)]   // hoy mismo: incluido, daysLeft 0
    [InlineData(7, true)]   // ultimo dia de la ventana (X=7): incluido
    [InlineData(8, false)]  // un dia despues de la ventana: excluido
    [InlineData(-1, false)] // ayer: excluido (no hay estado "vencido")
    public async Task Window_InclusiveOnBothEdges(int daysFromToday, bool expectsAlert)
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(daysFromToday)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var bucket = Bucket(payload, "UpcomingStarts");
        Assert.Equal(expectsAlert ? 1 : 0, bucket.Count);
        if (expectsAlert)
        {
            Assert.Equal(daysFromToday, Prop<int>(bucket[0], "DaysLeft"));
        }
    }

    [Fact]
    public async Task TimezoneEdge_ServiceAt22HsWallClock_DoesNotShiftDay()
    {
        // Borde M3: las horas se guardan como "pared con Kind=Utc" (NormalizeAirportWallClock). Un vuelo
        // a las 22:00 del dia D debe contar como dia D — si alguien convirtiera de zona en el computo,
        // 22:00 ART pasaria al dia D+1 y el aviso "HOY" apareceria/desapareceria un dia corrido.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 1, ReservaId = 1, Status = "HK",
            DepartureTime = System.DateTime.SpecifyKind(Today.AddHours(22), System.DateTimeKind.Utc)
        });
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "UpcomingStarts"));
        Assert.Equal(Today, Prop<System.DateTime>(item, "FirstStartDate")); // dia D, no D+1
        Assert.Equal(0, Prop<int>(item, "DaysLeft"));
    }

    // ===================== descarte "Listo" (D3): oculta + re-armado en ambas direcciones =====================

    [Fact]
    public async Task Dismissal_MatchingFirstStart_HidesAlert()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        context.UpcomingStartAlertDismissals.Add(new UpcomingStartAlertDismissal
        {
            ReservaId = 1,
            DismissedFirstStartDate = Today.AddDays(3),
            DismissedByUserId = "vendedor-A",
            DismissedAtUtc = System.DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload, "UpcomingStarts"));
    }

    [Theory]
    [InlineData(+1)] // el inicio se ATRASO despues del descarte (descarte quedo anclado a dia+2, ahora es dia+3)
    [InlineData(-1)] // el inicio se ADELANTO despues del descarte (descarte a dia+4, ahora dia+3)
    public async Task Dismissal_DifferentFirstStart_AlertReappears(int dismissedOffsetFromActual)
    {
        // Re-armado D3: cualquier cambio del primer inicio hace que la fecha descartada deje de coincidir
        // y el aviso REAPARECE. Mejor re-avisar de mas que callar de menos.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        context.UpcomingStartAlertDismissals.Add(new UpcomingStartAlertDismissal
        {
            ReservaId = 1,
            DismissedFirstStartDate = Today.AddDays(3 + dismissedOffsetFromActual),
            DismissedByUserId = "vendedor-A",
            DismissedAtUtc = System.DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Single(Bucket(payload, "UpcomingStarts"));
    }

    [Fact]
    public async Task DismissViaService_ThenBucketHidesAlert_SameHelperBothSides()
    {
        // Cruce bucket vs dismiss (riesgo R2 del ADR): el dismiss ancla la fecha que calcula
        // UpcomingStartCalculator y el bucket debe ocultar con esa MISMA definicion. Si alguien
        // duplicara el computo en uno de los dos lados, este test rompe.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);

        var before = await service.GetAlertsAsync(Admin, CancellationToken.None);
        Assert.Single(Bucket(before, "UpcomingStarts"));

        var outcome = await service.DismissUpcomingStartAsync("1", "vendedor-A", CancellationToken.None);
        Assert.Equal(UpcomingStartDismissOutcome.Dismissed, outcome);

        var after = await service.GetAlertsAsync(Admin, CancellationToken.None);
        Assert.Empty(Bucket(after, "UpcomingStarts"));
    }

    // ===================== ownership + fail-closed =====================

    [Fact]
    public async Task Ownership_SellerSeesOnlyOwnReservas_AdminSeesAll()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, responsible: "vendedor-A"));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        context.Reservas.Add(BuildReserva(2, responsible: "vendedor-B"));
        context.HotelBookings.Add(Hotel(2, 2, Today.AddDays(4)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);

        var sellerA = await service.GetAlertsAsync(new AlertCallerContext("vendedor-A", IsAdmin: false), CancellationToken.None);
        var aBucket = Bucket(sellerA, "UpcomingStarts");
        Assert.Single(aBucket);
        Assert.Equal("R-1", Prop<string>(aBucket[0], "NumeroReserva"));

        var admin = await service.GetAlertsAsync(Admin, CancellationToken.None);
        Assert.Equal(2, Bucket(admin, "UpcomingStarts").Count);
    }

    [Fact]
    public async Task Ownership_NonAdminWithNullUserId_FailClosed_SeesNothing()
    {
        // Sin la guarda, "ResponsibleUserId == null" mostraria todas las reservas sin responsable asignado.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, responsible: null));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext(null, IsAdmin: false), CancellationToken.None);

        Assert.Empty(Bucket(payload, "UpcomingStarts"));
    }

    [Fact]
    public async Task NonAdmin_StillDoesNotReceiveFinancialBuckets()
    {
        // Regresion F1b: aunque el polling se abra a vendedores por el bucket de proximos inicios, los
        // buckets financieros globales (UrgentTrips/SupplierDebts) siguen siendo admin-only en el server.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "R-1", Name = "Urgente", Status = EstadoReserva.Confirmed, Balance = 100m, StartDate = Today.AddDays(3), ResponsibleUserId = "otro" });
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Deudor", CurrentBalance = 500m, IsActive = true });
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("vendedor-A", IsAdmin: false), CancellationToken.None);

        Assert.Empty(Bucket(payload, "UrgentTrips"));
        Assert.Empty(Bucket(payload, "SupplierDebts"));
    }

    [Fact]
    public async Task TotalCount_IncludesUpcomingStartsForNonAdmin()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, responsible: "vendedor-A"));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(3)));
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("vendedor-A", IsAdmin: false), CancellationToken.None);

        Assert.Equal(1, Prop<int>(payload, "TotalCount"));
    }

    // ===================== CostsToConfirm (migrados del archivo viejo, sin cambios de criterio) =====================

    [Fact]
    public async Task CostsToConfirm_OnlyVisibleToCallersWhoCanSeeCost()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, responsible: "vendedor-A"));
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "Maitei", City = "Posadas", CostToConfirm = true, CostToConfirmReason = "NoKnownCost" });
        await context.SaveChangesAsync();

        var service = BuildService(context, upcomingStarts: false, catalogFlag: true);

        // Caller SIN ver-costos: el bucket no aparece (ni siquiera vacio).
        var noCost = await service.GetAlertsAsync(new AlertCallerContext("vendedor-A", IsAdmin: false, CanSeeCost: false), CancellationToken.None);
        Assert.False(HasKey(noCost, "CostsToConfirm"));

        // Caller CON ver-costos: ve su servicio a confirmar, con razon y SIN montos.
        var withCost = await service.GetAlertsAsync(new AlertCallerContext("vendedor-A", IsAdmin: false, CanSeeCost: true), CancellationToken.None);
        var item = Assert.Single(Bucket(withCost, "CostsToConfirm"));
        Assert.Equal("Hotel", Prop<string>(item, "ServiceKind"));
        Assert.Equal("NoKnownCost", Prop<string>(item, "Reason"));
        // Shape SIN montos: solo reserva, tipo, etiqueta y razon.
        var propNames = item.GetType().GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("NetCost", propNames);
        Assert.DoesNotContain("Tax", propNames);
        Assert.DoesNotContain("SalePrice", propNames);
    }

    [Fact]
    public async Task CostsToConfirm_CatalogFlagOff_BucketNotPresent()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, responsible: null));
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "Maitei", City = "Posadas", CostToConfirm = true, CostToConfirmReason = "NoKnownCost" });
        await context.SaveChangesAsync();

        // Flag de catalogo OFF -> no hay bucket CostsToConfirm aunque el admin vea costos.
        var service = BuildService(context, upcomingStarts: false, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("admin", IsAdmin: true, CanSeeCost: true), CancellationToken.None);

        Assert.False(HasKey(payload, "CostsToConfirm"));
    }
}
