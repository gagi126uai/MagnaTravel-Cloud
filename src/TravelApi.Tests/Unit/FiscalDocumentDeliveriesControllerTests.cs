using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit;

public class FiscalDocumentDeliveriesControllerTests
{
    [Fact]
    public void PartialCreditNoteSend_HasMessagePermissionAndBookingCancellationOwnership()
    {
        var method = typeof(FiscalDocumentDeliveriesController)
            .GetMethod(nameof(FiscalDocumentDeliveriesController.SendPartialCreditNote));
        var permission = method!.GetCustomAttribute<RequirePermissionAttribute>();
        var ownership = method.GetCustomAttribute<RequireOwnershipAttribute>();

        Assert.Contains(Permissions.MessagesSend,
            RequirePermissionAttribute.TryParsePolicyName(permission!.Policy!)!);
        Assert.NotNull(ownership);
    }

    [Fact]
    public async Task PartialCreditNoteSend_DelegatesOnlyCancellationIdAndServerActor()
    {
        var publicId = Guid.NewGuid();
        var messages = new Mock<IMessageService>();
        messages.Setup(service => service.SendPartialCreditNoteMessageAsync(
                It.IsAny<Guid>(), It.IsAny<OperationActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TravelApi.Application.DTOs.MessageDeliveryDto());
        var controller = new FiscalDocumentDeliveriesController(
            messages.Object, NullLogger<FiscalDocumentDeliveriesController>.Instance)
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

        await controller.SendPartialCreditNote(publicId, CancellationToken.None);

        messages.Verify(service => service.SendPartialCreditNoteMessageAsync(
            publicId, It.Is<OperationActor>(actor => actor.UserId == "u1"), It.IsAny<CancellationToken>()), Times.Once);
    }
}
