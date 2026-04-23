using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Controllers;
using Xunit;

namespace TravelApi.Tests.Unit;

public class ReservasControllerServiceRouteTests
{
    [Fact]
    public async Task UpdateService_UpdatesGenericServiceByExistingEndpoint()
    {
        var reservaService = new Mock<IReservaService>();
        var request = CreateRequest();
        var updatedService = new ServicioReservaDto
        {
            PublicId = Guid.NewGuid(),
            Description = request.Description,
            ServiceType = request.ServiceType,
            SalePrice = request.SalePrice,
            NetCost = request.NetCost
        };

        reservaService
            .Setup(svc => svc.UpdateServiceAsync("service-1", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedService);

        var controller = CreateController(reservaService);

        var result = await controller.UpdateService("service-1", request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(updatedService, ok.Value);
        reservaService.Verify(svc => svc.UpdateServiceAsync("service-1", request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveService_RemovesGenericServiceByExistingEndpoint()
    {
        var reservaService = new Mock<IReservaService>();
        reservaService
            .Setup(svc => svc.RemoveServiceAsync("service-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = CreateController(reservaService);

        var result = await controller.RemoveService("service-1", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        reservaService.Verify(svc => svc.RemoveServiceAsync("service-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateNestedService_DelegatesToGenericServiceUpdate()
    {
        var reservaService = new Mock<IReservaService>();
        var request = CreateRequest();
        var updatedService = new ServicioReservaDto
        {
            PublicId = Guid.NewGuid(),
            Description = request.Description,
            ServiceType = request.ServiceType,
            SalePrice = request.SalePrice,
            NetCost = request.NetCost
        };

        reservaService
            .Setup(svc => svc.UpdateServiceAsync("service-1", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedService);

        var controller = CreateController(reservaService);

        var result = await controller.UpdateNestedService("reserva-1", "service-1", request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(updatedService, ok.Value);
        reservaService.Verify(svc => svc.UpdateServiceAsync("service-1", request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveNestedService_DelegatesToGenericServiceDelete()
    {
        var reservaService = new Mock<IReservaService>();
        reservaService
            .Setup(svc => svc.RemoveServiceAsync("service-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = CreateController(reservaService);

        var result = await controller.RemoveNestedService("reserva-1", "service-1", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        reservaService.Verify(svc => svc.RemoveServiceAsync("service-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GenericServiceAliasRoutes_AreNestedUnderReserva()
    {
        AssertHttpTemplate<ReservasController>(nameof(ReservasController.UpdateNestedService), typeof(HttpPutAttribute), "{publicIdOrLegacyId}/services/{servicePublicIdOrLegacyId}");
        AssertHttpTemplate<ReservasController>(nameof(ReservasController.RemoveNestedService), typeof(HttpDeleteAttribute), "{publicIdOrLegacyId}/services/{servicePublicIdOrLegacyId}");
    }

    [Fact]
    public void SpecificServiceRoutes_AreNestedUnderReserva()
    {
        AssertControllerRoute<FlightSegmentsController>("api/reservas/{reservaId}/flights");
        AssertControllerRoute<HotelBookingsController>("api/reservas/{reservaId}/hotels");
        AssertControllerRoute<TransferBookingsController>("api/reservas/{reservaId}/transfers");
        AssertControllerRoute<PackageBookingsController>("api/reservas/{reservaId}/packages");

        AssertHttpTemplate<FlightSegmentsController>("Update", typeof(HttpPutAttribute), "{id}");
        AssertHttpTemplate<FlightSegmentsController>("Delete", typeof(HttpDeleteAttribute), "{id}");
        AssertHttpTemplate<HotelBookingsController>("Update", typeof(HttpPutAttribute), "{id}");
        AssertHttpTemplate<HotelBookingsController>("Delete", typeof(HttpDeleteAttribute), "{id}");
        AssertHttpTemplate<TransferBookingsController>("Update", typeof(HttpPutAttribute), "{id}");
        AssertHttpTemplate<TransferBookingsController>("Delete", typeof(HttpDeleteAttribute), "{id}");
        AssertHttpTemplate<PackageBookingsController>("Update", typeof(HttpPutAttribute), "{id}");
        AssertHttpTemplate<PackageBookingsController>("Delete", typeof(HttpDeleteAttribute), "{id}");
    }

    private static AddServiceRequest CreateRequest() => new(
        "Hotel",
        null,
        "Servicio legacy actualizado",
        "CONF-1",
        new DateTime(2026, 5, 1),
        null,
        1200m,
        900m);

    private static ReservasController CreateController(Mock<IReservaService> reservaService)
    {
        return new ReservasController(
            reservaService.Object,
            Mock.Of<IVoucherService>(),
            Mock.Of<IWhatsAppDeliveryService>(),
            Mock.Of<ITimelineService>(),
            NullLogger<ReservasController>.Instance);
    }

    private static void AssertControllerRoute<TController>(string expectedTemplate)
    {
        var attribute = typeof(TController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Single();

        Assert.Equal(expectedTemplate, attribute.Template);
    }

    private static void AssertHttpTemplate<TController>(string methodName, Type attributeType, string expectedTemplate)
    {
        var method = typeof(TController).GetMethod(methodName)
            ?? throw new InvalidOperationException($"Method {methodName} was not found on {typeof(TController).Name}.");

        var attribute = method
            .GetCustomAttributes(attributeType, inherit: false)
            .Cast<HttpMethodAttribute>()
            .Single();

        Assert.Equal(expectedTemplate, attribute.Template);
    }
}
