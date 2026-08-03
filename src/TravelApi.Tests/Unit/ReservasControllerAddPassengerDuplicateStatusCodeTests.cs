using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.Interfaces;
using TravelApi.Controllers;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Q1 (tanda Q, plan 2026-07-31 tarde): <see cref="PassengerDuplicateDocumentAndPassportAlertTests"/>
/// ya blinda que <see cref="PassengerDuplicateDocumentGuard"/> frena el duplicado A NIVEL DEL MOTOR
/// (<c>ReservaService</c>), pero ningun test verificaba que ese rechazo LLEGA al vendedor como 409 con
/// un mensaje legible — el mapeo vive en <see cref="ReservasController.AddPassenger"/>
/// (<c>catch (InvalidOperationException)</c> → <c>Conflict</c>). Este test cierra ese hueco: mockea el
/// motor devolviendo EXACTAMENTE la excepcion que tira el guard real y verifica el cuerpo HTTP completo
/// (status + mensaje sin jerga tecnica), no solo que "no explota".
/// </summary>
public class ReservasControllerAddPassengerDuplicateStatusCodeTests
{
    private static ReservasController BuildController(Mock<IReservaService> reservaService)
    {
        return new ReservasController(
            reservaService.Object,
            Mock.Of<IVoucherService>(),
            Mock.Of<ITimelineService>(),
            Mock.Of<ISupplierService>(),
            Mock.Of<IEntityReferenceResolver>(),
            Mock.Of<IBookingService>(),
            NullLogger<ReservasController>.Instance);
    }

    private static PassengerUpsertRequest RequestConMismoDocumentoQueElTitular() => new(
        FullName: "Acompañante Nuevo",
        DocumentType: "DNI",
        DocumentNumber: "30111222",
        BirthDate: null,
        Nationality: null,
        Phone: null,
        Email: null,
        Gender: null,
        Notes: null);

    [Fact]
    public async Task AddPassenger_ElMotorRechazaPorDocumentoDuplicado_Devuelve409ConMensajeCriolloYSinJerga()
    {
        // ARRANGE — el motor (ReservaService real, ya probado aparte) tira EXACTAMENTE el mismo tipo
        // de excepcion y el mismo texto que arma PassengerDuplicateDocumentGuard.BuildDuplicateMessage.
        var mensajeDelGuard = PassengerDuplicateDocumentGuard.BuildDuplicateMessage("Titular Existente");
        var reservaService = new Mock<IReservaService>();
        reservaService
            .Setup(s => s.AddPassengerAsync("F-2026-1000", It.IsAny<PassengerUpsertRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(mensajeDelGuard));

        var controller = BuildController(reservaService);

        // ACT
        var result = await controller.AddPassenger("F-2026-1000", RequestConMismoDocumentoQueElTitular(), CancellationToken.None);

        // ASSERT — 409 (conflicto de negocio, no un 400 de dato mal tipeado ni un 500 opaco), con el
        // mismo texto que ve el vendedor y SIN nombres de excepcion/stack (T-2, T-5).
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        var messageProperty = conflict.Value!.GetType().GetProperty("message");
        Assert.NotNull(messageProperty);
        var message = (string)messageProperty!.GetValue(conflict.Value)!;

        Assert.Equal(mensajeDelGuard, message);
        Assert.Contains("Titular Existente", message); // el vendedor necesita ubicar CUAL pasajero
        Assert.DoesNotContain("Exception", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("30111222", message); // nunca repite el numero de documento (dato sensible)
    }
}
