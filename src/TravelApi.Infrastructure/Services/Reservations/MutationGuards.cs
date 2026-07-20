using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
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
///  - TaxId Customer con factura CAE viva (CODE-06). Ajustado 2026-07-17: la
///    CONDICION fiscal (TaxConditionId/TaxCondition) YA NO entra en este guard —
///    es dato de HOY, se edita siempre con auditoria (ver docstring del metodo).
///  - TaxId Supplier con booking en reserva con CAE viva (CODE-13). Mismo ajuste
///    2026-07-17: la CONDICION del proveedor se edita siempre con auditoria.
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
    ///
    /// Tanda 6 (contrato pantalla-motor, 2026-07-20): este metodo solo JUNTA los hechos desde la base
    /// (que recibo tiene, si su factura vinculada esta viva); la REGLA que decide el texto del bloqueo vive
    /// en <see cref="PaymentCapabilityPolicy"/> (Domain, pura) — la MISMA que usa el armado de la ficha
    /// (<c>ReservaService.GetReservaByIdAsync</c>) para apagar "Editar" por fila ANTES de que el usuario
    /// llegue a este guard. Un solo lugar decide el texto; este metodo y el armado del DTO nunca pueden
    /// divergir en el motivo.
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

        var hasIssuedReceipt = receiptStatuses.Any(s =>
            string.Equals(s, PaymentReceiptStatuses.Issued, StringComparison.OrdinalIgnoreCase));
        var hasOnlyVoidedReceipt = !hasIssuedReceipt && receiptStatuses.Count > 0;

        // Vinculado a factura: si la factura esta viva (CAE no anulado), el pago
        // forma parte del comprobante AFIP y no se toca.
        // Excluimos las NC (ver doc de la clase): una NC no mantiene vivo al pago.
        var isLinkedToLiveInvoice = false;
        if (payment.RelatedInvoiceId.HasValue)
        {
            isLinkedToLiveInvoice = await db.Invoices.AsNoTracking().AnyAsync(
                i => i.Id == payment.RelatedInvoiceId.Value
                     && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante) // excluye NC
                     && !string.IsNullOrEmpty(i.CAE)
                     && i.AnnulmentStatus != AnnulmentStatus.Succeeded,
                ct);
        }

        var context = new PaymentCapabilityContext(
            HasIssuedReceipt: hasIssuedReceipt,
            HasOnlyVoidedReceipt: hasOnlyVoidedReceipt,
            IsLinkedToLiveInvoice: isLinkedToLiveInvoice,
            IsLinkedToAnyInvoice: payment.RelatedInvoiceId.HasValue);
        return PaymentCapabilityPolicy.For(context).CanEdit.Reason;
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
    /// CODE-06: bloquea cambiar el CUIT del cliente (<c>TaxId</c>) cuando tiene al menos
    /// una factura con CAE no anulada. El CUIT es una IDENTIDAD FISCAL: la factura AFIP ya
    /// salio con el CUIT original impreso, y ese dato no se puede reescribir sin anular el
    /// comprobante primero.
    ///
    /// <para><b>Decision del dueño (2026-07-17):</b> la CONDICION fiscal (<c>TaxConditionId</c>,
    /// <c>TaxCondition</c> — Consumidor Final, Monotributo, Responsable Inscripto, Exento) es un
    /// dato de HOY, no una identidad, y se puede editar SIEMPRE (con auditoria), aunque el cliente
    /// tenga facturas vivas. Por eso este guard SOLO se dispara para el eje <c>TaxId</c> — el
    /// caller (<c>CustomerService.UpdateCustomerAsync</c>) ya NO lo invoca cuando lo unico que
    /// cambio fue la condicion.</para>
    ///
    /// <para><b>Por que es seguro permitir el cambio de condicion con facturas vivas</b>: la
    /// historia fiscal de un comprobante NUNCA depende de la ficha del cliente al momento de
    /// consultarla. Cada evento fiscal (factura, NC, ND) congela sus propios datos al momento de
    /// emitirse — la factura ya emitida sigue teniendo la condicion con la que salio, la ficha del
    /// cliente HOY es solo el dato VIGENTE para la proxima operacion. Un cliente pasa legitimamente
    /// de Monotributo a Responsable Inscripto (o al reves) en la vida real: bloquear ese cambio
    /// solo porque tiene facturas viejas emitidas seria una regla incorrecta, no una proteccion.</para>
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
            return "No se puede modificar el CUIT del cliente porque tiene facturas emitidas (CAE) que lo referencian. " +
                   "Si el titular cambió de CUIT, registrá un cliente nuevo.";
        }

        return null;
    }

    /// <summary>
    /// CODE-13: bloquea cambiar el CUIT del proveedor (<c>TaxId</c>) cuando tiene al menos un
    /// booking ligado a una reserva con factura CAE no anulada. Aunque AFIP factura al cliente
    /// final (el proveedor nunca es el receptor del comprobante de venta), el CUIT del proveedor
    /// aparece en informes de comisiones, conciliaciones y en la trazabilidad fiscal del servicio,
    /// asi que se lo trata igual de estricto que una identidad: no se reescribe con historia viva.
    ///
    /// <para><b>Decision del dueño (2026-07-17):</b> la CONDICION fiscal del proveedor
    /// (<c>TaxCondition</c> — RI, Monotributo, Exento) es un dato de HOY, no una identidad, y se
    /// puede editar SIEMPRE (con auditoria), aunque el proveedor tenga reservas con facturas vivas.
    /// Por eso este guard SOLO se dispara para el eje <c>TaxId</c> — el caller
    /// (<c>SupplierService.UpdateSupplierAsync</c>) ya NO lo invoca cuando lo unico que cambio fue
    /// la condicion.</para>
    ///
    /// <para><b>Por que es seguro</b>: la condicion fiscal del PROVEEDOR ni siquiera entra en el
    /// comprobante de venta (ese lleva los datos del CLIENTE, no del operador) — no hay ningun
    /// comprobante emitido cuya integridad dependa de la condicion vigente del proveedor. Y un
    /// proveedor pasa legitimamente de Monotributo a Responsable Inscripto en la vida real:
    /// bloquear ese cambio solo porque hay reservas facturadas seria una regla incorrecta.</para>
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
            return "No se puede modificar el CUIT del proveedor porque hay reservas con facturas emitidas que lo referencian. " +
                   "Si la empresa cambió de CUIT, registrá un proveedor nuevo.";
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
    /// que NINGUN servicio de la reserva se puede anular (factura con CAE viva O voucher emitido), o
    /// <c>null</c> si se puede anular.
    ///
    /// <para><b>OJO — regla VIEJA, sin caller productivo desde Tanda 4 (2026-07-20):</b> esta regla
    /// (CAE viva O voucher) fue la fuente de verdad hasta ADR-044 T5 (2026-07-11), pero el guard real que
    /// corre al anular un servicio (<c>BookingCancellationService.CancelServiceAsync</c>) cambio: dejo de
    /// frenar solo por factura viva (factura+NC es el camino normal del negocio) y ahora usa
    /// <see cref="GetReservaVoucherOnlyBlockReasonAsync"/> (SOLO voucher). Usar este metodo para pre-bloquear
    /// la pantalla hoy mentiria (frenaria de mas). Se mantiene porque sus tests documentan la regla vieja
    /// (<c>MutationGuardsTests.cs</c>); si necesitas el pre-chequeo real, usa
    /// <see cref="GetReservaVoucherOnlyBlockReasonAsync"/>.</para>
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
            return "No se puede anular este servicio: la reserva tiene vouchers emitidos. " +
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
