using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-041 (2026-06-28): el PDF del RECIBO de cobro incluye el pie "Para abonar por transferencia:" con los
/// datos bancarios de la agencia cuando existen, y se omite la seccion (sin romper) cuando la agencia no tiene
/// cuentas. No inspeccionamos el binario del PDF (QuestPDF no es texto-extraible y el proyecto no agrega una
/// libreria solo para eso, ver InvoicePdfFiscalLegendTests): estos tests blindan que la rama nueva GENERA el PDF
/// en ambos caminos. QUE cuenta se elige lo cubre <see cref="AgencyBankAccountSelectorTests"/>.
/// </summary>
public class ReceiptPdfBankDetailsTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static readonly IMapper Mapper =
        new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

    private static PaymentService BuildService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "tester") };
        var identity = new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role);
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        var resolver = new Mock<IUserPermissionResolver>();
        resolver.Setup(r => r.GetPermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string>());

        return new PaymentService(
            context,
            new EntityReferenceResolver(context),
            Mapper,
            settings.Object,
            NullLogger<PaymentService>.Instance,
            resolver.Object,
            accessor);
    }

    /// <summary>Siembra agencia + reserva + cliente + un cobro CON recibo emitido en la moneda dada.</summary>
    private static async Task<int> SeedPaymentWithReceiptAsync(AppDbContext context, string paymentCurrency)
    {
        context.AgencySettings.Add(new AgencySettings { AgencyName = "Magna Travel" });

        var customer = new Customer { Id = 1, FullName = "Cliente Demo", IsActive = true };
        context.Customers.Add(customer);

        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva Demo",
            Status = EstadoReserva.Confirmed,
            PayerId = customer.Id,
            Payer = customer,
        };
        context.Reservas.Add(reserva);

        var payment = new Payment
        {
            Id = 1,
            ReservaId = reserva.Id,
            Reserva = reserva,
            Amount = 1500.50m,
            Currency = paymentCurrency,
            Method = "Transferencia",
            Reference = "OP-123",
            Status = "Confirmed",
            PaidAt = DateTime.UtcNow,
        };
        payment.Receipt = new PaymentReceipt
        {
            Id = 1,
            PaymentId = payment.Id,
            ReservaId = reserva.Id,
            ReceiptNumber = "RCP-2026-000001",
            Status = PaymentReceiptStatuses.Issued,
            IssuedAt = DateTime.UtcNow,
            Amount = payment.Amount,
        };
        context.Payments.Add(payment);

        await context.SaveChangesAsync();
        return payment.Id;
    }

    private static void SeedAgencyBankAccount(AppDbContext context, string currency, bool isPrimary)
    {
        context.BankAccounts.Add(new BankAccount
        {
            OwnerType = BankAccountOwnerType.Agency,
            OwnerId = 0,
            Currency = currency,
            IsPrimary = isPrimary,
            IsActive = true,
            HolderName = "Magna Travel SRL",
            Cbu = "0000000000000000000001",
            Alias = "magna.travel.ars",
            Bank = "Banco Nacion",
        });
        context.SaveChanges();
    }

    [Fact]
    public async Task ConCuentaBancariaEnLaMonedaDelCobro_GeneraPdfNoVacio()
    {
        using var context = CreateContext();
        var paymentId = await SeedPaymentWithReceiptAsync(context, Monedas.ARS);
        SeedAgencyBankAccount(context, Monedas.ARS, isPrimary: true);
        var service = BuildService(context);

        var pdf = await service.GetReceiptPdfAsync(paymentId, CancellationToken.None);

        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 0);
    }

    [Fact]
    public async Task SinCuentasBancarias_OmiteLaSeccion_YGeneraPdfNoVacio()
    {
        using var context = CreateContext();
        var paymentId = await SeedPaymentWithReceiptAsync(context, Monedas.ARS);
        // No se siembra ninguna BankAccount: la seccion debe omitirse sin romper el layout.
        var service = BuildService(context);

        var pdf = await service.GetReceiptPdfAsync(paymentId, CancellationToken.None);

        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 0);
    }

    [Fact]
    public async Task CobroEnUsd_ConSoloCuentaArs_GeneraPdfNoVacio()
    {
        using var context = CreateContext();
        var paymentId = await SeedPaymentWithReceiptAsync(context, Monedas.USD);
        // Cobro en USD pero la agencia solo tiene cuenta principal en ARS: la seccion igual se muestra
        // (fallback a las principales activas) y el PDF se genera.
        SeedAgencyBankAccount(context, Monedas.ARS, isPrimary: true);
        var service = BuildService(context);

        var pdf = await service.GetReceiptPdfAsync(paymentId, CancellationToken.None);

        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 0);
    }
}
