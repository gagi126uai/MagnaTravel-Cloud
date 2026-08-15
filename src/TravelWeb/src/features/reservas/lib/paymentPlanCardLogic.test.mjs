import { test } from "node:test";
import assert from "node:assert/strict";
import {
  MAX_FILAS_PLAN_DE_PAGOS,
  armarPayloadPlanDePagos,
  crearFilaVacia,
  filasDesdeInstallments,
  filasEstanCompletas,
  filasExcedenElMaximo,
  filasFueronEditadas,
  resolverMonedaPorDefectoDelPlan,
} from "./paymentPlanCardLogic.js";

test("crearFilaVacia: arranca vacía con la moneda por defecto recibida", () => {
  const fila = crearFilaVacia("k1", "USD");
  assert.deepEqual(fila, { key: "k1", dueText: "", amount: "", currency: "USD" });
});

test("crearFilaVacia: sin moneda por defecto, cae a ARS", () => {
  const fila = crearFilaVacia("k1", null);
  assert.equal(fila.currency, "ARS");
});

test("resolverMonedaPorDefectoDelPlan: usa la primera línea de porMoneda si existe", () => {
  const reserva = { porMoneda: [{ currency: "USD" }, { currency: "ARS" }] };
  assert.equal(resolverMonedaPorDefectoDelPlan(reserva), "USD");
});

test("resolverMonedaPorDefectoDelPlan: sin porMoneda, cae a ARS", () => {
  assert.equal(resolverMonedaPorDefectoDelPlan({}), "ARS");
  assert.equal(resolverMonedaPorDefectoDelPlan(null), "ARS");
  assert.equal(resolverMonedaPorDefectoDelPlan({ porMoneda: [] }), "ARS");
});

test("filasDesdeInstallments: mapea position/dueText/amount/currency del DTO al shape de pantalla", () => {
  const installments = [
    { position: 1, dueText: "Al confirmar", amount: 500, currency: "USD" },
    { position: 2, dueText: "Antes de viajar", amount: 300.5, currency: "ARS" },
  ];
  const filas = filasDesdeInstallments(installments);
  assert.deepEqual(filas, [
    { key: "plan-1", dueText: "Al confirmar", amount: "500", currency: "USD" },
    { key: "plan-2", dueText: "Antes de viajar", amount: "300.5", currency: "ARS" },
  ]);
});

test("filasDesdeInstallments: null/undefined -> lista vacía, no rompe", () => {
  assert.deepEqual(filasDesdeInstallments(null), []);
  assert.deepEqual(filasDesdeInstallments(undefined), []);
});

test("filasFueronEditadas: false cuando el contenido es igual (key distinto no cuenta)", () => {
  const precargadas = [{ key: "plan-1", dueText: "Al confirmar", amount: "500", currency: "USD" }];
  const actuales = [{ key: "otra-key", dueText: "Al confirmar", amount: "500", currency: "USD" }];
  assert.equal(filasFueronEditadas(actuales, precargadas), false);
});

test("filasFueronEditadas: true cuando cambia el texto, el monto o la moneda", () => {
  const precargadas = [{ key: "plan-1", dueText: "Al confirmar", amount: "500", currency: "USD" }];
  assert.equal(
    filasFueronEditadas([{ key: "plan-1", dueText: "Al confirmar la reserva", amount: "500", currency: "USD" }], precargadas),
    true
  );
  assert.equal(
    filasFueronEditadas([{ key: "plan-1", dueText: "Al confirmar", amount: "600", currency: "USD" }], precargadas),
    true
  );
  assert.equal(
    filasFueronEditadas([{ key: "plan-1", dueText: "Al confirmar", amount: "500", currency: "ARS" }], precargadas),
    true
  );
});

test("filasFueronEditadas: true cuando se agrega o borra una fila", () => {
  const precargadas = [{ key: "plan-1", dueText: "Al confirmar", amount: "500", currency: "USD" }];
  assert.equal(filasFueronEditadas([], precargadas), true);
  assert.equal(
    filasFueronEditadas(
      [...precargadas, { key: "nueva-1", dueText: "Antes de viajar", amount: "200", currency: "ARS" }],
      precargadas
    ),
    true
  );
});

test("filasEstanCompletas: una lista vacía cuenta como completa (borrar todo el plan es válido)", () => {
  assert.equal(filasEstanCompletas([]), true);
  assert.equal(filasEstanCompletas(null), true);
});

test("filasEstanCompletas: false si a alguna fila le falta el texto de 'cuándo'", () => {
  const filas = [{ key: "k1", dueText: "  ", amount: "500", currency: "ARS" }];
  assert.equal(filasEstanCompletas(filas), false);
});

test("filasEstanCompletas: false si el monto es 0 o negativo (backend exige > 0)", () => {
  assert.equal(filasEstanCompletas([{ key: "k1", dueText: "Al confirmar", amount: "0", currency: "ARS" }]), false);
  assert.equal(filasEstanCompletas([{ key: "k1", dueText: "Al confirmar", amount: "-5", currency: "ARS" }]), false);
});

test("filasEstanCompletas: true cuando todas las filas tienen texto y monto positivo", () => {
  const filas = [
    { key: "k1", dueText: "Al confirmar", amount: "500", currency: "ARS" },
    { key: "k2", dueText: "Antes de viajar", amount: "300", currency: "USD" },
  ];
  assert.equal(filasEstanCompletas(filas), true);
});

test("filasExcedenElMaximo: respeta el tope de 24 filas del backend", () => {
  const filaTipo = { key: "k", dueText: "x", amount: "1", currency: "ARS" };
  const veinticuatro = Array.from({ length: MAX_FILAS_PLAN_DE_PAGOS }, () => filaTipo);
  const veinticinco = Array.from({ length: MAX_FILAS_PLAN_DE_PAGOS + 1 }, () => filaTipo);
  assert.equal(filasExcedenElMaximo(veinticuatro), false);
  assert.equal(filasExcedenElMaximo(veinticinco), true);
});

test("armarPayloadPlanDePagos: arma el body del PUT, recorta texto y castea el monto a número", () => {
  const filas = [
    { key: "k1", dueText: "  Al confirmar  ", amount: "500", currency: "USD" },
    { key: "k2", dueText: "Antes de viajar", amount: "300.5", currency: "ARS" },
  ];
  const payload = armarPayloadPlanDePagos(filas);
  assert.deepEqual(payload, {
    installments: [
      { dueText: "Al confirmar", amount: 500, currency: "USD" },
      { dueText: "Antes de viajar", amount: 300.5, currency: "ARS" },
    ],
  });
});

test("armarPayloadPlanDePagos: lista vacía -> installments vacío (borra el plan, spec §6)", () => {
  assert.deepEqual(armarPayloadPlanDePagos([]), { installments: [] });
});
