import { aplanarReembolsosPendientesPorMoneda } from "./supplierPageLogic.js";

/**
 * Lógica pura de la Tanda P2 "circuito proveedor" (2026-07-22): el bloque "Reembolsos ya
 * registrados" de la solapa "Reembolsos" del operador, con sus dos acciones — Deshacer y
 * Corregir reserva (spec docs/ux/2026-07-22-p2-deshacer-reasociar-reembolso.md).
 *
 * Separada del componente para poder testearse sin montar React (mismo criterio que
 * supplierPageLogic.js).
 *
 * Funciones exportadas:
 *   - validarMotivoAccionReembolso: motivo obligatorio >= 20 caracteres (lo exige el motor
 *     en los dos endpoints: VoidAllocationRequest y ReassociateAllocationRequest).
 *   - construirPayloadDeshacer / construirPayloadCorregir: arman el body exacto que espera
 *     cada endpoint.
 *   - filtrarDestinosParaCorregir: de la lista de reembolsos PENDIENTES de este operador,
 *     deja solo los que sirven como destino al "Corregir reserva" (P3=A de la spec: misma
 *     moneda, excluyendo la reserva a la que ya está imputado).
 *   - esErrorCreditoYaUsado: detecta el código estable REFUND_CREDIT_ALREADY_USED que manda
 *     el backend cuando el cliente ya gastó el saldo a favor (P4=B: ahí se muestra el botón
 *     "Ir a la cuenta del cliente").
 *   - hayMontosEnmascarados: decide si el bloque "Reembolsos ya registrados" tiene que
 *     mostrar el aviso único "No tenés permiso para ver los montos." (fix de review B1,
 *     2026-07-22: antes el "—" de cada fila quedaba sin explicación si no había ningún
 *     reembolso pendiente arriba, que es donde vivía ese aviso hasta ahora).
 */

// El motor exige el mismo mínimo en los dos endpoints (VoidAllocationRequest.Reason y
// ReassociateAllocationRequest.Reason llevan [MinLength(20)]). Lo replicamos acá para
// habilitar el botón de confirmar ANTES de llamar al backend — es solo UX, el motor
// vuelve a validar igual.
export const MOTIVO_ACCION_REEMBOLSO_MIN = 20;
export const MOTIVO_ACCION_REEMBOLSO_MAX = 500;

/**
 * Código estable que manda OperatorRefundActionRejectedException cuando el saldo a favor
 * que generó el reembolso ya fue retirado o aplicado por el cliente. Definido acá una sola
 * vez (en vez de repetir el string mágico en cada lugar que lo compara) — mismo valor que
 * `OperatorRefundActionRejectedException.Codes.CreditAlreadyUsed` en el backend.
 */
export const CODIGO_CREDITO_YA_USADO = "REFUND_CREDIT_ALREADY_USED";

/**
 * Valida el motivo de "Deshacer" o "Corregir reserva" ANTES de llamar al backend.
 * Mismo contrato mínimo/máximo que el motor (MinLength(20) / MaxLength(500)).
 *
 * @param {string} motivo
 * @returns {string|null} mensaje de error en criollo, o null si es válido
 */
export function validarMotivoAccionReembolso(motivo) {
  const trimmed = (motivo ?? "").trim();
  if (trimmed.length < MOTIVO_ACCION_REEMBOLSO_MIN) {
    return `El motivo debe tener al menos ${MOTIVO_ACCION_REEMBOLSO_MIN} caracteres.`;
  }
  if (trimmed.length > MOTIVO_ACCION_REEMBOLSO_MAX) {
    return `El motivo no puede superar los ${MOTIVO_ACCION_REEMBOLSO_MAX} caracteres.`;
  }
  return null;
}

/**
 * Decide si el botón "Deshacer reembolso" puede apretarse.
 *
 * @param {{ motivo: string, submitting: boolean }} params
 * @returns {boolean}
 */
export function puedeConfirmarDeshacer({ motivo, submitting }) {
  if (submitting) return false;
  return validarMotivoAccionReembolso(motivo) === null;
}

/**
 * Decide si el botón "Mover a la reserva #N" puede apretarse: hace falta ELEGIR un destino
 * Y un motivo válido (los dos, no alcanza con uno solo).
 *
 * @param {{ destinoElegido: object|null, motivo: string, submitting: boolean }} params
 * @returns {boolean}
 */
export function puedeConfirmarCorregir({ destinoElegido, motivo, submitting }) {
  if (submitting) return false;
  if (!destinoElegido) return false;
  return validarMotivoAccionReembolso(motivo) === null;
}

/**
 * Arma el body del DELETE .../allocations/{id} (Deshacer). El motor espera { reason }.
 *
 * @param {string} motivo
 * @returns {{ reason: string }}
 */
export function construirPayloadDeshacer(motivo) {
  return { reason: (motivo ?? "").trim() };
}

/**
 * Arma el body del PATCH .../allocations/{id}/reassociate (Corregir reserva). El motor
 * espera { newBookingCancellationPublicId, reason } — ver ReassociateAllocationRequest.
 *
 * @param {{ bookingCancellationPublicId: string }} destinoElegido
 * @param {string} motivo
 * @returns {{ newBookingCancellationPublicId: string, reason: string }}
 */
export function construirPayloadCorregir(destinoElegido, motivo) {
  return {
    newBookingCancellationPublicId: destinoElegido?.bookingCancellationPublicId ?? "",
    reason: (motivo ?? "").trim(),
  };
}

/**
 * P3=A de la spec: la lista de destinos para "Corregir reserva" muestra SOLO las
 * anulaciones de este mismo operador que están esperando reembolso EN LA MISMA MONEDA que
 * el reembolso que se está corrigiendo, y NUNCA la reserva a la que ya está imputado (no
 * tiene sentido "corregir" a donde ya está).
 *
 * Reusa `aplanarReembolsosPendientesPorMoneda` (misma función que usa "Registrar reembolso
 * recibido") para no duplicar la lógica de aplanado por moneda — una fila del resultado de
 * esa función ya es "una anulación + una moneda", que es exactamente la unidad que
 * necesitamos acá.
 *
 * @param {Array<object>} itemsPendientes - respuesta cruda de getPendingBySupplier.
 * @param {{ currency: string, reservaPublicIdActual: string }} filtro
 * @returns {Array<object>} filas candidatas a destino (mismo shape que aplanarReembolsosPendientesPorMoneda)
 */
export function filtrarDestinosParaCorregir(itemsPendientes, { currency, reservaPublicIdActual }) {
  const filas = aplanarReembolsosPendientesPorMoneda(itemsPendientes);
  return filas.filter(
    (fila) => fila.currency === currency && fila.reservaPublicId !== reservaPublicIdActual
  );
}

/**
 * Detecta si el rechazo del motor es EXACTAMENTE el caso "el cliente ya usó ese saldo a
 * favor" (P4=B de la spec). Se compara por CÓDIGO estable, nunca por el texto del mensaje
 * (que puede cambiar de redacción) — mismo criterio que `esErrorSaldoYaUsado` en
 * DeshacerCierreSinMultaInline.jsx.
 *
 * `api.js` copia el `code` del body 409 tanto a `error.code` como a `error.payload.code`;
 * acá miramos `error.payload.code` porque es el mismo lugar que ya usa el resto de la app
 * para leer códigos de negocio del backend.
 *
 * @param {{ status?: number, payload?: { code?: string } }} error
 * @returns {boolean}
 */
export function esErrorCreditoYaUsado(error) {
  return error?.status === 409 && error?.payload?.code === CODIGO_CREDITO_YA_USADO;
}

/**
 * Fix de review B1 (2026-07-22): sin `cobranzas.see_cost`, el backend manda cada
 * `netAmount` en 0 y `amountsMasked=true` — la fila igual se ve, pero el monto sale "—"
 * sin ninguna explicación. Esta función decide si HAY que mostrar el aviso único
 * "No tenés permiso para ver los montos." debajo del encabezado del bloque (mismo texto y
 * criterio que ya usan OperatorRefundsPendingSection y RegistrarReembolsoRecibidoInline:
 * un aviso ÚNICO por pantalla, no repetido fila por fila).
 *
 * @param {Array<{ amountsMasked?: boolean }>} items - filas del bloque "ya registrados".
 * @returns {boolean}
 */
export function hayMontosEnmascarados(items) {
  if (!Array.isArray(items)) return false;
  return items.some((item) => item?.amountsMasked === true);
}
