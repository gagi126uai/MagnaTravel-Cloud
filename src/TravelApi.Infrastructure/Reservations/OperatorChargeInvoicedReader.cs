using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// (2026-07-15) Lector COMPARTIDO de los cargos del operador "facturados aparte" (ADR-044 T2 Addendum,
/// <see cref="PenaltyCollectionMode.FacturadaAparte"/>): el operador devuelve el reembolso INTEGRO de una
/// cancelacion pero factura este monto con su PROPIO documento. Es una deuda NUEVA de la agencia hacia el
/// operador — no una retencion — que hasta ahora solo se mostraba en el bloque "Circuito de cancelacion" del
/// extracto (<see cref="SupplierCancellationCircuitReader"/>) y NUNCA sumaba al saldo OFICIAL persistido
/// (<c>Supplier.CurrentBalance</c> / <c>SupplierBalanceByCurrency</c>).
///
/// <para><b>Por que este lector es su propia clase</b>: el MISMO predicado ("cargos vivos facturados aparte
/// de un proveedor, en cancelaciones no abortadas") lo necesitan ahora dos lugares: <see cref="SupplierDebtPersister"/>
/// (saldo oficial total) y el desglose de deuda POR RESERVA (<c>SupplierService.GetSupplierDebtByReservaAsync</c>,
/// que ademas necesita la identidad de la reserva). Centralizar el filtro aca evita que diverjan en silencio
/// — el mismo bug que esta obra viene a cerrar: la exclusion de <c>SupplierDebtPersister</c> estaba admitida y
/// documentada, pero nunca se corrigio.</para>
///
/// <para><b>Vigencia de un cargo</b> ("¿todavia es deuda viva?"): SOLO se excluyen los cargos de una
/// cancelacion <see cref="BookingCancellationStatus.Aborted"/> (el evento entero quedo sin efecto) o cuya fila
/// fue borrada (el cierre "sin multa" ANTES de emitir cualquier comprobante borra los cargos de la linea; ver
/// <c>BookingCancellationService.ReverseConfirmedPenaltyFromLinesAsync</c>). Deshacer la Nota de Debito YA
/// EMITIDA al cliente (ADR-044 "Deshacer una multa ya emitida", 2026-07-14) NO borra ni anula estos cargos: esa
/// accion desarma el comprobante que la AGENCIA le habia emitido a SU cliente (cuenta por COBRAR), pero la
/// deuda de la agencia HACIA EL OPERADOR (cuenta por PAGAR) es un circuito totalmente aparte y sigue viva —
/// tan viva que, si el paso se vuelve a emitir, el motor de emision reusa este MISMO cargo para la Nota de
/// Debito nueva (ver <c>BookingCancellationService.BuildCancellationDebitNoteItemsAsync</c>). Excluirlo aca
/// dejaria el saldo oficial MAS BAJO que la deuda real mientras el paso esta reabierto — el mismo tipo de bug
/// que esta obra viene a cerrar, en la direccion contraria.</para>
/// </summary>
internal static class OperatorChargeInvoicedReader
{
    /// <summary>
    /// Un cargo "facturado aparte" vivo, con la identidad de la reserva de origen (la necesita el desglose por
    /// reserva; <see cref="SupplierDebtPersister"/> solo usa <see cref="Currency"/> y <see cref="Amount"/>).
    /// </summary>
    internal readonly record struct Row(
        Guid ReservaPublicId,
        string? NumeroReserva,
        string? FileName,
        string Currency,
        decimal Amount);

    /// <summary>
    /// Carga los cargos "facturados aparte" vivos de UN proveedor. Volumen chico por proveedor (back-office),
    /// se materializa entero. La moneda ya viene normalizada (null -&gt; ARS), igual que el resto de la cuenta
    /// del proveedor.
    /// </summary>
    public static async Task<List<Row>> LoadAsync(AppDbContext db, int supplierId, CancellationToken ct)
    {
        var rows = await db.BookingCancellationLineOperatorCharges
            .AsNoTracking()
            .Where(charge =>
                charge.CollectionMode == PenaltyCollectionMode.FacturadaAparte
                && charge.BookingCancellationLine.SupplierId == supplierId
                && charge.BookingCancellationLine.BookingCancellation.Status != BookingCancellationStatus.Aborted)
            .Select(charge => new
            {
                charge.Amount,
                charge.Currency,
                ReservaPublicId = charge.BookingCancellationLine.BookingCancellation.Reserva!.PublicId,
                NumeroReserva = charge.BookingCancellationLine.BookingCancellation.Reserva!.NumeroReserva,
                FileName = charge.BookingCancellationLine.BookingCancellation.Reserva!.Name,
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new Row(
                r.ReservaPublicId, r.NumeroReserva, r.FileName,
                Monedas.Normalizar(r.Currency), r.Amount))
            .ToList();
    }
}
