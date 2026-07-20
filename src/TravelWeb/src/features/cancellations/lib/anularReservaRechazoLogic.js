/**
 * Tanda 3 del "contrato pantalla-motor" (spec docs/ux/2026-07-20-t3-t4-contrato-pantalla-motor.md):
 * el panel "Anular reserva" (CancelarReservaInline.jsx) tiene TRES puntos donde puede llegar
 * un 409 del motor (annul-with-credit, draft() y confirm() del camino con Nota de Crédito).
 * Antes de esta tanda, los tres mostraban siempre el mismo cartel genérico ("Probá de nuevo;
 * si el problema sigue, contactá a administración"), aunque el motor YA sabía la causa real
 * (por ejemplo: "esta reserva tiene servicios de varios operadores").
 *
 * Esta función es la ÚNICA fuente del mapeo código → texto criollo. Se usa en los tres
 * `catch` del componente para que un código nuevo se agregue una sola vez, no tres.
 *
 * Política de seguridad (no cambia con esta tanda, la hereda del componente): si el código
 * que llega no está en el mapa de abajo, se muestra el texto neutro que ya existía en ese
 * punto del panel — NUNCA el texto crudo que manda el backend (podría traer nombres
 * internos de clases, tablas o campos).
 *
 * Vive en un .js sin JSX (mismo patrón que multiCreditNoteFlow.js o penaltyPayload.js) para
 * poder testearlo con Node puro, sin bundler.
 */

// El código de negocio real puede venir en dos lugares distintos según qué endpoint falló:
//   - draft()/confirm() (camino con Nota de Crédito): la excepción es BusinessInvariantViolationException
//     y el GlobalExceptionHandler la manda en `invariantCode` (ej. "INV-152").
//   - annul-with-credit (camino saldo a favor): la excepción es AnnulWithCreditRejectedException
//     y el controller la manda directo en `code` (ej. "ANNUL_CREDIT_LIVE_INVOICE").
// OJO: el camino con Nota de Crédito TAMBIÉN manda un `code`, pero es un valor fijo y genérico
// ("business_invariant_violation") que identifica la FAMILIA de error, no la causa puntual —
// por eso acá se prioriza `invariantCode` sobre `code` (si viene invariantCode, es el bueno).
function leerCodigoDeNegocio(error) {
  const payload = error?.payload;
  if (!payload) return null;
  return payload.invariantCode || payload.code || null;
}

// Único código de la tabla que además de cambiar el texto agrega un botón en el cartel
// (freno de plata R1, D1 firmada el 2026-07-18: "sí, botón 'Emitir factura' en el cartel").
export const CODIGO_RECHAZO_ANULAR_RESERVA_CON_FRENO_DE_PLATA = "ANNUL_CREDIT_UNANCHORED_OPERATOR_REFUND";

// Mapa código → texto criollo, exactamente como lo firmó la spec UX (tabla "el mapa código →
// criollo"). Cuando la fila de la spec tenía un "camino" en texto (no un botón), ese texto
// se suma como segunda oración del mismo cartel — es lo que muestra el mockup de la spec.
const MAPA_CODIGO_A_TEXTO = {
  // Camino draft() — rescatado de CancelReservaModal.jsx (componente muerto, retirado en esta
  // misma tanda), único ajuste: "cancelación" → "anulación" (vocabulario del dueño).
  "INV-152":
    "Esta reserva tiene servicios de varios operadores. Por ahora la anulación de reservas con varios operadores no está disponible desde acá. Gestionala manualmente o pedile ayuda a un administrador.",
  // Camino draft().
  "INV-081": "Esta reserva ya tiene una anulación en curso. Actualizá la página para ver en qué quedó.",
  // Camino draft() o confirm() — la factura original ya se anuló con una NC, no queda nada por hacer.
  "INV-100": "La factura de esta reserva ya fue anulada con una nota de crédito. No queda nada más para anular.",
  // Camino confirm() — el panel estaba abierto y la anulación cambió de estado por otro lado.
  "INV-093": "Esta anulación cambió de estado mientras la tenías abierta. Actualizá la página para ver cómo sigue.",
  // Camino annul-with-credit — los 4 códigos "(nuevo)" de la spec, agregados por el backend
  // en esta misma tanda (antes viajaban sin ningún identificador, solo `message`).
  ANNUL_CREDIT_NOT_FIRM_STATE:
    "Esta reserva todavía no está En gestión ni Confirmada. Para anularla así, primero tiene que estar en una de esas dos etapas.",
  ANNUL_CREDIT_LIVE_INVOICE:
    "Se emitió una factura en esta reserva mientras la tenías abierta. Para anularla ahora hay que hacerlo por el camino con nota de crédito. Cerrá este panel y volvé a abrir 'Anular reserva' — el sistema ya te va a ofrecer el camino correcto.",
  ANNUL_CREDIT_NO_PAYER:
    "Esta reserva no tiene un cliente asignado, así que no hay a quién devolverle el saldo a favor. Asigná un cliente pagador a la reserva y volvé a intentar.",
  [CODIGO_RECHAZO_ANULAR_RESERVA_CON_FRENO_DE_PLATA]:
    "Ya le pagaste al operador por uno o más servicios y esta reserva todavía no tiene factura emitida para registrar ese reembolso a tu favor.",
};

/**
 * Resuelve qué texto mostrar en el cartel rojo del panel "Anular reserva" para un 409
 * recibido en cualquiera de los tres puntos de swallow, y si corresponde ofrecer el botón
 * "Emitir factura" (único caso de la tabla que lo lleva).
 *
 * `CONCURRENT_EDIT` NO pasa por acá: el componente lo sigue resolviendo antes de llamar a
 * esta función (regla de la spec: "ya funciona bien, no se toca").
 *
 * @param {object} error - error lanzado por el cliente HTTP (api.js). Trae `status` y,
 *   si el backend mandó body, `payload` con el JSON (invariantCode y/o code).
 * @param {string} textoNeutro - el mensaje genérico que ya mostraba ese punto del panel
 *   antes de esta tanda (distinto según sea annul-with-credit / draft / confirm). Es el
 *   fallback para cualquier código que no esté en la tabla de arriba, o si el 409 no
 *   trajo ningún código.
 * @returns {{ texto: string, mostrarBotonEmitirFactura: boolean }}
 */
export function resolverTextoRechazoAnularReserva(error, textoNeutro) {
  const codigo = leerCodigoDeNegocio(error);
  const textoMapeado = codigo ? MAPA_CODIGO_A_TEXTO[codigo] : undefined;

  return {
    texto: textoMapeado ?? textoNeutro,
    mostrarBotonEmitirFactura: codigo === CODIGO_RECHAZO_ANULAR_RESERVA_CON_FRENO_DE_PLATA,
  };
}
