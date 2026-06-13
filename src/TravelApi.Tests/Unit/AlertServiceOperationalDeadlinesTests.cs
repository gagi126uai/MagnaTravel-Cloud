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
/// Auditoria ERP 2026-06-12 (items 5 y 8): las 3 alarmas nuevas de /alerts —
///  - <c>operatorPaymentDeadlines</c>: vence el pago al operador (3 dias o vencido);
///  - <c>ticketingDeadlines</c>: vence la emision del aereo / time-limit (3 dias o vencido);
///  - <c>passportExpiries</c>: pasaporte que vence dentro de los 6 meses posteriores al inicio del viaje.
///
/// Estas alarmas NO tienen feature flag (son operativas, siempre activas). Solo aplican a reservas VIVAS
/// (InManagement/Confirmed/Traveling) y servicios/pasajeros no cancelados; mismo gating de visibilidad que
/// "Proximos inicios" (admin todas; vendedor las suyas; fail-closed sin UserId).
/// </summary>
public class AlertServiceOperationalDeadlinesTests
{
    private static DbContextOptions<AppDbContext> NewDbOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(System.Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    // Los flags de los buckets viejos se dejan OFF a proposito: probamos que las alarmas nuevas funcionan
    // SIN depender de EnableServiceDeadlineAlerts/EnableCatalogFindOrCreate (no tienen flag propio).
    private static AlertService BuildService(AppDbContext context)
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                UpcomingUnpaidReservationAlertDays = 30,
                EnableServiceDeadlineAlerts = false,
                EnableCatalogFindOrCreate = false,
                ServiceDeadlineAlertDays = 7
            });
        return new AlertService(context, mock.Object, NullLogger<AlertService>.Instance);
    }

    private static readonly System.DateTime Today = AgencyTimezone.TodayWallClockUtc();
    private static readonly AlertCallerContext Admin = new("admin", IsAdmin: true);

    private static Reserva BuildReserva(int id, string status = EstadoReserva.Confirmed, string? responsible = "vendedor-A")
        => new()
        {
            Id = id,
            NumeroReserva = $"R-{id}",
            Name = $"Reserva {id}",
            Status = status,
            ResponsibleUserId = responsible
        };

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

    private static T Prop<T>(object item, string name)
        => (T)item.GetType().GetProperty(name)!.GetValue(item)!;

    private static T? PropOrNull<T>(object item, string name) where T : class
        => item.GetType().GetProperty(name)!.GetValue(item) as T;

    private static HotelBooking Hotel(int id, int reservaId, System.DateTime? operatorPaymentDeadline, string status = "Solicitado")
        => new()
        {
            Id = id, ReservaId = reservaId, HotelName = $"Hotel {id}", City = "C",
            CheckIn = Today.AddDays(30), CheckOut = Today.AddDays(32),
            Status = status, OperatorPaymentDeadline = operatorPaymentDeadline
        };

    // ===================== alarma pago al operador (item 5) =====================

    [Theory]
    [InlineData(0, true)]    // vence HOY: dentro de la ventana
    [InlineData(3, true)]    // ultimo dia de la ventana (3 dias): incluido
    [InlineData(4, false)]   // un dia despues de la ventana: NO avisa todavia
    [InlineData(-2, true)]   // YA VENCIO: avisa (lo mas urgente, sin borde inferior)
    public async Task OperatorPaymentDeadline_AppearsInsideWindowOrOverdue(int daysFromToday, bool expectsAlert)
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(daysFromToday)));
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        var bucket = Bucket(payload, "OperatorPaymentDeadlines");
        Assert.Equal(expectsAlert ? 1 : 0, bucket.Count);
        if (expectsAlert)
        {
            Assert.Equal(daysFromToday, Prop<int>(bucket[0], "DaysLeft"));
            Assert.Equal("Hotel", Prop<string>(bucket[0], "ServiceKind"));
        }
    }

    [Fact]
    public async Task OperatorPaymentDeadline_NoDeadlineSet_NoAlert()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(Hotel(1, 1, operatorPaymentDeadline: null));
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        // Sin fecha cargada -> no hay alarma -> la clave ni siquiera aparece (path byte-identico historico).
        Assert.False(HasKey(payload, "OperatorPaymentDeadlines"));
    }

    [Fact]
    public async Task OperatorPaymentDeadline_CancelledService_NoAlert()
    {
        // Un servicio cancelado NO genera alarma de pago aunque tenga deadline en ventana.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(1), status: "Cancelado"));
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload, "OperatorPaymentDeadlines"));
    }

    [Theory]
    // Solo reservas vivas avisan: cotizacion/presupuesto/cancelada/finalizada/perdida/etc NO.
    [InlineData(EstadoReserva.Quotation, false)]
    [InlineData(EstadoReserva.Budget, false)]
    [InlineData(EstadoReserva.InManagement, true)]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Traveling, true)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.ToSettle, false)]
    [InlineData(EstadoReserva.Closed, false)]
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    public async Task OperatorPaymentDeadline_OnlyLiveReservations(string status, bool expectsAlert)
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, status));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(1)));
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Equal(expectsAlert ? 1 : 0, Bucket(payload, "OperatorPaymentDeadlines").Count);
    }

    [Fact]
    public async Task OperatorPaymentDeadline_CoversAllSixServiceTypes()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        var d = Today.AddDays(1);
        context.HotelBookings.Add(Hotel(1, 1, d));
        context.PackageBookings.Add(new PackageBooking { Id = 1, ReservaId = 1, PackageName = "P", StartDate = Today.AddDays(30), OperatorPaymentDeadline = d });
        context.FlightSegments.Add(new FlightSegment { Id = 1, ReservaId = 1, Status = "HK", DepartureTime = Today.AddDays(30), OperatorPaymentDeadline = d });
        context.TransferBookings.Add(new TransferBooking { Id = 1, ReservaId = 1, PickupDateTime = Today.AddDays(30), OperatorPaymentDeadline = d });
        context.AssistanceBookings.Add(new AssistanceBooking { Id = 1, ReservaId = 1, ValidFrom = Today.AddDays(30), ValidTo = Today.AddDays(40), OperatorPaymentDeadline = d });
        context.Servicios.Add(new ServicioReserva { Id = 1, ReservaId = 1, Status = "Confirmado", DepartureDate = Today.AddDays(30), Description = "Excursion X", OperatorPaymentDeadline = d });
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        var bucket = Bucket(payload, "OperatorPaymentDeadlines");
        Assert.Equal(6, bucket.Count);
        var kinds = bucket.Select(x => Prop<string>(x, "ServiceKind")).ToHashSet();
        Assert.Equal(new HashSet<string> { "Hotel", "Paquete", "Aereo", "Traslado", "Asistencia", "Servicio" }, kinds);
    }

    // ===================== alarma time-limit aereo (item 5) =====================

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(-1, true)]  // vencido
    public async Task TicketingDeadline_AppearsInsideWindowOrOverdue(int daysFromToday, bool expectsAlert)
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 1, ReservaId = 1, Status = "HK", DepartureTime = Today.AddDays(30),
            TicketingDeadline = Today.AddDays(daysFromToday)
        });
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        var bucket = Bucket(payload, "TicketingDeadlines");
        Assert.Equal(expectsAlert ? 1 : 0, bucket.Count);
        if (expectsAlert)
            Assert.Equal(daysFromToday, Prop<int>(bucket[0], "DaysLeft"));
    }

    [Fact]
    public async Task TicketingDeadline_CancelledFlight_NoAlert()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        // UN = cancelado aereo (estados UN/UC/HX/NO).
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 1, ReservaId = 1, Status = "UN", DepartureTime = Today.AddDays(30),
            TicketingDeadline = Today.AddDays(1)
        });
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload, "TicketingDeadlines"));
    }

    [Fact]
    public async Task TicketingDeadline_CancelledReservation_NoAlert()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, EstadoReserva.Cancelled));
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 1, ReservaId = 1, Status = "HK", DepartureTime = Today.AddDays(30),
            TicketingDeadline = Today.AddDays(1)
        });
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload, "TicketingDeadlines"));
    }

    // ===================== alarma pasaporte (item 8) =====================

    [Fact]
    public async Task Passport_ExpiresWithinSixMonthsAfterTrip_Alerts()
    {
        // Viaje empieza en 30 dias; pasaporte vence 3 meses despues del viaje (< 6 meses) -> avisa.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        var tripStart = Today.AddDays(30);
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "H", City = "C", CheckIn = tripStart, CheckOut = tripStart.AddDays(2), Status = "Solicitado" });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Juan Viajero", PassportExpiry = tripStart.AddMonths(3) });
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "PassportExpiries"));
        Assert.Equal("Juan Viajero", Prop<string>(item, "PassengerName"));
        Assert.Equal(tripStart, Prop<System.DateTime>(item, "TripStartDate"));
    }

    [Fact]
    public async Task Passport_ValidWellBeyondSixMonths_NoAlert()
    {
        // Pasaporte con vigencia larga (1 año despues del viaje): NO avisa.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        var tripStart = Today.AddDays(30);
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "H", City = "C", CheckIn = tripStart, CheckOut = tripStart.AddDays(2), Status = "Solicitado" });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Ana Larga", PassportExpiry = tripStart.AddMonths(12) });
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        Assert.False(HasKey(payload, "PassportExpiries"));
    }

    [Fact]
    public async Task Passport_ExpiresExactlyAtSixMonthBoundary_NoAlert()
    {
        // Borde: vence EXACTAMENTE a los 6 meses del viaje -> cumple la regla (>= limite), no avisa.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        var tripStart = Today.AddDays(30);
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "H", City = "C", CheckIn = tripStart, CheckOut = tripStart.AddDays(2), Status = "Solicitado" });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Borde", PassportExpiry = tripStart.AddMonths(6) });
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload, "PassportExpiries"));
    }

    [Fact]
    public async Task Passport_NoExpirySet_NoAlert()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        var tripStart = Today.AddDays(30);
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "H", City = "C", CheckIn = tripStart, CheckOut = tripStart.AddDays(2), Status = "Solicitado" });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Sin Pasaporte", PassportExpiry = null });
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        Assert.False(HasKey(payload, "PassportExpiries"));
    }

    [Fact]
    public async Task Passport_CancelledReservation_NoAlert()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, EstadoReserva.Cancelled));
        var tripStart = Today.AddDays(30);
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "H", City = "C", CheckIn = tripStart, CheckOut = tripStart.AddDays(2), Status = "Solicitado" });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Cancelada", PassportExpiry = tripStart.AddMonths(3) });
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        Assert.False(HasKey(payload, "PassportExpiries"));
    }

    [Fact]
    public async Task Passport_AllServicesCancelled_NoTripStart_NoAlert()
    {
        // Sin viaje (unico servicio cancelado) no hay fecha contra la que medir la vigencia -> no avisa.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "H", City = "C", CheckIn = Today.AddDays(30), CheckOut = Today.AddDays(32), Status = "Cancelado" });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Sin Viaje", PassportExpiry = Today.AddDays(60) });
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        Assert.False(HasKey(payload, "PassportExpiries"));
    }

    // ===================== ownership + fail-closed (compartido por las 3 alarmas) =====================

    [Fact]
    public async Task Ownership_SellerSeesOnlyOwnReservas_AdminSeesAll()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, responsible: "vendedor-A"));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(1)));
        context.Reservas.Add(BuildReserva(2, responsible: "vendedor-B"));
        context.HotelBookings.Add(Hotel(2, 2, Today.AddDays(1)));
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var sellerA = await service.GetAlertsAsync(new AlertCallerContext("vendedor-A", IsAdmin: false), CancellationToken.None);
        var aBucket = Bucket(sellerA, "OperatorPaymentDeadlines");
        Assert.Single(aBucket);
        Assert.Equal("R-1", Prop<string>(aBucket[0], "NumeroReserva"));

        var admin = await service.GetAlertsAsync(Admin, CancellationToken.None);
        Assert.Equal(2, Bucket(admin, "OperatorPaymentDeadlines").Count);
    }

    [Fact]
    public async Task Ownership_NonAdminWithNullUserId_FailClosed_SeesNothing()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, responsible: null));
        context.HotelBookings.Add(Hotel(1, 1, Today.AddDays(1)));
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "X", PassportExpiry = Today.AddDays(60) });
        context.FlightSegments.Add(new FlightSegment { Id = 1, ReservaId = 1, Status = "HK", DepartureTime = Today.AddDays(30), TicketingDeadline = Today.AddDays(1) });
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(new AlertCallerContext(null, IsAdmin: false), CancellationToken.None);

        Assert.Empty(Bucket(payload, "OperatorPaymentDeadlines"));
        Assert.Empty(Bucket(payload, "TicketingDeadlines"));
        Assert.Empty(Bucket(payload, "PassportExpiries"));
    }

    // ===================== contrato: TotalCount + casing camelCase =====================

    [Fact]
    public async Task TotalCount_IncludesAllNewAlarms_AndKeysAreCamelCase()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        var tripStart = Today.AddDays(30);
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "H", City = "C", CheckIn = tripStart, CheckOut = tripStart.AddDays(2), Status = "Solicitado", OperatorPaymentDeadline = Today.AddDays(1) });
        context.FlightSegments.Add(new FlightSegment { Id = 1, ReservaId = 1, Status = "HK", DepartureTime = tripStart, TicketingDeadline = Today.AddDays(2) });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pax", PassportExpiry = tripStart.AddMonths(3) });
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        // 1 pago + 1 time-limit + 1 pasaporte = 3 (no hay buckets financieros: sin saldo ni deuda).
        Assert.Equal(3, Prop<int>(payload, "TotalCount"));

        var json = System.Text.Json.JsonSerializer.Serialize(
            payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"operatorPaymentDeadlines\"", json);
        Assert.Contains("\"ticketingDeadlines\"", json);
        Assert.Contains("\"passportExpiries\"", json);
        // Las historicas conservan su casing camelCase (no PascalCase) — el DTO tipado lo garantiza.
        Assert.Contains("\"urgentTrips\"", json);
        Assert.DoesNotContain("\"OperatorPaymentDeadlines\"", json);
    }

    [Fact]
    public async Task NoAlarmsAndNoNewBuckets_ReturnsHistoricAnonymousObject()
    {
        // Reserva viva pero SIN deadlines ni pasaportes: el payload sigue siendo el objeto historico de 3
        // propiedades (no se cuela ninguna clave nueva). Protege el path byte-identico.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1));
        context.HotelBookings.Add(Hotel(1, 1, operatorPaymentDeadline: null));
        await context.SaveChangesAsync();

        var payload = await BuildService(context).GetAlertsAsync(Admin, CancellationToken.None);

        var names = payload.GetType().GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "SupplierDebts", "TotalCount", "UrgentTrips" }, names);
    }
}
