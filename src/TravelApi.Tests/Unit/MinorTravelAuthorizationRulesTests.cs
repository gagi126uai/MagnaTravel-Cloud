using System;
using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// PARTE 3 de la obra "gate ámbito" (decisión firmada del dueño, 2026-08-05): tests PUROS de
/// <see cref="MinorTravelAuthorizationRules"/> (sin DB, sin ReservaService), espejo de como se testean
/// <see cref="DniExpiryRules"/>/<see cref="PassportExpiryRules"/>. Cubre: sin fecha de nacimiento, sin
/// tramo Internacional, mayor de edad, menor de edad, y el borde "cumple 18 durante el viaje".
/// </summary>
public class MinorTravelAuthorizationRulesTests
{
    private static readonly DateTime Today = new(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SinFechaDeNacimiento_NoAvisa_SilencioTotal()
    {
        var alert = MinorTravelAuthorizationRules.GetAlertOrNull(
            birthDate: null,
            reservaHasInternationalService: true,
            tripStart: Today.AddDays(5),
            tripEnd: Today.AddDays(15),
            todayInArgentina: Today);

        Assert.Null(alert);
    }

    [Fact]
    public void SinTramoInternacional_NoAvisaAunqueSeaMenor()
    {
        var birthDate = Today.AddYears(-10); // 10 años

        var alert = MinorTravelAuthorizationRules.GetAlertOrNull(
            birthDate: birthDate,
            reservaHasInternationalService: false,
            tripStart: Today.AddDays(5),
            tripEnd: Today.AddDays(15),
            todayInArgentina: Today);

        Assert.Null(alert);
    }

    [Fact]
    public void MenorDeEdadConTramoInternacional_Avisa()
    {
        var birthDate = Today.AddYears(-10); // 10 años, claramente menor

        var alert = MinorTravelAuthorizationRules.GetAlertOrNull(
            birthDate: birthDate,
            reservaHasInternationalService: true,
            tripStart: Today.AddDays(5),
            tripEnd: Today.AddDays(15),
            todayInArgentina: Today);

        Assert.NotNull(alert);
        Assert.Equal(MinorTravelAlertLevel.Notice, alert!.Level);
        // T-6: literal EXACTO, no la constante (tautología si comparamos contra la propia constante).
        Assert.Equal(
            "Pasajero menor de edad en un tramo internacional. Revisá si necesita autorización para " +
            "salir del país: el trámite varía según el destino y con quién viaja.",
            alert.Text);
    }

    [Fact]
    public void MayorDeEdadConTramoInternacional_NoAvisa()
    {
        var birthDate = Today.AddYears(-30); // 30 años

        var alert = MinorTravelAuthorizationRules.GetAlertOrNull(
            birthDate: birthDate,
            reservaHasInternationalService: true,
            tripStart: Today.AddDays(5),
            tripEnd: Today.AddDays(15),
            todayInArgentina: Today);

        Assert.Null(alert);
    }

    [Fact]
    public void CumpleDieciochoJustoElUltimoDiaDelViaje_YaEsMayor_NoAvisa()
    {
        var tripEnd = Today.AddDays(15);
        var birthDate = tripEnd.AddYears(-18); // cumple 18 EXACTO el ultimo dia del viaje

        var alert = MinorTravelAuthorizationRules.GetAlertOrNull(
            birthDate: birthDate,
            reservaHasInternationalService: true,
            tripStart: Today.AddDays(5),
            tripEnd: tripEnd,
            todayInArgentina: Today);

        Assert.Null(alert);
    }

    [Fact]
    public void CumpleDieciochoUnDiaDespuesDeTerminarElViaje_SigueSiendoMenorDuranteElViaje_Avisa()
    {
        var tripEnd = Today.AddDays(15);
        var birthDate = tripEnd.AddDays(1).AddYears(-18); // cumple 18 el dia SIGUIENTE al fin del viaje

        var alert = MinorTravelAuthorizationRules.GetAlertOrNull(
            birthDate: birthDate,
            reservaHasInternationalService: true,
            tripStart: Today.AddDays(5),
            tripEnd: tripEnd,
            todayInArgentina: Today);

        Assert.NotNull(alert);
        Assert.Equal(MinorTravelAlertLevel.Notice, alert!.Level);
    }

    [Fact]
    public void SoloConFechaDeInicio_UsaElInicioComoFallbackDeFinDeViaje()
    {
        var tripStart = Today.AddDays(10);
        var birthDate = tripStart.AddYears(-15); // 15 años al inicio (unico dato de viaje conocido)

        var alert = MinorTravelAuthorizationRules.GetAlertOrNull(
            birthDate: birthDate,
            reservaHasInternationalService: true,
            tripStart: tripStart,
            tripEnd: null,
            todayInArgentina: Today);

        Assert.NotNull(alert);
    }

    [Fact]
    public void SinNingunaFechaDeViaje_UsaHoyComoReferencia()
    {
        var birthDate = Today.AddYears(-10); // 10 años hoy

        var alert = MinorTravelAuthorizationRules.GetAlertOrNull(
            birthDate: birthDate,
            reservaHasInternationalService: true,
            tripStart: null,
            tripEnd: null,
            todayInArgentina: Today);

        Assert.NotNull(alert);
    }

    [Fact]
    public void SinNingunaFechaDeViaje_YaEsMayorHoy_NoAvisa()
    {
        var birthDate = Today.AddYears(-40); // 40 años hoy

        var alert = MinorTravelAuthorizationRules.GetAlertOrNull(
            birthDate: birthDate,
            reservaHasInternationalService: true,
            tripStart: null,
            tripEnd: null,
            todayInArgentina: Today);

        Assert.Null(alert);
    }
}
