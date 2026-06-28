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
   * Bandeja GLOBAL: todos los reembolsos pendientes de todos los operadores.
   * Permiso requerido: tesoreria.supplier_payments.
   *
   * @returns {Promise<OperatorRefundPendingItemDto[]>}
   *   Cada item tiene: bookingCancellationPublicId, reservaPublicId, numeroReserva,
   *   clienteNombre, supplierPublicId, supplierName, semaphore (0-3 integer),
   *   operatorRefundDueBy, daysOverdue, estimatedRefundsByCurrency (array),
   *   amountsMasked.
   */
  getPending: () => api.get("/operator-refunds/pending"),

  /**
   * Reembolsos pendientes de UN proveedor específico (para la ficha del proveedor).
   * Mismo permiso que el endpoint global: tesoreria.supplier_payments.
   *
   * @param {string} supplierPublicId - GUID del proveedor.
   * @returns {Promise<OperatorRefundPendingItemDto[]>}
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
};
