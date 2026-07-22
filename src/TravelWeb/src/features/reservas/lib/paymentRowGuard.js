/**
 * Lógica pura de la Tanda 6 ("contrato pantalla-motor",
 * spec docs/ux/2026-07-20-t5-a-t9-contrato-pantalla-motor.md):
 * decide si Editar/Eliminar de UN cobro puntual se apagan mirando el candado
 * del PAGO (recibo emitido, recibo anulado, factura con CAE vivo), no solo
 * el estado general de la reserva.
 *
 * El motor ahora manda `payment.canEdit` y `payment.canDelete` por cada cobro
 * de `reserva.payments[]` — mismo shape `CapabilityDto` que ya usa
 * `reserva.capabilities.canInvoiceSale`: `{ allowed, reason }`. Cuando
 * `allowed` es true, `reason` viene null.
 *
 * Vive en un .js sin JSX (mismo patrón que receiptApprovalFlow.js) para
 * poder testearlo con Node puro, sin bundler.
 */

/**
 * @param {object} payment - un elemento de reserva.payments[]. Puede venir de
 *   un DTO viejo sin `canEdit`/`canDelete` (degradación elegante).
 * @returns {{ editarBloqueado: boolean, eliminarBloqueado: boolean, motivo: string|null }}
 */
export function resolverBloqueoFilaCobro(payment) {
  const canEdit = payment?.canEdit;
  const canDelete = payment?.canDelete;

  // Degradación elegante: si el backend todavía no manda estos campos (DTO viejo),
  // no se agrega ningún bloqueo nuevo — solo queda el gating por estado de la reserva.
  // (P4 2026-07-21: el candado local por recibo anulado ya no existe; el motor decide
  // botón por botón con canEdit/canDelete.)
  const editarBloqueado = canEdit ? canEdit.allowed === false : false;
  const eliminarBloqueado = canDelete ? canDelete.allowed === false : false;

  // Un solo renglón de motivo por cobro (regla de la spec T6): si Editar está
  // bloqueado se muestra SU motivo, porque el backend ya lo evalúa en el orden
  // más específico primero (recibo emitido > recibo anulado > factura con CAE,
  // MutationGuards.cs). Si solo Eliminar está bloqueado (sin bloquear Editar),
  // se muestra el motivo de Eliminar.
  let motivo = null;
  if (editarBloqueado) {
    motivo = canEdit.reason ?? null;
  } else if (eliminarBloqueado) {
    motivo = canDelete.reason ?? null;
  }

  return { editarBloqueado, eliminarBloqueado, motivo };
}
