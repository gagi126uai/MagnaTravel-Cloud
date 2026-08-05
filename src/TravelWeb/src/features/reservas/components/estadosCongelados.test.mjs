/**
 * Tests de lógica pura para los dos cambios de UI del 2026-06-22 + fixes de 2026-06-24:
 *
 * Cambio 1 — Comprobantes: en estado congelado solo se puede VER un comprobante
 *   ya emitido. Las acciones de escritura (Emitir, Anular comprobante) desaparecen.
 *
 * Cambio 2 — Banner "Pedí autorización": solo debe aparecer en "Confirmada".
 *   En Traveling y Closed el vendedor ya tiene el cartel de solo-lectura;
 *   el banner ámbar no aporta nada.
 *
 * BUG IMP-3 fix 2026-06-24 — Editar cobro se gobierna por la capacidad
 *   `canEditOrDeletePayment` del backend, no por el helper `congelado`.
 *   En Closed el backend pone esa capacidad en false aunque congelado=false.
 *
 * BUG 2 fix 2026-08-05 (saneo 2026-08-05, PR-7 "no se deja deuda de tests engañosos") —
 *   "Deshacer" (antes "Eliminar") YA NO se gobierna por `canEditOrDeletePayment`: el motor
 *   tiene un camino con rastro en CUALQUIER estado (DELETE en estados vivos, POST /annul en
 *   los 4 terminales — ver `undoPaymentFlow.js`), así que Deshacer SIEMPRE se ofrece. Lo
 *   único que puede apagarlo es el candado FISCAL por-pago (recibo emitido / factura viva),
 *   que es independiente del estado de la reserva — ver `resolverBloqueoFilaCobro` en
 *   `paymentRowGuard.js`, importada más abajo (lógica REAL, no una copia a mano).
 *   Los tests de este archivo que antes decían "Editar y Eliminar quedan OCULTOS juntos en
 *   terminal" estaban afirmando algo que ya no es cierto para Deshacer — corregidos abajo.
 *
 * BUG IMP-4 fix 2026-06-24 — esCongeladoParaRecibos NO incluye FullyInvoiced.
 *   Facturación y cobranza son ejes separados (ADR-037).
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/estadosCongelados.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
// Lógica REAL (no replicada) del candado fiscal por-pago que gobierna "Deshacer": se
// importa acá para que los tests de este archivo prueben el comportamiento verdadero,
// no una copia que puede quedar desactualizada (lección "falso verde" del 2026-08-03).
import { resolverBloqueoFilaCobro } from "../lib/paymentRowGuard.js";
// Lógica REAL del set de estados terminales (los únicos donde Deshacer pega /annul en
// vez de DELETE) — se usa para no hardcodear "Closed" a mano en los tests de abajo.
import { requiereAnularConRastro } from "../lib/undoPaymentFlow.js";

// ─── Réplica del helper esEstadoCongelado (para vouchers y documentos) ────────

/**
 * Un estado es "congelado para vouchers/documentos" cuando:
 *  - La reserva ya arrancó (Traveling) → el viaje está en curso.
 *  - Está perdida (Lost) o anulada (Cancelled) → proceso cerrado.
 *  - Esperando reembolso (PendingOperatorRefund) → en solo lectura.
 *  - Ya está completamente facturada (FullyInvoiced) → no se emiten más documentos.
 *
 * NO es congelado: Confirmed, InManagement, Budget, Quotation, Closed (sin FullyInvoiced).
 * (en Closed todavía se puede facturar, por eso no es congelado para vouchers salvo que
 * ya esté completamente facturado).
 *
 * NOTA: este helper NO se usa para los recibos de cobro (ver esCongeladoParaRecibos).
 */
function esEstadoCongelado(reserva) {
  if (!reserva) return false;
  return (
    reserva.status === "Traveling" ||
    reserva.status === "Lost" ||
    reserva.status === "Cancelled" ||
    reserva.status === "PendingOperatorRefund" ||
    reserva.invoicingStatus === "FullyInvoiced"
  );
}

/**
 * Réplica de esCongeladoParaRecibos (ReservaDetailPage.jsx).
 * Controla si se puede emitir o anular el RECIBO de un cobro.
 *
 * BUG IMP-4 fix 2026-06-24: NO incluye FullyInvoiced porque facturación y cobranza
 * son ejes separados (ADR-037). Una reserva FullyInvoiced puede seguir teniendo
 * cobros que necesiten recibo.
 */
function esCongeladoParaRecibos(reserva) {
  if (!reserva) return false;
  return (
    reserva.status === "Traveling" ||
    reserva.status === "Lost" ||
    reserva.status === "Cancelled" ||
    reserva.status === "PendingOperatorRefund"
  );
}

// ── Estados que SÍ son congelados para VOUCHERS ───────────────────────────────

test("congelado vouchers: Traveling → true (viaje en curso, solo lectura)", () => {
  assert.equal(esEstadoCongelado({ status: "Traveling", invoicingStatus: "NotInvoiced" }), true);
});

test("congelado vouchers: Lost → true (cerrada sin cobro)", () => {
  assert.equal(esEstadoCongelado({ status: "Lost", invoicingStatus: "NotInvoiced" }), true);
});

test("congelado vouchers: Cancelled → true (anulada formalmente)", () => {
  assert.equal(esEstadoCongelado({ status: "Cancelled", invoicingStatus: "NotInvoiced" }), true);
});

test("congelado vouchers: PendingOperatorRefund → true (anulada esperando reembolso, decisión 2026-06-22)", () => {
  assert.equal(esEstadoCongelado({ status: "PendingOperatorRefund", invoicingStatus: "NotInvoiced" }), true);
});

test("congelado vouchers: FullyInvoiced → true sin importar el status operativo", () => {
  // Una reserva Confirmed pero ya con facturación completa no puede emitir más documentos.
  assert.equal(esEstadoCongelado({ status: "Confirmed", invoicingStatus: "FullyInvoiced" }), true);
});

test("congelado vouchers: Closed + FullyInvoiced → true", () => {
  assert.equal(esEstadoCongelado({ status: "Closed", invoicingStatus: "FullyInvoiced" }), true);
});

// ── Estados que NO son congelados para VOUCHERS ───────────────────────────────

test("no congelado vouchers: Confirmed con factura parcial → false (puede emitir más)", () => {
  assert.equal(esEstadoCongelado({ status: "Confirmed", invoicingStatus: "PartiallyInvoiced" }), false);
});

test("no congelado vouchers: Confirmed sin facturar → false", () => {
  assert.equal(esEstadoCongelado({ status: "Confirmed", invoicingStatus: "NotInvoiced" }), false);
});

test("no congelado vouchers: InManagement → false", () => {
  assert.equal(esEstadoCongelado({ status: "InManagement", invoicingStatus: "NotInvoiced" }), false);
});

test("no congelado vouchers: Budget → false", () => {
  assert.equal(esEstadoCongelado({ status: "Budget", invoicingStatus: "NotInvoiced" }), false);
});

test("no congelado vouchers: Quotation → false", () => {
  assert.equal(esEstadoCongelado({ status: "Quotation", invoicingStatus: "NotInvoiced" }), false);
});

test("no congelado vouchers: Closed sin factura → false (puede facturar desde Finalizada, ADR-037)", () => {
  // En Closed todavía se puede emitir factura (desacople de facturación ADR-037).
  assert.equal(esEstadoCongelado({ status: "Closed", invoicingStatus: "NotInvoiced" }), false);
});

test("no congelado vouchers: reserva null → false (degradación elegante)", () => {
  assert.equal(esEstadoCongelado(null), false);
  assert.equal(esEstadoCongelado(undefined), false);
});

// ── esCongeladoParaRecibos: BUG IMP-4 fix ────────────────────────────────────

test("recibos: Traveling → congelado (viaje en curso)", () => {
  assert.equal(esCongeladoParaRecibos({ status: "Traveling" }), true);
});

test("recibos: Lost → congelado", () => {
  assert.equal(esCongeladoParaRecibos({ status: "Lost" }), true);
});

test("recibos: Cancelled → congelado", () => {
  assert.equal(esCongeladoParaRecibos({ status: "Cancelled" }), true);
});

test("recibos: PendingOperatorRefund → congelado", () => {
  assert.equal(esCongeladoParaRecibos({ status: "PendingOperatorRefund" }), true);
});

test("recibos: Confirmed + FullyInvoiced → NO congelado (BUG IMP-4 fix: ADR-037)", () => {
  // Una reserva totalmente facturada puede seguir teniendo cobros pendientes.
  // FullyInvoiced no bloquea emitir/anular el recibo de un cobro.
  assert.equal(esCongeladoParaRecibos({ status: "Confirmed", invoicingStatus: "FullyInvoiced" }), false);
});

test("recibos: Closed → NO congelado (el bloqueo de editar viene de canEditOrDeletePayment)", () => {
  // Closed: emitir un recibo de un cobro reciente sigue siendo válido.
  // El bloqueo de Editar/Eliminar viene del capability del backend, no de este helper.
  assert.equal(esCongeladoParaRecibos({ status: "Closed" }), false);
});

test("recibos: InManagement → NO congelado", () => {
  assert.equal(esCongeladoParaRecibos({ status: "InManagement" }), false);
});

test("recibos: null → false (degradación elegante)", () => {
  assert.equal(esCongeladoParaRecibos(null), false);
});

// ─── Réplica: lógica de PaymentReceiptActions con prop congelado ───────────────

/**
 * Réplica de la lógica de visibilidad dentro de PaymentReceiptActions
 * (ReservaDetailPage.jsx). No se puede importar el componente real acá: es JSX
 * inline dentro de una página, no un módulo exportado, así que `node --test`
 * no puede montarlo sin bundler — este es el caso "no se puede ejercitar sin
 * replicar" (se deja el mínimo indispensable, con este comentario explicando por qué).
 *
 * `congelado` aquí es el resultado de esCongeladoParaRecibos (sin FullyInvoiced).
 * `canEditarEliminar` viene de la capacidad `canEditOrDeletePayment.allowed` del backend.
 *
 * BUG IMP-3 fix 2026-06-24: Editar ya no depende de `congelado` sino de
 * `canEditarEliminar` (la capacidad real del backend).
 *
 * BUG 2 fix 2026-08-05: "Deshacer" (antes "Eliminar") YA NO depende de
 * `canEditarEliminar` — se ofrece SIEMPRE (`puedeDeshacer` es una constante `true`,
 * no una variable que dependa de props, porque en el componente real tampoco depende
 * de ninguna capacidad de reserva: alcanza con que el caller pase el callback). Lo que
 * decide si el botón queda gris es el candado FISCAL por-pago — eso NO se replica acá,
 * se ejercita con la función real `resolverBloqueoFilaCobro` en los tests de más abajo.
 */
function resolverAccionesRecibo({ receipt, payment, congelado, canEditarEliminar }) {
  const tieneRecibo = Boolean(receipt);
  const estaAnulado = receipt?.status === "Voided";
  const puedeEmitir = !tieneRecibo &&
    (payment?.entryType === "Payment") &&
    Number(payment?.amount || 0) > 0;

  const puedeEditar = Boolean(canEditarEliminar);
  const puedeDeshacer = true; // BUG 2: siempre se ofrece, ver JSDoc de arriba.

  if (tieneRecibo) {
    return {
      // El chip (número o "Comprobante anulado") siempre visible: es trazabilidad.
      chipVisible: true,
      // Ver PDF: visible solo si el recibo no está anulado.
      verPdfVisible: !estaAnulado,
      // Anular comprobante: solo si no anulado Y no congelado (para recibos).
      anularVisible: !estaAnulado && !congelado,
      editarVisible: puedeEditar,
      deshacerVisible: puedeDeshacer,
      // Emitir: no aplica (ya tiene recibo).
      emitirVisible: false,
      // "Sin comprobante": no aplica.
      sinComprobanteVisible: false,
    };
  }

  // Sin recibo y congelado para recibos: no se ofrece emitir ni "Sin comprobante",
  // pero Editar/Deshacer siguen su propia regla (no dependen de este `congelado`).
  if (congelado) {
    return {
      chipVisible: false,
      verPdfVisible: false,
      anularVisible: false,
      editarVisible: puedeEditar,
      deshacerVisible: puedeDeshacer,
      emitirVisible: false,
      sinComprobanteVisible: false,
    };
  }

  // Sin recibo y no congelado: se puede emitir (si el cobro lo permite).
  return {
    chipVisible: false,
    verPdfVisible: false,
    anularVisible: false,
    editarVisible: puedeEditar,
    deshacerVisible: puedeDeshacer,
    emitirVisible: puedeEmitir,
    sinComprobanteVisible: !puedeEmitir,
  };
}

// ── Con recibo vigente ─────────────────────────────────────────────────────────

test("recibo: con recibo vigente en normal + canEditarEliminar=true → chip + Ver PDF + Anular + Editar + Deshacer visibles", () => {
  const result = resolverAccionesRecibo({
    receipt: { status: "Issued", receiptNumber: "R-001" },
    payment: { entryType: "Payment", amount: 1000 },
    congelado: false,
    canEditarEliminar: true,
  });
  assert.equal(result.chipVisible, true);
  assert.equal(result.verPdfVisible, true);
  assert.equal(result.anularVisible, true);
  assert.equal(result.editarVisible, true);
  assert.equal(result.deshacerVisible, true);
  assert.equal(result.emitirVisible, false);
});

test("recibo: con recibo vigente en CONGELADO (para recibos) → chip + Ver PDF + Editar (si permitido) + Deshacer, Anular OCULTO", () => {
  // Decisión UX 2026-06-22: "ver/imprimir un papel ya hecho" sí; "anular" no.
  // BUG IMP-3: Editar depende de canEditarEliminar, no de congelado.
  const result = resolverAccionesRecibo({
    receipt: { status: "Issued", receiptNumber: "R-001" },
    payment: { entryType: "Payment", amount: 1000 },
    congelado: true,
    canEditarEliminar: true,
  });
  assert.equal(result.chipVisible, true);
  assert.equal(result.verPdfVisible, true);
  assert.equal(result.anularVisible, false, "Anular debe ocultarse cuando congelado para recibos");
  assert.equal(result.editarVisible, true, "Editar depende de canEditarEliminar, no de congelado");
  assert.equal(result.deshacerVisible, true, "Deshacer se ofrece siempre (BUG 2)");
  assert.equal(result.emitirVisible, false);
});

test("recibo: con recibo vigente + canEditarEliminar=false (Closed) → Editar OCULTO, pero Deshacer SIGUE OFRECIDO (BUG 2)", () => {
  // BUG IMP-3 fix: en Closed el backend devuelve canEditOrDeletePayment=false → Editar
  // se oculta (no tiene alternativa en terminal). BUG 2 (2026-08-05): antes este mismo
  // `false` también escondía "Eliminar" — afirmación que este test corregía a mano y que
  // ya NO es cierta: "Deshacer" tiene un camino con rastro en CUALQUIER estado (POST
  // /annul en terminal), así que se sigue ofreciendo. Si el motor la rechaza (recibo/
  // factura), es el candado FISCAL por-pago quien la apaga — ver el test con
  // resolverBloqueoFilaCobro más abajo, que ejercita la función REAL.
  const result = resolverAccionesRecibo({
    receipt: { status: "Issued", receiptNumber: "R-001" },
    payment: { entryType: "Payment", amount: 1000 },
    congelado: false,
    canEditarEliminar: false,
  });
  assert.equal(result.editarVisible, false, "Editar oculto cuando backend dice canEditOrDeletePayment=false");
  assert.equal(result.deshacerVisible, true, "Deshacer NO se oculta: siempre tiene un camino con rastro (BUG 2)");
  assert.equal(result.chipVisible, true, "El chip de número sigue visible");
  assert.equal(result.verPdfVisible, true, "Ver PDF sigue visible");
  assert.equal(result.anularVisible, true, "Anular sigue disponible si no congelado");
});

test("recibo: comprobante ya anulado en CONGELADO + canEditarEliminar=false → chip + Deshacer visibles, sin Ver PDF ni Anular ni Editar", () => {
  // Editar queda oculto por canEditarEliminar=false (nivel reserva); Deshacer NO usa esa
  // prop (BUG 2) — si el motor lo bloquea, es por el candado fiscal por-pago, no por esto.
  const result = resolverAccionesRecibo({
    receipt: { status: "Voided", receiptNumber: "R-002" },
    payment: { entryType: "Payment", amount: 1000 },
    congelado: true,
    canEditarEliminar: false,
  });
  assert.equal(result.chipVisible, true);
  assert.equal(result.verPdfVisible, false);
  assert.equal(result.anularVisible, false);
  assert.equal(result.editarVisible, false, "Editar oculto por canEditarEliminar=false (nivel reserva)");
  assert.equal(result.deshacerVisible, true, "Deshacer se sigue ofreciendo (BUG 2)");
});

test("P4-1: comprobante anulado + canEditarEliminar=true → Editar y Deshacer NO se ocultan", () => {
  // Antes de P4-1, un guard local `!reciboAnulado` escondía ambos botones sin importar
  // lo que dijera el backend. Ahora se OFRECEN: Editar mira canEditarEliminar (true acá),
  // Deshacer se ofrece siempre (BUG 2). Cuál queda gris lo decide el motor por botón
  // (payment.canEdit/canDelete) — cubierto en paymentRowGuard.test.mjs.
  const result = resolverAccionesRecibo({
    receipt: { status: "Voided", receiptNumber: "R-002" },
    payment: { entryType: "Payment", amount: 1000 },
    congelado: false,
    canEditarEliminar: true,
  });
  assert.equal(result.editarVisible, true, "Se ofrece Editar (puede quedar gris según el motor, pero ya no se esconde)");
  assert.equal(result.deshacerVisible, true, "Se ofrece Deshacer; el motor permite deshacer cobros con recibo anulado");
});

// ── Sin recibo ─────────────────────────────────────────────────────────────────

test("sin recibo: cobro emitible en normal + canEditarEliminar=true → Emitir + Editar + Deshacer visibles", () => {
  const result = resolverAccionesRecibo({
    receipt: null,
    payment: { entryType: "Payment", amount: 500 },
    congelado: false,
    canEditarEliminar: true,
  });
  assert.equal(result.emitirVisible, true);
  assert.equal(result.editarVisible, true);
  assert.equal(result.deshacerVisible, true);
  assert.equal(result.sinComprobanteVisible, false);
  assert.equal(result.chipVisible, false);
});

test("sin recibo: cobro emitible en CONGELADO (para recibos) + canEditarEliminar=false → Emitir/'Sin comprobante'/Editar ocultos, Deshacer visible", () => {
  // Regla de UX: en congelado para recibos no se ofrece emitir ni se muestra el texto
  // informativo. Editar sigue dependiendo de canEditarEliminar (acá false → oculto).
  // Deshacer (BUG 2) ya no depende de ninguno de los dos: sigue ofrecido.
  const result = resolverAccionesRecibo({
    receipt: null,
    payment: { entryType: "Payment", amount: 500 },
    congelado: true,
    canEditarEliminar: false,
  });
  assert.equal(result.emitirVisible, false, "Emitir debe ocultarse en congelado para recibos");
  assert.equal(result.sinComprobanteVisible, false, "'Sin comprobante' debe ocultarse en congelado para recibos");
  assert.equal(result.chipVisible, false);
  assert.equal(result.verPdfVisible, false);
  assert.equal(result.anularVisible, false);
  assert.equal(result.editarVisible, false);
  assert.equal(result.deshacerVisible, true, "Deshacer se ofrece siempre (BUG 2)");
});

test("sin recibo: cobro no emitible (ajuste/crédito) en normal + canEditarEliminar=false → 'Sin comprobante' visible, sin Editar, con Deshacer", () => {
  // Un cobro de tipo "Adjustment" no genera recibo aunque sea no-congelado.
  const result = resolverAccionesRecibo({
    receipt: null,
    payment: { entryType: "Adjustment", amount: 100 },
    congelado: false,
    canEditarEliminar: false,
  });
  assert.equal(result.emitirVisible, false);
  assert.equal(result.sinComprobanteVisible, true);
  assert.equal(result.editarVisible, false);
  assert.equal(result.deshacerVisible, true, "Deshacer se ofrece siempre (BUG 2)");
});

// ─── Editar cobro: SOLO gobernado por canEditarEliminar (nivel reserva) ────────

/**
 * BUG IMP-3 fix: Editar cobro se gobierna por la capacidad del backend, no por
 * `!congelado`. BUG 2 (2026-08-05): esta función YA NO representa "Editar y Deshacer
 * juntos" — solo Editar. Deshacer se prueba aparte, con la lógica real, más abajo.
 */
function muestraEditarCobro({ canEditarEliminar }) {
  return Boolean(canEditarEliminar);
}

test("editar cobro: canEditarEliminar=true → se OFRECE (visible)", () => {
  assert.equal(muestraEditarCobro({ canEditarEliminar: true }), true);
});

test("editar cobro: canEditarEliminar=false (Closed/terminal) → OCULTO (no tiene alternativa en terminal)", () => {
  assert.equal(muestraEditarCobro({ canEditarEliminar: false }), false);
});

// NOTA (sin test, a propósito — no hay ninguna función real que consuma este string hoy):
// el backend actualizó el texto de `canEditOrDeletePayment.reason` en terminal a "En este
// estado el cobro no se puede editar. Para corregirlo, deshacelo: queda registrado."
// (ReservaCapabilities.PaymentEditOnTerminalReason, 2026-08-05 — antes decía "borrar"). HOY el front no lo muestra como motivo: Editar simplemente se OCULTA en este
// caso (decisión ya existente, "no aplica" ≠ "candado fiscal fila-por-fila" — ver JSDoc de
// EditarEliminarCobro en ReservaDetailPage.jsx). Escribir un test contra un string que
// ningún código real lee sería otro "falso verde" — se documenta acá en vez de fingir cobertura.

// ─── Deshacer cobro: SIEMPRE ofrecido, en TODOS los estados — con lógica REAL ──

/**
 * BUG 2 (2026-08-05): a diferencia de Editar, "Deshacer" no tiene un flag "se ofrece
 * sí/no" — el componente real lo renderiza siempre que el caller le pase el callback
 * (`typeof onDeshacerCobro === "function"`), en los TRES estados de PaymentReceiptActions
 * y sin importar `canEditarEliminar`. Estos tests prueban eso con requiereAnularConRastro
 * (real, importada arriba): para los 4 estados terminales Y para los estados vivos, no hay
 * ningún status que deje a Deshacer sin un endpoint válido — por eso nunca se oculta por
 * estado, solo por el candado fiscal por-pago (ver el bloque siguiente).
 */
test("Deshacer: los 4 estados terminales tienen endpoint válido (/annul) — no hay motivo para ocultar el botón por estado", () => {
  for (const status of ["Closed", "Cancelled", "Lost", "PendingOperatorRefund"]) {
    assert.equal(requiereAnularConRastro(status), true, `status=${status} debe resolver a /annul`);
  }
});

test("Deshacer: los estados vivos tienen endpoint válido (DELETE) — tampoco hay motivo para ocultar el botón", () => {
  for (const status of ["Quotation", "Budget", "InManagement", "Confirmed", "Traveling"]) {
    assert.equal(requiereAnularConRastro(status), false, `status=${status} debe resolver a DELETE`);
  }
});

/**
 * Lo único que puede apagar "Deshacer" es el candado FISCAL por-pago (recibo emitido,
 * factura vinculada) — independiente del estado de la reserva. Se ejercita acá la función
 * REAL `resolverBloqueoFilaCobro` (paymentRowGuard.js), no una copia: si esa función
 * cambia, este test se entera solo. La cobertura exhaustiva de motivos/textos vive en
 * paymentRowGuard.test.mjs; acá solo se prueba la afirmación central de este archivo:
 * "Deshacer en un cobro SIN candado fiscal, en una reserva TERMINAL, no está bloqueado".
 */
test("Deshacer: cobro sin recibo ni factura, en reserva terminal (ej. Closed) → NO bloqueado por el candado fiscal", () => {
  const resultado = resolverBloqueoFilaCobro({
    canEdit: { allowed: true, reason: null },
    canDelete: { allowed: true, reason: null },
  });
  assert.equal(resultado.eliminarBloqueado, false, "Sin recibo/factura, el candado fiscal deja pasar Deshacer");
  assert.equal(resultado.motivo, null);
});

test("Deshacer: cobro vinculado a una factura (Editar permitido, Deshacer bloqueado) → motivo real del backend, tal cual (P-13)", () => {
  // Caso real donde SOLO Deshacer está bloqueado (Editar sigue permitido): el motivo que
  // se muestra es el de Deshacer, no uno inventado. Texto real vigente del backend
  // (PaymentCapabilityPolicy.DeleteBlockedByLiveInvoiceReason, actualizado 2026-08-05
  // para decir "deshacer" en vez de "eliminar").
  const motivoRealDeshacer =
    "Este cobro no se puede deshacer porque está vinculado a una factura. Generá una nota de crédito si corresponde.";
  const resultado = resolverBloqueoFilaCobro({
    canEdit: { allowed: true, reason: null },
    canDelete: { allowed: false, reason: motivoRealDeshacer },
  });
  assert.equal(resultado.eliminarBloqueado, true, "Vinculado a una factura, el candado fiscal SÍ bloquea Deshacer");
  assert.equal(resultado.motivo, motivoRealDeshacer, "El motivo se muestra tal cual lo manda el backend (P-13)");
});

// ─── Réplica: botones de escritura en vouchers (Zona C) ───────────────────────

/**
 * Réplica del gating soloLectura en ReservaVoucherTab.
 * Devuelve qué botones son visibles para un voucher dado.
 */
function resolverBotonesVoucher({ voucher, soloLectura, esAdmin, tienePermisoRevoke, esSupervisor }) {
  return {
    // Ver y Descargar siempre visibles (son documentos ya emitidos).
    verVisible: true,
    descargarVisible: true,
    // Los siguientes solo si no es soloLectura:
    editarVisible: !soloLectura && Boolean(voucher.externalOrigin) && voucher.status !== "Revoked",
    emitirVisible: !soloLectura && voucher.status === "Draft",
    aprobarVisible: !soloLectura && voucher.status === "PendingAuthorization" && (esAdmin || esSupervisor),
    rechazarVisible: !soloLectura && voucher.status === "PendingAuthorization" && (esAdmin || esSupervisor),
    anularVisible: !soloLectura && voucher.status !== "Revoked" && tienePermisoRevoke,
    // "Agregar documento" se evalúa a nivel de tab, no de voucher, pero la lógica es la misma.
    aniadirVisible: !soloLectura,
  };
}

test("vouchers: Ver y Descargar siempre visibles en soloLectura", () => {
  const result = resolverBotonesVoucher({
    voucher: { status: "Issued", externalOrigin: null },
    soloLectura: true,
    esAdmin: false,
    tienePermisoRevoke: true,
    esSupervisor: false,
  });
  assert.equal(result.verVisible, true);
  assert.equal(result.descargarVisible, true);
});

test("vouchers: en soloLectura se ocultan Emitir, Aprobar, Rechazar, Anular, Añadir", () => {
  const result = resolverBotonesVoucher({
    voucher: { status: "Draft", externalOrigin: null },
    soloLectura: true,
    esAdmin: true,
    tienePermisoRevoke: true,
    esSupervisor: false,
  });
  assert.equal(result.emitirVisible, false, "Emitir debe ocultarse en soloLectura");
  assert.equal(result.anularVisible, false, "Anular debe ocultarse en soloLectura");
  assert.equal(result.aniadirVisible, false, "Añadir debe ocultarse en soloLectura");
});

test("vouchers: Editar (externo) oculto en soloLectura", () => {
  const result = resolverBotonesVoucher({
    voucher: { status: "Issued", externalOrigin: "Operador ABC" },
    soloLectura: true,
    esAdmin: false,
    tienePermisoRevoke: false,
    esSupervisor: false,
  });
  assert.equal(result.editarVisible, false, "Editar debe ocultarse en soloLectura");
  assert.equal(result.verVisible, true);
});

test("vouchers: en modo normal, Emitir visible para Draft", () => {
  const result = resolverBotonesVoucher({
    voucher: { status: "Draft", externalOrigin: null },
    soloLectura: false,
    esAdmin: false,
    tienePermisoRevoke: false,
    esSupervisor: false,
  });
  assert.equal(result.emitirVisible, true);
});

test("vouchers: Aprobar/Rechazar visibles para PendingAuthorization + es supervisor", () => {
  const result = resolverBotonesVoucher({
    voucher: { status: "PendingAuthorization", externalOrigin: null },
    soloLectura: false,
    esAdmin: false,
    tienePermisoRevoke: false,
    esSupervisor: true,
  });
  assert.equal(result.aprobarVisible, true);
  assert.equal(result.rechazarVisible, true);
});

test("vouchers: Aprobar/Rechazar OCULTOS en soloLectura aunque sea supervisor", () => {
  const result = resolverBotonesVoucher({
    voucher: { status: "PendingAuthorization", externalOrigin: null },
    soloLectura: true,
    esAdmin: true,
    tienePermisoRevoke: true,
    esSupervisor: true,
  });
  assert.equal(result.aprobarVisible, false);
  assert.equal(result.rechazarVisible, false);
});

// ─── Réplica: banner "Pedí autorización" (Cambio 2) ──────────────────────────

/**
 * La franja ámbar "Pedí autorización" solo debe aparecer cuando el status
 * es exactamente "Confirmed". En Traveling y Closed el vendedor ya tiene
 * el cartel de solo-lectura de arriba; el banner no agrega nada.
 *
 * NOTA: no se toca isStatusLocked global (sigue siendo true en Traveling/Closed
 * para bloquear edición en otros componentes como ReservaHeader). El cambio es
 * solo en qué se le pasa al ReservaLockBanner.
 */
function calcularIsLockedParaBanner(status) {
  // Decisión UX 2026-06-22: el banner ámbar es solo para "Confirmada".
  return status === "Confirmed";
}

test("banner lock: Confirmed → true (muestra franja ámbar con 'Pedí autorización')", () => {
  assert.equal(calcularIsLockedParaBanner("Confirmed"), true);
});

test("banner lock: Traveling → false (no muestra franja ámbar)", () => {
  // En Traveling ya hay cartel de solo-lectura arriba; el banner no se necesita.
  assert.equal(calcularIsLockedParaBanner("Traveling"), false);
});

test("banner lock: Closed → false (no muestra franja ámbar)", () => {
  // En Closed ya hay cartel de solo-lectura; el banner no se necesita.
  assert.equal(calcularIsLockedParaBanner("Closed"), false);
});

test("banner lock: InManagement → false (no está bloqueada, no necesita franja)", () => {
  assert.equal(calcularIsLockedParaBanner("InManagement"), false);
});

test("banner lock: Lost → false (estado terminal, no tiene sentido pedir autorización)", () => {
  assert.equal(calcularIsLockedParaBanner("Lost"), false);
});

test("banner lock: Cancelled → false (anulada, no tiene sentido pedir autorización)", () => {
  assert.equal(calcularIsLockedParaBanner("Cancelled"), false);
});

test("banner lock: Budget → false (etapa temprana, no está bloqueada)", () => {
  assert.equal(calcularIsLockedParaBanner("Budget"), false);
});
