/**
 * Wrapper de llamadas HTTP para la funcionalidad "Reembolsos a cobrar del operador".
 *
 * ADR-041 Tanda 4: cuando se anula una reserva pagada, el operador queda debiendo
 * devolver el dinero que ya le habíamos pagado. Esta API permite listar esas deudas
 * pendientes y reabrir las que el operador tardó demasiado en pagar (abandonadas).
 *
 * Centraliza los endpoints para que ningún componente ni hook construya URLs
 * directamente. Sigue el mismo patrón que cancellationsApi.js.
 */

import { api } from "../../../api";

export const operatorRefundsApi = {
  /**
   * Reembolsos pendientes de UN proveedor específico (para la ficha del proveedor).
   * Permiso requerido: tesoreria.supplier_payments.
   *
   * (El wrapper del endpoint GLOBAL /operator-refunds/pending se eliminó junto con la
   * bandeja global — decisión 5, spec 2026-07-03 P1=C. El endpoint del backend sigue
   * existiendo por si algún día se agrega un aviso agregado en Cobranzas.)
   *
   * @param {string} supplierPublicId - GUID del proveedor.
   * @returns {Promise<OperatorRefundPendingItemDto[]>}
   *   Cada item tiene: bookingCancellationPublicId, reservaPublicId, numeroReserva,
   *   clienteNombre, supplierPublicId, supplierName, semaphore (0-3 integer),
   *   operatorRefundDueBy, daysOverdue, estimatedRefundsByCurrency (array),
   *   amountsMasked, penaltyPendingConfirmation, rowStatus, canRegisterRefund.
   */
  getPendingBySupplier: (supplierPublicId) =>
    api.get(`/suppliers/${supplierPublicId}/operator-refunds/pending`),

  /**
   * Reabre una cancelación que el job ya dio por "abandonada" (el operador nunca pagó)
   * para que se pueda registrar un reembolso tardío desde Caja.
   *
   * El motivo es OBLIGATORIO (mínimo 10 caracteres). Es la justificación auditada
   * de por qué se reabre una cuenta que ya se había dado por perdida.
   *
   * Permiso requerido: caja.edit.
   *
   * @param {string} cancellationPublicId - GUID del BookingCancellation.
   * @param {string} reason - Motivo (>= 10 caracteres).
   * @returns {Promise<void>}
   */
  reopenForLateRefund: (cancellationPublicId, reason) =>
    api.post(
      `/operator-refunds/cancellations/${cancellationPublicId}/reopen-for-late-refund`,
      { reason }
    ),

  /**
   * Registra el ingreso del reembolso Y lo imputa a UNA cancelación en una sola llamada
   * atómica (2026-07-01). Es el camino SIMPLE: todo el bruto recibido va a saldo a favor
   * del cliente, sin deducciones fiscales tipificadas (para eso existen RecordReceived +
   * Allocate por separado, que sigue usando la bandeja de reembolsos avanzada).
   *
   * Habilita el botón "Registrar reembolso recibido" de la ficha del operador.
   * Permiso requerido: caja.edit.
   *
   * @param {{
   *   supplierPublicId: string,
   *   bookingCancellationPublicId: string,
   *   receivedAmount: number,
   *   currency: string,
   *   receivedAt: string,
   *   method?: string,
   *   reference?: string,
   *   notes?: string,
   *   idempotencyKey: string,
   * }} payload
   * @returns {Promise<object>} OperatorRefundAllocationDto
   */
  recordAndAllocate: (payload) => api.post("/operator-refunds/record-and-allocate", payload),

  /**
   * Tanda P2 "circuito proveedor" (2026-07-22): reembolsos YA REGISTRADOS de un operador
   * (vivos y deshechos), paginado. Hermano de `getPendingBySupplier`: ese dice cuánto FALTA
   * cobrarle al operador, este dice qué ya se anotó como recibido — la solapa lo usa para
   * ofrecer "Deshacer" y "Corregir reserva" sobre una fila puntual.
   *
   * Permiso requerido: tesoreria.supplier_payments (mismo que el bloque "a cobrar").
   *
   * @param {string} supplierPublicId - GUID del proveedor.
   * @param {{ page?: number, pageSize?: number }} paging
   * @returns {Promise<{items: OperatorRefundRegisteredItemDto[], page:number, pageSize:number,
   *   totalCount:number, totalPages:number, hasNextPage:boolean, hasPreviousPage:boolean}>}
   *   Cada item tiene: publicId, refundReceivedPublicId, reservaPublicId, numeroReserva,
   *   clienteNombre, clientePublicId, currency, netAmount, amountsMasked, registeredAt,
   *   isVoided, voidedAt, voidedReason.
   */
  getRegisteredBySupplier: (supplierPublicId, { page = 1, pageSize = 25 } = {}) => {
    const params = new URLSearchParams();
    params.append("page", String(page));
    params.append("pageSize", String(pageSize));
    return api.get(`/suppliers/${supplierPublicId}/operator-refunds/registered?${params.toString()}`);
  },

  /**
   * Deshace (soft-void) una imputación ya registrada — botón "Deshacer" de la solapa
   * "Reembolsos". La fila NUNCA se borra: el motor la deja tachada como rastro auditable.
   * Libera el cap del ingreso físico para poder volver a imputarlo bien.
   *
   * El motivo es OBLIGATORIO (mínimo 20 caracteres, lo exige VoidAllocationRequest).
   * Permiso requerido: caja.edit.
   *
   * DELETE con body: `api.delete` no tiene un parámetro `data` como post/put/patch, así que
   * el body se arma a mano igual que hace `createRequestOptions` para los otros verbos
   * (JSON.stringify) — el cliente HTTP ya sabe agregar el Content-Type correcto al ver que
   * `options.body` no es undefined (ver `shouldSetJsonContentType` en api.js).
   *
   * @param {string} allocationPublicId - GUID de la imputación (allocation) a deshacer.
   * @param {string} reason - Motivo (>= 20 caracteres).
   * @returns {Promise<object>} OperatorRefundAllocationDto (la fila ya con IsVoided=true)
   */
  voidAllocation: (allocationPublicId, reason) =>
    api.delete(`/operator-refunds/allocations/${allocationPublicId}`, {
      body: JSON.stringify({ reason }),
    }),

  /**
   * Mueve una imputación ya registrada de la reserva equivocada a la correcta — botón
   * "Corregir reserva". Atómico en el backend: deshace la vieja y crea la nueva en una
   * sola transacción, así nunca queda la plata "en el aire" a mitad de camino.
   *
   * El motivo es OBLIGATORIO (mínimo 20 caracteres, lo exige ReassociateAllocationRequest).
   * Permiso requerido: caja.edit.
   *
   * @param {string} allocationPublicId - GUID de la imputación (allocation) a mover.
   * @param {{ newBookingCancellationPublicId: string, reason: string }} payload
   * @returns {Promise<object>} OperatorRefundAllocationDto (la fila ya en la reserva nueva)
   */
  reassociateAllocation: (allocationPublicId, { newBookingCancellationPublicId, reason }) =>
    api.patch(`/operator-refunds/allocations/${allocationPublicId}/reassociate`, {
      newBookingCancellationPublicId,
      reason,
    }),
};
