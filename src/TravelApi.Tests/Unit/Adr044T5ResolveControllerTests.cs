using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit;

public class Adr044T5ResolveControllerTests
{
    [Fact]
    public async Task ResolvePartialCreditNote_DelegatesAndReturnsUpdatedDto()
    {
        var publicId = Guid.NewGuid();
        var request = new ResolvePartialCreditNoteRequest(Guid.NewGuid(), 123.45m, "Resolución manual con sustento documental");
        var expected = new BookingCancellationDto { PublicId = publicId };
        var service = new Mock<IBookingCancellationService>();
        service.Setup(s => s.ResolvePartialCreditNoteAsync(publicId, request, "u1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = new CancellationsController(
            service.Object,
            Mock.Of<IOwnershipResolver>(),
            Mock.Of<IUserPermissionResolver>(),
            Mock.Of<IBnaExchangeRateService>(),
            NullLogger<CancellationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "u1") }, "Test")),
                },
            },
        };

        var result = await controller.ResolvePartialCreditNote(publicId, request, CancellationToken.None);

        Assert.Same(expected, Assert.IsType<OkObjectResult>(result.Result).Value);
        service.VerifyAll();
    }

    [Fact]
    public void ResolvePartialCreditNote_RequiresFiscalPermissionAndOwnership()
    {
        var method = typeof(CancellationsController).GetMethod(nameof(CancellationsController.ResolvePartialCreditNote));
        var permission = method!.GetCustomAttribute<RequirePermissionAttribute>();
        var ownership = method.GetCustomAttribute<RequireOwnershipAttribute>();

        Assert.Contains(Permissions.CobranzasInvoiceAnnul,
            RequirePermissionAttribute.TryParsePolicyName(permission!.Policy!)!);
        Assert.NotNull(ownership);
    }
}
