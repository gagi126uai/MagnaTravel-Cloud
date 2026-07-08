/**
 * Tests de lógica pura del cartel de "multa del operador" (spec "el paso de multa
 * vive en la ficha", 2026-07-08). Cubre los 7 estados de operatorPenaltySituation.state
 * y la regla de visibilidad de botones según los booleanos can* del DTO.
 *
 * Cómo correr:
 *   node --test src/features/cancellations/components/operatorPenaltyBanner.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import {
  slugDeEstadoMulta,
  familiaDeEstadoMulta,
  copyAccionTrabada,
  debeMostrarWaiveEnAccionTrabada,
  textoRastroWaived,
  tienePasoDeMultaOperador,
} from "../operatorPenaltyBanner.js";

// ============================================================================
// slugDeEstadoMulta
// ============================================================================

test("slugDeEstadoMulta: mapea cada uno de los 7 estados conocidos", () => {
  assert.equal(slugDeEstadoMulta("None"), "none");
  assert.equal(slugDeEstadoMulta("PendingDecision"), "pending-decision");
  assert.equal(slugDeEstadoMulta("DebitNoteQueued"), "debit-note-queued");
  assert.equal(slugDeEstadoMulta("DebitNoteFailed"), "debit-note-failed");
  assert.equal(slugDeEstadoMulta("DebitNoteNeedsAmountCurrency"), "needs-amount-currency");
  assert.equal(slugDeEstadoMulta("ConfirmedNoDebitNote"), "confirmed-no-debit-note");
  assert.equal(slugDeEstadoMulta("Waived"), "waived");
  assert.equal(slugDeEstadoMulta("Done"), "done");
});

test("slugDeEstadoMulta: estado desconocido no rompe el testid", () => {
  assert.equal(slugDeEstadoMulta("AlgoQueTodaviaNoExiste"), "desconocido");
  assert.equal(slugDeEstadoMulta(undefined), "desconocido");
});

// ============================================================================
// familiaDeEstadoMulta
// ============================================================================

test("familiaDeEstadoMulta: PendingDecision es 'pregunta'", () => {
  assert.equal(familiaDeEstadoMulta("PendingDecision"), "pregunta");
});

test("familiaDeEstadoMulta: DebitNoteQueued es 'procesando'", () => {
  assert.equal(familiaDeEstadoMulta("DebitNoteQueued"), "procesando");
});

test("familiaDeEstadoMulta: los 3 estados trabados son 'accionTrabada'", () => {
  assert.equal(familiaDeEstadoMulta("DebitNoteFailed"), "accionTrabada");
  assert.equal(familiaDeEstadoMulta("DebitNoteNeedsAmountCurrency"), "accionTrabada");
  assert.equal(familiaDeEstadoMulta("ConfirmedNoDebitNote"), "accionTrabada");
});

test("familiaDeEstadoMulta: Waived es 'waived'", () => {
  assert.equal(familiaDeEstadoMulta("Waived"), "waived");
});

test("familiaDeEstadoMulta: None y Done son 'soloLectura'", () => {
  assert.equal(familiaDeEstadoMulta("None"), "soloLectura");
  assert.equal(familiaDeEstadoMulta("Done"), "soloLectura");
});

test("familiaDeEstadoMulta: estado futuro desconocido cae a 'soloLectura' (degradación segura)", () => {
  assert.equal(familiaDeEstadoMulta("EstadoDelFuturo"), "soloLectura");
});

// ============================================================================
// copyAccionTrabada
// ============================================================================

test("copyAccionTrabada: DebitNoteFailed con permiso → botón Reintentar", () => {
  const r = copyAccionTrabada({ state: "DebitNoteFailed", canRetryDebitNote: true, canCorrectAmountCurrency: false });
  assert.equal(r.mensaje, "Anulada — el cargo de la multa al cliente no salió. Probá de nuevo.");
  assert.equal(r.accion, "reintentar");
  assert.equal(r.textoBoton, "Reintentar");
});

test("copyAccionTrabada: DebitNoteFailed sin permiso → sin botón (versión informativa)", () => {
  const r = copyAccionTrabada({ state: "DebitNoteFailed", canRetryDebitNote: false, canCorrectAmountCurrency: false });
  assert.equal(r.accion, null);
  assert.equal(r.textoBoton, null);
  // El mensaje se sigue mostrando aunque no haya botón.
  assert.ok(r.mensaje.length > 0);
});

test("copyAccionTrabada: DebitNoteNeedsAmountCurrency con permiso → botón Corregir", () => {
  const r = copyAccionTrabada({ state: "DebitNoteNeedsAmountCurrency", canRetryDebitNote: false, canCorrectAmountCurrency: true });
  assert.equal(r.accion, "corregir");
  assert.equal(r.textoBoton, "Corregir monto y moneda");
});

test("copyAccionTrabada: DebitNoteNeedsAmountCurrency sin permiso → sin botón", () => {
  const r = copyAccionTrabada({ state: "DebitNoteNeedsAmountCurrency", canRetryDebitNote: false, canCorrectAmountCurrency: false });
  assert.equal(r.accion, null);
});

test("copyAccionTrabada: ConfirmedNoDebitNote con permiso → botón Cobrarle la multa ahora", () => {
  const r = copyAccionTrabada({ state: "ConfirmedNoDebitNote", canRetryDebitNote: true, canCorrectAmountCurrency: false });
  assert.equal(r.accion, "emitir");
  assert.equal(r.textoBoton, "Cobrarle la multa ahora");
});

test("copyAccionTrabada: ConfirmedNoDebitNote sin permiso → sin botón", () => {
  const r = copyAccionTrabada({ state: "ConfirmedNoDebitNote", canRetryDebitNote: false, canCorrectAmountCurrency: false });
  assert.equal(r.accion, null);
});

// ============================================================================
// debeMostrarWaiveEnAccionTrabada
// ============================================================================

test("debeMostrarWaiveEnAccionTrabada: canWaive=true → visible", () => {
  assert.equal(debeMostrarWaiveEnAccionTrabada({ canWaive: true }), true);
});

test("debeMostrarWaiveEnAccionTrabada: canWaive=false → oculto", () => {
  assert.equal(debeMostrarWaiveEnAccionTrabada({ canWaive: false }), false);
});

test("debeMostrarWaiveEnAccionTrabada: canWaive ausente (DTO viejo) → oculto, degradación segura", () => {
  assert.equal(debeMostrarWaiveEnAccionTrabada({}), false);
});

test("debeMostrarWaiveEnAccionTrabada: situacion nula no rompe (defensivo)", () => {
  assert.equal(debeMostrarWaiveEnAccionTrabada(null), false);
});

// ============================================================================
// textoRastroWaived
// ============================================================================

test("textoRastroWaived: solo waivedAt, sin waivedByName (DTO viejo cacheado, nunca se deshizo)", () => {
  const texto = textoRastroWaived({ waivedAt: "2026-07-01T10:00:00Z", waivedByName: null, revertedAt: null, revertedByName: null });
  assert.equal(texto, "Cerrada sin multa el 01/07/2026");
});

test("textoRastroWaived: waivedAt + waivedByName → incluye quién lo cerró", () => {
  const texto = textoRastroWaived({
    waivedAt: "2026-07-01T10:00:00Z",
    waivedByName: "Gastón",
    revertedAt: null,
    revertedByName: null,
  });
  assert.equal(texto, "Cerrada sin multa el 01/07/2026 por Gastón");
});

test("textoRastroWaived: waivedAt + waivedByName + revertedAt + revertedByName", () => {
  const texto = textoRastroWaived({
    waivedAt: "2026-07-01T10:00:00Z",
    waivedByName: "Gastón",
    revertedAt: "2026-07-03T15:30:00Z",
    revertedByName: "Admin",
  });
  assert.equal(texto, "Cerrada sin multa el 01/07/2026 por Gastón · deshecho el 03/07/2026 (Admin)");
});

test("textoRastroWaived: sin waivedAt (defensivo, no debería pasar) usa texto genérico", () => {
  const texto = textoRastroWaived({ waivedAt: null, waivedByName: null, revertedAt: null, revertedByName: null });
  assert.equal(texto, "Cerrada sin multa del operador.");
});

// ============================================================================
// tienePasoDeMultaOperador
// ============================================================================

test("tienePasoDeMultaOperador: con operatorPenaltySituation, None → false", () => {
  assert.equal(
    tienePasoDeMultaOperador({ operatorPenaltySituation: { state: "None" } }),
    false
  );
});

test("tienePasoDeMultaOperador: con operatorPenaltySituation, Done → false", () => {
  assert.equal(
    tienePasoDeMultaOperador({ operatorPenaltySituation: { state: "Done" } }),
    false
  );
});

test("tienePasoDeMultaOperador: con operatorPenaltySituation, PendingDecision → true", () => {
  assert.equal(
    tienePasoDeMultaOperador({ operatorPenaltySituation: { state: "PendingDecision" } }),
    true
  );
});

test("tienePasoDeMultaOperador: sin operatorPenaltySituation, cae al fallback legado (Pending)", () => {
  assert.equal(
    tienePasoDeMultaOperador({ capabilities: { operatorPenaltyOutcome: "Pending" } }),
    true
  );
});

test("tienePasoDeMultaOperador: sin operatorPenaltySituation, fallback legado (Waived)", () => {
  assert.equal(
    tienePasoDeMultaOperador({ capabilities: { operatorPenaltyOutcome: "Waived" } }),
    true
  );
});

test("tienePasoDeMultaOperador: sin operatorPenaltySituation ni capabilities → false", () => {
  assert.equal(tienePasoDeMultaOperador({}), false);
});
