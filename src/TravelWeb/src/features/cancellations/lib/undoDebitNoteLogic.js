/**
 * Lógica PURA de "Deshacer una multa YA emitida y aprobada por ARCA" (ADR-044, spec
 * `docs/ux/2026-07-14-deshacer-multa-emitida.md`). Separada del componente
 * (DeshacerMultaEmitidaInline.jsx) para poder testear la validación del motivo y el
 * armado del payload sin DOM — mismo criterio que el resto de los paneles de multa
 * (ver DeshacerCierreSinMultaInline.jsx, otroCargoOperador.js).
 */

// Mismo límite que el resto de los "motivos" de la ficha (DeshacerCierreSinMultaInline,
// CerrarSinMultaInline): 5..500 caracteres, espejo del contrato del backend
// (UndoDebitNoteRequest.Reason, ver CancellationsController.cs).
const MOTIVO_MIN = 5;
const MOTIVO_MAX = 500;

/**
 * Valida el motivo obligatorio de "Deshacer".
 *
 * @param {string} motivo
 * @returns {string|null} - Mensaje de error, o null si es válido.
 */
export function validarMotivoDeshacerMulta(motivo) {
  const trimmed = (motivo ?? "").trim();
  if (trimmed.length < MOTIVO_MIN) {
    return `El motivo debe tener al menos ${MOTIVO_MIN} caracteres.`;
  }
  if (trimmed.length > MOTIVO_MAX) {
    return `El motivo no puede superar los ${MOTIVO_MAX} caracteres.`;
  }
  return null;
}

/**
 * Determina si el panel puede enviar el "Deshacer" (motivo válido + sin un envío ya en
 * curso). Junta la validación para que el componente no repita el condicional.
 *
 * @param {{ motivo: string, submitting: boolean }} params
 * @returns {boolean}
 */
export function puedeEnviarDeshacerMulta({ motivo, submitting }) {
  if (submitting) return false;
  return validarMotivoDeshacerMulta(motivo) === null;
}

/**
 * Arma el payload de `POST /cancellations/{publicId}/undo-debit-note`
 * (UndoDebitNoteRequest): un único campo, el motivo ya recortado. Se separa en su
 * propia función (en vez de armarlo a mano en el componente) para que quede UN solo
 * lugar si el contrato del backend cambia.
 *
 * @param {string} motivo
 * @returns {{ reason: string }}
 */
export function construirPayloadUndoDebitNote(motivo) {
  return { reason: (motivo ?? "").trim() };
}

/**
 * Gate ÚNICO (defensa en profundidad) para las DOS puertas de entrada a "Deshacer" una
 * Nota de Débito ya emitida: el link "· Deshacer: el operador cobró mal esta multa"
 * del cartel "confirmada" (Done) y el botón "Reintentar" del cartel "accionTrabada"
 * (estado "DebitNoteAnnulmentFailed") son la MISMA acción de negocio en dos momentos
 * distintos — comparten esta MISMA función para no poder divergir entre sí.
 *
 * FIX BLOQUEANTE B1 (gate de seguridad, revisión 2026-07-14): antes de este fix, el
 * botón "Reintentar" del estado fallido se habilitaba SOLO con `canUndoDebitNote` —
 * un usuario SIN rol Admin pero CON el permiso `cancellations.classify_agency_penalty`
 * podía ver ese botón y completar el deshacer de un comprobante con CAE. La regla
 * firmada de negocio dice que esta acción es, como mínimo, tan sensible como
 * "Deshacer" el cierre sin multa: **solo Administradores**, sin excepción. El backend
 * ya está cerrando su lado (`canUndoDebitNote` va a exigir Admin también, del lado del
 * servidor) — este chequeo del frontend es una CAPA ADICIONAL (defensa en profundidad),
 * NUNCA la única barrera real: la seguridad de verdad vive en el backend, esto solo
 * evita ofrecer en pantalla un botón que no debería estar.
 *
 * @param {{ canUndoDebitNote?: boolean, esAdmin?: boolean }} params
 * @returns {boolean}
 */
export function debeMostrarReintentarDeshacer({ canUndoDebitNote, esAdmin }) {
  return canUndoDebitNote === true && esAdmin === true;
}

/**
 * True si corresponde mostrar, en el paso 1 del panel de "Deshacer", la variante "el
 * cliente ya te había pagado esta multa" ("le va a quedar {monto} a favor para usar en
 * otra reserva") — spec sección 2.
 *
 * `collectedPenaltyAmount` (backend, 2026-07-14) es la porción de la multa que el
 * cliente YA pagó, en la moneda de la ND:
 *   - `0` → todavía no pagó nada (impaga): NO se muestra la línea, se usa el texto
 *     estándar.
 *   - `null`/`undefined` → el backend no pudo calcularlo: tampoco se muestra (nunca se
 *     inventa un monto que no vino).
 *   - cualquier número `> 0` → SÍ se muestra, con ese monto.
 *
 * @param {number|null|undefined} collectedPenaltyAmount
 * @returns {boolean}
 */
export function debeMostrarMontoAFavor(collectedPenaltyAmount) {
  return typeof collectedPenaltyAmount === "number" && collectedPenaltyAmount > 0;
}

// ============================================================================
// Tanda D1 (2026-07-16): "No se puede deshacer una multa con saldo a favor aplicado"
// (spec `docs/ux/2026-07-16-aplicar-saldo-a-multa-y-neteo.md`, §8).
// ============================================================================

// Código de invariante que manda el backend (BookingCancellationService, guard B3) cuando
// la ND tiene un puente de "saldo a favor aplicado" vivo — ver INV-UNDO-CREDITBRIDGE en
// BookingCancellationService.cs. Se detecta por CÓDIGO (nunca por el texto del mensaje,
// que puede cambiar de redacción) y el código en sí NUNCA se muestra al usuario.
const INVARIANT_CODE_SALDO_APLICADO = "INV-UNDO-CREDITBRIDGE";

/**
 * True si el error que devolvió POST .../undo-debit-note es EXACTAMENTE el caso "esta
 * multa tiene saldo a favor aplicado, hay que revertir esa aplicación antes de
 * deshacerla" (spec §8). Se detecta leyendo `error.payload.invariantCode` (el ProblemDetails
 * que arma GlobalExceptionHandler.cs para toda BusinessInvariantViolationException) — un
 * código estable, no el texto del mensaje, que es el que se muestra al usuario tal cual.
 *
 * @param {{ payload?: { invariantCode?: string } }|null|undefined} error - el error que
 *   lanza `api.js` (tiene `.payload` con el body JSON de la respuesta 409).
 * @returns {boolean}
 */
export function esErrorSaldoAplicadoAlDeshacerMulta(error) {
  return error?.payload?.invariantCode === INVARIANT_CODE_SALDO_APLICADO;
}
