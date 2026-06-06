using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-017 F1.4 (§2.5/§2.8, R9): buckets nuevos de /alerts compute-on-read.
///  - <c>ServiceDeadlines</c>: fechas limite de seña/pago al operador (Hotel/Paquete) y emision (Aereo),
///    gateado por <c>EnableServiceDeadlineAlerts</c>, filtrado por caller (D2), agrupado por PNR.
///  - <c>CostsToConfirm</c>: servicios marcados "costo a confirmar" (D7), gateado por el flag del catalogo
///    + <c>cobranzas.see_cost</c>, SIN montos.
/// Con ambos flags OFF /alerts queda byte-identico (regresion F1b incluida).
/// </summary>
public class AlertServiceDeadlineBucketsTests
{
    private static DbContextOptions<AppDbContext> NewDbOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(System.Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static AlertService BuildService(AppDbContext context, bool deadlineAlerts, bool catalogFlag)
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                UpcomingUnpaidReservationAlertDays = 30,
                EnableServiceDeadlineAlerts = deadlineAlerts,
                EnableCatalogFindOrCreate = catalogFlag,
                ServiceDeadlineAlertDays = 7
            });
        return new AlertService(context, mock.Object);
    }

    // Offsets robustos al desfasaje de 3h entre "hoy UTC" y "hoy Argentina".
    private static readonly System.DateTime Today = System.DateTime.UtcNow.Date;
    private static System.DateTime InWindow => Today.AddDays(3);   // dentro de la ventana de 7 dias
    private static System.DateTime OutOfWindow => Today.AddDays(30); // fuera de la ventana
    private static System.DateTime Overdue => Today.AddDays(-5);    // ya vencido

    private static Reserva ActiveReserva(int id, string? responsible)
        => new()
        {
            Id = id,
            NumeroReserva = $"R-{id}",
            Name = $"Reserva {id}",
            Status = EstadoReserva.Confirmed,
            StartDate = Today.AddDays(20),
            ResponsibleUserId = responsible
        };

    // El payload puede ser el objeto anonimo historico (path OFF) o el DTO AlertsResponse (path ON). Ambos se
    // leen por reflexion sobre el nombre C# (PascalCase); el casing camelCase es solo del JSON serializado.
    private static List<object> Bucket(object payload, string key)
    {
        var value = payload.GetType().GetProperty(key)?.GetValue(payload);
        return value is System.Collections.IEnumerable items
            ? items.Cast<object>().ToList()
            : new List<object>();
    }

    // "Tiene la clave" = la clave APARECERIA en el JSON. En el anonimo OFF la propiedad ni existe; en el DTO ON
    // existe siempre pero vale null cuando el bucket esta apagado (y entonces se omite del JSON). Ambos => false.
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

    // ===================== ServiceDeadlines: gating + inclusion/exclusion =====================

    [Fact]
    public async Task ServiceDeadlines_FlagOff_BucketNotPresent_PayloadByteIdentical()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ActiveReserva(1, null));
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, HotelName = "Maitei", City = "Posadas",
            OperatorPaymentDeadline = InWindow
        });
        await context.SaveChangesAsync();

        var service = BuildService(context, deadlineAlerts: false, catalogFlag: false);
        dynamic result = await service.GetAlertsAsync(new AlertCallerContext("admin", IsAdmin: true, CanSeeCost: true), CancellationToken.None);

        // Con ambos flags OFF el payload es el objeto anonimo historico: exactamente 3 propiedades.
        var names = ((object)result).GetType().GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "SupplierDebts", "TotalCount", "UrgentTrips" }, names);
    }

    [Fact]
    public async Task ServiceDeadlines_Admin_IncludesHotelPackageFlightInWindow()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ActiveReserva(1, "vendedor-A"));
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "Maitei", City = "Posadas", OperatorPaymentDeadline = InWindow });
        context.PackageBookings.Add(new PackageBooking { Id = 1, ReservaId = 1, PackageName = "Caribe", Destination = "Cancun", OperatorPaymentDeadline = InWindow });
        context.FlightSegments.Add(new FlightSegment { Id = 1, ReservaId = 1, AirlineCode = "AR", FlightNumber = "100", Origin = "EZE", Destination = "MIA", PNR = "ABC123", TicketingDeadline = InWindow });
        await context.SaveChangesAsync();

        var service = BuildService(context, deadlineAlerts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("admin", IsAdmin: true), CancellationToken.None);

        var bucket = Bucket(payload, "ServiceDeadlines");
        Assert.Equal(3, bucket.Count);
        Assert.Contains(bucket, x => Prop<string>(x, "ServiceKind") == "Hotel" && Prop<string>(x, "DeadlineKind") == "OperatorPayment");
        Assert.Contains(bucket, x => Prop<string>(x, "ServiceKind") == "Paquete" && Prop<string>(x, "DeadlineKind") == "OperatorPayment");
        Assert.Contains(bucket, x => Prop<string>(x, "ServiceKind") == "Aereo" && Prop<string>(x, "DeadlineKind") == "Ticketing");
    }

    [Fact]
    public async Task ServiceDeadlines_ExcludesCancelledOrClosedReservaServiceAndStartedTrip()
    {
        await using var context = new AppDbContext(NewDbOptions());
        // Reserva cancelada
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "R-1", Name = "Cancelada", Status = EstadoReserva.Cancelled, StartDate = Today.AddDays(20) });
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "H1", City = "C", OperatorPaymentDeadline = InWindow });
        // Reserva cerrada
        context.Reservas.Add(new Reserva { Id = 2, NumeroReserva = "R-2", Name = "Cerrada", Status = EstadoReserva.Closed, StartDate = Today.AddDays(20) });
        context.HotelBookings.Add(new HotelBooking { Id = 2, ReservaId = 2, HotelName = "H2", City = "C", OperatorPaymentDeadline = InWindow });
        // Reserva con viaje ya empezado (StartDate pasada)
        context.Reservas.Add(new Reserva { Id = 3, NumeroReserva = "R-3", Name = "Viajando", Status = EstadoReserva.Confirmed, StartDate = Today.AddDays(-2) });
        context.HotelBookings.Add(new HotelBooking { Id = 3, ReservaId = 3, HotelName = "H3", City = "C", OperatorPaymentDeadline = InWindow });
        // Reserva activa con servicio CANCELADO
        context.Reservas.Add(ActiveReserva(4, null));
        context.HotelBookings.Add(new HotelBooking { Id = 4, ReservaId = 4, HotelName = "H4", City = "C", Status = "Cancelado", OperatorPaymentDeadline = InWindow });
        // Reserva activa con deadline FUERA de ventana
        context.Reservas.Add(ActiveReserva(5, null));
        context.HotelBookings.Add(new HotelBooking { Id = 5, ReservaId = 5, HotelName = "H5", City = "C", OperatorPaymentDeadline = OutOfWindow });
        await context.SaveChangesAsync();

        var service = BuildService(context, deadlineAlerts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("admin", IsAdmin: true), CancellationToken.None);

        Assert.Empty(Bucket(payload, "ServiceDeadlines"));
    }

    [Fact]
    public async Task ServiceDeadlines_OverdueDeadline_MarkedIsOverdue()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ActiveReserva(1, null));
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "Maitei", City = "Posadas", OperatorPaymentDeadline = Overdue });
        await context.SaveChangesAsync();

        var service = BuildService(context, deadlineAlerts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("admin", IsAdmin: true), CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "ServiceDeadlines"));
        Assert.True(Prop<bool>(item, "IsOverdue"));
    }

    [Fact]
    public async Task ServiceDeadlines_SellerSeesOnlyOwnReservas_AdminSeesAll()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ActiveReserva(1, "vendedor-A"));
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "De A", City = "C", OperatorPaymentDeadline = InWindow });
        context.Reservas.Add(ActiveReserva(2, "vendedor-B"));
        context.HotelBookings.Add(new HotelBooking { Id = 2, ReservaId = 2, HotelName = "De B", City = "C", OperatorPaymentDeadline = InWindow });
        await context.SaveChangesAsync();

        var service = BuildService(context, deadlineAlerts: true, catalogFlag: false);

        var sellerA = await service.GetAlertsAsync(new AlertCallerContext("vendedor-A", IsAdmin: false), CancellationToken.None);
        var aBucket = Bucket(sellerA, "ServiceDeadlines");
        Assert.Single(aBucket);
        Assert.Equal("De A", Prop<string>(aBucket[0], "ServiceLabel"));

        var admin = await service.GetAlertsAsync(new AlertCallerContext("admin", IsAdmin: true), CancellationToken.None);
        Assert.Equal(2, Bucket(admin, "ServiceDeadlines").Count);
    }

    [Fact]
    public async Task ServiceDeadlines_NonAdmin_StillDoesNotReceiveFinancialBuckets()
    {
        // Regresion F1b: aunque el polling se abra a vendedores por el bucket de deadlines, los buckets
        // financieros globales (UrgentTrips/SupplierDebts) siguen siendo admin-only en el server.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "R-1", Name = "Urgente", Status = EstadoReserva.Confirmed, Balance = 100m, StartDate = Today.AddDays(3), ResponsibleUserId = "otro" });
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Deudor", CurrentBalance = 500m, IsActive = true });
        await context.SaveChangesAsync();

        var service = BuildService(context, deadlineAlerts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("vendedor-A", IsAdmin: false), CancellationToken.None);

        Assert.Empty(Bucket(payload, "UrgentTrips"));
        Assert.Empty(Bucket(payload, "SupplierDebts"));
    }

    // ===================== ServiceDeadlines: agrupacion aerea por PNR =====================

    [Fact]
    public async Task ServiceDeadlines_FlightsSamePnr_GroupedWithMinDeadline_TbdAndNullIndividual()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ActiveReserva(1, null));
        // Dos segmentos mismo PNR -> 1 aviso con MIN(deadline).
        context.FlightSegments.Add(new FlightSegment { Id = 1, ReservaId = 1, AirlineCode = "AR", FlightNumber = "100", Origin = "EZE", Destination = "MIA", PNR = "XYZ999", TicketingDeadline = Today.AddDays(5) });
        context.FlightSegments.Add(new FlightSegment { Id = 2, ReservaId = 1, AirlineCode = "AR", FlightNumber = "101", Origin = "MIA", Destination = "EZE", PNR = "XYZ999", TicketingDeadline = Today.AddDays(3) });
        // PNR "TBD" (placeholder) -> aviso individual.
        context.FlightSegments.Add(new FlightSegment { Id = 3, ReservaId = 1, AirlineCode = "LA", FlightNumber = "200", Origin = "EZE", Destination = "SCL", PNR = "tbd", TicketingDeadline = InWindow });
        // PNR null -> aviso individual.
        context.FlightSegments.Add(new FlightSegment { Id = 4, ReservaId = 1, AirlineCode = "LA", FlightNumber = "201", Origin = "SCL", Destination = "EZE", PNR = null, TicketingDeadline = InWindow });
        await context.SaveChangesAsync();

        var service = BuildService(context, deadlineAlerts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("admin", IsAdmin: true), CancellationToken.None);

        var bucket = Bucket(payload, "ServiceDeadlines");
        // 1 agrupado (XYZ999) + 2 individuales (TBD, null) = 3.
        Assert.Equal(3, bucket.Count);
        var grouped = bucket.Single(x => Prop<string>(x, "ServiceLabel").Contains("XYZ999"));
        Assert.Equal(Today.AddDays(3), Prop<System.DateTime>(grouped, "Deadline")); // MIN de los dos segmentos
    }

    // ===================== CostsToConfirm: gating por permiso + sin montos =====================

    [Fact]
    public async Task CostsToConfirm_OnlyVisibleToCallersWhoCanSeeCost()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ActiveReserva(1, "vendedor-A"));
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "Maitei", City = "Posadas", CostToConfirm = true, CostToConfirmReason = "NoKnownCost" });
        await context.SaveChangesAsync();

        var service = BuildService(context, deadlineAlerts: false, catalogFlag: true);

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
        context.Reservas.Add(ActiveReserva(1, null));
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "Maitei", City = "Posadas", CostToConfirm = true, CostToConfirmReason = "NoKnownCost" });
        await context.SaveChangesAsync();

        // Flag de catalogo OFF -> no hay bucket CostsToConfirm aunque el admin vea costos.
        var service = BuildService(context, deadlineAlerts: false, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("admin", IsAdmin: true, CanSeeCost: true), CancellationToken.None);

        Assert.False(HasKey(payload, "CostsToConfirm"));
    }

    [Fact]
    public async Task FlagsOff_SerializedPayload_HasOnlyHistoricKeys_InCamelCase_AndFinancialContentIntact()
    {
        // Blindaje del "byte-identico": el path OFF se serializa (con las opciones REALES de la API = camelCase)
        // con las MISMAS 3 claves y el contenido de los buckets financieros viaja completo (los elementos boxeados
        // como object usan su tipo runtime, no salen como "{}"). El casing del contrato es camelCase.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "R-URG", Name = "Urgente", Status = EstadoReserva.Confirmed, Balance = 100m, StartDate = Today.AddDays(3) });
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Deudor", CurrentBalance = 500m, IsActive = true });
        await context.SaveChangesAsync();

        var service = BuildService(context, deadlineAlerts: false, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("admin", IsAdmin: true, CanSeeCost: true), CancellationToken.None);

        var json = SerializeAsApi(payload);
        // Claves historicas en camelCase (el casing real del contrato).
        Assert.Contains("\"urgentTrips\"", json);
        Assert.Contains("\"supplierDebts\"", json);
        Assert.Contains("\"totalCount\"", json);
        // NO deben salir en PascalCase (el bug que ocultaba el test anterior al serializar con opciones default).
        Assert.DoesNotContain("\"UrgentTrips\"", json);
        Assert.DoesNotContain("\"SupplierDebts\"", json);
        Assert.DoesNotContain("\"TotalCount\"", json);
        // Buckets nuevos ausentes con ambos flags OFF.
        Assert.DoesNotContain("serviceDeadlines", json);
        Assert.DoesNotContain("costsToConfirm", json);
        // El elemento de UrgentTrips se serializo COMPLETO (runtime type), no como objeto vacio.
        Assert.Contains("R-URG", json);
        Assert.Contains("Deudor", json);
    }

    [Fact]
    public async Task FlagOn_SerializedPayload_KeysAreCamelCase_PreexistingKeysDoNotChangeCasing()
    {
        // BLOQUEANTE B1 (F1.4 review): prender un bucket nuevo NO debe renombrar las claves historicas. Antes el
        // path ON usaba Dictionary<string,object> y System.Text.Json dejaba las claves verbatim (PascalCase),
        // mientras el path OFF (objeto anonimo) emitia camelCase -> el mismo endpoint cambiaba de casing con el
        // flag. Con el DTO AlertsResponse el casing es camelCase en AMBOS paths.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ActiveReserva(1, "vendedor-A"));
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, HotelName = "Maitei", City = "Posadas",
            OperatorPaymentDeadline = InWindow, CostToConfirm = true, CostToConfirmReason = "NoKnownCost"
        });
        await context.SaveChangesAsync();

        // Ambos buckets nuevos activos (deadlines + catalogo) y caller que ve costos.
        var service = BuildService(context, deadlineAlerts: true, catalogFlag: true);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("admin", IsAdmin: true, CanSeeCost: true), CancellationToken.None);

        var json = SerializeAsApi(payload);
        // Claves nuevas en camelCase.
        Assert.Contains("\"serviceDeadlines\"", json);
        Assert.Contains("\"costsToConfirm\"", json);
        // Claves historicas: MISMO casing que en el path OFF (camelCase), NO PascalCase.
        Assert.Contains("\"urgentTrips\"", json);
        Assert.Contains("\"supplierDebts\"", json);
        Assert.Contains("\"totalCount\"", json);
        Assert.DoesNotContain("\"UrgentTrips\"", json);
        Assert.DoesNotContain("\"SupplierDebts\"", json);
        Assert.DoesNotContain("\"TotalCount\"", json);
        Assert.DoesNotContain("\"ServiceDeadlines\"", json);
        Assert.DoesNotContain("\"CostsToConfirm\"", json);
    }

    [Fact]
    public async Task ServiceDeadlines_NonAdminWithNullUserId_DoesNotSeeUnassignedReservas()
    {
        // BLOQUEANTE seguridad (F1.4 review): un no-admin sin claim de identidad (UserId null) NO debe ver los
        // deadlines de reservas con ResponsibleUserId null. Sin la guarda, EF expande "ResponsibleUserId == null"
        // y se las mostraria todas.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ActiveReserva(1, null)); // ResponsibleUserId null (sin asignar)
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "Huerfana", City = "C", OperatorPaymentDeadline = InWindow });
        await context.SaveChangesAsync();

        var service = BuildService(context, deadlineAlerts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext(null, IsAdmin: false), CancellationToken.None);

        Assert.Empty(Bucket(payload, "ServiceDeadlines"));
    }

    [Fact]
    public async Task TotalCount_IncludesNewBucketsForNonAdmin()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(ActiveReserva(1, "vendedor-A"));
        context.HotelBookings.Add(new HotelBooking { Id = 1, ReservaId = 1, HotelName = "Maitei", City = "Posadas", OperatorPaymentDeadline = InWindow });
        await context.SaveChangesAsync();

        var service = BuildService(context, deadlineAlerts: true, catalogFlag: false);
        var payload = await service.GetAlertsAsync(new AlertCallerContext("vendedor-A", IsAdmin: false), CancellationToken.None);

        Assert.Equal(1, Prop<int>(payload, "TotalCount"));
    }
}
