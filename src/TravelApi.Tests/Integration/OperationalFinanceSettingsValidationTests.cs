using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using TravelApi.Application.DTOs;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// B1.15 Fase 2a (FIX 2): la validacion del rango 0..100 de
/// MaxDiscountPercentWithoutOverride debe responder 400 (ModelState invalido)
/// y no 500 ni 503. Antes el service tiraba ArgumentOutOfRangeException, lo que
/// caia a 500 en GlobalExceptionHandler. Ahora el [Range] del DTO + ApiController
/// devuelve 400 antes de llegar al service.
/// </summary>
public class OperationalFinanceSettingsValidationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OperationalFinanceSettingsValidationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PutSettings_NegativeDiscount_Returns400()
    {
        var client = _factory.CreateClient();
        // Default = Admin, sin headers. PUT requires Authorize(Roles="Admin").
        var dto = new OperationalFinanceSettingsDto
        {
            RequireFullPaymentForOperativeStatus = true,
            RequireFullPaymentForVoucher = true,
            AfipInvoiceControlMode = "AllowAgentOverrideWithReason",
            EnableUpcomingUnpaidReservationNotifications = true,
            UpcomingUnpaidReservationAlertDays = 7,
            MaxDiscountPercentWithoutOverride = -5m
        };

        var response = await client.PutAsJsonAsync("/api/settings/operational-finance", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutSettings_DiscountOver100_Returns400()
    {
        var client = _factory.CreateClient();
        var dto = new OperationalFinanceSettingsDto
        {
            RequireFullPaymentForOperativeStatus = true,
            RequireFullPaymentForVoucher = true,
            AfipInvoiceControlMode = "AllowAgentOverrideWithReason",
            EnableUpcomingUnpaidReservationNotifications = true,
            UpcomingUnpaidReservationAlertDays = 7,
            MaxDiscountPercentWithoutOverride = 150m
        };

        var response = await client.PutAsJsonAsync("/api/settings/operational-finance", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutSettings_ValidDiscount_Returns200()
    {
        var client = _factory.CreateClient();
        var dto = new OperationalFinanceSettingsDto
        {
            RequireFullPaymentForOperativeStatus = true,
            RequireFullPaymentForVoucher = true,
            AfipInvoiceControlMode = "AllowAgentOverrideWithReason",
            EnableUpcomingUnpaidReservationNotifications = true,
            UpcomingUnpaidReservationAlertDays = 7,
            MaxDiscountPercentWithoutOverride = 25m
        };

        var response = await client.PutAsJsonAsync("/api/settings/operational-finance", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
