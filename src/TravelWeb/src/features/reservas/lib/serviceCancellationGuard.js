/**
 * Lógica pura de la Tanda 7 ("contrato pantalla-motor",
 * spec docs/ux/2026-07-20-t5-a-t9-contrato-pantalla-motor.md): la papelera de "anular
 * un servicio" avisa ANTES de que el usuario haga clic, y el cartel de error (si igual
 * se llega a intentar por una carrera) muestra el botón que corresponde al motivo real.
 *
 * Dos responsabilidades separadas, mismo archivo (van siempre de la mano en esta tanda):
 *   1. resolverBloqueoAnularServicio: pre-chequeo con el campo `canCancel` que ahora manda
 *      cada servicio (reserva.hotelBookings[]/flightSegments[]/etc.) — mismo shape
 *      CapabilityDto que ya usa paymentRowGuard.js (Tanda 6): { allowed, reason }.
 *   2. resolverRechazoAnularServicio: si igual se fuerza (carrera) y el POST explota con
 *      409, el body ahora trae un `code` estable — se usa para elegir el botón del modal,
 *      NUNCA para adivinar el motivo comparando texto libre.
 *
 * Vive en un .js sin JSX (mismo patrón que paymentRowGuard.js / anularReservaRechazoLogic.js)
 * para poder testearlo con Node puro, sin bundler.
 */

/**
 * Pre-chequeo de la papelera "Anular" de UN servicio puntual.
 *
 * `service.canCancel` llega null cuando el backend todavía no lo calculó (DTO viejo, o un
 * caller de test sin el servicio de cancelaciones inyectado) — eso es "no calculado", NO
 * "está bloqueado". En ese caso no agregamos ningún bloqueo nuevo (degradación elegante,
 * mismo criterio que canEdit/canDelete del pago en la Tanda 6): la papelera se comporta
 * exactamente igual que antes de esta tanda.
 *
 * @param {object} service - un servicio normalizado (salida de normalizeReservaServices).
 *   Puede traer `canCancel: { allowed, reason } | null | undefined`.
 * @returns {{ bloqueado: boolean, motivo: string|null }}
 */
export function resolverBloqueoAnularServicio(service) {
  const canCancel = service?.canCancel;
  const bloqueado = canCancel ? canCancel.allowed === false : false;
  const motivo = bloqueado ? (canCancel.reason ?? null) : null;

  return { bloqueado, motivo };
}

// Códigos estables que manda el backend en el 409 de POST /cancellations/cancel-service
// (ServiceCancellationRejectedException.Codes, mismo nombre literal de cada lado para que
// no puedan divergir con el tiempo). El texto que se muestra SIEMPRE es el `message` real
// del backend — estos códigos solo deciden qué botón de camino ofrecer al lado.
export const CODIGO_RECHAZO_ANULAR_SERVICIO = Object.freeze({
  VOUCHER_VIVO: "CANCEL_SERVICE_VOUCHER_LIVE",
  PAGO_SIN_FACTURA: "CANCEL_SERVICE_UNANCHORED_OPERATOR_REFUND",
  SIN_CLIENTE: "CANCEL_SERVICE_NO_PAYER",
});

/**
 * Resuelve qué botón de camino corresponde al 409 que devolvió POST cancel-service, leyendo
 * `error.payload.code` (nunca el texto del mensaje, que es el que se muestra al usuario tal
 * cual). Distingue "código conocido" (uno de los 3 catalogados) de "código ausente/no
 * reconocido" para que el modal pueda decidir si cae al camino de respaldo (el párrafo
 * genérico + "Ver facturas de la reserva" que ya existía antes de esta tanda, para no dejar
 * al usuario sin ningún próximo paso ante un 409 que esta tanda no contempló).
 *
 * @param {{ payload?: { code?: string } }|null|undefined} error - el error que lanza
 *   `api.js` (tiene `.payload` con el body JSON de la respuesta 409).
 * @returns {{ codigoConocido: boolean, boton: "emitir-factura"|"ver-vouchers"|null }}
 */
export function resolverRechazoAnularServicio(error) {
  const codigo = error?.payload?.code ?? null;

  if (codigo === CODIGO_RECHAZO_ANULAR_SERVICIO.PAGO_SIN_FACTURA) {
    return { codigoConocido: true, boton: "emitir-factura" };
  }
  if (codigo === CODIGO_RECHAZO_ANULAR_SERVICIO.VOUCHER_VIVO) {
    return { codigoConocido: true, boton: "ver-vouchers" };
  }
  if (codigo === CODIGO_RECHAZO_ANULAR_SERVICIO.SIN_CLIENTE) {
    // Motivo catalogado, pero sin camino disponible: no existe hoy ninguna pantalla en el
    // producto para asignar cliente a una reserva existente (backlog documentado en la
    // spec, no se inventa acá). El modal solo muestra el texto real + "Entendido".
    return { codigoConocido: true, boton: null };
  }

  return { codigoConocido: false, boton: null };
}
