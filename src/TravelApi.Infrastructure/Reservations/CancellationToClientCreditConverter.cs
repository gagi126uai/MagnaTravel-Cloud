using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// Caso (3) del flujo unificado de "Anular reserva" (2026-06-25): anular una reserva EN FIRME, SIN factura
/// con CAE vivo, pero CON cobros vivos. La reserva pasa a <c>Cancelled</c> y la plata que el cliente ya pago
/// se convierte en SALDO A FAVOR reutilizable (un <see cref="ClientCreditEntry"/> por cada moneda con cobros),
/// SIN emitir Nota de Credito (no hay factura que acreditar).
///
/// <para><b>Por que existe</b>: hasta hoy faltaba el "camino del medio". La baja simple a <c>Cancelled</c> se
/// BLOQUEA si hay cobros (ver <c>ReservaService.ApplyCancelledGuards</c>: "hay que anularla"), y la anulacion
/// formal con NC (<c>BookingCancellationService</c>) EXIGE una factura activa. Una reserva con cobros pero sin
/// factura quedaba en tierra de nadie. Este helper la deshace conservando la plata del cliente.</para>
///
/// <para><b>Mecanismo REUSADO (no se inventa nada nuevo)</b>: es el MISMO patron que
/// <see cref="OverpaymentCreditConverter"/> — por cada moneda con plata viva se crea (a) un
/// <see cref="ClientCreditEntry"/> en el bolsillo del cliente y (b) un <see cref="Payment"/> "puente" NEGATIVO
/// con <c>AffectsCash=false</c> que SACA esa plata del saldo de la reserva. La diferencia es el monto: el
/// converter de sobrepago mueve solo el EXCEDENTE (Balance &lt; 0); aca movemos TODO lo PAGADO a la reserva
/// (TotalPaid por moneda), porque la reserva entera se deshace. La plata se conserva: sale de la reserva,
/// entra al bolsillo del cliente.</para>
///
/// <para><b>Contrato de transaccion</b>: a diferencia de <see cref="OverpaymentCreditConverter"/> (que hace su
/// propio SaveChanges porque corre DESPUES del cobro), este helper NO hace SaveChanges. Es invocado DENTRO de
/// la transaccion envolvente del caller (<c>ReservaService.AnnulWithPaymentsToCreditAsync</c>, patron FC4), que
/// stagea ademas el cambio de estado a Cancelled y el audit y commitea todo de una. Asi la invariante se
/// cumple: o se anula la reserva Y la plata queda 100% como saldo a favor, o no se toca nada.</para>
///
/// <para><b>NO mueve caja</b>: la plata real ya entro cuando se registraron los cobros originales (esos tienen
/// su asiento de caja). El puente solo TRASLADA la posicion de esa plata al bolsillo del cliente; por eso
/// <c>AffectsCash=false</c> y nunca genera asiento de caja. Mismo principio que el puente de sobrepago.</para>
/// </summary>
public static class CancellationToClientCreditConverter
{
    /// <summary>
    /// Origen del puente (igual criterio que <see cref="OverpaymentCreditCleanup.BridgeMethod"/>: un metodo
    /// propio para distinguir estos pagos tecnicos de los cobros reales del cliente). Es un puente de anulacion,
    /// no de sobrepago, asi que lleva su propio nombre.
    /// </summary>
    public const string BridgeMethod = "SaldoAFavorPorAnulacion";

    /// <summary>
    /// Convierte la plata viva de la reserva en saldo a favor del cliente, por cada moneda con cobros. Por cada
    /// moneda con <c>TotalPaid &gt; 0</c> agrega un <see cref="ClientCreditEntry"/> (bolsillo del cliente) y un
    /// <see cref="Payment"/> puente NEGATIVO que deja esa moneda saldada en la reserva. NO hace SaveChanges
    /// (corre dentro de la transaccion del caller). Devuelve el detalle por moneda para la auditoria.
    ///
    /// <para><b>Precondiciones (las valida el caller; aca solo confiamos)</b>: la reserva esta cargada con su
    /// grafo economico (pagos + servicios) y tiene <c>PayerId</c>. Sin pagador no hay bolsillo de cliente: en
    /// ese caso se loguea Warning y NO se crea credito para esa reserva (la anulacion del caller decidira si
    /// sigue o aborta; este helper no rompe).</para>
    /// </summary>
    /// <returns>
    /// Lista de (moneda, monto trasladado a saldo a favor). Vacia si la reserva no tiene pagador o no hay plata
    /// viva. El caller la usa para el detail del audit.
    /// </returns>
    public static IReadOnlyList<(string Currency, decimal Amount)> Convert(
        AppDbContext db,
        Reserva reserva,
        string? actorUserId,
        string? actorUserName,
        ILogger logger)
    {
        if (reserva.PayerId is null)
        {
            // Sin pagador no hay bolsillo de cliente al que mover la plata. No rompemos: el caller ya valido
            // que haya cobros vivos; que no haya pagador es un estado raro que dejamos registrado.
            logger.LogWarning(
                "Anulacion con cobros: la reserva {ReservaId} no tiene pagador; no se puede trasladar la plata a saldo a favor.",
                reserva.Id);
            return Array.Empty<(string, decimal)>();
        }

        // Plata pagada A LA RESERVA, separada por moneda (imputada). Reusamos el calculador puro oficial para
        // no duplicar la matematica de la plata. TotalPaid de cada linea = lo que el cliente puso en esa moneda.
        var summary = ReservaMoneyCalculator.Calculate(reserva);

        var converted = new List<(string Currency, decimal Amount)>();

        foreach (var line in summary.PorMoneda.Values)
        {
            var paidInCurrency = EconomicRulesHelper.RoundCurrency(line.TotalPaid);

            // Solo movemos monedas con plata viva A FAVOR de la reserva. Si en una moneda el cliente no pago
            // nada (o el neto imputado es <= 0, p.ej. ya saldo a favor compensado), no hay nada que trasladar.
            if (paidInCurrency <= 0m)
                continue;

            var currency = Monedas.Normalizar(line.Currency);

            // (a) Bolsillo de saldo a favor del cliente por lo pagado en esta moneda. Origen ANULACION:
            //     OperatorRefundAllocationId y BookingCancellationId quedan en null (no hay refund de operador
            //     ni cancelacion formal). Es el MISMO discriminador que el credito de sobrepago: con BC null,
            //     consumir el entry NO intenta cerrar ningun BookingCancellation (guarda B5 de ClientCreditService).
            //     SourceReservaId deja rastro de que reserva se anulo para generarlo.
            var credit = new ClientCreditEntry
            {
                CustomerId = reserva.PayerId.Value,
                OperatorRefundAllocationId = null,
                BookingCancellationId = null,
                Currency = currency,
                CreditedAmount = paidInCurrency,
                RemainingBalance = paidInCurrency,
                IsFullyConsumed = false,
                CreatedAt = DateTime.UtcNow,
                SourcePaymentId = null,           // no nace de UN cobro puntual, sino del total pagado a la reserva
                SourceReservaId = reserva.Id,     // trazabilidad: reserva anulada que origino el saldo a favor
                CreatedByUserId = actorUserId,
                CreatedByUserName = actorUserName,
            };
            db.ClientCreditEntries.Add(credit);

            // (b) Puente NEGATIVO que saca lo pagado del saldo de la reserva en esta moneda. AffectsCash=false:
            //     NO mueve caja (la plata real ya entro con los cobros originales), solo traslada la posicion al
            //     bolsillo del cliente. El calculador suma los pagos vivos para TotalPaid, asi que un monto
            //     negativo igual a TotalPaid deja la reserva en 0 pagado en esa moneda (su saldo vuelve a la
            //     venta confirmada; como la reserva queda Cancelled, ese saldo deja de ser exigible).
            var bridge = new Payment
            {
                ReservaId = reserva.Id,
                Amount = -paidInCurrency,
                Currency = currency,
                Method = BridgeMethod,
                Notes = $"Cobros trasladados a saldo a favor del cliente por anulacion de la reserva {reserva.NumeroReserva}.",
                PaidAt = DateTime.UtcNow,
                Status = "Paid",
                EntryType = PaymentEntryTypes.Payment,
                AffectsCash = false,
                CreatedByUserId = actorUserId,
                CreatedByUserName = actorUserName,
            };
            db.Payments.Add(bridge);

            converted.Add((currency, paidInCurrency));

            logger.LogInformation(
                "Anulacion con cobros: reserva {ReservaId} -> saldo a favor del cliente {CustomerId} por {Currency} {Amount}.",
                reserva.Id, reserva.PayerId.Value, currency, paidInCurrency);
        }

        return converted;
    }
}
