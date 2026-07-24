import { debeMostrarCartelEmergente } from "./serviceResolutionActions.js";

// Code estructurado que manda el backend en los 409 de POST /reservas/{id}/revert-status
// que corresponden a un bloqueo de ADR-050 "deshacer anulación" (saldo a favor ya usado
// en otra reserva, nota de débito de la multa ya emitida, etc).
export const CODE_UNDO_ANNULMENT_BLOCKED = "UNDO_ANNULMENT_BLOCKED";

/**
 * Decide si el rechazo del motor al hacer "Volver atrás" (POST /reservas/{id}/revert-status)
 * se muestra en el Cartel emergente único o en un toast fugaz.
 *
 * T-6 (constitución del producto, decidir por datos estructurados y no por texto libre):
 * cuando el backend manda `code: "UNDO_ANNULMENT_BLOCKED"` la decisión es SIEMPRE Cartel,
 * sin mirar el largo del mensaje. Es necesario porque los dos rechazos firmados de
 * ADR-050 ("Ese saldo a favor ya se usó en otra reserva..." y "Ya se emitió la nota de
 * débito de la multa...") miden 78 y 79 caracteres — quedan POR DEBAJO del umbral de
 * "mensaje largo" y con el criterio viejo caían en un toast fugaz, pero son rechazos de
 * negocio sobre plata (deshacer una anulación) que la guía UX exige mostrar en Cartel
 * emergente único, no en un toast que el usuario puede no llegar a leer.
 *
 * Fallback legacy: los rechazos de este mismo endpoint que todavía NO traen `code`
 * (por ejemplo, validaciones más viejas del motor que el backend no migró a este
 * contrato) siguen decidiéndose por el largo del mensaje, exactamente como antes de
 * ADR-050 — no rompemos ese comportamiento existente.
 *
 * @param {object} params
 * @param {string|null|undefined} params.mensaje - texto del rechazo, se muestra TAL CUAL (P-13)
 * @param {string|null|undefined} params.code - code estructurado del payload del error, si vino
 * @returns {boolean} true = Cartel emergente único, false = toast
 */
export function debeMostrarCartelPorRechazoDeRevert({ mensaje, code }) {
    if (code === CODE_UNDO_ANNULMENT_BLOCKED) {
        return true;
    }

    return debeMostrarCartelEmergente(mensaje);
}
