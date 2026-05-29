using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1 (2026-05-14): helpers minimos para los tests de integracion del modulo
/// de cancelacion/refund. Solo crean las filas absolutamente necesarias para
/// que las FKs del aggregate root carguen — los tests no necesitan un dominio
/// completo, solo un "esqueleto" que satisfaga las restricciones de integridad
/// referencial de Postgres.
///
/// Por que no hay un factory generico:
///  - Las invariantes en test apuntan a CHECK constraints muy especificos
///    (montos, status, snapshots). Los datos relevantes para cada test cambian
///    bastante; un factory generico esconderia el escenario.
///  - Los inserts directos con valores mostrados in-line en el test son mas
///    legibles para el revisor y para el contador (audit).
/// </summary>
internal static class CancellationTestData
{
    /// <summary>
    /// Crea Customer + Supplier + Reserva + Invoice originales y los guarda.
    /// Devuelve los Ids generados por Postgres (las secuencias arrancan en 1
    /// despues de <c>ResetDatabaseAsync</c>).
    /// </summary>
    public static async Task<(int CustomerId, int SupplierId, int ReservaId, int InvoiceId)>
        SeedBaseAsync(AppDbContext ctx)
    {
        var customer = new Customer
        {
            FullName = "Cliente Test",
            TaxCondition = "Consumidor Final",
            IsActive = true,
        };
        var supplier = new Supplier
        {
            Name = "Operador Test",
            IsActive = true,
            TaxCondition = "IVA_RESP_INSCRIPTO",
        };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = $"F-INT-{Guid.NewGuid().ToString("N")[..8]}",
            Name = "Reserva integracion",
            Status = EstadoReserva.Confirmed,
            PayerId = customer.Id,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // Invoice original — minima: solo los campos requeridos. No genera CAE
        // real (los tests no llaman a AFIP).
        var invoice = new Invoice
        {
            TipoComprobante = 1, // A
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            ImporteTotal = 1000m,
            ImporteNeto = 826.45m,
            ImporteIva = 173.55m,
            ReservaId = reserva.Id,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        return (customer.Id, supplier.Id, reserva.Id, invoice.Id);
    }

    /// <summary>
    /// Construye una <see cref="BookingCancellation"/> con <see cref="FiscalSnapshot"/>
    /// COMPLETO (apto para AwaitingFiscalConfirmation+). El status default
    /// queda en Drafted; los tests lo cambian segun el escenario.
    /// </summary>
    public static BookingCancellation NewCancellation(
        int customerId,
        int supplierId,
        int reservaId,
        int originatingInvoiceId,
        BookingCancellationStatus status = BookingCancellationStatus.Drafted)
    {
        return new BookingCancellation
        {
            CustomerId = customerId,
            SupplierId = supplierId,
            ReservaId = reservaId,
            OriginatingInvoiceId = originatingInvoiceId,
            Status = status,
            Reason = "Cancelacion integracion test",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "tester",
            DraftedByUserName = "Tester Integracion",
            AmountPaidAtCancellation = 1000m,
            EstimatedRefundAmount = 800m,
            ReceivedRefundAmount = 0m,
            FiscalSnapshot = new FiscalSnapshot
            {
                CustomerTaxConditionAtEvent = "Consumidor Final",
                SupplierTaxConditionAtEvent = "IVA_RESP_INSCRIPTO",
                AgencyTaxConditionAtEvent = "Monotributo",
                CurrencyAtEvent = "ARS",
                ExchangeRateAtOriginalInvoice = 1m,
                Source = ExchangeRateSource.Manual,
                ManualJustification = "Test",
                FetchedAt = DateTime.UtcNow,
            },
        };
    }

    /// <summary>
    /// FC1.3 Fase 3 (ADR-010, 2026-05-29): crea una segunda <see cref="Invoice"/> que
    /// hace de NOTA DE CREDITO parcial y la persiste. Los tests de la bandeja de
    /// reconciliacion necesitan DOS facturas distintas: la original (la que se
    /// cancelo) y la NC parcial (la que disparo el caso). El indice UNICO de la
    /// bandeja es solo sobre <c>CreditNoteInvoiceId</c>, asi que basta con que esta
    /// NC tenga un Id propio distinto del original.
    ///
    /// <para>Minima: solo los campos requeridos, sin CAE real (los tests no llaman a
    /// AFIP). TipoComprobante 3 = Nota de Credito A (la original usa 1 = Factura A).</para>
    /// </summary>
    public static async Task<int> SeedCreditNoteInvoiceAsync(AppDbContext ctx, int reservaId)
    {
        var creditNote = new Invoice
        {
            TipoComprobante = 3, // Nota de Credito A
            PuntoDeVenta = 1,
            NumeroComprobante = 99,
            ImporteTotal = 750m,
            ImporteNeto = 619.83m,
            ImporteIva = 130.17m,
            ReservaId = reservaId,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();
        return creditNote.Id;
    }

    /// <summary>
    /// FiscalSnapshot incompleto (Source=Unset, TC=0, Currency=null). Solo es
    /// legal en <see cref="BookingCancellationStatus.Drafted"/> o
    /// <see cref="BookingCancellationStatus.Aborted"/>.
    /// </summary>
    public static FiscalSnapshot IncompleteSnapshot() => new FiscalSnapshot
    {
        Source = ExchangeRateSource.Unset,
        ExchangeRateAtOriginalInvoice = 0m,
        CurrencyAtEvent = null,
        FetchedAt = default,
    };
}
