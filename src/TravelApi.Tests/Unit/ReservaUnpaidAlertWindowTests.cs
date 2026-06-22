using System;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-037 (2026-06-21): cobertura PURA de la ventana del aviso "Debe — no viaja" (flag
/// <c>IsWithinUnpaidAlertWindow</c>). Replica el criterio del job nocturno de notificaciones (deuda +
/// salida en ventana + notificacion habilitada). "Hoy" se inyecta para testear sin reloj.
/// </summary>
public class ReservaUnpaidAlertWindowTests
{
    private static readonly DateTime Today = new(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Within_WhenDebtAndStartInWindowAndNotificationsOn()
    {
        // Deuda + salida en 5 dias (ventana 7) + notif ON -> entra en el aviso.
        var result = ReservaUnpaidAlertWindow.IsWithin(
            notificationsEnabled: true,
            alertDays: 7,
            balance: 500m,
            startDate: Today.AddDays(5),
            today: Today);

        Assert.True(result);
    }

    [Fact]
    public void NotWithin_WhenNoDebt()
    {
        // Sin deuda (saldado): no hay nada que avisar.
        var result = ReservaUnpaidAlertWindow.IsWithin(
            notificationsEnabled: true,
            alertDays: 7,
            balance: 0m,
            startDate: Today.AddDays(5),
            today: Today);

        Assert.False(result);
    }

    [Fact]
    public void NotWithin_WhenStartOutsideWindow()
    {
        // Sale dentro de 30 dias, ventana de 7: queda fuera de la ventana.
        var result = ReservaUnpaidAlertWindow.IsWithin(
            notificationsEnabled: true,
            alertDays: 7,
            balance: 500m,
            startDate: Today.AddDays(30),
            today: Today);

        Assert.False(result);
    }

    [Fact]
    public void NotWithin_WhenStartInThePast()
    {
        // Salida en el pasado (el viaje ya arranco): el aviso "no viaja" no aplica (lo cubre otra alerta).
        var result = ReservaUnpaidAlertWindow.IsWithin(
            notificationsEnabled: true,
            alertDays: 7,
            balance: 500m,
            startDate: Today.AddDays(-1),
            today: Today);

        Assert.False(result);
    }

    [Fact]
    public void NotWithin_WhenNotificationsDisabled()
    {
        // Notificacion deshabilitada: el flag nunca prende, aunque haya deuda y salida proxima.
        var result = ReservaUnpaidAlertWindow.IsWithin(
            notificationsEnabled: false,
            alertDays: 7,
            balance: 500m,
            startDate: Today.AddDays(5),
            today: Today);

        Assert.False(result);
    }

    [Fact]
    public void NotWithin_WhenNoStartDate()
    {
        // Sin fecha de salida no se puede ubicar en la ventana.
        var result = ReservaUnpaidAlertWindow.IsWithin(
            notificationsEnabled: true,
            alertDays: 7,
            balance: 500m,
            startDate: null,
            today: Today);

        Assert.False(result);
    }

    [Fact]
    public void Within_OnTheBordersOfTheWindow()
    {
        // Borde inferior: sale HOY. Borde superior: sale el ultimo dia de la ventana. Ambos inclusivos.
        Assert.True(ReservaUnpaidAlertWindow.IsWithin(true, 7, 500m, Today, Today));
        Assert.True(ReservaUnpaidAlertWindow.IsWithin(true, 7, 500m, Today.AddDays(7), Today));
        Assert.False(ReservaUnpaidAlertWindow.IsWithin(true, 7, 500m, Today.AddDays(8), Today));
    }
}
