using System;
using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Semaforo de DNI vencido para cabotaje (decision firmada del dueño, 2026-08-03): tests PUROS de
/// <see cref="DniExpiryRules"/> (sin DB, sin ReservaService), espejo de como se testearia
/// <c>PassportExpiryRules</c> si tuviera su propio archivo. Cubre TODAS las combinaciones descriptas
/// en el plan: tipo no-DNI, sin vencimiento, sin servicio Nacional, pasaporte que cubre, borde exacto
/// fin de viaje, y el caso sin fechas de viaje.
/// </summary>
public class DniExpiryRulesTests
{
    private static readonly DateTime Today = new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TipoNoDni_NoAvisaAunqueElRestoDeLasCondicionesSeCumplan()
    {
        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "Pasaporte",
            documentExpiry: Today.AddDays(-10),
            reservaHasDomesticService: true,
            passportExpiry: null,
            tripStart: Today.AddDays(5),
            tripEnd: Today.AddDays(15),
            todayInArgentina: Today);

        Assert.Null(alert);
    }

    [Fact]
    public void SinVencimientoCargado_NoAvisa()
    {
        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "DNI",
            documentExpiry: null,
            reservaHasDomesticService: true,
            passportExpiry: null,
            tripStart: Today.AddDays(5),
            tripEnd: Today.AddDays(15),
            todayInArgentina: Today);

        Assert.Null(alert);
    }

    [Fact]
    public void SinServicioNacionalEnLaReserva_NoAvisaAunqueElDniEsteVencido()
    {
        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "DNI",
            documentExpiry: Today.AddDays(-100),
            reservaHasDomesticService: false,
            passportExpiry: null,
            tripStart: Today.AddDays(5),
            tripEnd: Today.AddDays(15),
            todayInArgentina: Today);

        Assert.Null(alert);
    }

    [Fact]
    public void ConFechasDeViaje_DniVenceAntesDelFinDelViaje_AvisaRojoConTextoDeViaje()
    {
        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "DNI",
            documentExpiry: Today.AddDays(10), // vence EN MEDIO del viaje
            reservaHasDomesticService: true,
            passportExpiry: null,
            tripStart: Today.AddDays(5),
            tripEnd: Today.AddDays(15),
            todayInArgentina: Today);

        Assert.NotNull(alert);
        Assert.Equal(DniAlertLevel.Expired, alert!.Level);
        // T-6: literal EXACTO, no la constante (si alguien cambia el texto sin darse cuenta, este test
        // tiene que romper — comparar contra la propia constante es una tautologia que nunca falla).
        Assert.Equal(
            "El DNI de este pasajero se vence antes del viaje. Para volar dentro del país piden DNI vigente (o pasaporte vigente).",
            alert.Text);
    }

    [Fact]
    public void ConFechasDeViaje_BordeExacto_DniVenceElMismoDiaQueTerminaElViaje_Avisa()
    {
        var tripEnd = Today.AddDays(15);

        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "DNI",
            documentExpiry: tripEnd, // vence EXACTO el ultimo dia del viaje
            reservaHasDomesticService: true,
            passportExpiry: null,
            tripStart: Today.AddDays(5),
            tripEnd: tripEnd,
            todayInArgentina: Today);

        Assert.NotNull(alert);
        Assert.Equal(DniAlertLevel.Expired, alert!.Level);
    }

    [Fact]
    public void ConFechasDeViaje_DniVenceUnDiaDespuesDelFinDelViaje_NoAvisa()
    {
        var tripEnd = Today.AddDays(15);

        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "DNI",
            documentExpiry: tripEnd.AddDays(1), // le alcanza justo
            reservaHasDomesticService: true,
            passportExpiry: null,
            tripStart: Today.AddDays(5),
            tripEnd: tripEnd,
            todayInArgentina: Today);

        Assert.Null(alert);
    }

    [Fact]
    public void ConFechasDeViaje_PasaporteVigenteQueCubreElViaje_NoAvisaAunqueElDniEsteVencido()
    {
        var tripEnd = Today.AddDays(15);

        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "DNI",
            documentExpiry: Today.AddDays(-5), // DNI ya vencido
            reservaHasDomesticService: true,
            passportExpiry: tripEnd.AddYears(1), // pasaporte vigente MUCHO despues del viaje
            tripStart: Today.AddDays(5),
            tripEnd: tripEnd,
            todayInArgentina: Today);

        Assert.Null(alert);
    }

    [Fact]
    public void ConFechasDeViaje_PasaporteVenceAntesDelFinDelViaje_NoCubreYElAvisoDeDniSigueEnPie()
    {
        var tripEnd = Today.AddDays(15);

        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "DNI",
            documentExpiry: Today.AddDays(10),
            reservaHasDomesticService: true,
            passportExpiry: tripEnd.AddDays(-1), // el pasaporte TAMPOCO le alcanza
            tripStart: Today.AddDays(5),
            tripEnd: tripEnd,
            todayInArgentina: Today);

        Assert.NotNull(alert);
        Assert.Equal(DniAlertLevel.Expired, alert!.Level);
    }

    [Fact]
    public void ConFechasDeViaje_DniHolgado_NoAvisa()
    {
        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "DNI",
            documentExpiry: Today.AddYears(3),
            reservaHasDomesticService: true,
            passportExpiry: null,
            tripStart: Today.AddDays(5),
            tripEnd: Today.AddDays(15),
            todayInArgentina: Today);

        Assert.Null(alert);
    }

    [Fact]
    public void SinFechasDeViaje_DniYaVencidoHoy_AvisaConTextoSinFechas()
    {
        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "DNI",
            documentExpiry: Today.AddDays(-1),
            reservaHasDomesticService: true,
            passportExpiry: null,
            tripStart: null,
            tripEnd: null,
            todayInArgentina: Today);

        Assert.NotNull(alert);
        Assert.Equal(DniAlertLevel.Expired, alert!.Level);
        // T-6: literal EXACTO, no la constante (misma razon que arriba: evitar la tautologia).
        Assert.Equal("El DNI de este pasajero está vencido.", alert.Text);
    }

    [Fact]
    public void SinFechasDeViaje_DniVenceHoyMismo_NoAvisa()
    {
        // Mismo criterio que PassportExpiryRules sin fechas: solo "expiry < today" dispara, "== today" no.
        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "DNI",
            documentExpiry: Today,
            reservaHasDomesticService: true,
            passportExpiry: null,
            tripStart: null,
            tripEnd: null,
            todayInArgentina: Today);

        Assert.Null(alert);
    }

    [Fact]
    public void SinFechasDeViaje_DniTodaviaVigente_NoAvisa()
    {
        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "DNI",
            documentExpiry: Today.AddYears(2),
            reservaHasDomesticService: true,
            passportExpiry: null,
            tripStart: null,
            tripEnd: null,
            todayInArgentina: Today);

        Assert.Null(alert);
    }

    [Fact]
    public void SoloConFechaDeInicio_UsaElInicioComoFallbackDeFinDeViaje()
    {
        // Sin EndDate cargado, se usa StartDate como "fin del viaje" (mismo fallback que PassportExpiryRules).
        var tripStart = Today.AddDays(10);

        var alert = DniExpiryRules.GetAlertOrNull(
            documentType: "DNI",
            documentExpiry: tripStart, // vence el mismo dia del (unico) inicio conocido
            reservaHasDomesticService: true,
            passportExpiry: null,
            tripStart: tripStart,
            tripEnd: null,
            todayInArgentina: Today);

        Assert.NotNull(alert);
        Assert.Equal(DniAlertLevel.Expired, alert!.Level);
    }
}
