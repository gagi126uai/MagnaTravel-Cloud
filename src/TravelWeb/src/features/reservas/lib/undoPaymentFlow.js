/**
 * Lógica pura de "Deshacer cobro" (fix BUG 2, prueba integral 2026-08-05: la UI decía
 * "Eliminar" para una acción que el backend nunca borra — regla absoluta del producto,
 * constitución P-1/P-14/F-6, "nada se dice Eliminar; todo se deshace/anula con rastro").
 *
 * El backend (PaymentsController/PaymentService, ADR-032/ADR-035) tiene DOS caminos,
 * los dos con rastro (soft-delete + contra-asiento de caja, nunca un borrado real):
 *
 *   - DELETE /api/payments/{id}   → estados "vivos" de la reserva (se puede corregir
 *     un cobro mal cargado libremente).
 *   - POST /api/payments/{id}/annul → SOLO en los 4 estados terminales donde el DELETE
 *     queda cerrado por el motor (PaymentService.EnsurePaymentEditableByStateAsync):
 *     Closed (Finalizada), Cancelled (Anulada), Lost (Perdida), PendingOperatorRefund
 *     (Esperando reembolso). Es la ÚNICA forma válida de corregir un cobro en esos casos.
 *
 * T-3: el guard real vive en el motor (si el motor rechaza, `resolverRutaDeshacerCobro`
 * no lo sabe de antemano en casos raros — el backend manda su propio mensaje y el front
 * lo muestra tal cual, P-13). Esta función solo REFLEJA la regla ya escrita en el backend
 * para elegir a qué endpoint golpear; no inventa ni relaja ninguna validación.
 *
 * Vive en un `.js` sin JSX (mismo patrón que `paymentRowGuard.js`) para poder testearse
 * con Node puro: node --test src/features/reservas/lib/undoPaymentFlow.test.mjs
 */

import { formatCurrency } from "../../../lib/utils.js";

// Mismo set EXACTO que el backend (ver PaymentService.cs, comentario de DeletePaymentAsync:
// "ADR-035 ... TERMINALES {Closed, Cancelled, Lost, PendingOperatorRefund}"). Si el motor
// cambia este set algún día, el chequeo real sigue siendo el del backend (T-3) — acá solo
// se refleja para no golpear el endpoint equivocado y forzar un 409 innecesario.
const ESTADOS_QUE_SOLO_ADMITEN_ANULAR_CON_RASTRO = new Set([
  "Closed",
  "Cancelled",
  "Lost",
  "PendingOperatorRefund",
]);

/**
 * @param {string|null|undefined} reservaStatus - `reserva.status`, ya cargado en pantalla.
 * @returns {boolean} true si el ÚNICO camino válido es anular con rastro (/annul).
 */
export function requiereAnularConRastro(reservaStatus) {
  return ESTADOS_QUE_SOLO_ADMITEN_ANULAR_CON_RASTRO.has(reservaStatus);
}

/**
 * Decide qué endpoint golpear para deshacer un cobro puntual, mirando el estado de la
 * reserva que la pantalla YA tiene cargado (no hace ninguna llamada nueva).
 *
 * @param {string} paymentPublicId - publicId del cobro (ya resuelto por el caller con getPublicId).
 * @param {string|null|undefined} reservaStatus - `reserva.status`.
 * @returns {{ metodo: "post"|"delete", ruta: string }} - `metodo` es el verbo del cliente
 *   HTTP (`api.post`/`api.delete`, en minúscula para llamarlo directo: `api[metodo](ruta)`).
 */
export function resolverRutaDeshacerCobro(paymentPublicId, reservaStatus) {
  if (requiereAnularConRastro(reservaStatus)) {
    return { metodo: "post", ruta: `/payments/${paymentPublicId}/annul` };
  }
  return { metodo: "delete", ruta: `/payments/${paymentPublicId}` };
}

/**
 * Arma el texto del diálogo de confirmación antes de deshacer un cobro (P-14: explicar
 * el efecto en la plata, en criollo, sin jerga). Las claves devueltas coinciden con las
 * que ya acepta `ConfirmModal`/`askConfirmation` (title/message/confirmText/cancelText/type)
 * — mismo diálogo que ya existía para "Eliminar cobro", solo con el texto cambiado.
 *
 * @param {{ amount?: number, currency?: string }} payment - el cobro a deshacer.
 * @returns {{ title: string, message: string, confirmText: string, cancelText: string, type: string }}
 */
export function construirConfirmacionDeshacerCobro(payment) {
  const monto = formatCurrency(payment?.amount ?? 0, payment?.currency ?? "ARS");
  return {
    title: "¿Deshacer este cobro?",
    message: `El cobro de ${monto} se va a deshacer y el saldo de la reserva se recalcula. Queda registrado en el historial.`,
    confirmText: "Deshacer cobro",
    cancelText: "No, volver",
    // "warning" (ícono de alerta amarillo) en vez de "danger" (ícono de tacho de basura):
    // esta acción ya no se presenta como un borrado, así que tampoco debe VERSE como uno.
    type: "warning",
  };
}

/**
 * F5 (review 2026-08-05): `DELETE /payments/{id}` y `POST /payments/{id}/annul` están
 * gateados con `[Authorize(Roles = "Admin")]` y devuelven un 403 SIN CUERPO cuando el
 * usuario no es admin (lo arma el middleware de autorización de ASP.NET, no el
 * controller). Un 404 (cobro ya no existe) tampoco trae body en estos dos endpoints
 * (`return NotFound()`, sin ProblemDetails). El mapeo GLOBAL de errores
 * (`lib/errors.js`) trata esos bare status como "statusText sin contexto del servidor"
 * y muestra el genérico de RED ("No se pudo conectar...") — una mentira acá: no es un
 * problema de conexión, es de permiso o de que el cobro dejó de existir.
 *
 * Esta función NO reemplaza el mapeo global (sigue sirviendo a todas las demás
 * pantallas con su propio criterio) — es un mapeo LOCAL, solo para esta acción
 * puntual, que el caller consulta ANTES de caer al mensaje genérico del motor.
 *
 * @param {number|null|undefined} status - `error.status` del cliente HTTP.
 * @returns {string|null} el mensaje local en criollo si el status tiene uno propio
 *   para esta acción; `null` si el caller debe seguir con el mensaje del motor
 *   (`getApiErrorMessage`) — P-13: cualquier otro rechazo se muestra tal cual.
 */
export function resolverMensajeErrorDeshacerCobro(status) {
  if (status === 403) {
    return "No tenés permiso para deshacer cobros. Pedíselo a un administrador.";
  }
  if (status === 404) {
    return "No se encontró el cobro. Actualizá la página.";
  }
  return null;
}
