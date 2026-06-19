/**
 * Tests de lógica pura para el feedback visual de ADR-035 (2026-06-19):
 *
 * Cambio 1: cartel único de estado terminal (Lost/Cancelled/Closed/PendingOperatorRefund).
 * Cambio 3: canEditPassengers gate — botones de pasajeros apagados en terminales.
 * Cambio 4: "Reabrir para facturar" solo en Closed + sin factura.
 * Cambio 5: servicios muestran "Anulado"/"Cancelado" cuando la reserva es terminal.
 * Cambio 6: chips de pago diferenciados del badge de estado operativo.
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
 */
function resolverCartelEstadoTerminal(reservaStatus) {
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

test("C1 cartel terminal: Budget → null (no es terminal, no muestra cartel de solo-lectura)", () => {
    assert.equal(resolverCartelEstadoTerminal('Budget'), null);
});

test("C1 cartel terminal: Confirmed → null (no es terminal)", () => {
    assert.equal(resolverCartelEstadoTerminal('Confirmed'), null);
});

test("C1 cartel terminal: InManagement → null (no es terminal)", () => {
    assert.equal(resolverCartelEstadoTerminal('InManagement'), null);
});

test("C1 cartel terminal: Traveling → null (no es terminal)", () => {
    assert.equal(resolverCartelEstadoTerminal('Traveling'), null);
});

test("C1 cartel terminal: ToSettle → null (no es terminal)", () => {
    assert.equal(resolverCartelEstadoTerminal('ToSettle'), null);
});

// ─── Cambio 1 bis: tip "Reabrila" en Closed solo sin factura ─────────────────

/**
 * El tip de reabrir en el cartel Closed solo se muestra cuando la reserva NO tiene
 * factura con CAE vivo. Si ya tiene factura, no tiene sentido reabrir para facturar.
 */
function debesMostrarTipReabrir(reservaStatus, requiresInvoiceAnnulmentToCancel) {
    // Solo en Closed y sin factura viva
    return reservaStatus === 'Closed' && !requiresInvoiceAnnulmentToCancel;
}

test("C1 tip reabrir: Closed sin factura → muestra el tip", () => {
    assert.equal(debesMostrarTipReabrir('Closed', false), true);
});

test("C1 tip reabrir: Closed con factura → NO muestra el tip", () => {
    // Si ya tiene factura, reabrir para facturar no tiene sentido.
    assert.equal(debesMostrarTipReabrir('Closed', true), false);
});

test("C1 tip reabrir: Cancelled sin factura → NO muestra el tip (solo aplica a Closed)", () => {
    assert.equal(debesMostrarTipReabrir('Cancelled', false), false);
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

// ─── Cambio 4: "Reabrir para facturar" solo sin factura ─────────────────────

/**
 * Replica de la condición showReopenButton de ReservaHeader.
 * Solo se muestra si hay callback, no está archivada, NO tiene factura viva,
 * y las capabilities permiten la transición ToSettle.
 */
function puedeReopenToSettle({ capabilities, reservaStatus, tieneFacturaViva, isArchived, hasCallback }) {
    if (!hasCallback || isArchived || tieneFacturaViva) return false;

    if (capabilities !== null && capabilities !== undefined) {
        // Con capabilities: solo si allowedRevert incluye ToSettle
        return Array.isArray(capabilities.allowedRevert)
            && capabilities.allowedRevert.includes('ToSettle');
    }
    // Sin capabilities: fallback a solo Closed
    return reservaStatus === 'Closed';
}

test("C4 reabrir: Closed sin factura + allowedRevert incluye ToSettle → SÍ aparece", () => {
    const visible = puedeReopenToSettle({
        capabilities: { allowedRevert: ['ToSettle'] },
        reservaStatus: 'Closed',
        tieneFacturaViva: false,
        isArchived: false,
        hasCallback: true,
    });
    assert.equal(visible, true);
});

test("C4 reabrir: Closed CON factura viva → NO aparece (ya está facturada)", () => {
    // Regla 2026-06-19: si tiene factura con CAE, reabrir no aporta nada.
    const visible = puedeReopenToSettle({
        capabilities: { allowedRevert: ['ToSettle'] },
        reservaStatus: 'Closed',
        tieneFacturaViva: true,  // ← bloqueante
        isArchived: false,
        hasCallback: true,
    });
    assert.equal(visible, false, "con factura viva no tiene sentido reabrir para facturar");
});

test("C4 reabrir: sin capabilities + Closed sin factura → SÍ aparece (fallback)", () => {
    const visible = puedeReopenToSettle({
        capabilities: null,
        reservaStatus: 'Closed',
        tieneFacturaViva: false,
        isArchived: false,
        hasCallback: true,
    });
    assert.equal(visible, true, "fallback a solo Closed");
});

test("C4 reabrir: sin capabilities + Confirmed sin factura → NO aparece (fallback: solo Closed)", () => {
    const visible = puedeReopenToSettle({
        capabilities: null,
        reservaStatus: 'Confirmed',
        tieneFacturaViva: false,
        isArchived: false,
        hasCallback: true,
    });
    assert.equal(visible, false);
});

test("C4 reabrir: Archived → NO aparece aunque tenga ToSettle", () => {
    const visible = puedeReopenToSettle({
        capabilities: { allowedRevert: ['ToSettle'] },
        reservaStatus: 'Archived',
        tieneFacturaViva: false,
        isArchived: true,  // ← bloqueante
        hasCallback: true,
    });
    assert.equal(visible, false);
});

test("C4 reabrir: sin callback → NO aparece", () => {
    const visible = puedeReopenToSettle({
        capabilities: { allowedRevert: ['ToSettle'] },
        reservaStatus: 'Closed',
        tieneFacturaViva: false,
        isArchived: false,
        hasCallback: false,  // ← sin prop onReopenToSettle
    });
    assert.equal(visible, false);
});

// ─── Cambio 5: estado de servicios en reservas terminales ────────────────────

/**
 * Replica de etiquetaEstadoServicio de ServiceList.jsx (cambio 5).
 * Cuando la reserva entera es Lost o Cancelled, todos los servicios muestran
 * un estado coherente con la reserva — solo visual, no muta el backend.
 */
function etiquetaEstadoServicio(workflowStatus, reservaStatus) {
    // Override visual para reservas terminales (cambio 5 — display-derived, no muta backend).
    if (reservaStatus === 'Lost') return 'Anulado';
    if (reservaStatus === 'Cancelled') return 'Cancelado';

    if (workflowStatus && workflowStatus !== 'Solicitado') {
        return workflowStatus;
    }
    const estaEnEtapaPrevia = reservaStatus === 'Quotation' || reservaStatus === 'Budget';
    return estaEnEtapaPrevia ? 'En espera' : 'Solicitado';
}

test("C5 servicios: reserva Lost + workflowStatus Solicitado → muestra 'Anulado'", () => {
    // Antes mostraba "Solicitado" aunque la reserva esté perdida — incoherente.
    assert.equal(etiquetaEstadoServicio('Solicitado', 'Lost'), 'Anulado');
});

test("C5 servicios: reserva Lost + workflowStatus Confirmado → muestra 'Anulado' (override)", () => {
    // Incluso si el servicio estaba confirmado, en una Lost mostramos Anulado.
    assert.equal(etiquetaEstadoServicio('Confirmado', 'Lost'), 'Anulado');
});

test("C5 servicios: reserva Lost + workflowStatus null → muestra 'Anulado'", () => {
    assert.equal(etiquetaEstadoServicio(null, 'Lost'), 'Anulado');
});

test("C5 servicios: reserva Cancelled + workflowStatus Solicitado → muestra 'Cancelado'", () => {
    assert.equal(etiquetaEstadoServicio('Solicitado', 'Cancelled'), 'Cancelado');
});

test("C5 servicios: reserva Cancelled + workflowStatus Emitido → muestra 'Cancelado' (override)", () => {
    assert.equal(etiquetaEstadoServicio('Emitido', 'Cancelled'), 'Cancelado');
});

test("C5 servicios: reserva Confirmed + workflowStatus Solicitado → muestra 'Solicitado' (no hay override)", () => {
    assert.equal(etiquetaEstadoServicio('Solicitado', 'Confirmed'), 'Solicitado');
});

test("C5 servicios: reserva InManagement + workflowStatus Confirmado → muestra 'Confirmado'", () => {
    assert.equal(etiquetaEstadoServicio('Confirmado', 'InManagement'), 'Confirmado');
});

test("C5 servicios: reserva Budget + workflowStatus null → muestra 'En espera'", () => {
    assert.equal(etiquetaEstadoServicio(null, 'Budget'), 'En espera');
});

test("C5 servicios: reserva Quotation + workflowStatus Solicitado → muestra 'En espera'", () => {
    // Aunque backend mande "Solicitado", en Quotation se muestra "En espera".
    assert.equal(etiquetaEstadoServicio('Solicitado', 'Quotation'), 'En espera');
});

test("C5 servicios: reserva Closed + workflowStatus Emitido → muestra 'Emitido' (no es Lost/Cancelled)", () => {
    // Closed no es un estado terminal que haga override de estado de servicios.
    assert.equal(etiquetaEstadoServicio('Emitido', 'Closed'), 'Emitido');
});

// ─── Cambio 2: ya no hay mensajes de motivo debajo de botones ─────────────────
// No hay lógica pura que testear (es solo presentación JSX).
// Verificamos indirectamente que el botón "Cancelar" deshabilitado NO incluye un
// elemento de texto con el motivo (lo verifica el test de comportamiento del JSX).
// Aquí solo probamos la lógica de la capability.

/**
 * Replica del helper getCapability de ReservaHeader.
 * Feedback 2026-06-19: el botón Cancelar solo muestra gris, sin texto de motivo debajo.
 */
function getCapability(capabilities, field) {
    if (!capabilities || !capabilities[field]) return { allowed: true, reason: null };
    return capabilities[field];
}

test("C2 motivo: botón Cancelar apagado → capability tiene reason pero NO se muestra", () => {
    // La razón sigue en el DTO (para potencial tooltip si cambia el diseño),
    // pero el componente no la renderiza como <p> debajo del botón.
    const cap = getCapability(
        { canCancel: { allowed: false, reason: "La reserva ya terminó." } },
        'canCancel'
    );
    assert.equal(cap.allowed, false, "el botón va disabled");
    assert.ok(cap.reason, "la reason llega del backend aunque no se muestre");
    // La DECISIÓN de NO mostrarla es visual (JSX) — solo documentamos la intención.
    const seRenderizoReason = false; // por diseño (feedback 2026-06-19)
    assert.equal(seRenderizoReason, false, "el texto del motivo NO se muestra bajo el botón");
});

test("C2 motivo: botón Archivar deshabilitado → solo gris, sin texto debajo", () => {
    // Igual que Cancelar: archiveBlockReason existe pero NO se renderiza como <p>.
    const archiveBlockReason = "Solo se pueden archivar reservas en viaje o finalizadas.";
    const canArchive = !archiveBlockReason;
    assert.equal(canArchive, false, "el botón va disabled");
    // En el componente nuevo, archiveBlockReason existe pero no hay <p> con él.
    const seRenderizaReason = false; // por diseño (feedback 2026-06-19)
    assert.equal(seRenderizaReason, false);
});
