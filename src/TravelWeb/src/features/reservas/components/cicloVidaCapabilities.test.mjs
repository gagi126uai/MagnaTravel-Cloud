/**
 * Tests de lógica pura para el lote de capabilities de ciclo de vida (2026-06-24).
 *
 * Cubre cuatro reglas nuevas:
 *   G1 — Estado "Finalizado" en servicios: etiqueta + color verde pálido (no cancelado, no tachado).
 *   G3 — canCancelServices: "Cancelar" visible solo cuando allowed=true (InManagement/Confirmada).
 *        En pre-venta → la UI debe mostrar "Borrar" en lugar de "Cancelar".
 *   G5 — canReschedule: "Reprogramar viaje" visible solo cuando allowed=true.
 *        Fallback a canEditServices si el backend no mandó canReschedule (DTO viejo).
 *   B3 — canUploadDocument: zona de carga y Renombrar/Eliminar ocultos cuando allowed=false.
 *   G6 — canEmitVoucher: botón "Emitir" oculto en Finalizada (allowed=false).
 *        soloLectura sigue bloqueando todo en estados congelados.
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/cicloVidaCapabilities.test.mjs
 *
 * NOTA: estos tests replican la lógica pura extraída de los componentes.
 * No renderizan JSX — son tests de lógica de negocio, igual que los demás en este directorio.
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica de etiquetaEstadoServicio (ServiceList.jsx) ──────────────────────

function etiquetaEstadoServicio(workflowStatus, reservaStatus) {
    if (reservaStatus === 'Lost') return 'Anulado';
    if (reservaStatus === 'Cancelled') return 'Anulado';
    if (workflowStatus === 'Finalizado') return 'Finalizado';
    if (workflowStatus && workflowStatus !== 'Solicitado') return workflowStatus;
    const estaEnEtapaPrevia = reservaStatus === 'Quotation' || reservaStatus === 'Budget';
    return estaEnEtapaPrevia ? 'En espera' : 'Solicitado';
}

// ─── Réplica de claseColorEstadoServicio (ServiceList.jsx) ────────────────────

function claseColorEstadoServicio(workflowStatus, reservaStatus) {
    const etiqueta = etiquetaEstadoServicio(workflowStatus, reservaStatus);
    if (etiqueta === 'Confirmado') return 'bg-green-100 text-green-700';
    if (etiqueta === 'Cancelado') return 'bg-rose-100 text-rose-700';
    if (etiqueta === 'Anulado') return 'bg-slate-100 text-slate-500';
    if (etiqueta === 'En espera') return 'bg-slate-100 text-slate-600';
    if (etiqueta === 'Finalizado') return 'bg-emerald-50 text-emerald-600';
    return 'bg-amber-100 text-amber-700';
}

// ─── Réplica lógica puedeCancelarServicios (ServiceList.jsx) ──────────────────
// La lógica real combina capabilities.canCancelServices + fallback a canCancel.

function calcularPuedeCancelarServicios(capabilities) {
    if (!capabilities) return true; // degradación sin capabilities
    return capabilities.canCancelServices?.allowed ?? capabilities.canCancel?.allowed ?? true;
}

// ─── Réplica lógica showRescheduleButton (ReservaHeader.jsx) ─────────────────

function calcularShowReschedule(capabilities, isArchived, hasOnReschedule) {
    if (!hasOnReschedule) return false;
    if (isArchived) return false;
    // G5: usa canReschedule si viene; fallback a canEditServices (DTO viejo).
    const rescheduleCap = capabilities?.canReschedule ?? capabilities?.canEditServices;
    if (!rescheduleCap) return true; // sin capabilities degrada a mostrar
    return rescheduleCap.allowed;
}

// ─── Réplica lógica soloLecturaDocumentos (ReservaDocumentsTab.jsx) ──────────

function calcularSoloLecturaDocumentos(canUploadDocument) {
    return canUploadDocument != null && !canUploadDocument.allowed;
}

// ─── Réplica lógica puedeEmitirVoucher (ReservaVoucherTab.jsx) ────────────────

function calcularPuedeEmitirVoucher(soloLectura, canEmitVoucher) {
    return !soloLectura && (canEmitVoucher == null || canEmitVoucher.allowed);
}

// =============================================================================
// G1 — Estado "Finalizado" en servicios
// =============================================================================

test("G1: workflowStatus=Finalizado → etiqueta 'Finalizado'", () => {
    assert.equal(etiquetaEstadoServicio('Finalizado', 'Closed'), 'Finalizado');
});

test("G1: workflowStatus=Finalizado en reserva InManagement → 'Finalizado' (prioridad sobre estado de reserva)", () => {
    // Si el backend manda Finalizado, lo mostramos sin importar el estado de la reserva
    assert.equal(etiquetaEstadoServicio('Finalizado', 'InManagement'), 'Finalizado');
});

test("G1: Finalizado → clase de color emerald (no rose/tachado/slate)", () => {
    const clase = claseColorEstadoServicio('Finalizado', 'Closed');
    assert.ok(clase.includes('emerald'), `Se esperaba clase emerald, se obtuvo: "${clase}"`);
    assert.ok(!clase.includes('rose'), 'No debe usar rose (cancelado)');
    assert.ok(!clase.includes('slate-500'), 'No debe usar slate-500 (anulado)');
});

test("G1: Lost todavía overridea a Anulado aunque el servicio tenga workflowStatus=Finalizado", () => {
    // La reserva Lost vence sobre el workflowStatus individual del servicio
    assert.equal(etiquetaEstadoServicio('Finalizado', 'Lost'), 'Anulado');
});

test("G1: Cancelled todavía overridea a Anulado", () => {
    assert.equal(etiquetaEstadoServicio('Finalizado', 'Cancelled'), 'Anulado');
});

test("G1: Confirmado NO cambia por estado Closed de reserva (solo Lost/Cancelled hacen override)", () => {
    // En Closed la reserva terminó bien; el estado de los servicios viene del backend
    assert.equal(etiquetaEstadoServicio('Confirmado', 'Closed'), 'Confirmado');
});

test("G1: color Finalizado es diferente del color Confirmado (verde intenso vs emerald pálido)", () => {
    const colorFinalizado = claseColorEstadoServicio('Finalizado', 'Closed');
    const colorConfirmado = claseColorEstadoServicio('Confirmado', 'Closed');
    assert.notEqual(colorFinalizado, colorConfirmado);
    assert.ok(colorFinalizado.includes('emerald'));
    assert.ok(colorConfirmado.includes('green-100'));
});

// =============================================================================
// G3 — canCancelServices: "Cancelar" vs "Borrar" en pre-venta
// =============================================================================

test("G3: canCancelServices.allowed=true → puede cancelar servicios", () => {
    const cap = { canCancelServices: { allowed: true } };
    assert.equal(calcularPuedeCancelarServicios(cap), true);
});

test("G3: canCancelServices.allowed=false → no puede cancelar (pre-venta o terminal)", () => {
    const cap = { canCancelServices: { allowed: false, reason: "La reserva está en Presupuesto" } };
    assert.equal(calcularPuedeCancelarServicios(cap), false);
});

test("G3: sin canCancelServices → fallback a canCancel (DTO viejo)", () => {
    const cap = { canCancel: { allowed: true } };
    assert.equal(calcularPuedeCancelarServicios(cap), true);
});

test("G3: sin canCancelServices + canCancel.allowed=false → no puede cancelar", () => {
    const cap = { canCancel: { allowed: false } };
    assert.equal(calcularPuedeCancelarServicios(cap), false);
});

test("G3: sin capabilities → degradación elegante (muestra botones, allowed=true)", () => {
    assert.equal(calcularPuedeCancelarServicios(null), true);
    assert.equal(calcularPuedeCancelarServicios(undefined), true);
});

test("G3: canCancelServices toma prioridad sobre canCancel cuando ambos están presentes", () => {
    // canCancelServices=false, canCancel=true → canCancelServices gana
    const cap = { canCancelServices: { allowed: false }, canCancel: { allowed: true } };
    assert.equal(calcularPuedeCancelarServicios(cap), false);
});

// =============================================================================
// G5 — canReschedule: visibilidad del botón "Reprogramar viaje"
// =============================================================================

test("G5: canReschedule.allowed=true → botón visible", () => {
    const cap = { canReschedule: { allowed: true } };
    assert.equal(calcularShowReschedule(cap, false, true), true);
});

test("G5: canReschedule.allowed=false → botón oculto", () => {
    const cap = { canReschedule: { allowed: false, reason: "La reserva está en Presupuesto" } };
    assert.equal(calcularShowReschedule(cap, false, true), false);
});

test("G5: sin canReschedule → fallback a canEditServices (DTO viejo)", () => {
    const cap = { canEditServices: { allowed: true } };
    assert.equal(calcularShowReschedule(cap, false, true), true);
});

test("G5: sin canReschedule + canEditServices.allowed=false → oculto", () => {
    const cap = { canEditServices: { allowed: false } };
    assert.equal(calcularShowReschedule(cap, false, true), false);
});

test("G5: isArchived=true → siempre oculto (reserva archivada es historial)", () => {
    const cap = { canReschedule: { allowed: true } };
    assert.equal(calcularShowReschedule(cap, true, true), false);
});

test("G5: sin callback onReschedule → siempre oculto (no hay handler)", () => {
    const cap = { canReschedule: { allowed: true } };
    assert.equal(calcularShowReschedule(cap, false, false), false);
});

test("G5: sin capabilities → degrada a mostrar (true)", () => {
    assert.equal(calcularShowReschedule(null, false, true), true);
});

// =============================================================================
// B3 — canUploadDocument: zona de carga y botones Renombrar/Eliminar
// =============================================================================

test("B3: canUploadDocument.allowed=true → NO es solo lectura (se puede subir)", () => {
    assert.equal(calcularSoloLecturaDocumentos({ allowed: true }), false);
});

test("B3: canUploadDocument.allowed=false → solo lectura (ocultar zona de carga)", () => {
    assert.equal(calcularSoloLecturaDocumentos({ allowed: false }), true);
});

test("B3: canUploadDocument=null → no es solo lectura (degradación elegante, DTO viejo)", () => {
    assert.equal(calcularSoloLecturaDocumentos(null), false);
});

test("B3: canUploadDocument=undefined → no es solo lectura (degradación elegante)", () => {
    assert.equal(calcularSoloLecturaDocumentos(undefined), false);
});

// =============================================================================
// G6 — canEmitVoucher: botón "Emitir" en la solapa de vouchers
// =============================================================================

test("G6: soloLectura=false + canEmitVoucher.allowed=true → puede emitir", () => {
    assert.equal(calcularPuedeEmitirVoucher(false, { allowed: true }), true);
});

test("G6: soloLectura=false + canEmitVoucher.allowed=false → NO puede emitir (ej. Finalizada)", () => {
    assert.equal(calcularPuedeEmitirVoucher(false, { allowed: false }), false);
});

test("G6: soloLectura=true + canEmitVoucher.allowed=true → NO puede emitir (estado congelado)", () => {
    // soloLectura=true bloquea todo, incluso si la capability lo permitiría
    assert.equal(calcularPuedeEmitirVoucher(true, { allowed: true }), false);
});

test("G6: soloLectura=true + canEmitVoucher.allowed=false → NO puede emitir (doble bloqueo)", () => {
    assert.equal(calcularPuedeEmitirVoucher(true, { allowed: false }), false);
});

test("G6: canEmitVoucher=null → comportamiento anterior (gobernado solo por soloLectura)", () => {
    // Sin capability: si no está congelado, puede emitir
    assert.equal(calcularPuedeEmitirVoucher(false, null), true);
    assert.equal(calcularPuedeEmitirVoucher(true, null), false);
});

test("G6: canEmitVoucher=undefined → mismo comportamiento que null (DTO viejo)", () => {
    assert.equal(calcularPuedeEmitirVoucher(false, undefined), true);
    assert.equal(calcularPuedeEmitirVoucher(true, undefined), false);
});

// ─── Combinaciones de borde: Finalizada (Closed) ─────────────────────────────

test("Closed: canEmitVoucher=false + soloLectura=false → solo bloquea Emitir, no todo", () => {
    // En Closed el backend manda canEmitVoucher.allowed=false pero soloLectura sigue siendo false.
    // El resultado: no emite, pero Añadir/Aprobar/Rechazar/Anular pueden seguir según soloLectura.
    assert.equal(calcularPuedeEmitirVoucher(false, { allowed: false }), false);
});

test("Traveling: soloLectura=true + canEmitVoucher=null → bloquea todo", () => {
    // Estado congelado: soloLectura bloquea independientemente de la capability
    assert.equal(calcularPuedeEmitirVoucher(true, null), false);
});
