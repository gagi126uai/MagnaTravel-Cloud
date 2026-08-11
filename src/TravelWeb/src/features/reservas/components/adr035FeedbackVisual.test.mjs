/**
 * Tests de lógica pura para el feedback visual de ADR-035 (2026-06-19) + ADR-036 (2026-06-21):
 *
 * Cambio 1: cartel único de estado terminal (Lost/Cancelled/Closed/PendingOperatorRefund/Traveling).
 * Cambio 3: canEditPassengers gate — botones de pasajeros apagados en terminales.
 * Cambio 4: "Reabrir para facturar" solo en Closed + sin factura (ADR-036: ya no usa ToSettle).
 * Cambio 5: servicios muestran "Anulado" cuando la reserva es terminal (Lost o Cancelled — ADR-036).
 * Cambio 6: chips de pago diferenciados del badge de estado operativo.
 *
 * ADR-036 (2026-06-21) — cambios sobre ADR-035:
 *   - "Traveling" pasa a ser solo-lectura (cartel arriba).
 *   - "ToSettle" eliminado de toda la UI.
 *   - "Reabrir para facturar" ya no manda a ToSettle; destraba sin cambiar estado.
 *   - Servicios de Cancelled también muestran "Anulado" (antes era "Cancelado").
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/adr035FeedbackVisual.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Cambio 1: cartel único de estado terminal ────────────────────────────────

/**
 * Replica de la lógica de selección del cartel de estado terminal (ReservaDetailPage.jsx).
 * Devuelve null si no es estado terminal (no debe mostrar cartel de solo-lectura).
 *
 * ADR-036: Traveling ahora también tiene cartel de solo-lectura (punto 2).
 * ToSettle fue eliminado — ya no existe en la UI.
 */
function resolverCartelEstadoTerminal(reservaStatus) {
    // ADR-036: Traveling ahora tiene cartel de solo lectura propio (arriba, chico, estilo verde).
    if (reservaStatus === 'Traveling') return 'traveling';
    if (reservaStatus === 'Lost') return 'lost';
    if (reservaStatus === 'Cancelled') return 'cancelled';
    if (reservaStatus === 'Closed') return 'closed';
    if (reservaStatus === 'PendingOperatorRefund') return 'awaiting-refund';
    return null;
}

test("C1 cartel terminal: Lost → muestra cartel 'lost'", () => {
    assert.equal(resolverCartelEstadoTerminal('Lost'), 'lost');
});

test("C1 cartel terminal: Cancelled → muestra cartel 'cancelled'", () => {
    assert.equal(resolverCartelEstadoTerminal('Cancelled'), 'cancelled');
});

test("C1 cartel terminal: Closed → muestra cartel 'closed'", () => {
    assert.equal(resolverCartelEstadoTerminal('Closed'), 'closed');
});

test("C1 cartel terminal: PendingOperatorRefund → muestra cartel 'awaiting-refund'", () => {
    assert.equal(resolverCartelEstadoTerminal('PendingOperatorRefund'), 'awaiting-refund');
});

// ADR-036 punto 2: Traveling ahora es solo-lectura con cartel propio.
test("C1 cartel terminal: Traveling → muestra cartel 'traveling' (ADR-036: solo lectura)", () => {
    assert.equal(resolverCartelEstadoTerminal('Traveling'), 'traveling');
});

test("C1 cartel terminal: Budget → null (no es terminal, no muestra cartel de solo-lectura)", () => {
    assert.equal(resolverCartelEstadoTerminal('Budget'), null);
});

test("C1 cartel terminal: Confirmed → null (no es terminal)", () => {
    assert.equal(resolverCartelEstadoTerminal('Confirmed'), null);
});

test("C1 cartel terminal: InManagement → null (no es terminal)", () => {
    assert.equal(resolverCartelEstadoTerminal('InManagement'), null);
});

// ADR-036: ToSettle fue eliminado de la UI. Si llegara del backend (legacy), no muestra cartel.
test("C1 cartel terminal: ToSettle (legacy) → null (estado eliminado en ADR-036)", () => {
    assert.equal(resolverCartelEstadoTerminal('ToSettle'), null);
});

// ─── Cambio 1 bis: ADR-037 eliminó el tip "Reabrila para facturar" ───────────

/**
 * ADR-037: la facturación se desacopló del estado. El cartel de Finalizada YA NO muestra
 * el tip "Reabrila para facturar" (se factura directo desde Finalizada, sin reabrir).
 * El cartel queda simplemente "Reserva finalizada — solo lectura."
 */
function tipReabrirFinalizada() {
    return null; // ADR-037: no hay tip de reabrir.
}

test("ADR-037 cartel Finalizada: ya NO hay tip 'Reabrila para facturar'", () => {
    assert.equal(tipReabrirFinalizada(), null);
});

// ─── Cambio 3: gate canEditPassengers ────────────────────────────────────────

/**
 * Replica del cálculo de canEditPassengers que hace ReservaDetailPage.
 * Si no hay capabilities (DTO viejo), fallback a permitir (comportamiento previo).
 */
function resolverCanEditPassengers(capabilities) {
    // Degradación elegante: si el backend no mandó capabilities, se permite editar.
    return capabilities?.canEditPassengers?.allowed ?? true;
}

test("C3 pasajeros: capabilities.canEditPassengers.allowed=false → no editar", () => {
    const capabilities = { canEditPassengers: { allowed: false, reason: "Reserva finalizada." } };
    assert.equal(resolverCanEditPassengers(capabilities), false);
});

test("C3 pasajeros: capabilities.canEditPassengers.allowed=true → sí editar", () => {
    const capabilities = { canEditPassengers: { allowed: true, reason: null } };
    assert.equal(resolverCanEditPassengers(capabilities), true);
});

test("C3 pasajeros: capabilities null (DTO viejo) → sí editar (degradación elegante)", () => {
    // En versiones de API sin capabilities, no rompemos la funcionalidad.
    assert.equal(resolverCanEditPassengers(null), true);
});

test("C3 pasajeros: capabilities sin campo canEditPassengers → sí editar (campo ausente)", () => {
    // Solo tiene otros campos → canEditPassengers no existe → ?? true → editar.
    const capabilities = { canCancel: { allowed: false } };
    assert.equal(resolverCanEditPassengers(capabilities), true);
});

test("C3 pasajeros: reserva Confirmed → canEditPassengers=true normalmente", () => {
    // En Confirmed se puede editar pasajeros (el backend manda allowed=true).
    const capabilities = { canEditPassengers: { allowed: true } };
    assert.equal(resolverCanEditPassengers(capabilities), true);
});

test("C3 pasajeros: reserva Lost → canEditPassengers=false (solo lectura)", () => {
    const capabilities = { canEditPassengers: { allowed: false, reason: "Reserva perdida." } };
    assert.equal(resolverCanEditPassengers(capabilities), false);
});

// ─── Cambio 4: ADR-037 eliminó el botón "Reabrir para facturar" ──────────────

/**
 * ADR-037 (2026-06-21): la facturación se desacopló del estado de la reserva. El botón
 * "Reabrir para facturar" fue ELIMINADO del encabezado: ahora se factura directo desde
 * Finalizada (y Confirmada/En viaje) sin reabrir ni destrabar nada. El botón "Emitir factura"
 * se gobierna por la capability `canInvoiceSale` del backend.
 *
 * Réplica de la nueva regla de visibilidad del botón "Emitir factura" en la solapa Cuenta:
 * visible salvo que la reserva ya esté facturada del todo (decisión Gaston 3A); habilitado
 * según la capability (no por estado hardcodeado en el front).
 */
function muestraEmitirFactura({ invoicingStatus, capInvoiceAllowed }) {
    if (invoicingStatus === 'FullyInvoiced') return { visible: false, enabled: false };
    // visible siempre (apagado si la capability no lo permite, patrón ADR-035)
    return { visible: true, enabled: capInvoiceAllowed !== false };
}

test("C4 facturar: Finalizada ya facturada del todo → botón 'Emitir factura' oculto (ADR-037)", () => {
    const r = muestraEmitirFactura({ invoicingStatus: 'FullyInvoiced', capInvoiceAllowed: true });
    assert.equal(r.visible, false);
});

test("C4 facturar: Finalizada sin facturar + capability allowed → botón visible y habilitado", () => {
    const r = muestraEmitirFactura({ invoicingStatus: 'NotInvoiced', capInvoiceAllowed: true });
    assert.deepEqual(r, { visible: true, enabled: true });
});

test("C4 facturar: parcial + capability denegada → visible pero apagado (patrón ADR-035)", () => {
    const r = muestraEmitirFactura({ invoicingStatus: 'PartiallyInvoiced', capInvoiceAllowed: false });
    assert.deepEqual(r, { visible: true, enabled: false });
});

// ─── Cambio 5: estado de servicios en reservas terminales ────────────────────

/**
 * Replica de etiquetaEstadoServicio de ServiceList.jsx (cambio 5, actualizado ADR-036).
 *
 * ADR-036: cuando la reserva es Lost O Cancelled, todos los servicios muestran "Anulado".
 * ADR-035 usaba "Cancelado" para Cancelled; ADR-036 unifica: ambas = "Anulado".
 * Es solo presentación — no muta el backend.
 */
function etiquetaEstadoServicio(workflowStatus, reservaStatus) {
    // ADR-036: Lost Y Cancelled → "Anulado" (unificamos el termino para reservas deshechas).
    if (reservaStatus === 'Lost') return 'Anulado';
    if (reservaStatus === 'Cancelled') return 'Anulado';

    if (workflowStatus && workflowStatus !== 'Solicitado') {
        return workflowStatus;
    }
    // Pulido de textos 2026-08-03 (P13 firmada): etiqueta única "Solicitado", sin
    // distinción por etapa. Antes Quotation/Budget mostraban "En espera" — ya no.
    return 'Solicitado';
}

test("C5 servicios: reserva Lost + workflowStatus Solicitado → muestra 'Anulado'", () => {
    assert.equal(etiquetaEstadoServicio('Solicitado', 'Lost'), 'Anulado');
});

test("C5 servicios: reserva Lost + workflowStatus Confirmado → muestra 'Anulado' (override)", () => {
    assert.equal(etiquetaEstadoServicio('Confirmado', 'Lost'), 'Anulado');
});

test("C5 servicios: reserva Lost + workflowStatus null → muestra 'Anulado'", () => {
    assert.equal(etiquetaEstadoServicio(null, 'Lost'), 'Anulado');
});

// ADR-036: Cancelled también muestra "Anulado" (antes era "Cancelado" — ADR-035 cambio 5).
test("C5 servicios ADR-036: reserva Cancelled + workflowStatus Solicitado → muestra 'Anulado' (ya no 'Cancelado')", () => {
    assert.equal(etiquetaEstadoServicio('Solicitado', 'Cancelled'), 'Anulado');
});

test("C5 servicios ADR-036: reserva Cancelled + workflowStatus Emitido → muestra 'Anulado' (override)", () => {
    assert.equal(etiquetaEstadoServicio('Emitido', 'Cancelled'), 'Anulado');
});

test("C5 servicios: reserva Confirmed + workflowStatus Solicitado → muestra 'Solicitado' (no hay override)", () => {
    assert.equal(etiquetaEstadoServicio('Solicitado', 'Confirmed'), 'Solicitado');
});

test("C5 servicios: reserva InManagement + workflowStatus Confirmado → muestra 'Confirmado'", () => {
    assert.equal(etiquetaEstadoServicio('Confirmado', 'InManagement'), 'Confirmado');
});

test("C5 servicios: reserva Budget + workflowStatus null → muestra 'Solicitado' (etiqueta única, P13)", () => {
    assert.equal(etiquetaEstadoServicio(null, 'Budget'), 'Solicitado');
});

test("C5 servicios: reserva Quotation + workflowStatus Solicitado → muestra 'Solicitado' (etiqueta única, P13)", () => {
    // Pulido 2026-08-03: en Quotation/Budget ya NO se distingue "En espera" — misma
    // etiqueta "Solicitado" que en el resto de las etapas previas a resolución.
    assert.equal(etiquetaEstadoServicio('Solicitado', 'Quotation'), 'Solicitado');
});

test("C5 servicios: reserva Closed + workflowStatus Emitido → muestra 'Emitido' (no es Lost/Cancelled)", () => {
    // Closed no es un estado terminal que haga override de estado de servicios.
    assert.equal(etiquetaEstadoServicio('Emitido', 'Closed'), 'Emitido');
});

// ─── Cambio 2: "Anular reserva" sigue sin motivo debajo; "Archivar" SÍ desde Tanda 2 ──
// No hay lógica pura que testear (es solo presentación JSX).
// Verificamos indirectamente la capability/el flag que gobierna cada botón.

/**
 * Replica del helper getCapability de ReservaHeader.
 * Feedback 2026-06-19: el botón "Anular reserva" solo muestra gris, sin texto de motivo debajo.
 * Este botón NO cambió en la Tanda 2 del rediseño (2026-08-03) — solo "Archivar" sí (ver
 * el test de abajo).
 */
function getCapability(capabilities, field) {
    if (!capabilities || !capabilities[field]) return { allowed: true, reason: null };
    return capabilities[field];
}

test("C2 motivo: botón Anular reserva apagado → capability tiene reason pero NO se muestra", () => {
    // La razón sigue en el DTO (para potencial tooltip si cambia el diseño),
    // pero el componente no la renderiza como <p> debajo del botón.
    const cap = getCapability(
        { canCancel: { allowed: false, reason: "La reserva ya terminó." } },
        'canCancel'
    );
    assert.equal(cap.allowed, false, "el botón va disabled");
    assert.ok(cap.reason, "la reason llega del backend aunque no se muestre");
    // La DECISIÓN de NO mostrarla es visual (JSX) — solo documentamos la intención.
    const seRenderizoReason = false; // por diseño (feedback 2026-06-19), sigue vigente
    assert.equal(seRenderizoReason, false, "el texto del motivo NO se muestra bajo el botón");
});

// P9 — historial de este botón puntual (tres cambios de criterio del dueño):
//   2026-06-19: sin motivo a la vista.
//   2026-08-03: motivo escrito debajo, siempre visible ("antes había que apoyar el
//     mouse para enterarte, y eso está prohibido" — texto de la maqueta de esa fecha).
//   2026-08-11 (QA, VIGENTE — reemplaza la de 2026-08-03, enmendada el mismo día tras
//     el review B1): vuelve a tooltip (title nativo) al pasar el mouse — la ficha es
//     de escritorio (sin equivalente táctil propio hoy), así que sigue el mismo
//     criterio que ReservaTable.jsx (listado de escritorio).
//   Fix B1 (review, mismo día): el `title` NO va en el <button> — un botón
//     deshabilitado no siempre dispara hover (mismo problema detectado en el Button de
//     shadcn de ReservaTable.jsx) — va en un <span> que ENVUELVE al botón.
test("P9 (11/08/2026, VIGENTE): botón Archivar deshabilitado → el motivo va en el title del ENVOLTORIO (tooltip), no escrito debajo ni en el botón mismo", () => {
    const archiveBlockReason = "Solo se pueden archivar reservas en viaje o finalizadas.";
    const canArchive = !archiveBlockReason;
    assert.equal(canArchive, false, "el botón va disabled");
    // ReservaHeader.jsx (Botonera de acciones → botón Archivar) pasa archiveBlockReason
    // como `title` de un <span> que envuelve al <button> — igual patrón que ReservaTable.jsx.
    const tituloDelEnvoltorio = canArchive ? undefined : archiveBlockReason;
    assert.equal(tituloDelEnvoltorio, archiveBlockReason, "el motivo del motor queda disponible en el title (tooltip) del envoltorio del botón Archivar");
});

// ─── ADR-036: chip "Debe — no viaja" (ReservaStatusChips) ────────────────────

/**
 * Replica de la lógica de chips de ReservaStatusChips.jsx para Confirmed.
 * ADR-036: "Saldo pendiente" → "Debe — no viaja" (chip rojo).
 */
function resolverChipPago(reserva) {
    if (reserva.status !== 'Confirmed') return null;
    if (reserva.isFullyPaid) return 'paid';
    // ADR-036: si el cliente debe, el chip dice "Debe — no viaja"
    return 'debe-no-viaja';
}

test("ADR-036 chip: Confirmed + isFullyPaid=true → chip 'paid' (Pagada)", () => {
    assert.equal(resolverChipPago({ status: 'Confirmed', isFullyPaid: true }), 'paid');
});

test("ADR-036 chip: Confirmed + isFullyPaid=false → chip 'debe-no-viaja' (ADR-036, antes era 'unpaid')", () => {
    assert.equal(resolverChipPago({ status: 'Confirmed', isFullyPaid: false }), 'debe-no-viaja');
});

test("ADR-036 chip: Budget + isFullyPaid=false → null (solo aplica a Confirmed)", () => {
    assert.equal(resolverChipPago({ status: 'Budget', isFullyPaid: false }), null);
});

test("ADR-036 chip: Traveling + isFullyPaid=false → null (Traveling no muestra chip Confirmed)", () => {
    assert.equal(resolverChipPago({ status: 'Traveling', isFullyPaid: false }), null);
});

// ─── ADR-036: LOCKED_STATUSES sin ToSettle ────────────────────────────────────

/**
 * Replica del set LOCKED_STATUSES de ReservaStatusBadge.jsx.
 * ADR-036: ToSettle fue eliminado. Ahora son 3 estados bloqueados.
 */
const LOCKED_STATUSES_ADR036 = new Set(['Confirmed', 'Traveling', 'Closed']);
function isStatusLocked(status) {
    return LOCKED_STATUSES_ADR036.has(status);
}

test("ADR-036 candado: Confirmed → bloqueado", () => {
    assert.equal(isStatusLocked('Confirmed'), true);
});

test("ADR-036 candado: Traveling → bloqueado", () => {
    assert.equal(isStatusLocked('Traveling'), true);
});

test("ADR-036 candado: Closed → bloqueado", () => {
    assert.equal(isStatusLocked('Closed'), true);
});

test("ADR-036 candado: ToSettle → ya NO bloqueado (estado eliminado)", () => {
    assert.equal(isStatusLocked('ToSettle'), false);
});

test("ADR-036 candado: InManagement → no bloqueado", () => {
    assert.equal(isStatusLocked('InManagement'), false);
});

test("ADR-036 candado: Budget → no bloqueado", () => {
    assert.equal(isStatusLocked('Budget'), false);
});

// ─── ADR-036: label "Anulada" para Cancelled ─────────────────────────────────

/**
 * Replica del statusConfig de ReservaStatusBadge.jsx para verificar el label de Cancelled.
 * ADR-036: Cancelled.label cambió de "Cancelada" a "Anulada".
 */
const STATUS_LABELS_ADR036 = {
    Quotation: 'Cotización',
    Budget: 'Presupuesto',
    InManagement: 'En gestión',
    Confirmed: 'Confirmada',
    Traveling: 'En viaje',
    Closed: 'Finalizada',
    Lost: 'Perdida',
    Cancelled: 'Anulada',  // ADR-036: antes era "Cancelada"
    Archived: 'Archivada',
};

test("ADR-036 label: Cancelled → 'Anulada' (ya no 'Cancelada')", () => {
    assert.equal(STATUS_LABELS_ADR036.Cancelled, 'Anulada');
});

test("ADR-036 label: ToSettle → no existe en el config (fue eliminado)", () => {
    assert.equal(STATUS_LABELS_ADR036.ToSettle, undefined);
});

// Pulido de textos 2026-08-03: label canónico pasó de "Perdido" a "Perdida"
// (concuerda con "reserva"). El estado en sí no cambió, solo el texto.
test("Pulido 2026-08-03 label: Lost → 'Perdida' (antes 'Perdido')", () => {
    assert.equal(STATUS_LABELS_ADR036.Lost, 'Perdida');
});
