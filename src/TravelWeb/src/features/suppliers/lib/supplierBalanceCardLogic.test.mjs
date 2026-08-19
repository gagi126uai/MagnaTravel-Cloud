import { test } from "node:test";
import assert from "node:assert/strict";
import { construirFotoDeSaldoOperador, resolverPalabraSaldoOperador } from "./supplierBalanceCardLogic.js";

test("saldo positivo (la agencia le debe al operador): tono rose, palabra 'Le debés'", () => {
  const [tarjeta] = construirFotoDeSaldoOperador(
    [{ currency: "ARS", iTheyOwe: 300000, theyOweMe: 0, prepayment: 0, economicClosingBalance: 300000 }],
    true
  );
  assert.equal(tarjeta.tono, "rose");
  assert.equal(tarjeta.palabra, "Le debés");
  assert.match(tarjeta.montoTexto, /300/);
});

test("saldo negativo (a favor de la agencia): tono emerald, palabra 'A favor'", () => {
  const [tarjeta] = construirFotoDeSaldoOperador(
    [{ currency: "ARS", iTheyOwe: 0, theyOweMe: 0, prepayment: 50000, economicClosingBalance: -50000 }],
    true
  );
  assert.equal(tarjeta.tono, "emerald");
  assert.equal(tarjeta.palabra, "A favor");
});

test("saldo en cero: tono neutral, palabra 'Al día'", () => {
  const [tarjeta] = construirFotoDeSaldoOperador(
    [{ currency: "ARS", iTheyOwe: 0, theyOweMe: 0, prepayment: 0, economicClosingBalance: 0 }],
    true
  );
  assert.equal(tarjeta.tono, "neutral");
  assert.equal(tarjeta.palabra, "Al día");
});

test("un centavo de diferencia por redondeo no dispara 'Le debés'/'A favor' (tolerancia EPS)", () => {
  const [tarjeta] = construirFotoDeSaldoOperador([{ currency: "ARS", economicClosingBalance: 0.004 }], true);
  assert.equal(tarjeta.tono, "neutral");
  assert.equal(tarjeta.palabra, "Al día");
});

test("sin permiso cobranzas.see_cost: toda la tarjeta va en gris con guion, sin filtrar montos reales", () => {
  const [tarjeta] = construirFotoDeSaldoOperador(
    [{ currency: "ARS", iTheyOwe: 999999, theyOweMe: 999999, prepayment: 999999, economicClosingBalance: 999999 }],
    false
  );
  assert.equal(tarjeta.tono, "neutral");
  assert.equal(tarjeta.montoTexto, "—");
  assert.equal(tarjeta.palabra, "—");
  for (const fila of tarjeta.filas) {
    assert.equal(fila.montoTexto, "—");
    assert.equal(fila.tono, "neutral");
  }
});

test("las 3 filas del desglose usan el glosario firmado (etiqueta) y el tono correcto", () => {
  const [tarjeta] = construirFotoDeSaldoOperador(
    [{ currency: "ARS", iTheyOwe: 100, theyOweMe: 200, prepayment: 300, economicClosingBalance: -100 }],
    true
  );
  const porClave = Object.fromEntries(tarjeta.filas.map((f) => [f.clave, f]));

  assert.equal(porClave.facturasPorPagar.etiqueta, "Facturas por pagar");
  assert.equal(porClave.facturasPorPagar.tono, "neutral");

  assert.equal(porClave.teTieneQueDevolver.etiqueta, "Te tiene que devolver");
  assert.equal(porClave.teTieneQueDevolver.tono, "amber");

  assert.equal(porClave.saldoAFavorTuyo.etiqueta, "Saldo a favor tuyo");
  assert.equal(porClave.saldoAFavorTuyo.tono, "emerald");
});

test("ordena pesos primero, dólares después, cuando hay varias monedas", () => {
  const tarjetas = construirFotoDeSaldoOperador(
    [
      { currency: "USD", economicClosingBalance: 100 },
      { currency: "ARS", economicClosingBalance: 100 },
    ],
    true
  );
  assert.deepEqual(tarjetas.map((t) => t.currency), ["ARS", "USD"]);
});

test("cae a closingBalance cuando economicClosingBalance no viaja (mismo invariante de backend)", () => {
  const [tarjeta] = construirFotoDeSaldoOperador([{ currency: "ARS", closingBalance: 500 }], true);
  assert.equal(tarjeta.tono, "rose");
});

test("resolverPalabraSaldoOperador cubre los 3 tonos del glosario del operador", () => {
  assert.equal(resolverPalabraSaldoOperador("rose"), "Le debés");
  assert.equal(resolverPalabraSaldoOperador("emerald"), "A favor");
  assert.equal(resolverPalabraSaldoOperador("neutral"), "Al día");
});

test("sin monedas: devuelve lista vacía, nunca revienta con null/undefined", () => {
  assert.deepEqual(construirFotoDeSaldoOperador([], true), []);
  assert.deepEqual(construirFotoDeSaldoOperador(null, true), []);
  assert.deepEqual(construirFotoDeSaldoOperador(undefined, true), []);
});
