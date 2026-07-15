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
  hayCargoTrasladableSinFacturaDestino,
  primerCargoTrasladableSinFacturaDestino,
  tituloProcesandoMulta,
  calcularAvisoPlazoDeshacerMulta,
  textoRastroDeshacerMulta,
  SUGGESTED_PENALTY_PATHS,
  sugerenciaCaminoMulta,
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

test("familiaDeEstadoMulta: None es 'soloLectura'", () => {
  assert.equal(familiaDeEstadoMulta("None"), "soloLectura");
});

test("familiaDeEstadoMulta: Done es 'confirmada' (ADR-044 T4, 2026-07-10)", () => {
  assert.equal(familiaDeEstadoMulta("Done"), "confirmada");
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
// hayCargoTrasladableSinFacturaDestino / primerCargoTrasladableSinFacturaDestino
// (ADR-044 T4, 2026-07-10 — distinguir "falta elegir factura" de "falta monto/moneda")
// ============================================================================

test("hayCargoTrasladableSinFacturaDestino: true si hay un cargo NO Withholding sin targetInvoicePublicId", () => {
  const charges = [{ kind: "AdministrativeFee", targetInvoicePublicId: null }];
  assert.equal(hayCargoTrasladableSinFacturaDestino(charges), true);
});

test("hayCargoTrasladableSinFacturaDestino: false si todos los cargos ya tienen factura destino", () => {
  const charges = [{ kind: "AdministrativeFee", targetInvoicePublicId: "inv-1" }];
  assert.equal(hayCargoTrasladableSinFacturaDestino(charges), false);
});

test("hayCargoTrasladableSinFacturaDestino: Withholding sin factura destino NO cuenta (nunca emite renglón de ND)", () => {
  const charges = [{ kind: "Withholding", targetInvoicePublicId: null }];
  assert.equal(hayCargoTrasladableSinFacturaDestino(charges), false);
});

test("hayCargoTrasladableSinFacturaDestino: lista vacía o ausente → false (defensivo)", () => {
  assert.equal(hayCargoTrasladableSinFacturaDestino([]), false);
  assert.equal(hayCargoTrasladableSinFacturaDestino(undefined), false);
});

test("primerCargoTrasladableSinFacturaDestino: devuelve el primer cargo trasladable sin factura", () => {
  const cargoBueno = { kind: "AdministrativeFee", targetInvoicePublicId: null, publicId: "charge-1" };
  const charges = [
    { kind: "Withholding", targetInvoicePublicId: null, publicId: "charge-0" },
    cargoBueno,
  ];
  assert.deepEqual(primerCargoTrasladableSinFacturaDestino(charges), cargoBueno);
});

test("primerCargoTrasladableSinFacturaDestino: undefined si no hay ninguno (defensivo)", () => {
  assert.equal(primerCargoTrasladableSinFacturaDestino([]), undefined);
});

// ============================================================================
// copyAccionTrabada: rama nueva "elegirFactura" (ADR-044 T4)
// ============================================================================

test("copyAccionTrabada: DebitNoteNeedsAmountCurrency con cargo sin factura destino → accion 'elegirFactura'", () => {
  const r = copyAccionTrabada({
    state: "DebitNoteNeedsAmountCurrency",
    canRetryDebitNote: false,
    canCorrectAmountCurrency: true,
    manualReviewReason: "Todavía no se eligió a qué factura corresponde el cargo del operador.",
    charges: [{ kind: "AdministrativeFee", targetInvoicePublicId: null }],
  });
  assert.equal(r.accion, "elegirFactura");
  assert.equal(r.textoBoton, "Elegir la factura");
  // FIX F3 (gate de exposición, 2026-07-10): el mensaje es SIEMPRE el copy fijo de la
  // spec, nunca manualReviewReason — aunque acá venga un texto en criollo razonable.
  assert.equal(r.mensaje, "Anulada — el cargo de la multa al cliente quedó trabado: falta elegir a qué factura corresponde.");
});

test("copyAccionTrabada: FIX F3 — un manualReviewReason con pinta de texto técnico NUNCA se muestra (defensa en profundidad)", () => {
  // Simula justo el bug que el gate encontró: si algo técnico se colara en
  // manualReviewReason (bug del backend, DTO viejo, lo que sea), el front no debe
  // reventarlo al usuario. La rama "elegirFactura" IGNORA por completo este campo.
  const textoTecnico = "InvariantViolation: BookingCancellationLineOperatorCharge#4821 TargetInvoiceId=NULL (state=ManualReview)";
  const r = copyAccionTrabada({
    state: "DebitNoteNeedsAmountCurrency",
    canRetryDebitNote: false,
    canCorrectAmountCurrency: true,
    manualReviewReason: textoTecnico,
    charges: [{ kind: "AdministrativeFee", targetInvoicePublicId: null }],
  });
  assert.notEqual(r.mensaje, textoTecnico);
  assert.equal(r.mensaje, "Anulada — el cargo de la multa al cliente quedó trabado: falta elegir a qué factura corresponde.");
});

test("copyAccionTrabada: 'elegirFactura' sin manualReviewReason (null/undefined) usa el mismo texto fijo", () => {
  const r = copyAccionTrabada({
    state: "DebitNoteNeedsAmountCurrency",
    canRetryDebitNote: false,
    canCorrectAmountCurrency: true,
    manualReviewReason: null,
    charges: [{ kind: "AdministrativeFee", targetInvoicePublicId: null }],
  });
  assert.equal(r.mensaje, "Anulada — el cargo de la multa al cliente quedó trabado: falta elegir a qué factura corresponde.");
});

test("copyAccionTrabada: DebitNoteNeedsAmountCurrency SIN cargo sin factura → sigue siendo 'corregir' (caso viejo, sin regresión)", () => {
  const r = copyAccionTrabada({
    state: "DebitNoteNeedsAmountCurrency",
    canRetryDebitNote: false,
    canCorrectAmountCurrency: true,
    manualReviewReason: null,
    charges: [{ kind: "AdministrativeFee", targetInvoicePublicId: "inv-1" }],
  });
  assert.equal(r.accion, "corregir");
  assert.equal(r.mensaje, "Anulada — el cargo de la multa al cliente quedó trabado: falta confirmar el monto y la moneda.");
});

test("copyAccionTrabada: 'elegirFactura' sin permiso → sin botón (versión informativa), mismo copy fijo", () => {
  const r = copyAccionTrabada({
    state: "DebitNoteNeedsAmountCurrency",
    canRetryDebitNote: false,
    canCorrectAmountCurrency: false,
    manualReviewReason: "Falta elegir la factura.",
    charges: [{ kind: "AdministrativeFee", targetInvoicePublicId: null }],
  });
  assert.equal(r.accion, null);
  assert.equal(r.textoBoton, null);
  assert.equal(r.mensaje, "Anulada — el cargo de la multa al cliente quedó trabado: falta elegir a qué factura corresponde.");
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

test("situacionesConPanelDeMulta: Done (multa confirmada) SÍ pasa por el panel (ADR-044 T4, 2026-07-10)", () => {
  const resultado = situacionesConPanelDeMulta({
    operatorPenaltySituations: [{ state: "Done", amount: 200, currency: "USD" }],
  });
  assert.deepEqual(resultado, [{ state: "Done", amount: 200, currency: "USD" }]);
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

// ============================================================================
// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): estados nuevos
// DebitNoteAnnulling / DebitNoteAnnulmentFailed + polling + degradación segura.
// ============================================================================

test("slugDeEstadoMulta: DebitNoteAnnulling y DebitNoteAnnulmentFailed tienen slug propio", () => {
  assert.equal(slugDeEstadoMulta("DebitNoteAnnulling"), "debit-note-annulling");
  assert.equal(slugDeEstadoMulta("DebitNoteAnnulmentFailed"), "debit-note-annulment-failed");
});

test("familiaDeEstadoMulta: DebitNoteAnnulling es 'procesando' (mismo cartel ámbar que DebitNoteQueued)", () => {
  assert.equal(familiaDeEstadoMulta("DebitNoteAnnulling"), "procesando");
});

test("familiaDeEstadoMulta: DebitNoteAnnulmentFailed es 'accionTrabada' (mismo cartel naranja que los otros 3 trabados)", () => {
  assert.equal(familiaDeEstadoMulta("DebitNoteAnnulmentFailed"), "accionTrabada");
});

test("debePollearSituacionMulta: DebitNoteAnnulling entra en 'procesando' → sigue pollenado solo (reusa el mismo hook)", () => {
  const familia = familiaDeEstadoMulta("DebitNoteAnnulling");
  assert.equal(debePollearSituacionMulta(familia), true);
});

test("familiaDeEstadoMulta: un token todavía más nuevo (que ni siquiera este frontend actualizado conoce) sigue degradando a 'soloLectura'", () => {
  assert.equal(familiaDeEstadoMulta("EstadoDelFuturoQueTodaviaNoExiste"), "soloLectura");
});

test("tituloProcesandoMulta: DebitNoteQueued (emisión normal) usa el texto de siempre", () => {
  assert.equal(tituloProcesandoMulta("DebitNoteQueued"), "Anulada — se está emitiendo la multa al cliente.");
});

test("tituloProcesandoMulta: DebitNoteAnnulling (deshaciendo) usa el texto nuevo de la spec", () => {
  assert.equal(tituloProcesandoMulta("DebitNoteAnnulling"), "Anulada — se está dejando sin efecto la multa.");
});

test("tituloProcesandoMulta: cualquier otro estado (defensivo) cae al texto de siempre", () => {
  assert.equal(tituloProcesandoMulta("AlgoQueNoDeberiaLlegarAca"), "Anulada — se está emitiendo la multa al cliente.");
});

test("copyAccionTrabada: DebitNoteAnnulmentFailed con canUndoDebitNote Y esAdmin → botón Reintentar", () => {
  const r = copyAccionTrabada({ state: "DebitNoteAnnulmentFailed", canUndoDebitNote: true, esAdmin: true });
  assert.equal(r.mensaje, "Anulada — no se pudo dejar sin efecto la multa. Probá de nuevo.");
  assert.equal(r.accion, "reintentarDeshacer");
  assert.equal(r.textoBoton, "Reintentar");
});

test("copyAccionTrabada: DebitNoteAnnulmentFailed sin canUndoDebitNote → sin botón (versión informativa)", () => {
  const r = copyAccionTrabada({ state: "DebitNoteAnnulmentFailed", canUndoDebitNote: false, esAdmin: true });
  assert.equal(r.accion, null);
  assert.equal(r.textoBoton, null);
  assert.ok(r.mensaje.length > 0);
});

test("FIX BLOQUEANTE B1 (revisión 2026-07-14): copyAccionTrabada — DebitNoteAnnulmentFailed con canUndoDebitNote=true PERO esAdmin=false → sin botón", () => {
  // Este es exactamente el bug que reportó el gate de seguridad: un usuario sin rol
  // Admin (pero con permiso de clasificar multas) NO debe ver "Reintentar" aunque el
  // backend diga canUndoDebitNote=true.
  const r = copyAccionTrabada({ state: "DebitNoteAnnulmentFailed", canUndoDebitNote: true, esAdmin: false });
  assert.equal(r.accion, null);
  assert.equal(r.textoBoton, null);
});

test("copyAccionTrabada: DebitNoteAnnulmentFailed se gatea con canUndoDebitNote+esAdmin, NUNCA con canRetryDebitNote (son permisos distintos)", () => {
  // canRetryDebitNote=true pero canUndoDebitNote=false (o ausente): igual sin botón.
  const r = copyAccionTrabada({ state: "DebitNoteAnnulmentFailed", canRetryDebitNote: true, canUndoDebitNote: false, esAdmin: true });
  assert.equal(r.accion, null);
});

// ============================================================================
// calcularAvisoPlazoDeshacerMulta (spec sección 5 — plazo RG 4540 de 15 días,
// aviso SUAVE, NUNCA bloquea)
// ============================================================================

const MS_POR_DIA = 24 * 60 * 60 * 1000;
const EMITIDO = new Date("2026-07-01T10:00:00Z").getTime();

test("calcularAvisoPlazoDeshacerMulta: sin debitNoteIssuedAt (null/undefined) → no corresponde, null", () => {
  assert.equal(calcularAvisoPlazoDeshacerMulta(null, Date.now()), null);
  assert.equal(calcularAvisoPlazoDeshacerMulta(undefined, Date.now()), null);
});

test("calcularAvisoPlazoDeshacerMulta: fecha inválida (defensivo) → null, no rompe", () => {
  assert.equal(calcularAvisoPlazoDeshacerMulta("no-es-una-fecha", Date.now()), null);
});

test("calcularAvisoPlazoDeshacerMulta: recién emitida (mismo día) → tono suave, quedan 15 días", () => {
  const aviso = calcularAvisoPlazoDeshacerMulta(new Date(EMITIDO).toISOString(), EMITIDO);
  assert.equal(aviso.tono, "suave");
  assert.ok(aviso.texto.startsWith("Quedan 15 días"));
  assert.ok(aviso.texto.includes("vence el"));
});

test("calcularAvisoPlazoDeshacerMulta: dentro del plazo (6 días quedando) — mismo ejemplo del mockup de la spec", () => {
  const ahora = EMITIDO + 9 * MS_POR_DIA; // 9 días transcurridos → quedan 6.
  const aviso = calcularAvisoPlazoDeshacerMulta(new Date(EMITIDO).toISOString(), ahora);
  assert.equal(aviso.tono, "suave");
  assert.ok(aviso.texto.startsWith("Quedan 6 días"));
});

test("calcularAvisoPlazoDeshacerMulta: borde exacto — a los 15 días completos transcurridos, el plazo ya se considera vencido (tono fuerte)", () => {
  const ahora = EMITIDO + 15 * MS_POR_DIA; // exactamente 15 días completos.
  const aviso = calcularAvisoPlazoDeshacerMulta(new Date(EMITIDO).toISOString(), ahora);
  assert.equal(aviso.tono, "fuerte");
});

test("calcularAvisoPlazoDeshacerMulta: un instante antes del borde (14 días y pico) — todavía suave, queda 1 día", () => {
  const ahora = EMITIDO + 15 * MS_POR_DIA - 1; // 1 ms antes del borde de arriba.
  const aviso = calcularAvisoPlazoDeshacerMulta(new Date(EMITIDO).toISOString(), ahora);
  assert.equal(aviso.tono, "suave");
  assert.ok(aviso.texto.startsWith("Quedan 1 día "));
});

test("calcularAvisoPlazoDeshacerMulta: pasado el plazo (20 días) → tono fuerte con el texto EXACTO elegido por Gastón", () => {
  const ahora = EMITIDO + 20 * MS_POR_DIA;
  const aviso = calcularAvisoPlazoDeshacerMulta(new Date(EMITIDO).toISOString(), ahora);
  assert.equal(aviso.tono, "fuerte");
  assert.equal(
    aviso.texto,
    "Pasaron más de 15 días desde que se emitió este comprobante. Se puede deshacer igual, " +
      "pero convendría consultarlo con un contador antes de seguir."
  );
});

// ============================================================================
// textoRastroDeshacerMulta (spec sección 4 — rastro del último "Deshacer").
// Backend (2026-07-14): recibe TAL CUAL el objeto `situacion.lastDebitNoteUndo`
// ({ undoneAt, undoneByName, reason } | null).
// ============================================================================

test("textoRastroDeshacerMulta: lastDebitNoteUndo null (la ND nunca se deshizo) → null, no se muestra nada", () => {
  assert.equal(textoRastroDeshacerMulta(null), null);
  assert.equal(textoRastroDeshacerMulta(undefined), null);
});

test("textoRastroDeshacerMulta: objeto sin undoneAt (defensivo) → null", () => {
  assert.equal(textoRastroDeshacerMulta({}), null);
  assert.equal(textoRastroDeshacerMulta({ undoneAt: null, undoneByName: "Ana", reason: "algo" }), null);
});

test("textoRastroDeshacerMulta: solo undoneAt, undoneByName null y sin motivo → se corta después de la fecha", () => {
  const texto = textoRastroDeshacerMulta({ undoneAt: "2026-07-14T10:00:00Z", undoneByName: null, reason: "" });
  assert.equal(texto, "El comprobante anterior se dejó sin efecto el 14/07.");
});

test("textoRastroDeshacerMulta: undoneAt + undoneByName, sin motivo → se corta después de 'por Fulano'", () => {
  const texto = textoRastroDeshacerMulta({ undoneAt: "2026-07-14T10:00:00Z", undoneByName: "Ana" });
  assert.equal(texto, "El comprobante anterior se dejó sin efecto el 14/07 por Ana.");
});

test("textoRastroDeshacerMulta: undoneAt + undoneByName + reason → texto completo (mockup exacto de la spec)", () => {
  const texto = textoRastroDeshacerMulta({
    undoneAt: "2026-07-14T10:00:00Z",
    undoneByName: "Ana",
    reason: "el operador cobró la multa en pesos, no en dólares.",
  });
  assert.equal(
    texto,
    'El comprobante anterior se dejó sin efecto el 14/07 por Ana — motivo: "el operador cobró la multa en pesos, no en dólares."'
  );
});

test("textoRastroDeshacerMulta: undoneAt + reason pero SIN undoneByName (null) → omite 'por {nombre}', va directo al motivo", () => {
  const texto = textoRastroDeshacerMulta({
    undoneAt: "2026-07-14T10:00:00Z",
    undoneByName: null,
    reason: "el operador cobró la multa en pesos, no en dólares.",
  });
  assert.equal(
    texto,
    'El comprobante anterior se dejó sin efecto el 14/07 — motivo: "el operador cobró la multa en pesos, no en dólares."'
  );
});

// ============================================================================
// sugerenciaCaminoMulta — Configuracion de multas de cancelacion (2026-07-14, Pieza 2)
// ============================================================================

test("sugerenciaCaminoMulta: null (operador 'no se sabe', o paso que ya no es pregunta) → CERO cambio visual", () => {
  const sugerencia = sugerenciaCaminoMulta(null);
  assert.deepEqual(sugerencia, {
    ordenBotones: "siPrimero",
    siResaltado: true,
    noResaltado: true,
    notita: null,
  });
});

test("sugerenciaCaminoMulta: undefined (DTO viejo sin el campo) se comporta igual que null", () => {
  const sugerencia = sugerenciaCaminoMulta(undefined);
  assert.deepEqual(sugerencia, {
    ordenBotones: "siPrimero",
    siResaltado: true,
    noResaltado: true,
    notita: null,
  });
});

test("sugerenciaCaminoMulta: probablyNoPenalty → 'No cobró' primero y resaltado, con su notita exacta", () => {
  const sugerencia = sugerenciaCaminoMulta(SUGGESTED_PENALTY_PATHS.ProbablyNoPenalty);
  assert.equal(sugerencia.ordenBotones, "noPrimero");
  assert.equal(sugerencia.siResaltado, false);
  assert.equal(sugerencia.noResaltado, true);
  assert.equal(sugerencia.notita, "💡 Este operador casi nunca cobra multa (según su ficha).");
});

test("sugerenciaCaminoMulta: probablyPenalty → 'Sí cobró' primero y resaltado, con su notita exacta", () => {
  const sugerencia = sugerenciaCaminoMulta(SUGGESTED_PENALTY_PATHS.ProbablyPenalty);
  assert.equal(sugerencia.ordenBotones, "siPrimero");
  assert.equal(sugerencia.siResaltado, true);
  assert.equal(sugerencia.noResaltado, false);
  assert.equal(sugerencia.notita, "💡 Este operador casi siempre cobra multa (según su ficha).");
});

test("sugerenciaCaminoMulta: valor futuro desconocido degrada a 'sin sugerencia' (nunca rompe la pantalla)", () => {
  const sugerencia = sugerenciaCaminoMulta("algoQueTodaviaNoExiste");
  assert.deepEqual(sugerencia, {
    ordenBotones: "siPrimero",
    siResaltado: true,
    noResaltado: true,
    notita: null,
  });
});

test("sugerenciaCaminoMulta: nunca esconde ni deshabilita ningún camino (los dos booleanos de resaltado nunca son false a la vez)", () => {
  for (const valor of [null, undefined, SUGGESTED_PENALTY_PATHS.ProbablyNoPenalty, SUGGESTED_PENALTY_PATHS.ProbablyPenalty, "otro"]) {
    const sugerencia = sugerenciaCaminoMulta(valor);
    assert.ok(sugerencia.siResaltado || sugerencia.noResaltado, `Con valor=${valor} algún camino debe seguir visible/resaltado`);
  }
});
