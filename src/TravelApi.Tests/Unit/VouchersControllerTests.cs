using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Controllers;
using Xunit;

namespace TravelApi.Tests.Unit;

public class VouchersControllerTests
{
    [Fact]
    public async Task IssueVoucher_ReturnsBadRequest_WhenReservationProxyRaisesBusinessValidation()
    {
        const string validationMessage = "Debe indicar un motivo de excepcion de al menos 10 caracteres porque la reserva tiene saldo pendiente.";
        var service = new Mock<IVoucherService>();
        service
            .Setup(s => s.IssueVoucherAsync(
                "voucher-1",
                It.IsAny<IssueVoucherRequest>(),
                It.IsAny<OperationActor>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException($$"""{"message":"{{validationMessage}}"}"""));

        var controller = CreateController(service.Object);

        var result = await controller.IssueVoucher("voucher-1", new IssueVoucherRequest(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.Equal(validationMessage, ReadMessage(badRequest.Value));
    }

    [Fact]
    public void RevokeVoucher_ExposesExpectedRoute()
    {
        var method = typeof(VouchersController).GetMethod(nameof(VouchersController.RevokeVoucher))
            ?? throw new InvalidOperationException("RevokeVoucher action was not found.");

        var attribute = Assert.Single(method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false));
        var post = Assert.IsType<HttpPostAttribute>(attribute);

        Assert.Equal("api/vouchers/{voucherPublicIdOrLegacyId}/revoke", post.Template);
    }

    private static VouchersController CreateController(IVoucherService service)
    {
        var controller = new VouchersController(service, NullLogger<VouchersController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-1"),
                    new Claim(ClaimTypes.Name, "Operador"),
                    new Claim(ClaimTypes.Role, "Ops")
                }, "TestAuth"))
            }
        };

        return controller;
    }

    private static string? ReadMessage(object? value)
    {
        var json = JsonSerializer.Serialize(value);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("message", out var message)
            ? message.GetString()
            : null;
    }
}
