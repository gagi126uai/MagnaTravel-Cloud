using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-022 §4.9 (fix S1, 2026-06-11): limpieza del SALDO A FAVOR de sobrepago cuando se ANULA o se EDITA A
/// LA BAJA el cobro que lo genero.
///
/// <para><b>Por que existe</b>: cuando un cobro deja la reserva sobre-pagada, el sistema crea un
/// <see cref="ClientCreditEntry"/> de origen "sobrepago" (saldo a favor del cliente) y un <see cref="Payment"/>
/// PUENTE negativo (<c>Method="SaldoAFavor"</c>, <c>AffectsCash=false</c>) que SACA el excedente del saldo de
/// la reserva. Ese puente lo cuenta <c>ReservaMoneyCalculator.AccumulatePayments</c> (suma los pagos vivos sin
/// mirar AffectsCash). Si despues se BORRA el cobro fuente y NO se limpia el puente, el puente sigue vivo
/// (-excedente) e infla la deuda de la reserva, mientras el saldo a favor queda FANTASMA: credito sin caja
/// detras. Este helper cierra esa fuga.</para>
///
/// <para><b>Sin estado, sobre el AppDbContext del caller</b> (mismo patron que <see cref="ReservaMoneyPersister"/>
/// y <see cref="SupplierDebtPersister"/>): NO hace <c>SaveChanges</c>; las mutaciones quedan en la transaccion
/// del caller para no partir la operacion en dos commits.</para>
///
/// <para><b>El puente se ata al cobro fuente por <see cref="Payment.OriginalPaymentId"/></b>: al crear el
/// puente se setea <c>OriginalPaymentId = cobroFuente.Id</c>, asi se lo encuentra de forma estructural (FK
/// real) en vez de parsear el texto de las Notes. El credito se ata al cobro por
/// <see cref="ClientCreditEntry.SourcePaymentId"/>.</para>
/// </summary>
public static class OverpaymentCreditCleanup
{
    /// <summary>Metodo del Payment puente del sobrepago (no mueve caja, traslada el excedente al bolsillo).</summary>
    public const string BridgeMethod = "SaldoAFavor";

    /// <summary>
    /// Mensaje de negocio cuando un usuario intenta borrar o editar DIRECTAMENTE el Payment puente del
    /// saldo a favor (la "fila rara negativa" del historial). El puente es respaldo interno: solo lo
    /// crea/anula el sistema cuando se anula o edita el cobro que origino el saldo a favor.
    /// </summary>
    public const string DirectBridgeMutationBlockReason =
        "Este movimiento es el respaldo interno de un saldo a favor; gestionalo desde el cobro original.";

    /// <summary>
    /// ADR-022 §4.9 (fix S1-bis, 2026-06-11): true si <paramref name="payment"/> ES el Payment puente de un
    /// saldo a favor de sobrepago.
    ///
    /// <para><b>Por que importa</b>: el puente es un <see cref="Payment"/> normal (EntryType=Payment,
    /// Status=Paid) y por lo tanto aparece en los DELETE/PUT estandar de pagos. Si un usuario lo borra/edita a
    /// mano, el <see cref="ClientCreditEntry"/> del saldo a favor sigue vivo pero el excedente vuelve a la
    /// reserva -> el excedente existe DOS veces (reserva sobrepagada + credito en el bolsillo). Por eso los
    /// paths de mutacion directa de pagos rechazan cuando esto da true. El puente solo lo manipula el sistema
    /// (<see cref="ReverseOverpaymentArtifactsAsync"/>), que opera sobre la entidad directamente y NO pasa por
    /// esos paths, asi que esta guarda no lo bloquea.</para>
    ///
    /// <para>La firma del puente es exacta: <c>Method == BridgeMethod</c> + <c>AffectsCash == false</c> (lo
    /// distingue de un cobro real) + <c>OriginalPaymentId != null</c> (lo ata al cobro fuente; lo distingue de
    /// otros movimientos AffectsCash=false como el puente de NC).</para>
    /// </summary>
    public static bool IsOverpaymentBridge(Payment payment)
    {
        return payment.Method == BridgeMethod
            && !payment.AffectsCash
            && payment.OriginalPaymentId != null;
    }

    /// <summary>
    /// Devuelve el motivo de BLOQUEO (mensaje de negocio en espanol) si el cobro <paramref name="sourcePaymentId"/>
    /// genero un saldo a favor de sobrepago que YA fue usado (total o parcialmente), o <c>null</c> si no hay
    /// credito o el credito esta intacto (todavia se puede revertir limpio).
    ///
    /// <para>Se considera "usado" si el saldo restante bajo del monto acreditado (<c>RemainingBalance &lt;
    /// CreditedAmount</c>) O si tiene al menos un retiro/aplicacion (<see cref="ClientCreditWithdrawal"/>),
    /// incluido un <c>KeptAsCredit</c>: una vez que el cliente decidio algo sobre ese saldo, no compensamos
    /// automaticamente — hay que anular ese uso primero.</para>
    /// </summary>
    public static async Task<string?> GetConsumedBlockReasonAsync(
        AppDbContext db,
        int sourcePaymentId,
        CancellationToken ct = default)
    {
        // El credito de sobrepago se ata al cobro por SourcePaymentId. Puede no existir (cobro que nunca
        // sobrepago) -> sin credito no hay nada que bloquear.
        var credit = await db.ClientCreditEntries.AsNoTracking()
            .Where(c => c.SourcePaymentId == sourcePaymentId)
            .Select(c => new { c.Id, c.CreditedAmount, c.RemainingBalance })
            .FirstOrDefaultAsync(ct);
        if (credit == null) return null;

        var hasWithdrawals = await db.ClientCreditWithdrawals.AsNoTracking()
            .AnyAsync(w => w.ClientCreditEntryId == credit.Id, ct);

        // RemainingBalance < CreditedAmount = ya se gasto algo; cualquier withdrawal (incluido KeptAsCredit)
        // = el cliente ya decidio sobre el saldo. En ambos casos no compensamos solos.
        var alreadyConsumed = credit.RemainingBalance < credit.CreditedAmount || hasWithdrawals;
        if (!alreadyConsumed) return null;

        return "No se puede anular ni reducir este cobro porque genero un saldo a favor del cliente que ya " +
               "fue usado. Anula primero el uso de ese saldo a favor.";
    }

    /// <summary>
    /// Revierte los artefactos del sobrepago de un cobro: anula el <see cref="ClientCreditEntry"/> intacto
    /// (deja rastro de auditoria) y soft-deletea el <see cref="Payment"/> puente, de modo que al recalcular la
    /// reserva ni quede credito sin respaldo ni el puente fantasma inflando la deuda. NO hace <c>SaveChanges</c>.
    ///
    /// <para><b>Precondicion</b>: el caller DEBE haber chequeado antes <see cref="GetConsumedBlockReasonAsync"/>
    /// y abortado si devolvio motivo. Aca asumimos que el credito (si existe) esta intacto.</para>
    ///
    /// <para>El credito se ANULA (no se borra fisicamente): se pone <c>RemainingBalance=0</c> y
    /// <c>IsFullyConsumed=true</c>, y se registra el retiro como una baja administrativa via Notes/audit del
    /// puente. Mantener la fila preserva la trazabilidad del saldo que existio.</para>
    /// </summary>
    public static async Task ReverseOverpaymentArtifactsAsync(
        AppDbContext db,
        int sourcePaymentId,
        string? actorUserId,
        string? actorUserName,
        CancellationToken ct = default)
    {
        // 1) Anular el credito de sobrepago intacto (no borrarlo: deja rastro de que existio).
        var credit = await db.ClientCreditEntries
            .FirstOrDefaultAsync(c => c.SourcePaymentId == sourcePaymentId, ct);
        if (credit != null)
        {
            credit.RemainingBalance = 0m;
            credit.IsFullyConsumed = true;
        }

        // 2) Soft-deletear el Payment puente (Method SaldoAFavor, AffectsCash=false) atado al cobro fuente por
        //    OriginalPaymentId. Soft-delete -> AccumulatePayments deja de contarlo y la deuda no se infla.
        var bridge = await db.Payments
            .FirstOrDefaultAsync(p =>
                p.OriginalPaymentId == sourcePaymentId &&
                p.Method == BridgeMethod &&
                !p.AffectsCash &&
                !p.IsDeleted, ct);
        if (bridge != null)
        {
            bridge.IsDeleted = true;
            bridge.DeletedAt = DateTime.UtcNow;
        }
    }
}
