using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1.15 Fase 0' (CODE-01..06, 13, 14): tests del helper estatico que centraliza
/// los guards de mutacion. Cubre:
///  - GetPaymentMutationBlockReasonAsync: receipt Issued, receipt Voided,
///    factura CAE viva, sin guards (allow).
///  - GetServiceMutationBlockReasonAsync: reserva con CAE viva, voucher Issued,
///    sin guards.
///  - GetReservaDatesMutationBlockReasonAsync: simetrico al de servicio.
///  - GetBookingMutationBlockReasonAsync: simetrico, mensajes por tipo.
///  - GetCustomerTaxIdMutationBlockReasonAsync: factura CAE viva del payer.
///  - GetSupplierTaxIdMutationBlockReasonAsync: hotel/transfer/package/flight
///    referenciando al supplier en reserva con CAE viva.
///  - GetPassengerMutationBlockReasonAsync: voucher Issued con assignment, o
///    reserva con CAE viva.
///
/// Patron de tests calcado de DeleteGuards (PaymentServiceDeleteTests.cs).
/// </summary>
public class MutationGuardsTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public MutationGuardsTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    private async Task<AppDbContext> SeedReservaAsync(int reservaId = 1, int payerId = 0)
    {
        var ctx = new AppDbContext(_dbOptions);
        var reserva = new Reserva
        {
            Id = reservaId,
            NumeroReserva = $"F-2026-{reservaId:D4}",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            PayerId = payerId == 0 ? null : payerId
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return ctx;
    }

    private static Invoice MakeInvoice(
        int id,
        int reservaId,
        string? cae = "012345",
        AnnulmentStatus status = AnnulmentStatus.None,
        int tipoComprobante = 6) // 6 = Factura B (default: una factura comun)
        => new()
        {
            Id = id,
            ReservaId = reservaId,
            CAE = cae,
            AnnulmentStatus = status,
            TipoComprobante = tipoComprobante,
            ImporteTotal = 100m,
            ImporteNeto = 82.64m,
            ImporteIva = 17.36m
        };

    // Atajo para armar una Nota de Credito B (tipo 8) con CAE propio y sin anularse
    // a si misma (AnnulmentStatus=None) — el caso que ANTES bloqueaba para siempre.
    private static Invoice MakeCreditNote(int id, int reservaId, int? originalInvoiceId = null, string? cae = "099999")
        => new()
        {
            Id = id,
            ReservaId = reservaId,
            CAE = cae,
            AnnulmentStatus = AnnulmentStatus.None,
            TipoComprobante = 8, // NC B
            OriginalInvoiceId = originalInvoiceId,
            ImporteTotal = 100m,
            ImporteNeto = 82.64m,
            ImporteIva = 17.36m
        };

    // ============= GetPaymentMutationBlockReasonAsync =============

    [Fact]
    public async Task PaymentMutation_NoReceiptNoInvoice_AllowsEdit()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Payments.Add(new Payment { Id = 10, ReservaId = 1, Amount = 100m, Method = "Cash", Status = "Paid" });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPaymentMutationBlockReasonAsync(ctx, 10);
        Assert.Null(reason);
    }

    [Fact]
    public async Task PaymentMutation_ReceiptIssued_BlocksWithEmittedMessage()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Payments.Add(new Payment { Id = 11, ReservaId = 1, Amount = 100m, Method = "Cash", Status = "Paid" });
        ctx.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 50, PaymentId = 11, ReservaId = 1,
            ReceiptNumber = "REC-001", Amount = 100m,
            Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPaymentMutationBlockReasonAsync(ctx, 11);
        Assert.NotNull(reason);
        Assert.Contains("recibo emitido", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaymentMutation_ReceiptVoided_BlocksWithAuditMessage()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Payments.Add(new Payment { Id = 12, ReservaId = 1, Amount = 100m, Method = "Cash", Status = "Paid" });
        ctx.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 51, PaymentId = 12, ReservaId = 1,
            ReceiptNumber = "REC-002", Amount = 100m,
            Status = PaymentReceiptStatuses.Voided,
            IssuedAt = DateTime.UtcNow.AddDays(-1), VoidedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPaymentMutationBlockReasonAsync(ctx, 12);
        Assert.NotNull(reason);
        Assert.Contains("anulado", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auditoría", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaymentMutation_RelatedInvoiceCaeAlive_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(60, 1));
        ctx.Payments.Add(new Payment
        {
            Id = 13, ReservaId = 1, Amount = 100m, Method = "Cash", Status = "Paid",
            RelatedInvoiceId = 60
        });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPaymentMutationBlockReasonAsync(ctx, 13);
        Assert.NotNull(reason);
        Assert.Contains("factura", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaymentMutation_RelatedInvoiceAnnulled_Allows()
    {
        // Si la NC fue aprobada (Succeeded), el bloqueo se levanta — la factura
        // ya no esta viva fiscalmente.
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(61, 1, status: AnnulmentStatus.Succeeded));
        ctx.Payments.Add(new Payment
        {
            Id = 14, ReservaId = 1, Amount = 100m, Method = "Cash", Status = "Paid",
            RelatedInvoiceId = 61
        });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPaymentMutationBlockReasonAsync(ctx, 14);
        Assert.Null(reason);
    }

    // ============= GetServiceMutationBlockReasonAsync =============

    [Fact]
    public async Task ServiceMutation_ReservaWithLiveCae_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 100, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S", ConfirmationNumber = "ABC", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 1000m, NetCost = 0m
        });
        ctx.Invoices.Add(MakeInvoice(70, 1));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetServiceMutationBlockReasonAsync(ctx, 100);
        Assert.NotNull(reason);
        Assert.Contains("factura", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CAE", reason);
    }

    [Fact]
    public async Task ServiceMutation_ReservaWithIssuedVoucher_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 101, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S", ConfirmationNumber = "ABC", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 1000m, NetCost = 0m
        });
        ctx.Vouchers.Add(new Voucher
        {
            Id = 80, ReservaId = 1, FileName = "v.pdf", Status = VoucherStatuses.Issued
        });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetServiceMutationBlockReasonAsync(ctx, 101);
        Assert.NotNull(reason);
        Assert.Contains("voucher", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServiceMutation_ReservaClean_Allows()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 102, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S", ConfirmationNumber = "ABC", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 1000m, NetCost = 0m
        });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetServiceMutationBlockReasonAsync(ctx, 102);
        Assert.Null(reason);
    }

    // ===== Fix NC 2026-05-30: las NC NO cuentan como "factura viva" (4 escenarios) =====

    private static ServicioReserva MakeServicio(int id, int reservaId)
        => new()
        {
            Id = id, ReservaId = reservaId, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S", ConfirmationNumber = "ABC", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 1000m, NetCost = 0m
        };

    [Fact]
    public async Task ServiceMutation_LiveInvoiceNoCreditNote_Blocks()
    {
        // Escenario 1: factura viva (CAE, no anulada), sin NC -> BLOQUEA (igual que hoy).
        await using var ctx = await SeedReservaAsync();
        ctx.Servicios.Add(MakeServicio(200, 1));
        ctx.Invoices.Add(MakeInvoice(200, 1)); // Factura B viva
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetServiceMutationBlockReasonAsync(ctx, 200);
        Assert.NotNull(reason);
        Assert.Contains("factura", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServiceMutation_AnnulledInvoicePlusTotalCreditNote_Allows()
    {
        // Escenario 2 (EL FIX): la factura original quedo Succeeded por una NC TOTAL.
        // Antes la propia NC se contaba a si misma como "factura viva" y la reserva
        // quedaba bloqueada para siempre. Ahora: factura no cuenta (Succeeded) + NC
        // excluida -> LIBERA.
        await using var ctx = await SeedReservaAsync();
        ctx.Servicios.Add(MakeServicio(201, 1));
        ctx.Invoices.Add(MakeInvoice(201, 1, status: AnnulmentStatus.Succeeded)); // factura anulada
        ctx.Invoices.Add(MakeCreditNote(202, 1, originalInvoiceId: 201));         // NC total viva
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetServiceMutationBlockReasonAsync(ctx, 201);
        Assert.Null(reason);
    }

    [Fact]
    public async Task ServiceMutation_LiveInvoicePlusPartialCreditNote_Blocks()
    {
        // Escenario 3: NC PARCIAL. La factura original NO esta Succeeded (sigue viva
        // por el resto). La factura cuenta -> BLOQUEA (decision del dueño: bloqueo total
        // en parcial). La NC excluida no cambia el resultado.
        await using var ctx = await SeedReservaAsync();
        ctx.Servicios.Add(MakeServicio(203, 1));
        ctx.Invoices.Add(MakeInvoice(203, 1, status: AnnulmentStatus.None)); // factura sigue viva
        ctx.Invoices.Add(MakeCreditNote(204, 1, originalInvoiceId: 203));    // NC parcial
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetServiceMutationBlockReasonAsync(ctx, 203);
        Assert.NotNull(reason);
        Assert.Contains("factura", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServiceMutation_OnlyCreditNoteNoLiveInvoice_Allows()
    {
        // Escenario 4: solo una NC con CAE y ninguna factura viva -> LIBERA.
        await using var ctx = await SeedReservaAsync();
        ctx.Servicios.Add(MakeServicio(205, 1));
        ctx.Invoices.Add(MakeCreditNote(206, 1)); // NC suelta (sin factura origen viva)
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetServiceMutationBlockReasonAsync(ctx, 205);
        Assert.Null(reason);
    }

    [Fact]
    public async Task ServiceMutation_NewInvoiceAfterCreditNote_Blocks()
    {
        // Guarda contra un falso negativo: una FACTURA nueva emitida DESPUES de una NC
        // (OriginalInvoiceId null) sigue siendo factura y DEBE contar. No la excluimos.
        await using var ctx = await SeedReservaAsync();
        ctx.Servicios.Add(MakeServicio(207, 1));
        ctx.Invoices.Add(MakeInvoice(208, 1, status: AnnulmentStatus.Succeeded)); // 1a factura anulada
        ctx.Invoices.Add(MakeCreditNote(209, 1, originalInvoiceId: 208));         // NC de esa anulacion
        ctx.Invoices.Add(MakeInvoice(210, 1, status: AnnulmentStatus.None));      // factura NUEVA viva
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetServiceMutationBlockReasonAsync(ctx, 207);
        Assert.NotNull(reason);
        Assert.Contains("factura", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ===== Mismos 4 escenarios sobre el guard de PAGO (CODE-01) =====

    private static Payment MakePaymentLinkedToInvoice(int paymentId, int invoiceId)
        => new()
        {
            Id = paymentId, ReservaId = 1, Amount = 100m, Method = "Cash", Status = "Paid",
            RelatedInvoiceId = invoiceId
        };

    [Fact]
    public async Task PaymentMutation_RelatedTotalCreditNote_Allows()
    {
        // El pago quedo re-vinculado a una NC (no a una factura viva): NO bloquea.
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeCreditNote(300, 1));
        ctx.Payments.Add(MakePaymentLinkedToInvoice(40, 300));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPaymentMutationBlockReasonAsync(ctx, 40);
        Assert.Null(reason);
    }

    [Fact]
    public async Task PaymentMutation_RelatedLiveInvoice_Blocks()
    {
        // El pago esta vinculado a una factura viva (no NC) -> BLOQUEA (igual que hoy).
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(301, 1));
        ctx.Payments.Add(MakePaymentLinkedToInvoice(41, 301));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPaymentMutationBlockReasonAsync(ctx, 41);
        Assert.NotNull(reason);
        Assert.Contains("factura", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ============= GetReservaDatesMutationBlockReasonAsync =============

    [Fact]
    public async Task DatesMutation_LiveCae_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(71, 1));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetReservaDatesMutationBlockReasonAsync(ctx, 1);
        Assert.NotNull(reason);
        Assert.Contains("fechas", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DatesMutation_AnnulledCae_Allows()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(72, 1, status: AnnulmentStatus.Succeeded));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetReservaDatesMutationBlockReasonAsync(ctx, 1);
        Assert.Null(reason);
    }

    // ============= GetBookingMutationBlockReasonAsync =============

    [Fact]
    public async Task BookingMutation_HotelLabel_LiveCae_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(73, 1));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetBookingMutationBlockReasonAsync(ctx, 1, "Hotel");
        Assert.NotNull(reason);
        Assert.Contains("hotel", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BookingMutation_FlightLabel_LiveCae_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(74, 1));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetBookingMutationBlockReasonAsync(ctx, 1, "Flight");
        Assert.NotNull(reason);
        Assert.Contains("vuelo", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BookingMutation_NoCaeNoVoucher_Allows()
    {
        await using var ctx = await SeedReservaAsync();
        var reason = await MutationGuards.GetBookingMutationBlockReasonAsync(ctx, 1, "Hotel");
        Assert.Null(reason);
    }

    // ============= GetCustomerTaxIdMutationBlockReasonAsync =============

    [Fact]
    public async Task CustomerTaxIdMutation_LiveCaeOnPayer_Blocks()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var customer = new Customer { Id = 1, FullName = "Cliente test" };
        ctx.Customers.Add(customer);
        ctx.Reservas.Add(new Reserva
        {
            Id = 5,
            NumeroReserva = "F-CLI-1",
            Name = "Reserva cli",
            Status = EstadoReserva.Confirmed,
            PayerId = 1
        });
        await ctx.SaveChangesAsync();
        ctx.Invoices.Add(MakeInvoice(80, 5));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetCustomerTaxIdMutationBlockReasonAsync(ctx, 1);
        Assert.NotNull(reason);
        Assert.Contains("CUIT", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomerTaxIdMutation_NoInvoices_Allows()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        ctx.Customers.Add(new Customer { Id = 2, FullName = "Cliente sin facturas" });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetCustomerTaxIdMutationBlockReasonAsync(ctx, 2);
        Assert.Null(reason);
    }

    // ============= GetSupplierTaxIdMutationBlockReasonAsync =============

    [Fact]
    public async Task SupplierTaxIdMutation_HotelInLiveCaeReserva_Blocks()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        ctx.Suppliers.Add(new Supplier { Id = 1, Name = "Operador X" });
        ctx.Reservas.Add(new Reserva
        {
            Id = 7, NumeroReserva = "F-SUP-1", Name = "Res supl",
            Status = EstadoReserva.Confirmed
        });
        await ctx.SaveChangesAsync();
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 90, ReservaId = 7, SupplierId = 1,
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13),
            HotelName = "Hotel Y", SalePrice = 500m, NetCost = 300m
        });
        ctx.Invoices.Add(MakeInvoice(81, 7));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetSupplierTaxIdMutationBlockReasonAsync(ctx, 1);
        Assert.NotNull(reason);
        Assert.Contains("CUIT", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SupplierTaxIdMutation_NoBookingInLiveCae_Allows()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        ctx.Suppliers.Add(new Supplier { Id = 2, Name = "Operador limpio" });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetSupplierTaxIdMutationBlockReasonAsync(ctx, 2);
        Assert.Null(reason);
    }

    // ============= GetPassengerMutationBlockReasonAsync =============

    [Fact]
    public async Task PassengerMutation_AssignedToIssuedVoucher_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Passengers.Add(new Passenger { Id = 30, ReservaId = 1, FullName = "Pax test" });
        ctx.Vouchers.Add(new Voucher { Id = 90, ReservaId = 1, FileName = "v.pdf", Status = VoucherStatuses.Issued });
        ctx.VoucherPassengerAssignments.Add(new VoucherPassengerAssignment { Id = 1, VoucherId = 90, PassengerId = 30 });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPassengerMutationBlockReasonAsync(ctx, 30);
        Assert.NotNull(reason);
        Assert.Contains("voucher", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PassengerMutation_ReservaWithLiveCae_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Passengers.Add(new Passenger { Id = 31, ReservaId = 1, FullName = "Pax CAE" });
        ctx.Invoices.Add(MakeInvoice(82, 1));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPassengerMutationBlockReasonAsync(ctx, 31);
        Assert.NotNull(reason);
        Assert.Contains("factura", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PassengerMutation_NoConstraints_Allows()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Passengers.Add(new Passenger { Id = 32, ReservaId = 1, FullName = "Pax libre" });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPassengerMutationBlockReasonAsync(ctx, 32);
        Assert.Null(reason);
    }

    [Fact]
    public async Task PassengerMutation_VoucherDraft_Allows()
    {
        // Voucher en estado Draft (no Issued) NO bloquea — solo Issued importa.
        await using var ctx = await SeedReservaAsync();
        ctx.Passengers.Add(new Passenger { Id = 33, ReservaId = 1, FullName = "Pax draft" });
        ctx.Vouchers.Add(new Voucher { Id = 91, ReservaId = 1, FileName = "v.pdf", Status = VoucherStatuses.Draft });
        ctx.VoucherPassengerAssignments.Add(new VoucherPassengerAssignment { Id = 2, VoucherId = 91, PassengerId = 33 });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPassengerMutationBlockReasonAsync(ctx, 33);
        Assert.Null(reason);
    }

    // ============= GetReservaCancellationBlockReasonAsync (ADR-025, read-model) =============

    [Fact]
    public async Task CancellationBlock_LiveCae_Blocks()
    {
        // Reserva con factura CAE viva -> ningun servicio se puede cancelar.
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(73, 1));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetReservaCancellationBlockReasonAsync(ctx, 1);
        Assert.NotNull(reason);
        Assert.Contains("factura", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationBlock_IssuedVoucher_Blocks()
    {
        // Reserva con voucher emitido -> ningun servicio se puede cancelar.
        await using var ctx = await SeedReservaAsync();
        ctx.Vouchers.Add(new Voucher { Id = 95, ReservaId = 1, FileName = "v.pdf", Status = VoucherStatuses.Issued });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetReservaCancellationBlockReasonAsync(ctx, 1);
        Assert.NotNull(reason);
        Assert.Contains("voucher", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationBlock_NoCaeNoVoucher_Allows()
    {
        // Reserva sin factura viva ni voucher emitido -> se puede cancelar (null).
        await using var ctx = await SeedReservaAsync();

        var reason = await MutationGuards.GetReservaCancellationBlockReasonAsync(ctx, 1);
        Assert.Null(reason);
    }

    [Fact]
    public async Task CancellationBlock_AnnulledCae_Allows()
    {
        // Factura anulada (NC aprobada, AnnulmentStatus.Succeeded) -> no bloquea.
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(74, 1, status: AnnulmentStatus.Succeeded));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetReservaCancellationBlockReasonAsync(ctx, 1);
        Assert.Null(reason);
    }

    [Fact]
    public async Task CancellationBlock_DraftVoucher_Allows()
    {
        // Voucher en Draft (no Issued) NO bloquea — mismo criterio que el resto de los guards.
        await using var ctx = await SeedReservaAsync();
        ctx.Vouchers.Add(new Voucher { Id = 96, ReservaId = 1, FileName = "v.pdf", Status = VoucherStatuses.Draft });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetReservaCancellationBlockReasonAsync(ctx, 1);
        Assert.Null(reason);
    }
}
