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
  debePollearSituacionMulta,
  seAgotoElBudgetDePollingDeMulta,
  textoMultiOperador,
  tituloConNombreOperador,
  listaDeSituacionesMulta,
  hayMasDeUnOperadorConMulta,
  situacionesConPanelDeMulta,
  situacionesConPreguntaDeMulta,
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

test("familiaDeEstadoMulta: MultiOperatorNeedsManualReview es 'multiOperador' (ADR-044 T1)", () => {
  assert.equal(familiaDeEstadoMulta("MultiOperatorNeedsManualReview"), "multiOperador");
});

test("slugDeEstadoMulta: MultiOperatorNeedsManualReview mapea a 'multi-operador' (ADR-044 T1)", () => {
  assert.equal(slugDeEstadoMulta("MultiOperatorNeedsManualReview"), "multi-operador");
});

// ============================================================================
// textoMultiOperador / tituloConNombreOperador (ADR-044 T1, 2026-07-10)
// ============================================================================

test("textoMultiOperador: nunca nombra 'nota de débito' ni 'revisión manual' (voz de los avisos)", () => {
  const texto = textoMultiOperador();
  assert.ok(texto.length > 0);
  assert.ok(!texto.toLowerCase().includes("nota de débito"));
  assert.ok(!texto.toLowerCase().includes("revisión manual"));
});

test("tituloConNombreOperador: sin nombre, devuelve el mensaje intacto (caso mono-operador)", () => {
  assert.equal(tituloConNombreOperador(undefined, "Anulada — algo pasó."), "Anulada — algo pasó.");
  assert.equal(tituloConNombreOperador(null, "Anulada — algo pasó."), "Anulada — algo pasó.");
});

test("tituloConNombreOperador: con nombre, lo antepone separado por guion largo", () => {
  assert.equal(
    tituloConNombreOperador("Turismo Cardozo", "Anulada — algo pasó."),
    "Turismo Cardozo — Anulada — algo pasó."
  );
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

test("tienePasoDeMultaOperador: con operatorPenaltySituations (lista), algún operador activo → true (ADR-044 T1)", () => {
  assert.equal(
    tienePasoDeMultaOperador({
      operatorPenaltySituations: [{ state: "None" }, { state: "DebitNoteFailed" }],
    }),
    true
  );
});

test("tienePasoDeMultaOperador: con operatorPenaltySituations (lista), TODOS None/Done → false", () => {
  assert.equal(
    tienePasoDeMultaOperador({
      operatorPenaltySituations: [{ state: "None" }, { state: "Done" }],
    }),
    false
  );
});

// ============================================================================
// listaDeSituacionesMulta / hayMasDeUnOperadorConMulta / situacionesConPanelDeMulta
// (ADR-044 T1, 2026-07-10 — multa por operador)
// ============================================================================

test("listaDeSituacionesMulta: prefiere la lista nueva cuando viene con elementos", () => {
  const reserva = {
    operatorPenaltySituations: [{ state: "DebitNoteFailed", supplierPublicId: "op-1" }],
    operatorPenaltySituation: { state: "None" },
  };
  assert.deepEqual(listaDeSituacionesMulta(reserva), [{ state: "DebitNoteFailed", supplierPublicId: "op-1" }]);
});

test("listaDeSituacionesMulta: lista vacía o ausente cae al singular legado", () => {
  assert.deepEqual(
    listaDeSituacionesMulta({ operatorPenaltySituations: [], operatorPenaltySituation: { state: "Waived" } }),
    [{ state: "Waived" }]
  );
  assert.deepEqual(
    listaDeSituacionesMulta({ operatorPenaltySituation: { state: "Waived" } }),
    [{ state: "Waived" }]
  );
});

test("listaDeSituacionesMulta: sin ninguno de los dos campos → lista vacía (nunca rompe)", () => {
  assert.deepEqual(listaDeSituacionesMulta({}), []);
  assert.deepEqual(listaDeSituacionesMulta(null), []);
});

test("hayMasDeUnOperadorConMulta: un solo operador (caso de hoy) → false", () => {
  assert.equal(
    hayMasDeUnOperadorConMulta({ operatorPenaltySituations: [{ state: "PendingDecision" }] }),
    false
  );
  assert.equal(
    hayMasDeUnOperadorConMulta({ operatorPenaltySituation: { state: "PendingDecision" } }),
    false
  );
});

test("hayMasDeUnOperadorConMulta: dos o más operadores → true", () => {
  assert.equal(
    hayMasDeUnOperadorConMulta({
      operatorPenaltySituations: [{ state: "PendingDecision" }, { state: "MultiOperatorNeedsManualReview" }],
    }),
    true
  );
});

test("situacionesConPanelDeMulta: PARIDAD — una lista de 1 elemento produce lo mismo que el singular equivalente", () => {
  const situacionTrabada = { state: "DebitNoteFailed", canRetryDebitNote: true };

  const viaLista = situacionesConPanelDeMulta({ operatorPenaltySituations: [situacionTrabada] });
  const viaSingular = situacionesConPanelDeMulta({ operatorPenaltySituation: situacionTrabada });

  assert.deepEqual(viaLista, [situacionTrabada]);
  assert.deepEqual(viaLista, viaSingular);
});

test("situacionesConPanelDeMulta: filtra 'pregunta' y 'waived' (tienen su propio bloque, no pasan por el panel)", () => {
  const resultado = situacionesConPanelDeMulta({
    operatorPenaltySituations: [
      { state: "PendingDecision" },
      { state: "Waived" },
      { state: "DebitNoteQueued" },
    ],
  });
  assert.deepEqual(resultado, [{ state: "DebitNoteQueued" }]);
});

test("situacionesConPanelDeMulta: multi-operador — deja pasar los MultiOperatorNeedsManualReview de cada operador", () => {
  const operador1 = { state: "MultiOperatorNeedsManualReview", supplierPublicId: "op-1", supplierName: "Turismo Cardozo" };
  const operador2 = { state: "MultiOperatorNeedsManualReview", supplierPublicId: "op-2", supplierName: "Aerolíneas del Sur" };

  const resultado = situacionesConPanelDeMulta({ operatorPenaltySituations: [operador1, operador2] });
  assert.deepEqual(resultado, [operador1, operador2]);
});

test("situacionesConPanelDeMulta: sin situación de multa → lista vacía", () => {
  assert.deepEqual(situacionesConPanelDeMulta({}), []);
});

// ============================================================================
// situacionesConPreguntaDeMulta (ADR-044 T1, 2026-07-10 — fix bloqueante:
// la pregunta "¿cobró multa?" ahora es POR OPERADOR, no una sola compartida)
// ============================================================================

test("situacionesConPreguntaDeMulta: PARIDAD — lista de 1 elemento produce lo mismo que el singular equivalente", () => {
  const situacionPregunta = { state: "PendingDecision", canConfirm: true };

  const viaLista = situacionesConPreguntaDeMulta({ operatorPenaltySituations: [situacionPregunta] });
  const viaSingular = situacionesConPreguntaDeMulta({ operatorPenaltySituation: situacionPregunta });

  assert.deepEqual(viaLista, [situacionPregunta]);
  assert.deepEqual(viaLista, viaSingular);
});

test("situacionesConPreguntaDeMulta: filtra por familia 'pregunta' (PendingDecision) exclusivamente", () => {
  const resultado = situacionesConPreguntaDeMulta({
    operatorPenaltySituations: [
      { state: "PendingDecision", canConfirm: true },
      { state: "DebitNoteQueued", canConfirm: false },
      { state: "Done", canConfirm: false },
    ],
  });
  assert.deepEqual(resultado, [{ state: "PendingDecision", canConfirm: true }]);
});

test("situacionesConPreguntaDeMulta: PendingDecision con canConfirm=false NO se muestra (sin permiso u otra precondición)", () => {
  const resultado = situacionesConPreguntaDeMulta({
    operatorPenaltySituations: [{ state: "PendingDecision", canConfirm: false }],
  });
  assert.deepEqual(resultado, []);
});

test("situacionesConPreguntaDeMulta: multi-operador — un bloque por cada operador PendingDecision confirmable", () => {
  const operador1 = { state: "PendingDecision", canConfirm: true, supplierPublicId: "op-1", supplierName: "Turismo Cardozo" };
  const operador2 = { state: "PendingDecision", canConfirm: true, supplierPublicId: "op-2", supplierName: "Aerolíneas del Sur" };
  const operador3Confirmado = { state: "ConfirmedNoDebitNote", canConfirm: false, supplierPublicId: "op-3" };

  const resultado = situacionesConPreguntaDeMulta({
    operatorPenaltySituations: [operador1, operador2, operador3Confirmado],
  });
  assert.deepEqual(resultado, [operador1, operador2]);
});

test("situacionesConPreguntaDeMulta: sin situación de multa → lista vacía", () => {
  assert.deepEqual(situacionesConPreguntaDeMulta({}), []);
});

// ============================================================================
// debePollearSituacionMulta / seAgotoElBudgetDePollingDeMulta
// (refresco automático del cartel "procesando" — bug del F5 manual, 2026-07-08)
// ============================================================================

test("debePollearSituacionMulta: familia 'procesando' → true (única familia sin acción del agente)", () => {
  assert.equal(debePollearSituacionMulta("procesando"), true);
});

test("debePollearSituacionMulta: 'pregunta' → false (el agente ya tiene botones Sí/No)", () => {
  assert.equal(debePollearSituacionMulta("pregunta"), false);
});

test("debePollearSituacionMulta: 'accionTrabada' → false (se refresca sola al resolver la acción)", () => {
  assert.equal(debePollearSituacionMulta("accionTrabada"), false);
});

test("debePollearSituacionMulta: 'waived' → false", () => {
  assert.equal(debePollearSituacionMulta("waived"), false);
});

test("debePollearSituacionMulta: 'soloLectura' → false", () => {
  assert.equal(debePollearSituacionMulta("soloLectura"), false);
});

test("seAgotoElBudgetDePollingDeMulta: recién arrancado (0 ms) → todavía no", () => {
  assert.equal(seAgotoElBudgetDePollingDeMulta(0, 180_000), false);
});

test("seAgotoElBudgetDePollingDeMulta: justo antes del tope → todavía no", () => {
  assert.equal(seAgotoElBudgetDePollingDeMulta(179_999, 180_000), false);
});

test("seAgotoElBudgetDePollingDeMulta: justo en el tope → se agotó", () => {
  assert.equal(seAgotoElBudgetDePollingDeMulta(180_000, 180_000), true);
});

test("seAgotoElBudgetDePollingDeMulta: pasado el tope → se agotó", () => {
  assert.equal(seAgotoElBudgetDePollingDeMulta(300_000, 180_000), true);
});

test("seAgotoElBudgetDePollingDeMulta: tope custom respetado", () => {
  assert.equal(seAgotoElBudgetDePollingDeMulta(59_999, 60_000), false);
  assert.equal(seAgotoElBudgetDePollingDeMulta(60_000, 60_000), true);
});
