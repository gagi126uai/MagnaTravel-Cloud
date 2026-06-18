using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-022 §4.9 (Q1): convierte el SOBREPAGO de una reserva en saldo a favor del cliente (bolsillo del
/// cliente). Punto UNICO de la conversion, compartido por los TRES caminos que cobran/recalculan:
/// el canonico (<c>PaymentService.CreatePaymentAsync</c> y la edicion de monto), el legacy anidado
/// (<c>ReservaService.AddPaymentAsync</c>, POST /api/reservas/{id}/payments) y la restauracion de un cobro
/// anulado (<c>PaymentService.RestorePaymentAsync</c>).
///
/// <para><b>Por que existe (fix bugs #6 y #9, 2026-06-17)</b>: antes esta logica vivia dentro de
/// <c>PaymentService.ConvertOverpaymentToClientCreditAsync</c> (privado). El path legacy de cobro y el de
/// restaurar un cobro NO la llamaban, asi que un sobrepago por esos caminos quedaba como saldo NEGATIVO
/// atrapado en la reserva, invisible al bolsillo del cliente y a "aplicar saldo a favor a otra reserva"
/// (FC4). Extraerla a un helper compartido cierra esa divergencia: la conversion es identica en los tres
/// caminos, sin duplicar reglas.</para>
///
/// <para><b>Contrato de transaccion</b>: a diferencia de <see cref="OverpaymentCreditCleanup"/> y
/// <see cref="ReservaMoneyPersister"/> (que NO hacen SaveChanges), este helper SI persiste y recalcula por
/// su cuenta, porque ese era el contrato del metodo original que reemplaza: se invoca DESPUES del SaveChanges
/// del cobro y del recalculo del saldo, ya con la reserva en su estado final. Replicarlo exacto evita
/// cambiar el comportamiento del camino canonico.</para>
///
/// <para><b>NO mueve caja</b>: el cobro ya asento la plata real que entro; esto es una imputacion de
/// posicion del cliente (mueve plata de "saldo de la reserva" al "bolsillo del cliente"), no un egreso.
/// NO crea asiento de caja.</para>
/// </summary>
public static class OverpaymentCreditConverter
{
    /// <summary>
    /// Si la reserva del <paramref name="payment"/> quedo a favor del cliente en la moneda a la que se
    /// imputo el cobro (la fila <c>ReservaMoneyByCurrency.Balance &lt; 0</c> de esa moneda), mueve el
    /// excedente al bolsillo del cliente como un <see cref="ClientCreditEntry"/> de origen "sobrepago" y
    /// deja la reserva saldada en 0 en esa moneda. El bolsillo es POR MONEDA.
    ///
    /// <para><b>Idempotente por construccion</b>: solo convierte mientras exista un excedente (Balance &lt; 0)
    /// en la moneda. Tras la primera conversion el puente deja el balance en 0, asi que una segunda corrida
    /// (p.ej. restaurar de nuevo) no crea otro credito. Esto es lo que permite re-correrla con seguridad en
    /// los caminos legacy/restore.</para>
    ///
    /// <para><b>Precondicion para convertir</b>: la reserva tiene un pagador (PayerId) y el excedente es
    /// &gt; 0. Sin pagador no hay bolsillo de cliente posible: se loguea un Warning y se deja el saldo a
    /// favor en la reserva (no rompe).</para>
    ///
    /// <para>Hace su propio <c>SaveChanges</c> + recalculo de saldo (ver nota de contrato en la cabecera de
    /// la clase).</para>
    /// </summary>
    public static async Task ConvertAsync(
        AppDbContext db,
        Payment payment,
        string? actorUserId,
        string? actorUserName,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (payment.ReservaId is null) return;
        var reservaId = payment.ReservaId.Value;

        // La moneda del SALDO al que se imputo el pago: la imputada si cruzo, si no la real del pago.
        var saldoCurrency = Monedas.Normalizar(payment.ImputedCurrency ?? payment.Currency);

        // Saldo de esa moneda DESPUES del recalculo. Balance < 0 = la reserva esta sobre-pagada (a favor
        // del cliente) en esa moneda. El excedente es el valor absoluto.
        var row = await db.ReservaMoneyByCurrency
            .FirstOrDefaultAsync(
                m => m.ReservaId == reservaId && m.Currency == saldoCurrency,
                cancellationToken);
        if (row is null || row.Balance >= 0m) return;

        var overpaid = EconomicRulesHelper.RoundCurrency(-row.Balance);
        if (overpaid <= 0m) return;

        var reserva = await db.Reservas
            .FirstOrDefaultAsync(r => r.Id == reservaId, cancellationToken);
        if (reserva?.PayerId is null)
        {
            // Sin pagador no hay bolsillo de cliente. Se deja el saldo a favor en la reserva (no se rompe).
            logger.LogWarning(
                "Sobrepago detectado en reserva {ReservaId} ({Currency} {Overpaid}) pero la reserva no tiene pagador; no se convierte a saldo a favor.",
                reservaId, saldoCurrency, overpaid);
            return;
        }

        // Crear el bolsillo de saldo a favor del cliente por el excedente, en la moneda del saldo.
        var credit = new ClientCreditEntry
        {
            CustomerId = reserva.PayerId.Value,
            // Origen SOBREPAGO: FKs de cancelacion en null (es el discriminador de la guarda B5).
            OperatorRefundAllocationId = null,
            BookingCancellationId = null,
            Currency = saldoCurrency,
            CreditedAmount = overpaid,
            RemainingBalance = overpaid,
            IsFullyConsumed = false,
            CreatedAt = DateTime.UtcNow,
            // Trazabilidad del sobrepago: que cobro y que reserva lo generaron + actor.
            SourcePaymentId = payment.Id,
            SourceReservaId = reservaId,
            CreatedByUserId = actorUserId,
            CreatedByUserName = actorUserName,
        };
        db.ClientCreditEntries.Add(credit);

        // El excedente se SACA del saldo de la reserva con un Payment "puente" NEGATIVO y AffectsCash=false
        // (NO mueve caja, NO genera asiento): el calculator suma los pagos vivos para TotalPaid, asi que un
        // monto negativo baja lo "pagado a la reserva" por el excedente y la deja en 0 en esa moneda. La
        // plata YA entro a caja (asiento del cobro original); este puente solo TRASLADA la posicion del
        // excedente al bolsillo del cliente, no es un hecho de caja. AffectsCash=false => el guard del
        // asiento (RegisterPayment) nunca lo asienta.
        var bridge = new Payment
        {
            ReservaId = reservaId,
            Amount = -overpaid,
            Currency = saldoCurrency,
            Method = OverpaymentCreditCleanup.BridgeMethod,
            Notes = $"Sobrepago trasladado a saldo a favor del cliente (cobro {payment.PublicId}).",
            PaidAt = DateTime.UtcNow,
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = false,
            // ADR-022 §4.9 (fix S1): atamos el puente al cobro fuente por OriginalPaymentId. Es la FK real que
            // luego usa OverpaymentCreditCleanup para encontrarlo al anular/editar el cobro, sin parsear Notes.
            OriginalPaymentId = payment.Id,
            CreatedByUserId = actorUserId,
            CreatedByUserName = actorUserName,
        };
        db.Payments.Add(bridge);

        await db.SaveChangesAsync(cancellationToken);

        // Recalcular: el Payment puente (AffectsCash=false pero imputado) deja la reserva en 0 en esa moneda.
        await ReservaMoneyPersister.PersistAsync(db, reservaId, cancellationToken);

        logger.LogInformation(
            "Sobrepago convertido a saldo a favor. ReservaId={ReservaId} CustomerId={CustomerId} {Currency} {Overpaid} CreditPublicId={CreditPublicId}",
            reservaId, reserva.PayerId.Value, saldoCurrency, overpaid, credit.PublicId);
    }
}
