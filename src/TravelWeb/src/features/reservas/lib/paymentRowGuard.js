/**
 * Lógica pura de la Tanda 6 ("contrato pantalla-motor",
 * spec docs/ux/2026-07-20-t5-a-t9-contrato-pantalla-motor.md):
 * decide si Editar/Deshacer de UN cobro puntual se apagan mirando el candado
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
 * @param {{ editarVisible?: boolean }} [opciones] - editarVisible=false cuando el
 *   botón "Editar" NO se renderiza en esta fila (BUG 2, 2026-08-05: en los 4 estados
 *   terminales, Editar se oculta a nivel reserva — ver `puedeEditar` en
 *   ReservaDetailPage.jsx). Default true: mismo comportamiento de siempre para los
 *   call sites que no la pasan.
 * @returns {{ editarBloqueado: boolean, eliminarBloqueado: boolean, motivo: string|null }}
 */
export function resolverBloqueoFilaCobro(payment, { editarVisible = true } = {}) {
  const canEdit = payment?.canEdit;
  const canDelete = payment?.canDelete;

  // Degradación elegante: si el backend todavía no manda estos campos (DTO viejo),
  // no se agrega ningún bloqueo nuevo — solo queda el gating por estado de la reserva.
  // (P4 2026-07-21: el candado local por recibo anulado ya no existe; el motor decide
  // botón por botón con canEdit/canDelete.)
  const editarBloqueado = canEdit ? canEdit.allowed === false : false;
  const eliminarBloqueado = canDelete ? canDelete.allowed === false : false;

  // FIX BLOQUEANTE (P-9/P-11, review 2026-08-05): un solo renglón de motivo por
  // cobro, y tiene que hablar del botón que el usuario REALMENTE ve. Antes esto
  // priorizaba SIEMPRE el motivo de Editar cuando ambos estaban bloqueados — correcto
  // mientras los dos botones se muestran juntos, pero mentiroso en terminal: ahí
  // Editar está OCULTO (editarVisible=false) y "Deshacer" quedaba con un renglón que
  // hablaba de "editar el pago... registrá un nuevo pago", una acción que ni siquiera
  // está en pantalla.
  //
  // Regla nueva: el motivo de Editar solo compite si Editar SE MUESTRA. Si Editar
  // está oculto, el único candidato es el de Deshacer — y si Deshacer tampoco está
  // bloqueado, no hay nada que explicar: el renglón directamente no se pinta (P-9/P-11
  // también prohíbe un candado 🔒 huérfano al lado de un botón habilitado).
  let motivo = null;
  if (editarVisible && editarBloqueado) {
    motivo = canEdit.reason ?? null;
  } else if (eliminarBloqueado) {
    motivo = canDelete.reason ?? null;
  }

  return { editarBloqueado, eliminarBloqueado, motivo };
}
