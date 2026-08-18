using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tanda 7.2 post-rollout (2026-08-18): <see cref="Payment"/> y <see cref="SupplierPayment"/> tienen
/// soft-delete con <c>HasQueryFilter(!IsDeleted)</c>, pero sus hijas (<see cref="PaymentReceipt"/> y
/// <see cref="SupplierInvoicePaymentApplication"/>) NO espejaban ese filtro. Una consulta que arranca
/// DIRECTO desde la tabla hija (sin pasar por la navegacion del padre) podia devolver filas que
/// pertenecen a un pago ya deshecho.
///
/// Regla de negocio (Gaston): nada importante se borra — los pagos se deshacen con rastro (soft-delete),
/// pero un pago deshecho no puede seguir "existiendo" para consultas nuevas que no pidan expresamente
/// verlo con <c>IgnoreQueryFilters()</c>.
///
/// Estos tests documentan el bug ANTES del fix (deberian fallar contra el AppDbContext viejo, sin los
/// HasQueryFilter en PaymentReceipt/SupplierInvoicePaymentApplication) y quedan en verde como candado de
/// regresion despues del fix.
/// </summary>
public class PaymentChildQueryFilterMirrorTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task PaymentReceipts_DefaultQuery_ExcludesReceiptOfSoftDeletedPayment()
    {
        await using var context = CreateContext();

        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9001", Name = "Reserva test", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);

        // Pago A: DESHECHO (soft-delete), pero su recibo Voided se PRESERVA (no hay soft-delete de recibos,
        // ver comentario del indice unico en AppDbContext). Es el escenario real: PaymentService.DeletePaymentAsync
        // permite borrar un pago con recibo Voided y la fila del recibo queda en la base para siempre.
        context.Payments.Add(new Payment
        {
            Id = 901, ReservaId = 1, Amount = 300m, IsDeleted = true, DeletedAt = DateTime.UtcNow,
            Status = "Paid", Method = "Transfer", PaidAt = DateTime.UtcNow, EntryType = PaymentEntryTypes.Payment
        });
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 901, PaymentId = 901, ReservaId = 1, ReceiptNumber = "RCP-2026-000001",
            Amount = 300m, Status = PaymentReceiptStatuses.Voided, IssuedAt = DateTime.UtcNow.AddDays(-1),
            VoidedAt = DateTime.UtcNow, VoidedByUserId = "u1", VoidReason = "Error de carga"
        });

        // Pago B: VIVO, con recibo Issued vigente.
        context.Payments.Add(new Payment
        {
            Id = 902, ReservaId = 1, Amount = 500m, IsDeleted = false,
            Status = "Paid", Method = "Transfer", PaidAt = DateTime.UtcNow, EntryType = PaymentEntryTypes.Payment
        });
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 902, PaymentId = 902, ReservaId = 1, ReceiptNumber = "RCP-2026-000002",
            Amount = 500m, Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow
        });

        await context.SaveChangesAsync();

        // Consulta DIRECTA a la tabla hija, sin pasar por Payment.Receipt y sin IgnoreQueryFilters:
        // este es exactamente el tipo de consulta que hoy se filtra mal.
        var visibleReceiptNumbers = await context.PaymentReceipts
            .Select(r => r.ReceiptNumber)
            .ToListAsync();

        Assert.DoesNotContain("RCP-2026-000001", visibleReceiptNumbers);
        Assert.Contains("RCP-2026-000002", visibleReceiptNumbers);

        // Con IgnoreQueryFilters() el recibo del pago deshecho SIGUE estando (nada se borra de verdad,
        // solo se oculta de las consultas por defecto).
        var allReceiptNumbers = await context.PaymentReceipts
            .IgnoreQueryFilters()
            .Select(r => r.ReceiptNumber)
            .ToListAsync();
        Assert.Contains("RCP-2026-000001", allReceiptNumbers);
        Assert.Contains("RCP-2026-000002", allReceiptNumbers);
    }

    [Fact]
    public async Task SupplierInvoicePaymentApplications_DefaultQuery_ExcludesApplicationOfSoftDeletedSupplierPayment()
    {
        await using var context = CreateContext();

        var supplier = new Supplier { Id = 1, Name = "Operador test", TaxId = "30-12345678-9", TaxCondition = "IVA_RESP_INSCRIPTO" };
        context.Suppliers.Add(supplier);

        var invoice = new SupplierInvoice
        {
            Id = 1, SupplierId = 1, Number = "FAC-001", Currency = "ARS",
            IssuedAt = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(15)
        };
        context.SupplierInvoices.Add(invoice);

        // Pago a operador DESHECHO. La aplicacion contra la factura NO tiene soft-delete propio
        // (SupplierInvoicePaymentApplication no declara IsDeleted), asi que hoy sigue "visible" en
        // consultas directas aunque el pago que la respalda ya no exista para el resto del sistema.
        context.SupplierPayments.Add(new SupplierPayment
        {
            Id = 951, SupplierId = 1, Amount = 100m, Currency = "ARS", Method = "Transfer",
            IsDeleted = true, DeletedAt = DateTime.UtcNow
        });
        context.SupplierInvoicePaymentApplications.Add(new SupplierInvoicePaymentApplication
        {
            Id = 951, SupplierInvoiceId = 1, SupplierPaymentId = 951, Amount = 100m,
            CreatedByUserId = "u1"
        });

        // Pago a operador VIVO, con su aplicacion vigente.
        context.SupplierPayments.Add(new SupplierPayment
        {
            Id = 952, SupplierId = 1, Amount = 50m, Currency = "ARS", Method = "Transfer", IsDeleted = false
        });
        context.SupplierInvoicePaymentApplications.Add(new SupplierInvoicePaymentApplication
        {
            Id = 952, SupplierInvoiceId = 1, SupplierPaymentId = 952, Amount = 50m,
            CreatedByUserId = "u1"
        });

        await context.SaveChangesAsync();

        var visibleApplicationIds = await context.SupplierInvoicePaymentApplications
            .Select(a => a.SupplierPaymentId)
            .ToListAsync();

        Assert.DoesNotContain(951, visibleApplicationIds);
        Assert.Contains(952, visibleApplicationIds);

        var allApplicationIds = await context.SupplierInvoicePaymentApplications
            .IgnoreQueryFilters()
            .Select(a => a.SupplierPaymentId)
            .ToListAsync();
        Assert.Contains(951, allApplicationIds);
        Assert.Contains(952, allApplicationIds);
    }
}
