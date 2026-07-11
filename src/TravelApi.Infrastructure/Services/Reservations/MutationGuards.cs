using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services.Reservations;

/// <summary>
/// Reglas compartidas que decidan si una entidad puede MUTARSE (UPDATE) cuando
/// su contexto fiscal/contable la deja "congelada". Cada metodo devuelve el
/// motivo de bloqueo (mensaje accionable en espanol) o <c>null</c> si la
/// operacion esta permitida.
///
/// Patron simetrico al de <see cref="DeleteGuards"/>: la regla vive en un solo
/// lugar y se reusa desde todos los servicios. Cualquier path nuevo de update
/// debe consultarlas tambien.
///
/// Origen: auditoria 2026-05-09 (3 agentes en paralelo) detecto que el sistema
/// permite editar entidades que deberian estar congeladas:
///  - Pago vinculado a Receipt Issued/Voided o factura con CAE viva (CODE-01).
///  - Servicio/Booking de reserva con CAE viva o voucher Issued (CODE-04, CODE-05).
///  - Fechas de reserva con CAE viva o voucher Issued (CODE-03).
///  - TaxId/TaxCondition Customer con factura CAE viva (CODE-06).
///  - TaxId/TaxCondition Supplier con booking en reserva con CAE viva (CODE-13).
///  - Datos personales Pasajero con voucher Issued o reserva con CAE viva (CODE-14).
///
/// "FACTURA viva" = <c>InvoiceComprobanteHelpers.IsCreditNote(i.TipoComprobante) == false
/// &amp;&amp; !string.IsNullOrEmpty(i.CAE) &amp;&amp; i.AnnulmentStatus != AnnulmentStatus.Succeeded</c>.
/// Estados Pending y Failed del flow de anulacion NO levantan el bloqueo — la NC
/// todavia no fue aprobada por AFIP, asi que la factura sigue viva fiscalmente.
///
/// POR QUE SE EXCLUYEN LAS NOTAS DE CREDITO (fix 2026-05-30, validado por contador
/// + dominio con fuentes ARCA): una Nota de Credito tambien es una fila
/// <see cref="Invoice"/> con su propio CAE y, salvo que la anulen a ella misma,
/// con <c>AnnulmentStatus = None</c>. La NC NACE para corregir/anular una factura,
/// no para bloquear: nunca se anula a si misma. Si la contaramos como "factura viva",
/// emitir una NC TOTAL dejaria la reserva bloqueada PARA SIEMPRE (la factura original
/// queda Succeeded, pero la propia NC se cuenta a si misma y reactiva el bloqueo).
/// Fiscalmente la NC RESTA, no SUMA: lo que bloquea es que quede una FACTURA viva,
/// no que exista un CAE cualquiera.
///
/// Esto resuelve los 4 escenarios de forma automatica:
///  - Factura viva sin NC -> cuenta -> BLOQUEA (correcto, igual que antes).
///  - Factura + NC TOTAL (factura original quedo Succeeded) -> factura no cuenta
///    (Succeeded) + NC excluida -> LIBERA (este era el bug a resolver).
///  - Factura + NC PARCIAL (la factura sigue viva por el resto) -> factura cuenta
///    -> BLOQUEA (decision del dueño: en parcial el bloqueo es total).
///  - Solo NC sin factura viva -> nada cuenta -> LIBERA.
///
/// NOTA TECNICA (EF Core): el helper <c>InvoiceComprobanteHelpers.IsCreditNote</c>
/// NO se puede invocar dentro de estas queries porque EF no lo traduce a SQL. Por
/// eso la exclusion se expande inline con <see cref="LiveInvoiceCreditNoteTypes"/>
/// (los cbteTipo de NC: 3=A, 8=B, 13=C, 53=M). Si algun dia se agrega un tipo de NC
/// nuevo, hay que actualizar esa constante Y el helper a la par.
/// </summary>
public static class MutationGuards
{
    /// <summary>
    /// cbteTipo de las Notas de Credito de AFIP (3=A, 8=B, 13=C, 53=M). Se usa para
    /// EXCLUIR las NC del conteo de "facturas vivas" en las queries de los guards.
    ///
    /// Es el mismo conjunto que <c>InvoiceComprobanteHelpers.IsCreditNote</c>, pero
    /// como array literal porque EF Core no traduce ese helper a SQL (ver nota tecnica
    /// en el doc de la clase). Mantener ambos sincronizados si cambia la lista.
    ///
    /// IMPORTANTE: solo se excluyen las NC. Las Facturas (1/6/11/51) y las Notas de
    /// Debito (2/7/12/52) SI cuentan como comprobante vivo. Tambien cuenta un Invoice
    /// con TipoComprobante sin clasificar (p. ej. 0 en datos viejos): preferimos
    /// bloquear de mas que liberar de mas frente a un dato fiscal ambiguo.
    /// </summary>
    private static readonly int[] LiveInvoiceCreditNoteTypes = { 3, 8, 13, 53 };

    /// <summary>
    /// Devuelve true si existe alguna FACTURA viva (no NC) con CAE asignado y
    /// AnnulmentStatus distinto a Succeeded apuntando a la reserva. Helper privado,
    /// reusado por los guards de servicio/booking/fechas/passenger.
    ///
    /// Las Notas de Credito se EXCLUYEN del conteo (ver doc de la clase): una NC
    /// resta, no suma, y no debe mantener viva la reserva por si misma.
    /// </summary>
    private static Task<bool> HasLiveCaeForReservaAsync(AppDbContext db, int reservaId, CancellationToken ct)
    {
        return db.Invoices.AsNoTracking().AnyAsync(
            i => i.ReservaId == reservaId
                 && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante) // excluye NC
                 && !string.IsNullOrEmpty(i.CAE)
                 && i.AnnulmentStatus != AnnulmentStatus.Succeeded,
            ct);
    }

    /// <summary>
    /// Devuelve true si existe algun voucher en estado Issued para la reserva.
    /// Granularidad reserva-level (B1.7 puede refinar a voucher-level por servicio).
    /// </summary>
    private static Task<bool> HasIssuedVoucherForReservaAsync(AppDbContext db, int reservaId, CancellationToken ct)
    {
        return db.Vouchers.AsNoTracking().AnyAsync(
            v => v.ReservaId == reservaId && v.Status == VoucherStatuses.Issued,
            ct);
    }

    /// <summary>
    /// CODE-01: bloquea editar un pago cuando tiene Receipt asociado (Issued o
    /// Voided) o esta vinculado a una factura con CAE no anulada. Editar el
    /// monto/metodo/referencia post-recibo o post-CAE rompe la inmutabilidad
    /// fiscal: el recibo entregado al cliente y la factura AFIP ya reflejan
    /// los datos originales.
    /// </summary>
    public static async Task<string?> GetPaymentMutationBlockReasonAsync(
        AppDbContext db,
        int paymentId,
        CancellationToken ct = default)
    {
        var payment = await db.Payments.AsNoTracking()
            .Where(p => p.Id == paymentId)
            .Select(p => new { p.RelatedInvoiceId })
            .FirstOrDefaultAsync(ct);
        if (payment == null) return null;

        // Receipt asociado: Issued (recibo activo entregado al cliente) o Voided
        // (ocupa numeracion correlativa, debe preservarse — ARCA + Contable 2026-05-06).
        var receiptStatuses = await db.PaymentReceipts.AsNoTracking()
            .Where(r => r.PaymentId == paymentId)
            .Select(r => r.Status)
            .ToListAsync(ct);

        if (receiptStatuses.Count > 0)
        {
            var hasIssued = receiptStatuses.Any(s =>
                string.Equals(s, PaymentReceiptStatuses.Issued, StringComparison.OrdinalIgnoreCase));
            if (hasIssued)
            {
                return "No se puede editar el pago porque tiene un recibo emitido. " +
                       "Anulá el recibo y registrá un nuevo pago.";
            }

            // Solo Voided.
            return "No se puede editar el pago porque tiene un recibo anulado que debe preservarse para auditoria.";
        }

        // Vinculado a factura: si la factura esta viva (CAE no anulado), el pago
        // forma parte del comprobante AFIP y no se toca.
        // Excluimos las NC (ver doc de la clase): una NC no mantiene vivo al pago.
        if (payment.RelatedInvoiceId.HasValue)
        {
            var invoiceLive = await db.Invoices.AsNoTracking().AnyAsync(
                i => i.Id == payment.RelatedInvoiceId.Value
                     && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante) // excluye NC
                     && !string.IsNullOrEmpty(i.CAE)
                     && i.AnnulmentStatus != AnnulmentStatus.Succeeded,
                ct);
            if (invoiceLive)
            {
                return "No se puede editar el pago porque esta vinculado a una factura emitida (CAE). " +
                       "Generá una nota de credito si corresponde.";
            }
        }

        return null;
    }

    /// <summary>
    /// CODE-05: bloquea editar un servicio generico (ServicioReserva) si la
    /// reserva tiene factura con CAE viva o voucher Issued. Editar el servicio
    /// cambia montos/proveedor/fechas, datos que el voucher entregado y la
    /// factura AFIP ya reflejan.
    /// </summary>
    public static async Task<string?> GetServiceMutationBlockReasonAsync(
        AppDbContext db,
        int servicioReservaId,
        CancellationToken ct = default)
    {
        var reservaId = await db.Servicios.AsNoTracking()
            .Where(s => s.Id == servicioReservaId)
            .Select(s => s.ReservaId)
            .FirstOrDefaultAsync(ct);
        if (!reservaId.HasValue || reservaId.Value == 0) return null;

        return await GetReservaMutationBlockReasonInternalAsync(
            db, reservaId.Value,
            entityLabel: "el servicio",
            ct: ct);
    }

    /// <summary>
    /// CODE-03: bloquea cambiar fechas de la reserva (StartDate/EndDate) si la
    /// reserva tiene factura con CAE viva o voucher Issued. El periodo del
    /// servicio aparece en el voucher y en la descripcion de la factura.
    /// </summary>
    public static Task<string?> GetReservaDatesMutationBlockReasonAsync(
        AppDbContext db,
        int reservaId,
        CancellationToken ct = default)
    {
        return GetReservaMutationBlockReasonInternalAsync(
            db, reservaId,
            entityLabel: "las fechas de la reserva",
            ct: ct);
    }

    /// <summary>
    /// CODE-04: bloquea editar un Hotel/Flight/Package/Transfer booking si la
    /// reserva tiene factura con CAE viva o voucher Issued. Aplica a los 4
    /// tipos sin distincion — Fase 0' usa granularidad reserva-level.
    ///
    /// El parametro <paramref name="bookingType"/> se usa para personalizar el
    /// mensaje de error (ej: "el hotel" vs "el vuelo"). Valores aceptados:
    /// Hotel, Flight, Package, Transfer. Otros caen al label generico.
    /// </summary>
    public static Task<string?> GetBookingMutationBlockReasonAsync(
        AppDbContext db,
        int reservaId,
        string bookingType,
        CancellationToken ct = default)
    {
        var label = bookingType?.ToLowerInvariant() switch
        {
            "hotel" => "el hotel",
            "flight" => "el vuelo",
            "package" => "el paquete",
            "transfer" => "el traslado",
            _ => "el servicio"
        };
        return GetReservaMutationBlockReasonInternalAsync(db, reservaId, entityLabel: label, ct: ct);
    }

    /// <summary>
    /// CODE-06: bloquea cambiar datos fiscales del cliente (TaxId,
    /// TaxConditionId, TaxCondition) cuando tiene al menos una factura con CAE
    /// no anulada. Cambiar el CUIT del receptor de un comprobante AFIP no es
    /// reversible — la factura quedo emitida con el TaxId original; cualquier
    /// cambio rompe la inmutabilidad fiscal.
    /// </summary>
    public static async Task<string?> GetCustomerTaxIdMutationBlockReasonAsync(
        AppDbContext db,
        int customerId,
        CancellationToken ct = default)
    {
        // Buscar FACTURAS vivas (no NC) con CAE en cualquier reserva del cliente.
        // Reserva.PayerId es el FK al Customer.
        // Excluimos las NC (ver doc de la clase): una NC resta, no bloquea por si sola.
        var hasLiveInvoice = await db.Invoices.AsNoTracking().AnyAsync(
            i => !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante) // excluye NC
                 && !string.IsNullOrEmpty(i.CAE)
                 && i.AnnulmentStatus != AnnulmentStatus.Succeeded
                 && i.Reserva != null
                 && i.Reserva.PayerId == customerId,
            ct);

        if (hasLiveInvoice)
        {
            return "No se pueden modificar los datos fiscales del cliente (CUIT/condicion ARCA) porque tiene facturas emitidas (CAE). " +
                   "Anulá esas facturas con nota de credito antes de cambiar la condicion fiscal.";
        }

        return null;
    }

    /// <summary>
    /// CODE-13: bloquea cambiar datos fiscales del proveedor (TaxId,
    /// TaxCondition) cuando tiene al menos un booking ligado a una reserva
    /// con factura CAE no anulada. Aunque AFIP factura al cliente final, el
    /// CUIT del proveedor aparece en informes de comisiones, conciliaciones
    /// y en la trazabilidad fiscal del servicio.
    /// </summary>
    public static async Task<string?> GetSupplierTaxIdMutationBlockReasonAsync(
        AppDbContext db,
        int supplierId,
        CancellationToken ct = default)
    {
        // Reservas que tienen al menos un booking tipado del proveedor.
        var supplierReservaIds = db.HotelBookings.AsNoTracking()
                .Where(b => b.SupplierId == supplierId)
                .Select(b => b.ReservaId)
            .Concat(db.TransferBookings.AsNoTracking()
                .Where(b => b.SupplierId == supplierId)
                .Select(b => b.ReservaId))
            .Concat(db.PackageBookings.AsNoTracking()
                .Where(b => b.SupplierId == supplierId)
                .Select(b => b.ReservaId))
            .Concat(db.FlightSegments.AsNoTracking()
                .Where(s => s.SupplierId == supplierId)
                .Select(s => s.ReservaId))
            .Concat(db.AssistanceBookings.AsNoTracking()
                .Where(b => b.SupplierId == supplierId)
                .Select(b => b.ReservaId));

        // Excluimos las NC (ver doc de la clase): solo una FACTURA viva bloquea.
        var hasLiveInvoice = await db.Invoices.AsNoTracking().AnyAsync(
            i => !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante) // excluye NC
                 && !string.IsNullOrEmpty(i.CAE)
                 && i.AnnulmentStatus != AnnulmentStatus.Succeeded
                 && i.ReservaId.HasValue
                 && supplierReservaIds.Contains(i.ReservaId.Value),
            ct);

        if (hasLiveInvoice)
        {
            return "No se pueden modificar los datos fiscales del proveedor (CUIT/condicion ARCA) porque hay reservas con facturas emitidas (CAE) que lo referencian. " +
                   "Anulá esas facturas primero o registrá un proveedor nuevo.";
        }

        return null;
    }

    /// <summary>
    /// CODE-14: bloquea cambiar datos personales del pasajero (FullName,
    /// DocumentType, DocumentNumber, BirthDate, Nationality, Gender) cuando
    /// el pasajero esta asignado a un voucher Issued o cuando la reserva tiene
    /// factura con CAE no anulada. El nombre y DNI estan impresos en el voucher
    /// entregado y, si hay factura A, los datos del titular figuran en AFIP.
    ///
    /// Email/Phone/Notes no se consideran datos fiscales en esta fase y se
    /// permiten editar libremente — el caller decide si pasa por aca.
    /// </summary>
    public static async Task<string?> GetPassengerMutationBlockReasonAsync(
        AppDbContext db,
        int passengerId,
        CancellationToken ct = default)
    {
        var passenger = await db.Passengers.AsNoTracking()
            .Where(p => p.Id == passengerId)
            .Select(p => new { p.ReservaId })
            .FirstOrDefaultAsync(ct);
        if (passenger == null) return null;

        // Voucher Issued con este pasajero asignado: bloqueo individual (mas
        // estricto que reserva-level — un voucher entregado no se reescribe).
        var assignedToIssuedVoucher = await db.VoucherPassengerAssignments.AsNoTracking().AnyAsync(
            a => a.PassengerId == passengerId
                 && a.Voucher != null
                 && a.Voucher.Status == VoucherStatuses.Issued,
            ct);
        if (assignedToIssuedVoucher)
        {
            return "No se pueden modificar los datos personales del pasajero porque esta asignado a un voucher emitido. " +
                   "Anulá el voucher primero si necesitas corregir datos.";
        }

        // Reserva con CAE viva: bloqueo reserva-level.
        var hasLiveInvoice = await HasLiveCaeForReservaAsync(db, passenger.ReservaId, ct);
        if (hasLiveInvoice)
        {
            return "No se pueden modificar los datos personales del pasajero porque la reserva tiene una factura emitida (CAE). " +
                   "Anulá la factura con nota de credito primero.";
        }

        return null;
    }

    /// <summary>
    /// ADR-025 (read-model para el front de cancelacion parcial, 2026-06-13): devuelve el motivo por el
    /// que NINGUN servicio de la reserva se puede cancelar (factura con CAE viva o voucher emitido), o
    /// <c>null</c> si se puede cancelar. El bloqueo es a NIVEL RESERVA (igual que el resto de los guards
    /// de mutacion, CODE-04/05): si la reserva esta congelada fiscalmente, todos sus servicios lo estan.
    ///
    /// <para>Existe para que la pantalla de detalle pueda PRE-BLOQUEAR los casilleros de "cancelar varios
    /// servicios" sin tener que intentar la cancelacion y comerse el 409. Es la MISMA fuente de verdad
    /// que usa <c>BookingCancellationService.ResolveServiceCancellationBlockReasonAsync</c> al cancelar
    /// (que termina en <see cref="GetReservaMutationBlockReasonInternalAsync"/>), asi que lo que el front
    /// ve y lo que el backend enforza nunca pueden divergir.</para>
    ///
    /// <para>El mensaje es del candado fiscal (CAE/voucher). El bloqueo por ESTADO de la reserva (p. ej.
    /// ya cancelada) lo resuelve la maquina de estados aparte; aca solo cubrimos el candado fiscal, que
    /// es lo que el front todavia no podia conocer por estado.</para>
    /// </summary>
    public static Task<string?> GetReservaCancellationBlockReasonAsync(
        AppDbContext db,
        int reservaId,
        CancellationToken ct = default)
    {
        return GetReservaMutationBlockReasonInternalAsync(
            db, reservaId,
            entityLabel: "los servicios de la reserva",
            ct: ct);
    }

    /// <summary>
    /// ADR-044 T5 Addendum, Decision A (2026-07-11): SOLO el candado de VOUCHER emitido, SIN el de factura
    /// viva. Existe porque <c>BookingCancellationService.CancelServiceAsync</c> reemplazo su bloqueo
    /// binario de "factura viva" (que impedia cancelar CUALQUIER servicio de una reserva ya facturada — el
    /// caso normal del negocio) por una compuerta de 3 salidas que SI permite cancelar con factura viva
    /// (resolviendo la nota de credito o dejandola visible para resolucion manual, nunca en silencio). El
    /// candado de voucher, en cambio, sigue EXACTAMENTE igual que hoy: un voucher entregado al cliente no se
    /// reescribe. Reusa el MISMO helper privado <see cref="HasIssuedVoucherForReservaAsync"/> que ya usaba
    /// el guard combinado — una sola fuente de verdad, ahora expuesta sola.
    /// </summary>
    public static async Task<string?> GetReservaVoucherOnlyBlockReasonAsync(
        AppDbContext db,
        int reservaId,
        CancellationToken ct = default)
    {
        if (reservaId == 0) return null;

        if (await HasIssuedVoucherForReservaAsync(db, reservaId, ct))
        {
            return "No se puede cancelar este servicio: la reserva tiene vouchers emitidos. " +
                   "Anulá los vouchers primero si necesitás corregir datos.";
        }

        return null;
    }

    /// <summary>
    /// Helper interno usado por los guards a nivel reserva (servicio/booking/
    /// fechas). Devuelve un mensaje uniforme parametrizado por la entidad que
    /// se intenta modificar.
    /// </summary>
    private static async Task<string?> GetReservaMutationBlockReasonInternalAsync(
        AppDbContext db,
        int reservaId,
        string entityLabel,
        CancellationToken ct)
    {
        if (reservaId == 0) return null;

        if (await HasLiveCaeForReservaAsync(db, reservaId, ct))
        {
            return $"No se puede modificar {entityLabel}: la reserva tiene una factura emitida (CAE) sin anular. " +
                   "Anulá la factura con nota de credito primero.";
        }

        if (await HasIssuedVoucherForReservaAsync(db, reservaId, ct))
        {
            return $"No se puede modificar {entityLabel}: la reserva tiene vouchers emitidos. " +
                   "Anulá los vouchers primero si necesitas corregir datos.";
        }

        return null;
    }
}
