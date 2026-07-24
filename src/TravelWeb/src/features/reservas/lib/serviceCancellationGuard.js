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
 * Obra "anular sin factura" (2026-07-23, decisión del dueño): el freno R1 (pago al
 * operador sin factura de venta) DEJÓ DE BLOQUEAR "anular un servicio" — el backend ya no
 * tira ese código en el POST de cancel-service, siempre deja la línea-ancla del reembolso
 * en su lugar. El código PAGO_SIN_FACTURA queda definido acá porque el backend lo sigue
 * mandando en DOS acciones que SÍ siguen bloqueando (reasignar el operador de un servicio
 * pagado y bajar el estado de un servicio confirmado y pagado) — pero ya sin botón "Emitir
 * factura": el texto del motor ahora orienta a "gestioná primero el reembolso con el
 * operador", así que el mapeo de acá abajo deja de ofrecer ese camino.
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

// Códigos estables que manda el backend (ServiceCancellationRejectedException.Codes, mismo
// nombre literal de cada lado para que no puedan divergir con el tiempo). El texto que se
// muestra SIEMPRE es el `message` real del backend — estos códigos solo deciden qué botón de
// camino ofrecer al lado (cuando corresponde ofrecer alguno).
//
// PAGO_SIN_FACTURA ya NO llega en el POST de cancel-service ("anular servicio" dejó de
// bloquear, obra 2026-07-23) — sigue llegando en el PUT/PATCH de edición cuando se reasigna
// el operador de un servicio pagado o se baja su estado (ver resolverRechazoAnularServicio).
export const CODIGO_RECHAZO_ANULAR_SERVICIO = Object.freeze({
  VOUCHER_VIVO: "CANCEL_SERVICE_VOUCHER_LIVE",
  PAGO_SIN_FACTURA: "CANCEL_SERVICE_UNANCHORED_OPERATOR_REFUND",
  SIN_CLIENTE: "CANCEL_SERVICE_NO_PAYER",
});

/**
 * Resuelve qué botón de camino corresponde a un 409 con alguno de los códigos de
 * CODIGO_RECHAZO_ANULAR_SERVICIO, leyendo `error.payload.code` (nunca el texto del mensaje,
 * que es el que se muestra al usuario tal cual). Distingue "código conocido" (uno de los 3
 * catalogados) de "código ausente/no reconocido" para que el modal pueda decidir si cae al
 * camino de respaldo (el párrafo genérico + "Entendido", sin ningún botón extra).
 *
 * Obra "anular sin factura" (2026-07-23, decisión del dueño): PAGO_SIN_FACTURA quedó SIN
 * botón — antes ofrecía "Emitir factura" para el freno R1 de "anular servicio", pero ese
 * freno se eliminó. El código lo siguen mandando dos acciones distintas que SÍ bloquean
 * (reasignar operador de un servicio pagado / bajar su estado), con un mensaje que ya NO pide
 * factura, así que no corresponde ofrecer ningún camino: el modal/cartel solo muestra el
 * texto real del motor + "Entendido".
 *
 * @param {{ payload?: { code?: string } }|null|undefined} error - el error que lanza
 *   `api.js` (tiene `.payload` con el body JSON de la respuesta 409).
 * @returns {{ codigoConocido: boolean, boton: "ver-vouchers"|null }}
 */
export function resolverRechazoAnularServicio(error) {
  const codigo = error?.payload?.code ?? null;

  if (codigo === CODIGO_RECHAZO_ANULAR_SERVICIO.VOUCHER_VIVO) {
    return { codigoConocido: true, boton: "ver-vouchers" };
  }
  if (codigo === CODIGO_RECHAZO_ANULAR_SERVICIO.PAGO_SIN_FACTURA) {
    // Sigue siendo un motivo catalogado (lo siguen mandando "reasignar operador" y "bajar
    // estado"), pero ya no ofrece ningún botón — ver nota de la función de arriba.
    return { codigoConocido: true, boton: null };
  }
  if (codigo === CODIGO_RECHAZO_ANULAR_SERVICIO.SIN_CLIENTE) {
    // Motivo catalogado, pero sin camino disponible: no existe hoy ninguna pantalla en el
    // producto para asignar cliente a una reserva existente (backlog documentado en la
    // spec, no se inventa acá). El modal solo muestra el texto real + "Entendido".
    return { codigoConocido: true, boton: null };
  }

  return { codigoConocido: false, boton: null };
}
