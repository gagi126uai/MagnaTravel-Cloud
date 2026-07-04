/**
 * Tests de lógica pura para la sección "Reembolsos a cobrar del operador".
 *
 * Cubre:
 *   - Resolución del semáforo (integer a configuración visual)
 *   - Enmascarado de montos (amountsMasked)
 *   - Validación del motivo de reembolso tardío
 *   - Construcción del payload para POST reopen-for-late-refund
 *   - Identificación de items abandonados (candidatos a reembolso tardío)
 *   - RESTOS (2026-07-03): etiqueta de RowStatus en español
 *
 * Cómo correr: node --test src/features/suppliers/components/operatorRefundsPending.test.mjs
 *
 * Patrón del proyecto: funciones replicadas inline (sin import del componente).
 * Si cambia la lógica en OperatorRefundsPendingSection.jsx, actualizar acá también.
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica replicada de OperatorRefundsPendingSection ───────────────────────

/**
 * Semáforo del backend: integer sin JsonStringEnumConverter.
 * OnTime=0, DueSoon=1, Overdue=2, Abandoned=3.
 */
const SEMAPHORE_CONFIG = {
  0: { label: "A tiempo", rowHasRedBorder: false, isAbandoned: false, isOverdue: false },
  1: { label: "Por vencer", rowHasRedBorder: false, isAbandoned: false, isOverdue: false },
  2: { label: "Vencido", rowHasRedBorder: true, isAbandoned: false, isOverdue: true },
  3: { label: "Abandonado", rowHasRedBorder: false, isAbandoned: true, isOverdue: false },
};

const SEMAPHORE_UNKNOWN = {
  label: "Desconocido",
  rowHasRedBorder: false,
  isAbandoned: false,
  isOverdue: false,
};

function resolverSemaphore(semaphoreValue) {
  return SEMAPHORE_CONFIG[semaphoreValue] ?? SEMAPHORE_UNKNOWN;
}

/**
 * Decide si mostrar el monto de un item.
 * Si amountsMasked=true, el backend mandó los montos en 0 → mostramos "—".
 */
function resolverMontoPorMoneda(linea, amountsMasked) {
  if (amountsMasked) return "—";
  return linea.estimatedAmount;
}

/**
 * Valida el motivo antes del POST al backend.
 * El backend exige mínimo 10 caracteres (validado server-side también).
 */
function validarMotivoReembolsoTardio(motivo) {
  const trimmed = (motivo ?? "").trim();
  if (trimmed.length === 0) {
    return "El motivo es obligatorio.";
  }
  if (trimmed.length < 10) {
    return "El motivo debe tener al menos 10 caracteres.";
  }
  return null;
}

/**
 * Construye el payload para POST reopen-for-late-refund.
 * El backend espera { reason: string }.
 */
function armarPayloadReopenForLateRefund(motivo) {
  return { reason: (motivo ?? "").trim() };
}

// ─── RESTOS (2026-07-03): réplica de ROW_STATUS_LABELS ──────────────────────────
// AwaitingRefund=0 y Abandoned=2 quedan afuera del mapeo a propósito (ver el comentario real
// en OperatorRefundsPendingSection.jsx): 0 es el estado normal, 2 ya tiene su badge del semáforo.
// El desglose de la línea de moneda ("Pagaste X − Multa Y = te devuelven Z (estimado).") ya NO
// se replica acá: la solapa usa la función REAL construirTextoCuentaReembolso (fuente única con
// el panel de registrar), testeada contra la exportación real en supplierPageLogic.test.mjs.

const ROW_STATUS_LABELS = {
  1: "Parcialmente devuelto",
  3: "Cerrada con resto",
  4: "En proceso",
};

/**
 * Devuelve los items de la lista que son abandonados (semaphore = 3).
 * (Solo afecta el badge visual "Abandonado"; el botón de reembolso tardío lo decide
 *  puedeReabrirTardio, ver abajo — FIX A 2026-07-04.)
 */
function filtrarAbandonados(items) {
  return items.filter((i) => i.semaphore === 3);
}

/**
 * FIX A (2026-07-04): decide si la fila muestra el botón "Registrar reembolso tardío".
 * Lo manda el backend (canReopenForLateRefund), NO el semáforo: es true para una cancelación
 * abandonada O para una CERRADA que quedó con un resto que el operador todavía debe.
 */
function puedeReabrirTardio(item) {
  return item.canReopenForLateRefund === true;
}

/**
 * Devuelve los items vencidos (semaphore = 2).
 * Estos NUNCA desaparecen de la lista (requerimiento explícito de Gastón).
 */
function filtrarVencidos(items) {
  return items.filter((i) => i.semaphore === 2);
}

// ─── Tests: resolución del semáforo ──────────────────────────────────────────

test("resolverSemaphore — 0 (OnTime) da 'A tiempo' sin borde rojo", () => {
  const config = resolverSemaphore(0);
  assert.equal(config.label, "A tiempo");
  assert.equal(config.rowHasRedBorder, false);
  assert.equal(config.isAbandoned, false);
  assert.equal(config.isOverdue, false);
});

test("resolverSemaphore — 1 (DueSoon) da 'Por vencer'", () => {
  const config = resolverSemaphore(1);
  assert.equal(config.label, "Por vencer");
  assert.equal(config.isAbandoned, false);
  assert.equal(config.isOverdue, false);
});

test("resolverSemaphore — 2 (Overdue) da 'Vencido' con borde rojo", () => {
  const config = resolverSemaphore(2);
  assert.equal(config.label, "Vencido");
  assert.equal(config.rowHasRedBorder, true);
  assert.equal(config.isOverdue, true);
  assert.equal(config.isAbandoned, false);
});

test("resolverSemaphore — 3 (Abandoned) da 'Abandonado' y marca como abandonado", () => {
  const config = resolverSemaphore(3);
  assert.equal(config.label, "Abandonado");
  assert.equal(config.isAbandoned, true);
  assert.equal(config.isOverdue, false);
});

test("resolverSemaphore — valor desconocido devuelve fallback 'Desconocido'", () => {
  const config = resolverSemaphore(99);
  assert.equal(config.label, "Desconocido");
  assert.equal(config.isAbandoned, false);
});

test("resolverSemaphore — undefined devuelve fallback", () => {
  const config = resolverSemaphore(undefined);
  assert.equal(config.label, "Desconocido");
});

// ─── Tests: enmascarado de montos ─────────────────────────────────────────────

test("resolverMontoPorMoneda — sin enmascarado devuelve el monto estimado", () => {
  const linea = { currency: "ARS", estimatedAmount: 15000 };
  const resultado = resolverMontoPorMoneda(linea, false);
  assert.equal(resultado, 15000);
});

test("resolverMontoPorMoneda — con amountsMasked=true devuelve '—'", () => {
  const linea = { currency: "ARS", estimatedAmount: 0 };
  const resultado = resolverMontoPorMoneda(linea, true);
  assert.equal(resultado, "—");
});

test("resolverMontoPorMoneda — monto USD sin enmascarado devuelve el monto", () => {
  const linea = { currency: "USD", estimatedAmount: 350.5 };
  const resultado = resolverMontoPorMoneda(linea, false);
  assert.equal(resultado, 350.5);
});

test("resolverMontoPorMoneda — monto 0 SIN enmascarar devuelve 0 (no '—')", () => {
  // Caso: la deuda real estimada es cero (no confundir con enmascarado)
  const linea = { currency: "ARS", estimatedAmount: 0 };
  const resultado = resolverMontoPorMoneda(linea, false);
  assert.equal(resultado, 0);
});

// ─── Tests: validación del motivo de reembolso tardío ────────────────────────

test("validarMotivoReembolsoTardio — motivo vacío devuelve error de obligatorio", () => {
  const error = validarMotivoReembolsoTardio("");
  assert.ok(error);
  assert.ok(error.toLowerCase().includes("obligatorio") || error.toLowerCase().includes("obligatorio"));
});

test("validarMotivoReembolsoTardio — motivo undefined devuelve error", () => {
  const error = validarMotivoReembolsoTardio(undefined);
  assert.ok(error);
});

test("validarMotivoReembolsoTardio — motivo de 9 caracteres devuelve error de longitud", () => {
  const error = validarMotivoReembolsoTardio("123456789"); // 9 chars
  assert.ok(error);
  assert.ok(error.includes("10"));
});

test("validarMotivoReembolsoTardio — motivo de exactamente 10 caracteres es válido", () => {
  const error = validarMotivoReembolsoTardio("1234567890"); // 10 chars
  assert.equal(error, null);
});

test("validarMotivoReembolsoTardio — motivo largo es válido", () => {
  const error = validarMotivoReembolsoTardio("El operador envió el reembolso con demora por problema bancario.");
  assert.equal(error, null);
});

test("validarMotivoReembolsoTardio — motivo con solo espacios es inválido", () => {
  // trim() lo reduce a cadena vacía
  const error = validarMotivoReembolsoTardio("   ");
  assert.ok(error);
});

test("validarMotivoReembolsoTardio — espacios alrededor no cuentan para el mínimo", () => {
  // "  12345  " → trim → "12345" → 5 chars → inválido
  const error = validarMotivoReembolsoTardio("  12345  ");
  assert.ok(error);
  assert.ok(error.includes("10"));
});

// ─── Tests: construcción del payload ─────────────────────────────────────────

test("armarPayloadReopenForLateRefund — produce el campo 'reason' con el motivo trimmeado", () => {
  const payload = armarPayloadReopenForLateRefund("  El operador pagó tarde  ");
  assert.deepEqual(Object.keys(payload), ["reason"]);
  assert.equal(payload.reason, "El operador pagó tarde");
});

test("armarPayloadReopenForLateRefund — motivo sin espacios extra queda igual", () => {
  const payload = armarPayloadReopenForLateRefund("Motivo valido sin espacios");
  assert.equal(payload.reason, "Motivo valido sin espacios");
});

test("armarPayloadReopenForLateRefund — motivo vacío produce reason vacío (la validación lo descarta antes)", () => {
  // Este caso no debería llegar al API porque validarMotivoReembolsoTardio lo descarta,
  // pero verificamos que el payload se construye igualmente.
  const payload = armarPayloadReopenForLateRefund("");
  assert.equal(payload.reason, "");
});

// ─── Tests: filtrado de items por semáforo ───────────────────────────────────

test("filtrarAbandonados — devuelve solo los items con semaphore=3", () => {
  const items = [
    { id: "a", semaphore: 0 },
    { id: "b", semaphore: 1 },
    { id: "c", semaphore: 2 },
    { id: "d", semaphore: 3 },
    { id: "e", semaphore: 3 },
  ];
  const abandonados = filtrarAbandonados(items);
  assert.equal(abandonados.length, 2);
  assert.equal(abandonados[0].id, "d");
  assert.equal(abandonados[1].id, "e");
});

test("filtrarAbandonados — sin abandonados devuelve array vacío", () => {
  const items = [
    { id: "a", semaphore: 0 },
    { id: "b", semaphore: 2 },
  ];
  assert.equal(filtrarAbandonados(items).length, 0);
});

test("filtrarVencidos — devuelve solo los items con semaphore=2", () => {
  const items = [
    { id: "a", semaphore: 0 },
    { id: "b", semaphore: 2 },
    { id: "c", semaphore: 2 },
    { id: "d", semaphore: 3 },
  ];
  const vencidos = filtrarVencidos(items);
  assert.equal(vencidos.length, 2);
  assert.equal(vencidos[0].id, "b");
});

test("filtrarVencidos — vencidos NUNCA desaparecen: filtro no los excluye", () => {
  // Este test documenta el requerimiento: los vencidos deben permanecer en la lista.
  // En la UI, no hay botón de "ocultar vencidos" ni lógica que los descarte.
  const itemsVencidos = [
    { id: "v1", semaphore: 2, daysOverdue: 5 },
    { id: "v2", semaphore: 2, daysOverdue: 30 },
  ];
  const resultado = filtrarVencidos(itemsVencidos);
  // Todos los vencidos siguen en la lista, sin importar cuántos días llevan
  assert.equal(resultado.length, 2);
});

// ─── FIX A (2026-07-04): gate del botón de reembolso tardío (canReopenForLateRefund) ────────

test("puedeReabrirTardio — true cuando canReopenForLateRefund=true (abandonada)", () => {
  assert.equal(puedeReabrirTardio({ semaphore: 3, canReopenForLateRefund: true }), true);
});

test("puedeReabrirTardio — true para una CERRADA con resto (semaphore no es 3)", () => {
  // Cerrada con residuo: el semáforo NO es Abandoned (3), pero el backend igual la marca reabrible.
  assert.equal(puedeReabrirTardio({ semaphore: 2, canReopenForLateRefund: true }), true);
});

test("puedeReabrirTardio — false cuando canReopenForLateRefund=false (esperando/registrable directo)", () => {
  assert.equal(puedeReabrirTardio({ semaphore: 0, canReopenForLateRefund: false }), false);
});

test("puedeReabrirTardio — false cuando el flag no viene (no se asume reabrible)", () => {
  assert.equal(puedeReabrirTardio({ semaphore: 3 }), false);
});

test("filtrarAbandonados y filtrarVencidos son mutuamente excluyentes", () => {
  const items = [
    { id: "a", semaphore: 0 },
    { id: "b", semaphore: 1 },
    { id: "c", semaphore: 2 },
    { id: "d", semaphore: 3 },
  ];
  const abandonados = filtrarAbandonados(items).map((i) => i.id);
  const vencidos = filtrarVencidos(items).map((i) => i.id);
  // Ningún id aparece en ambas listas
  const interseccion = abandonados.filter((id) => vencidos.includes(id));
  assert.equal(interseccion.length, 0);
});

// ============================================================================
// RESTOS (2026-07-03): ROW_STATUS_LABELS — etiqueta EN ESPAÑOL, nunca el enum crudo
// ============================================================================

test("ROW_STATUS_LABELS — 1 (PartiallyRefunded) da 'Parcialmente devuelto'", () => {
  assert.equal(ROW_STATUS_LABELS[1], "Parcialmente devuelto");
});

test("ROW_STATUS_LABELS — 3 (ClosedWithResidue) da 'Cerrada con resto'", () => {
  assert.equal(ROW_STATUS_LABELS[3], "Cerrada con resto");
});

test("ROW_STATUS_LABELS — 4 (InProcess) da 'En proceso'", () => {
  assert.equal(ROW_STATUS_LABELS[4], "En proceso");
});

test("ROW_STATUS_LABELS — 0 (AwaitingRefund) NO tiene label (estado normal, ya lo cuenta el semáforo)", () => {
  assert.equal(ROW_STATUS_LABELS[0], undefined);
});

test("ROW_STATUS_LABELS — 2 (Abandoned) NO tiene label (ya tiene su propio badge de semáforo)", () => {
  assert.equal(ROW_STATUS_LABELS[2], undefined);
});

test("ROW_STATUS_LABELS — ningún label es el nombre crudo del enum en inglés", () => {
  for (const label of Object.values(ROW_STATUS_LABELS)) {
    assert.ok(!/PartiallyRefunded|ClosedWithResidue|InProcess|Abandoned|AwaitingRefund/.test(label));
  }
});

// ============================================================================
// Desglose de la línea de moneda de la solapa (decisión 1 / mockup B): usa la función REAL
// construirTextoCuentaReembolso — sus casos (cuenta completa, residuo con "− Ya devuelto",
// motivo del $0, enmascarado "—") están testeados contra la exportación real en
// supplierPageLogic.test.mjs. No se replica acá para no derivar en silencio.
// ============================================================================
