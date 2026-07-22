namespace TravelApi.Domain.Reservations;

/// <summary>
/// Tanda 6 (plan de remediacion "contrato pantalla-motor", 2026-07-20): hechos MINIMOS para decidir si UN
/// pago puntual admite editar o eliminar. Son los mismos hechos que ya consultan los guards reales de
/// escritura (<c>MutationGuards.GetPaymentMutationBlockReasonAsync</c> /
/// <c>DeleteGuards.GetPaymentDeleteBlockReasonAsync</c>, capa Infrastructure). Esta clase es PURA (sin EF,
/// sin acceso a la base) para que los DOS consumidores la lean sin duplicar la regla:
///  - los guards reales, que SI consultan la base antes de invocarla (son la defensa final, en el momento
///    del POST/PUT/DELETE);
///  - el armado de la ficha de la reserva (<c>ReservaService.GetReservaByIdAsync</c>), que YA trae el recibo
///    de cada pago y todas las facturas de la reserva en el MISMO query (con <c>Include</c>), asi que arma
///    este contexto sin pagar una consulta nueva por cada cobro (evita N+1 en una lista con muchos pagos).
/// </summary>
/// <param name="HasIssuedReceipt">
/// True si el pago tiene un recibo en estado Issued (recibo activo, ya entregado al cliente).
/// </param>
/// <param name="HasOnlyVoidedReceipt">
/// True si el pago tiene un recibo, pero SOLO en estado Voided (anulado, sin ninguno vigente).
/// </param>
/// <param name="IsLinkedToLiveInvoice">
/// True si el pago esta vinculado (RelatedInvoiceId) a una FACTURA viva: no es Nota de Credito, tiene CAE
/// asignado y su anulacion no fue exitosa (<c>AnnulmentStatus != Succeeded</c>). Es el hecho que usa la
/// regla de EDITAR: mientras la factura siga viva, el pago forma parte de un comprobante AFIP y no se toca.
/// </param>
/// <param name="IsLinkedToAnyInvoice">
/// True si el pago tiene CUALQUIER factura vinculada, este viva o no. Es el hecho que usa la regla de
/// ELIMINAR: a diferencia de editar, borrar el pago fisicamente queda bloqueado apenas estuvo ALGUNA VEZ
/// ligado a una factura, aunque esa factura ya se haya anulado del todo (nota de credito aprobada). Es mas
/// estricta que la regla de editar A PROPOSITO: borrar es irreversible, asi que se prefiere conservar el
/// rastro completo. Es comportamiento PREEXISTENTE (DeleteGuards, regla C28 del 2026-05-11), no una
/// decision nueva de esta tanda — esta clase solo lo expone, no lo cambia.
/// </param>
public sealed record PaymentCapabilityContext(
    bool HasIssuedReceipt,
    bool HasOnlyVoidedReceipt,
    bool IsLinkedToLiveInvoice,
    bool IsLinkedToAnyInvoice);

/// <summary>
/// Tanda 6: las dos capacidades de UN pago puntual, ya resueltas. El front apaga "Editar"/"Eliminar" con el
/// motivo (<see cref="Cap.Reason"/>) cuando <see cref="Cap.Allowed"/> es false, ANTES de que el usuario abra
/// el formulario — en vez de dejarlo completar todo y recien ahi enterarse del rechazo.
/// </summary>
public sealed record PaymentCapabilities(Cap CanEdit, Cap CanDelete);

/// <summary>
/// Tanda 6 (2026-07-20, plan de remediacion "contrato pantalla-motor"): FUENTE UNICA de "se puede editar o
/// eliminar ESTE pago puntual, y si no, por que no". Los textos son LITERALMENTE los mismos que ya devuelven
/// los guards reales (Decision C / D2 del plan: "ningun mensaje se reescribe si ya esta bien"), salvo la
/// UNICA correccion de prolijidad ya aprobada en el mensaje de "eliminar con recibo vigente" (verbo correcto
/// + tilde faltante — ver <see cref="DeleteBlockedByIssuedReceiptReason"/>).
///
/// <para><b>Que NO es</b>: NO es la defensa final. El guard real (el que SI toca la base en el momento de
/// escribir) sigue siendo quien rechaza la operacion. Si esta politica y el guard discreparan, MANDA el
/// guard — por eso los dos evaluan los MISMOS hechos con <see cref="For"/>, para que nunca puedan discrepar
/// (mismo patron que <c>ReservaCapabilityPolicy</c>, con su test cruzado contra el guard real).</para>
/// </summary>
public static class PaymentCapabilityPolicy
{
    // ===== Motivos de EDITAR (identicos a MutationGuards.GetPaymentMutationBlockReasonAsync) =====

    public const string EditBlockedByIssuedReceiptReason =
        "No se puede editar el pago porque tiene un recibo emitido. Anulá el recibo y registrá un nuevo pago.";

    public const string EditBlockedByVoidedReceiptReason =
        "No se puede editar el pago porque tiene un recibo anulado que debe preservarse para auditoría.";

    // "(CAE)" se mantiene A PROPOSITO en este texto: es la sigla que el usuario YA ve en el comprobante
    // AFIP y esta redaccion esta FIRMADA tal cual en docs/ux/2026-07-20-t5-a-t9-contrato-pantalla-motor.md
    // (linea 142). Tanda P4 (2026-07-22): solo se corrigen las tildes faltantes ("esta"->"está",
    // "credito"->"crédito"), no se toca la sigla.
    public const string EditBlockedByLiveInvoiceReason =
        "No se puede editar el pago porque está vinculado a una factura emitida (CAE). Generá una nota de crédito si corresponde.";

    // ===== Motivos de ELIMINAR (fuente UNICA: DeleteGuards/MutationGuards DELEGAN en esta clase) =====

    /// <summary>
    /// Correccion de prolijidad (Tanda 6, ya aprobada por el dueño en la spec — no es rediseño de la regla):
    /// el texto original decia "No se puede ANULAR el pago... ANULA primero el comprobante" (verbo cambiado
    /// por error + falta la tilde). Al exponer este motivo por fila en la ficha se corrige al verbo que
    /// corresponde a la accion real que el usuario esta intentando ("eliminar") y se agrega la tilde faltante.
    /// </summary>
    public const string DeleteBlockedByIssuedReceiptReason =
        "No se puede eliminar el pago porque tiene un comprobante vigente. Anulá primero el comprobante.";

    public const string DeleteBlockedByLiveInvoiceReason =
        "No se puede eliminar el pago porque está vinculado a una factura. Generá una nota de crédito si corresponde.";

    /// <summary>Evalua las dos capacidades del pago a partir de su contexto minimo. Pura: no toca la base.</summary>
    public static PaymentCapabilities For(PaymentCapabilityContext ctx) => new(
        CanEdit: EvaluateEdit(ctx),
        CanDelete: EvaluateDelete(ctx));

    /// <summary>
    /// Orden de evaluacion IDENTICO al guard real: recibo Issued primero (el motivo mas especifico), despues
    /// recibo Voided-solo, despues factura viva. Se devuelve siempre el PRIMER motivo que aplica — nunca se
    /// muestran dos motivos a la vez para el mismo pago.
    /// </summary>
    private static Cap EvaluateEdit(PaymentCapabilityContext ctx)
    {
        if (ctx.HasIssuedReceipt) return Cap.No(EditBlockedByIssuedReceiptReason);
        if (ctx.HasOnlyVoidedReceipt) return Cap.No(EditBlockedByVoidedReceiptReason);
        if (ctx.IsLinkedToLiveInvoice) return Cap.No(EditBlockedByLiveInvoiceReason);
        return Cap.Yes;
    }

    /// <summary>
    /// A diferencia de editar: un recibo SOLO Voided (sin uno vigente) NO bloquea eliminar (regla vigente
    /// desde 2026-05-11, C28) — el pago se puede borrar (soft-delete) y el recibo anulado se preserva aparte
    /// para auditoria. Tampoco distingue factura viva de no-viva: CUALQUIER vinculo a factura bloquea (ver
    /// el doc de <see cref="PaymentCapabilityContext.IsLinkedToAnyInvoice"/> para el motivo de esa asimetria).
    /// </summary>
    private static Cap EvaluateDelete(PaymentCapabilityContext ctx)
    {
        if (ctx.HasIssuedReceipt) return Cap.No(DeleteBlockedByIssuedReceiptReason);
        if (ctx.IsLinkedToAnyInvoice) return Cap.No(DeleteBlockedByLiveInvoiceReason);
        return Cap.Yes;
    }
}
