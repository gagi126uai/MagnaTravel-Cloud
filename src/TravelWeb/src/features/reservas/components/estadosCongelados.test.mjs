/**
 * Tests de lógica pura para los dos cambios de UI del 2026-06-22:
 *
 * Cambio 1 — Comprobantes: en estado congelado solo se puede VER un comprobante
 *   ya emitido. Las acciones de escritura (Emitir, Anular comprobante, Editar cobro,
 *   Eliminar cobro) desaparecen.
 *
 * Cambio 2 — Banner "Pedí autorización": solo debe aparecer en "Confirmada".
 *   En Traveling y Closed el vendedor ya tiene el cartel de solo-lectura;
 *   el banner ámbar no aporta nada.
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/estadosCongelados.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica del helper esEstadoCongelado (ReservaDetailPage.jsx) ─────────────

/**
 * Un estado es "congelado" cuando:
 *  - La reserva ya arrancó (Traveling) → el viaje está en curso.
 *  - Está perdida (Lost) o anulada (Cancelled) → proceso cerrado.
 *  - Ya está completamente facturada (FullyInvoiced) → no se emiten más documentos.
 *
 * NO es congelado: Confirmed, InManagement, Budget, Quotation, Closed, etc.
 * (en Closed todavía se puede facturar, por eso no es congelado).
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

// ── Estados que SÍ son congelados ─────────────────────────────────────────────

test("congelado: Traveling → true (viaje en curso, solo lectura)", () => {
  assert.equal(esEstadoCongelado({ status: "Traveling", invoicingStatus: "NotInvoiced" }), true);
});

test("congelado: Lost → true (cerrada sin cobro)", () => {
  assert.equal(esEstadoCongelado({ status: "Lost", invoicingStatus: "NotInvoiced" }), true);
});

test("congelado: Cancelled → true (anulada formalmente)", () => {
  assert.equal(esEstadoCongelado({ status: "Cancelled", invoicingStatus: "NotInvoiced" }), true);
});

test("congelado: PendingOperatorRefund → true (anulada esperando reembolso, decisión 2026-06-22)", () => {
  assert.equal(esEstadoCongelado({ status: "PendingOperatorRefund", invoicingStatus: "NotInvoiced" }), true);
});

test("congelado: FullyInvoiced → true sin importar el status operativo", () => {
  // Una reserva Confirmed pero ya con facturación completa no puede emitir más.
  assert.equal(esEstadoCongelado({ status: "Confirmed", invoicingStatus: "FullyInvoiced" }), true);
});

test("congelado: Closed + FullyInvoiced → true", () => {
  assert.equal(esEstadoCongelado({ status: "Closed", invoicingStatus: "FullyInvoiced" }), true);
});

// ── Estados que NO son congelados ─────────────────────────────────────────────

test("no congelado: Confirmed con factura parcial → false (puede emitir más)", () => {
  assert.equal(esEstadoCongelado({ status: "Confirmed", invoicingStatus: "PartiallyInvoiced" }), false);
});

test("no congelado: Confirmed sin facturar → false", () => {
  assert.equal(esEstadoCongelado({ status: "Confirmed", invoicingStatus: "NotInvoiced" }), false);
});

test("no congelado: InManagement → false", () => {
  assert.equal(esEstadoCongelado({ status: "InManagement", invoicingStatus: "NotInvoiced" }), false);
});

test("no congelado: Budget → false", () => {
  assert.equal(esEstadoCongelado({ status: "Budget", invoicingStatus: "NotInvoiced" }), false);
});

test("no congelado: Quotation → false", () => {
  assert.equal(esEstadoCongelado({ status: "Quotation", invoicingStatus: "NotInvoiced" }), false);
});

test("no congelado: Closed sin factura → false (puede facturar desde Finalizada, ADR-037)", () => {
  // En Closed todavía se puede emitir factura (desacople de facturación ADR-037).
  assert.equal(esEstadoCongelado({ status: "Closed", invoicingStatus: "NotInvoiced" }), false);
});

test("no congelado: reserva null → false (degradación elegante)", () => {
  assert.equal(esEstadoCongelado(null), false);
  assert.equal(esEstadoCongelado(undefined), false);
});

// ─── Réplica: lógica de PaymentReceiptActions con prop congelado ───────────────

/**
 * Réplica de la lógica de visibilidad dentro de PaymentReceiptActions.
 * Devuelve un objeto con qué elementos se muestran para un cobro dado.
 */
function resolverAccionesRecibo({ receipt, payment, congelado }) {
  const tieneRecibo = Boolean(receipt);
  const estaAnulado = receipt?.status === "Voided";
  const puedeEmitir = !tieneRecibo &&
    (payment?.entryType === "Payment") &&
    Number(payment?.amount || 0) > 0;

  if (tieneRecibo) {
    return {
      // El chip (número o "Comprobante anulado") siempre visible: es trazabilidad.
      chipVisible: true,
      // Ver PDF: visible solo si el recibo no está anulado.
      verPdfVisible: !estaAnulado,
      // Anular comprobante: solo si no anulado Y no congelado.
      anularVisible: !estaAnulado && !congelado,
      // Emitir: no aplica (ya tiene recibo).
      emitirVisible: false,
      // "Sin comprobante": no aplica.
      sinComprobanteVisible: false,
    };
  }

  // Sin recibo y congelado: nada se muestra (ni "Sin comprobante").
  if (congelado) {
    return {
      chipVisible: false,
      verPdfVisible: false,
      anularVisible: false,
      emitirVisible: false,
      sinComprobanteVisible: false,
    };
  }

  // Sin recibo y no congelado: se puede emitir (si el cobro lo permite).
  return {
    chipVisible: false,
    verPdfVisible: false,
    anularVisible: false,
    emitirVisible: puedeEmitir,
    sinComprobanteVisible: !puedeEmitir,
  };
}

// ── Con recibo vigente ─────────────────────────────────────────────────────────

test("recibo: con recibo vigente en normal → chip + Ver PDF + Anular visibles", () => {
  const result = resolverAccionesRecibo({
    receipt: { status: "Issued", receiptNumber: "R-001" },
    payment: { entryType: "Payment", amount: 1000 },
    congelado: false,
  });
  assert.equal(result.chipVisible, true);
  assert.equal(result.verPdfVisible, true);
  assert.equal(result.anularVisible, true);
  assert.equal(result.emitirVisible, false);
});

test("recibo: con recibo vigente en CONGELADO → chip + Ver PDF visibles, Anular OCULTO", () => {
  // Decisión UX 2026-06-22: "ver/imprimir un papel ya hecho" sí; "anular" no.
  const result = resolverAccionesRecibo({
    receipt: { status: "Issued", receiptNumber: "R-001" },
    payment: { entryType: "Payment", amount: 1000 },
    congelado: true,
  });
  assert.equal(result.chipVisible, true);
  assert.equal(result.verPdfVisible, true);
  assert.equal(result.anularVisible, false, "Anular debe ocultarse en congelado");
  assert.equal(result.emitirVisible, false);
});

test("recibo: comprobante ya anulado en CONGELADO → solo chip visible (sin Ver PDF ni Anular)", () => {
  const result = resolverAccionesRecibo({
    receipt: { status: "Voided", receiptNumber: "R-002" },
    payment: { entryType: "Payment", amount: 1000 },
    congelado: true,
  });
  assert.equal(result.chipVisible, true);
  assert.equal(result.verPdfVisible, false);
  assert.equal(result.anularVisible, false);
});

// ── Sin recibo ─────────────────────────────────────────────────────────────────

test("sin recibo: cobro emitible en normal → botón Emitir visible", () => {
  const result = resolverAccionesRecibo({
    receipt: null,
    payment: { entryType: "Payment", amount: 500 },
    congelado: false,
  });
  assert.equal(result.emitirVisible, true);
  assert.equal(result.sinComprobanteVisible, false);
  assert.equal(result.chipVisible, false);
});

test("sin recibo: cobro emitible en CONGELADO → nada visible (ni Emitir ni 'Sin comprobante')", () => {
  // Regla de UX: en congelado no se ofrece emitir ni se muestra el texto informativo.
  const result = resolverAccionesRecibo({
    receipt: null,
    payment: { entryType: "Payment", amount: 500 },
    congelado: true,
  });
  assert.equal(result.emitirVisible, false, "Emitir debe ocultarse en congelado");
  assert.equal(result.sinComprobanteVisible, false, "'Sin comprobante' debe ocultarse en congelado");
  assert.equal(result.chipVisible, false);
  assert.equal(result.verPdfVisible, false);
  assert.equal(result.anularVisible, false);
});

test("sin recibo: cobro no emitible (ajuste/crédito) en normal → 'Sin comprobante' visible", () => {
  // Un cobro de tipo "Adjustment" no genera recibo aunque sea no-congelado.
  const result = resolverAccionesRecibo({
    receipt: null,
    payment: { entryType: "Adjustment", amount: 100 },
    congelado: false,
  });
  assert.equal(result.emitirVisible, false);
  assert.equal(result.sinComprobanteVisible, true);
});

// ─── Réplica: visibilidad de la columna "Acciones" (Editar/Eliminar cobro) ────

/**
 * Los botones Editar cobro y Eliminar cobro mueven plata → solo en no-congelado.
 */
function muestraColumnAccionesCobro(congelado) {
  return !congelado;
}

test("columna acciones cobro: no congelado → visible", () => {
  assert.equal(muestraColumnAccionesCobro(false), true);
});

test("columna acciones cobro: congelado → oculta", () => {
  assert.equal(muestraColumnAccionesCobro(true), false);
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
    // "Añadir Documento" se evalúa a nivel de tab, no de voucher, pero la lógica es la misma.
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
