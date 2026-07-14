using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Controllers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-042 / data-exposure (2026-07-02): el endpoint POST retry-credit-notes JAMAS debe devolver el texto crudo
/// de un <see cref="InvalidOperationException"/> de .NET/EF en el body (carrera -> "Sequence contains no
/// elements." o "The instance of entity type 'BookingCancellation' cannot be tracked ... {Id: 42}"). El crudo
/// va al log; al usuario un generico en criollo.
/// </summary>
public class Adr042RetryCreditNotesControllerTests
{
    private static CancellationsController BuildController(IBookingCancellationService bcService)
    {
        var controller = new CancellationsController(
            bcService,
            Mock.Of<IOwnershipResolver>(),
            Mock.Of<IUserPermissionResolver>(),
            Mock.Of<IBnaExchangeRateService>(),
            NullLogger<CancellationsController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "vendedor-1") }, "Test")),
            },
        };
        return controller;
    }

    [Theory]
    // Mensajes crudos que .NET/EF podrian tirar ante una carrera (el BC desaparece / doble-tracking).
    [InlineData("Sequence contains no elements.")]
    [InlineData("The instance of entity type 'BookingCancellation' cannot be tracked because another instance with the key value '{Id: 42}' is already being tracked.")]
    public async Task RetryCreditNotes_InvalidOperationCruda_DevuelveGenerico_SinFiltrarInternos(string rawMessage)
    {
        var bcService = new Mock<IBookingCancellationService>();
        bcService
            .Setup(s => s.RetryCreditNotesAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(rawMessage));

        var controller = BuildController(bcService.Object);

        var result = await controller.RetryCreditNotes(Guid.NewGuid(), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        var message = (string?)conflict.Value!.GetType().GetProperty("message")!.GetValue(conflict.Value);
        // Copy generico en criollo.
        Assert.Equal("No se pudo completar la operación. Volvé a intentar.", message);
        // Y NADA del crudo .NET/EF: ni la frase interna, ni el nombre de la entidad, ni el Id.
        Assert.DoesNotContain("Sequence contains", message);
        Assert.DoesNotContain("entity type", message);
        Assert.DoesNotContain("BookingCancellation", message);
        Assert.DoesNotContain("Id:", message);
    }
}
