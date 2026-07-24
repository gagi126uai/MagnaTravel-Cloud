using Microsoft.EntityFrameworkCore;
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
        // Tanda B (2026-07-16): BookingCancellationService.ConfirmAsync ahora resuelve la condicion
        // fiscal de la AGENCIA server-side (ResolveServerSideTaxIdentity), leyendo la fila real de
        // AfipSettings en vez de un dato que mandaba el frontend. Sin esta fila, cualquier Confirm de
        // este fixture rebotaria con INV-118 ANTES de llegar a lo que cada test de integracion quiere
        // probar. Guardado con AnyAsync: ResetDatabaseAsync NO trunca "AfipSettings" (no es una tabla
        // del modulo de cancelacion), asi que si SeedBaseAsync se llama mas de una vez dentro de la
        // MISMA clase de test (mismo container Postgres, varios [Fact]) no queremos una segunda fila
        // duplicada — dejariamos la eleccion de "cual fila lee ConfirmAsync" librada al azar.
        //
        // OJO: AnyAsync() consulta la BASE, no el ChangeTracker local — si un caller (ej.
        // Adr044T5EmissionIntegrationTests) ya hizo ctx.AfipSettings.Add(...) SIN guardar todavia
        // antes de llamar a este metodo, AnyAsync() no la ve (todavia no esta commiteada) y
        // agregariamos una SEGUNDA fila en el mismo SaveChanges de mas abajo. Por eso el guard
        // tambien mira el ChangeTracker (entidades ya en cola, pendientes de guardar).
        bool alreadyQueuedOrPersisted =
            ctx.ChangeTracker.Entries<AfipSettings>().Any(e => e.State == EntityState.Added)
            || await ctx.AfipSettings.AnyAsync();
        if (!alreadyQueuedOrPersisted)
        {
            ctx.AfipSettings.Add(new AfipSettings { Cuit = 20111111112, TaxCondition = "Monotributo" });
        }

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

        // Invoice original — factura de venta EMITIDA. Lleva CAE + Resultado="A" (no se llama a AFIP: es un
        // CAE de mentira, pero PRESENTE) porque DraftAsync/pre-flight cuentan como "factura activa para anular"
        // SOLO las que tienen CAE (fix fiscal 7abb84f: una fila sin CAE es una factura fantasma, no una venta
        // real). Sin el CAE, todo Draft/Confirm de estos tests de integracion rebota con "no tiene factura
        // activa". El valor del CAE es irrelevante para la logica (solo se chequea que NO este vacio).
        var invoice = new Invoice
        {
            TipoComprobante = 1, // A
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            ImporteTotal = 1000m,
            ImporteNeto = 826.45m,
            ImporteIva = 173.55m,
            ReservaId = reserva.Id,
            CAE = "68000000000000",
            Resultado = "A",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        return (customer.Id, supplier.Id, reserva.Id, invoice.Id);
    }

    /// <summary>
    /// Obra "candado coherente" C2 (2026-07-22): <c>BookingCancellationService.CancelServiceAsync</c> ahora
    /// exige una <see cref="ReservaEditAuthorization"/> VIVA cuando la reserva esta Confirmada (mismo
    /// candado que ya protegia Update/Delete de servicios tipados). Los tests de integracion de este modulo
    /// prueban la LOGICA de cada flujo de cancelacion (no el candado en si), asi que llaman a este helper
    /// EXPLICITAMENTE despues de sembrar la reserva cuando su escenario va a intentar cancelar un servicio
    /// de verdad — mismo criterio "seed explicito, no factory oculto" que el resto de este archivo.
    /// </summary>
    public static async Task SeedLiveEditAuthorizationAsync(AppDbContext ctx, int reservaId)
    {
        ctx.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            ReservaId = reservaId,
            Reason = "Autorizacion de test para ejercitar CancelServiceAsync",
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// (2026-07-03) Siembra un pago VIVO al operador imputado a la reserva. Es la pieza que hace que
    /// una anulacion tenga un circuito REAL de reembolso: al armar las lineas, <c>AssignRefundCapsAsync</c>
    /// les asigna <see cref="BookingCancellationLine.RefundCap"/> &gt; 0 (= min(pool pagado al operador,
    /// NetCost del servicio)).
    ///
    /// <para><b>Por que hace falta</b> (decision del dueño 2026-07-03): una anulacion a la que NUNCA se le
    /// pago plata al operador (todas las lineas con RefundCap == 0 y ReceivedRefundAmount == 0) ahora se
    /// AUTO-CIERRA en la transicion post-CAE — no queda "esperando reembolso" para siempre, porque no hay
    /// nada que el operador deba devolver. Para poder ejercer el flujo de esperar/imputar un reembolso del
    /// operador, el seed tiene que PAGARLE al operador primero (esta fila), o el <c>OnArcaSucceededAsync</c>
    /// cerraria la BC y el allocate posterior rebotaria con INV-093.</para>
    ///
    /// <para>El caller ademas debe darle <c>NetCost</c> al servicio (el cap se topea por el costo), en ARS y
    /// &gt;= al monto que despues va a imputar. Pago NO cruzado (Currency == ImputedCurrency == ARS), mismo
    /// patron que los tests unit que ya siembran <see cref="SupplierPayment"/> directo al DbContext.</para>
    /// </summary>
    public static async Task SeedSupplierPaymentAsync(
        AppDbContext ctx, int supplierId, int reservaId, decimal amount)
    {
        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplierId,
            ReservaId = reservaId,
            Amount = amount,
            Currency = "ARS",
            ImputedCurrency = "ARS",
            ImputedAmount = amount,
            PaidAt = DateTime.UtcNow,
            Method = "Transfer",
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Construye una <see cref="BookingCancellation"/> con <see cref="FiscalSnapshot"/>
    /// COMPLETO (apto para AwaitingFiscalConfirmation+). El status default
    /// queda en Drafted; los tests lo cambian segun el escenario.
    ///
    /// <para>
    /// ADR-025 (2026-06-13): un BC real SIEMPRE nace con al menos una
    /// <see cref="BookingCancellationLine"/> (la arma <c>BuildCancellationLinesAsync</c>
    /// o el backfill). El allocate del operador imputa el reintegro a la(s) linea(s) de
    /// ESE operador (INV-126/INV-118 viven a nivel linea). Por eso el seed tambien debe
    /// nacer con una linea, o el primer allocate del test rebota con INV-126 ("el proveedor
    /// del reintegro no corresponde a ninguna linea"). Aca adjuntamos UNA linea del mismo
    /// <paramref name="supplierId"/> del BC, en la moneda <paramref name="lineCurrency"/>
    /// (misma que el snapshot del evento en mono-operador). EF la inserta en cascada al
    /// persistir el BC (la FK la resuelve por la navigation property, igual que produccion).
    /// </para>
    /// </summary>
    /// <summary>
    /// Obra "anular sin factura" (2026-07-23): <paramref name="originatingInvoiceId"/> pasa a <c>int?</c>
    /// (espeja <c>BookingCancellation.OriginatingInvoiceId</c>, ahora opcional). TODOS los callers existentes
    /// siguen compilando sin tocarlos: pasan un <c>int</c> real, que convierte implícito a <c>int?</c>. Los
    /// tests nuevos que necesiten un BC SIN ancla fiscal pasan <c>null</c> explícito.
    /// </summary>
    public static BookingCancellation NewCancellation(
        int customerId,
        int supplierId,
        int reservaId,
        int? originatingInvoiceId,
        BookingCancellationStatus status = BookingCancellationStatus.Drafted,
        string lineCurrency = "ARS")
    {
        var bc = new BookingCancellation
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
                CurrencyAtEvent = lineCurrency,
                ExchangeRateAtOriginalInvoice = 1m,
                Source = ExchangeRateSource.Manual,
                ManualJustification = "Test",
                FetchedAt = DateTime.UtcNow,
            },
        };

        bc.Lines.Add(NewCancellationLine(supplierId, lineCurrency));
        return bc;
    }

    /// <summary>
    /// ADR-025: crea una <see cref="BookingCancellationLine"/> Full del operador
    /// <paramref name="supplierId"/> con un <see cref="BookingCancellationLine.RefundCap"/>
    /// holgado, replicando lo que arma <c>BuildCancellationLinesAsync</c> en produccion.
    ///
    /// <para>El cap se deja generoso (igual al SalePrice) para que los allocates de los
    /// tests no choquen contra el tope del operador — los tests de cap del refund validan
    /// el cap del <see cref="OperatorRefundReceived"/>, no el de la linea. Sin FK a un
    /// servicio real: <see cref="BookingCancellationLine.ServiceId"/>=0 es el centinela de
    /// backfill (no hay servicio puntual en el esqueleto del test).</para>
    /// </summary>
    public static BookingCancellationLine NewCancellationLine(
        int supplierId,
        string currency = "ARS",
        decimal lineSaleAmount = 1000m,
        decimal refundCap = 1000m)
    {
        return new BookingCancellationLine
        {
            SupplierId = supplierId,
            ServiceTable = CancellableServiceTable.Generic,
            ServiceId = 0, // centinela backfill: el esqueleto del test no apunta a un servicio real
            Scope = BookingCancellationLineScope.Full,
            Currency = string.IsNullOrWhiteSpace(currency) ? "ARS" : currency,
            LineSaleAmount = lineSaleAmount,
            RefundCap = refundCap,
            ReceivedRefundAmount = 0m,
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
