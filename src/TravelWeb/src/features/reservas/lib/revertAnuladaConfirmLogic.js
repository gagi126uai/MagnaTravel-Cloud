/**
 * Confirmación EXTRA de "Volver atrás" cuando la reserva de ORIGEN está ANULADA
 * (Cancelled/PendingOperatorRefund) — ADR-050 (2026-07-24, decisión firmada del dueño):
 * en ese caso el revert ya NO es un simple cambio de estado, deshace la anulación
 * ENTERA (revive los servicios de ese acto, retira el saldo a favor no usado con
 * contra-asiento, aborta el registro de reembolso del operador). Como tiene
 * consecuencias reales de plata, se le explica al usuario ANTES de disparar el POST
 * /reservas/{id}/revert-status — no alcanza con el motivo obligatorio genérico que
 * el modal ya pide para cualquier revert.
 *
 * Para reverts desde OTROS estados (no anulada) este paso extra NO aplica — el flujo
 * de siempre (motivo + submit) sigue igual.
 *
 * Texto FIJADO (T-6, decidir por datos estructurados, no por texto libre): pedido
 * textual del coordinador, no se parafrasea.
 */
export function construirConfirmacionDeshacerAnulacion() {
    return {
        title: "Deshacer la anulación",
        text:
            "Los servicios anulados vuelven a como estaban y se retira el registro de la " +
            "devolución del operador. Si el cliente tenía saldo a favor por esta anulación " +
            "y no se usó, también se retira. ¿Confirmás?",
        confirmText: "Sí, deshacer",
        confirmColor: "amber",
    };
}
