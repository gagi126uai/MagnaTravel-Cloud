/**
 * Tests de lógica pura para la Tanda P2 "circuito proveedor" (2026-07-22): el bloque
 * "Reembolsos ya registrados" y sus dos acciones, Deshacer y Corregir reserva.
 *
 * Cubre:
 *   - validarMotivoAccionReembolso / puedeConfirmarDeshacer / puedeConfirmarCorregir:
 *     el motor exige mínimo 20 caracteres en los dos endpoints (VoidAllocationRequest y
 *     ReassociateAllocationRequest).
 *   - construirPayloadDeshacer / construirPayloadCorregir: el body exacto que espera cada
 *     endpoint del motor (mismo criterio que supplierPageLogic.test.mjs para
 *     construirPayloadPagoProveedor — este proyecto no mockea fetch, testea el
 *     payload-builder que alimenta al wrapper de la API).
 *   - filtrarDestinosParaCorregir: P3=A de la spec — lista filtrada a la misma moneda,
 *     excluyendo la reserva a la que ya está imputado el reembolso.
 *   - esErrorCreditoYaUsado: detección del código REFUND_CREDIT_ALREADY_USED (P4=B).
 *
 * Cómo correr:
 *   node --test src/features/suppliers/lib/operatorRefundRegisteredLogic.test.mjs
 */

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  MOTIVO_ACCION_REEMBOLSO_MIN,
  CODIGO_CREDITO_YA_USADO,
  validarMotivoAccionReembolso,
  puedeConfirmarDeshacer,
  puedeConfirmarCorregir,
  construirPayloadDeshacer,
  construirPayloadCorregir,
  filtrarDestinosParaCorregir,
  esErrorCreditoYaUsado,
  hayMontosEnmascarados,
} from "./operatorRefundRegisteredLogic.js";

// ─── validarMotivoAccionReembolso ──────────────────────────────────────────────

describe("validarMotivoAccionReembolso", () => {
  it("rechaza motivo vacío", () => {
    const error = validarMotivoAccionReembolso("");
    assert.ok(error);
    assert.ok(error.includes(String(MOTIVO_ACCION_REEMBOLSO_MIN)));
  });

  it("rechaza motivo de 19 caracteres (uno menos del mínimo)", () => {
    const motivo19 = "a".repeat(19);
    const error = validarMotivoAccionReembolso(motivo19);
    assert.ok(error);
  });

  it("acepta motivo de exactamente 20 caracteres", () => {
    const motivo20 = "a".repeat(20);
    assert.equal(validarMotivoAccionReembolso(motivo20), null);
  });

  it("acepta motivo largo válido", () => {
    const error = validarMotivoAccionReembolso("El monto se cargó mal, correspondía a otra reserva del mismo operador.");
    assert.equal(error, null);
  });

  it("descuenta espacios alrededor para el mínimo (motivo con solo espacios cuenta como vacío)", () => {
    const error = validarMotivoAccionReembolso("                    "); // 20 espacios, trim() -> ""
    assert.ok(error);
  });

  it("rechaza motivo que supera el máximo (500 caracteres)", () => {
    const motivoMuyLargo = "a".repeat(501);
    const error = validarMotivoAccionReembolso(motivoMuyLargo);
    assert.ok(error);
    assert.ok(error.toLowerCase().includes("superar"));
  });
});

// ─── puedeConfirmarDeshacer: habilita el botón "Deshacer reembolso" ────────────

describe("puedeConfirmarDeshacer", () => {
  it("false con motivo corto", () => {
    assert.equal(puedeConfirmarDeshacer({ motivo: "muy corto", submitting: false }), false);
  });

  it("true con motivo válido y no está enviando", () => {
    assert.equal(
      puedeConfirmarDeshacer({ motivo: "a".repeat(25), submitting: false }),
      true
    );
  });

  it("false mientras está enviando, aunque el motivo sea válido (anti doble-click)", () => {
    assert.equal(
      puedeConfirmarDeshacer({ motivo: "a".repeat(25), submitting: true }),
      false
    );
  });
});

// ─── puedeConfirmarCorregir: habilita el botón "Mover a la reserva #N" ─────────

describe("puedeConfirmarCorregir", () => {
  const destinoValido = { key: "bc-1-USD", bookingCancellationPublicId: "bc-1", numeroReserva: "1058" };

  it("false sin destino elegido, aunque el motivo sea válido", () => {
    assert.equal(
      puedeConfirmarCorregir({ destinoElegido: null, motivo: "a".repeat(25), submitting: false }),
      false
    );
  });

  it("false con destino elegido pero motivo corto", () => {
    assert.equal(
      puedeConfirmarCorregir({ destinoElegido: destinoValido, motivo: "corto", submitting: false }),
      false
    );
  });

  it("true con destino elegido y motivo válido", () => {
    assert.equal(
      puedeConfirmarCorregir({ destinoElegido: destinoValido, motivo: "a".repeat(25), submitting: false }),
      true
    );
  });

  it("false mientras está enviando (anti doble-click)", () => {
    assert.equal(
      puedeConfirmarCorregir({ destinoElegido: destinoValido, motivo: "a".repeat(25), submitting: true }),
      false
    );
  });
});

// ─── construirPayloadDeshacer: body de DELETE .../allocations/{id} ────────────

describe("construirPayloadDeshacer", () => {
  it("produce { reason } con el motivo trimmeado", () => {
    const payload = construirPayloadDeshacer("  Se cargó con el monto equivocado  ");
    assert.deepEqual(Object.keys(payload), ["reason"]);
    assert.equal(payload.reason, "Se cargó con el monto equivocado");
  });
});

// ─── construirPayloadCorregir: body de PATCH .../allocations/{id}/reassociate ─

describe("construirPayloadCorregir", () => {
  it("produce { newBookingCancellationPublicId, reason } con el destino y el motivo trimmeado", () => {
    const destino = { bookingCancellationPublicId: "bc-1058", numeroReserva: "1058" };
    const payload = construirPayloadCorregir(destino, "  Estaba imputado a la reserva equivocada  ");
    assert.deepEqual(Object.keys(payload).sort(), ["newBookingCancellationPublicId", "reason"]);
    assert.equal(payload.newBookingCancellationPublicId, "bc-1058");
    assert.equal(payload.reason, "Estaba imputado a la reserva equivocada");
  });

  it("con destino null no revienta: manda newBookingCancellationPublicId vacío (el botón ya está deshabilitado en ese caso)", () => {
    const payload = construirPayloadCorregir(null, "a".repeat(25));
    assert.equal(payload.newBookingCancellationPublicId, "");
  });
});

// ─── filtrarDestinosParaCorregir: P3=A — lista filtrada por moneda y reserva ───

describe("filtrarDestinosParaCorregir", () => {
  // Fixture con la forma cruda que devuelve getPendingBySupplier (antes de aplanar).
  const itemsPendientes = [
    {
      bookingCancellationPublicId: "bc-1051",
      reservaPublicId: "reserva-1051",
      numeroReserva: "1051",
      clienteNombre: "Pérez",
      amountsMasked: false,
      canRegisterRefund: true,
      estimatedRefundsByCurrency: [
        { currency: "USD", estimatedAmount: 400, paidToOperator: 500, penaltyRetained: 100, amountReceived: 0, zeroRefundReason: null },
      ],
    },
    {
      bookingCancellationPublicId: "bc-1058",
      reservaPublicId: "reserva-1058",
      numeroReserva: "1058",
      clienteNombre: "Ruiz",
      amountsMasked: false,
      canRegisterRefund: true,
      estimatedRefundsByCurrency: [
        { currency: "USD", estimatedAmount: 400, paidToOperator: 400, penaltyRetained: 0, amountReceived: 0, zeroRefundReason: null },
        { currency: "ARS", estimatedAmount: 80000, paidToOperator: 80000, penaltyRetained: 0, amountReceived: 0, zeroRefundReason: null },
      ],
    },
    {
      // Reserva ya imputada actualmente (la del reembolso que se está corrigiendo): debe excluirse.
      bookingCancellationPublicId: "bc-1042",
      reservaPublicId: "reserva-1042",
      numeroReserva: "1042",
      clienteNombre: "Fam. García",
      amountsMasked: false,
      canRegisterRefund: true,
      estimatedRefundsByCurrency: [
        { currency: "USD", estimatedAmount: 400, paidToOperator: 500, penaltyRetained: 100, amountReceived: 0, zeroRefundReason: null },
      ],
    },
  ];

  it("deja solo la moneda pedida, excluyendo la reserva actual", () => {
    const destinos = filtrarDestinosParaCorregir(itemsPendientes, {
      currency: "USD",
      reservaPublicIdActual: "reserva-1042",
    });
    const reservas = destinos.map((d) => d.numeroReserva).sort();
    assert.deepEqual(reservas, ["1051", "1058"]);
  });

  it("filtra también por moneda: una reserva con USD y ARS solo aparece una vez para ARS", () => {
    const destinos = filtrarDestinosParaCorregir(itemsPendientes, {
      currency: "ARS",
      reservaPublicIdActual: "reserva-1042",
    });
    assert.equal(destinos.length, 1);
    assert.equal(destinos[0].numeroReserva, "1058");
    assert.equal(destinos[0].currency, "ARS");
  });

  it("sin ninguna anulación pendiente en esa moneda, devuelve lista vacía (spec: cartel 'No hay otra reserva...')", () => {
    const destinos = filtrarDestinosParaCorregir(itemsPendientes, {
      currency: "EUR",
      reservaPublicIdActual: "reserva-1042",
    });
    assert.equal(destinos.length, 0);
  });

  it("lista vacía de pendientes no revienta", () => {
    const destinos = filtrarDestinosParaCorregir([], { currency: "USD", reservaPublicIdActual: "reserva-1042" });
    assert.deepEqual(destinos, []);
  });
});

// ─── esErrorCreditoYaUsado: P4=B — botón "Ir a la cuenta del cliente" ──────────

describe("esErrorCreditoYaUsado", () => {
  it("true cuando el 409 trae exactamente el código REFUND_CREDIT_ALREADY_USED", () => {
    const error = { status: 409, payload: { message: "No se puede anular...", code: CODIGO_CREDITO_YA_USADO } };
    assert.equal(esErrorCreditoYaUsado(error), true);
  });

  it("false con otro código de negocio (no confunde casos distintos)", () => {
    const error = { status: 409, payload: { message: "Ya estaba deshecho", code: "ALGO_DISTINTO" } };
    assert.equal(esErrorCreditoYaUsado(error), false);
  });

  it("false cuando el 409 no trae code (mensaje de negocio genérico, sin caso especial)", () => {
    const error = { status: 409, payload: { message: "La cancelación fue modificada por otra operación." } };
    assert.equal(esErrorCreditoYaUsado(error), false);
  });

  it("false si el status no es 409 (nunca confía solo en el código, aunque coincida)", () => {
    const error = { status: 500, payload: { code: CODIGO_CREDITO_YA_USADO } };
    assert.equal(esErrorCreditoYaUsado(error), false);
  });

  it("false con error null/undefined (no revienta)", () => {
    assert.equal(esErrorCreditoYaUsado(null), false);
    assert.equal(esErrorCreditoYaUsado(undefined), false);
  });
});

// ─── hayMontosEnmascarados: fix de review B1 — aviso único del bloque "ya registrados" ─

describe("hayMontosEnmascarados", () => {
  it("true si ALGÚN item viene con amountsMasked=true (sin cobranzas.see_cost)", () => {
    const items = [
      { publicId: "a", amountsMasked: false },
      { publicId: "b", amountsMasked: true },
    ];
    assert.equal(hayMontosEnmascarados(items), true);
  });

  it("false si NINGÚN item está enmascarado", () => {
    const items = [
      { publicId: "a", amountsMasked: false },
      { publicId: "b", amountsMasked: false },
    ];
    assert.equal(hayMontosEnmascarados(items), false);
  });

  it("false con lista vacía (no hay nada que explicar)", () => {
    assert.equal(hayMontosEnmascarados([]), false);
  });

  it("false con null/undefined (no revienta, ej. mientras todavía está cargando)", () => {
    assert.equal(hayMontosEnmascarados(null), false);
    assert.equal(hayMontosEnmascarados(undefined), false);
  });

  it("un solo item deshecho y enmascarado también dispara el aviso (la fila se sigue viendo, solo el monto se oculta)", () => {
    const items = [{ publicId: "a", isVoided: true, amountsMasked: true }];
    assert.equal(hayMontosEnmascarados(items), true);
  });
});
