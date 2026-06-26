/**
 * Tests de lógica pura para el KPI "Cobrado este mes" (F4-7).
 *
 * Cubre los dos bugs fijados el 2026-06-26:
 *   Bug #1 — la línea chica de saldo a favor nunca aparecía porque el backend manda
 *            `creditApplicationsByCurrency` con shape `{ currency, amount }` pero el grid
 *            leía `pm.value`. Fix: mapear `amount → value` al armar el item.
 *   Bug #2 — un mes con cobros SOLO en USD caía al escalar ARS (mostraba "$ 3.400"
 *            en vez de "US$ 3.400") porque `length > 1` excluía los arrays de 1 elemento.
 *            Fix: usar `valuesByCurrency` cuando `length >= 1`.
 *
 * Cómo correr: node --test src/features/payments/lib/cobradoMesMetric.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura replicada de PaymentsCollectionsPage.jsx ────────────────────
// El patrón del proyecto: replicar la función pura aquí para testearla sin bundler.
// Si cambia la lógica original en JSX, actualizar acá también.

/**
 * Arma el objeto de métrica para "Cobrado este mes" a partir del summary del backend.
 * Usada tanto en PaymentsCollectionsPage como en PaymentsHomePage (mismo patrón).
 *
 * Regla: usar `valuesByCurrency` cuando hay al menos 1 entrada en `collectedThisMonthByCurrency`.
 * Solo cae al escalar `collectedThisMonth` si el array viene vacío o ausente.
 *
 * @param {object|null} summary - CollectionsSummaryDto del backend
 * @returns {{ label, testId, valuesByCurrency?, value?, creditApplicationsByCurrency }}
 */
function buildCobradoMesItem(summary) {
  const byCurrency = summary?.collectedThisMonthByCurrency;
  // Bug fix #2: era `length > 1` — ahora `>= 1` para no ignorar arrays de 1 elemento (ej: solo USD)
  const tieneMonedas = Array.isArray(byCurrency) && byCurrency.length >= 1;

  // Bug fix #1: mapear `amount → value` para que FinanceMetricsGrid pueda leer `pm.value`.
  // El backend manda `{ currency, amount }` en creditApplicationsThisMonthByCurrency.
  const creditByCurrency = (summary?.creditApplicationsThisMonthByCurrency ?? [])
    .map((pm) => ({ currency: pm.currency, value: pm.amount }));

  return {
    label: "Cobrado este mes",
    testId: "kpi-cobrado-mes",
    ...(tieneMonedas
      ? { valuesByCurrency: byCurrency.map((pm) => ({ currency: pm.currency, value: pm.amount })) }
      : { value: summary?.collectedThisMonth || 0 }),
    creditApplicationsByCurrency: creditByCurrency,
  };
}

// ─── Tests: modo bimoneda vs escalar ─────────────────────────────────────────

test("buildCobradoMesItem — summary null → value=0, sin valuesByCurrency", () => {
  const item = buildCobradoMesItem(null);
  assert.equal(item.value, 0);
  assert.equal(item.valuesByCurrency, undefined);
  assert.deepEqual(item.creditApplicationsByCurrency, []);
});

test("buildCobradoMesItem — collectedThisMonthByCurrency ausente → escalar ARS de compatibilidad", () => {
  // DTO viejo sin el campo: cae al escalar para no romper integración.
  const summary = { collectedThisMonth: 50000 };
  const item = buildCobradoMesItem(summary);
  assert.equal(item.value, 50000);
  assert.equal(item.valuesByCurrency, undefined);
});

test("buildCobradoMesItem — collectedThisMonthByCurrency vacío → escalar de compatibilidad", () => {
  // El backend mandó el campo pero vacío (mes sin cobros en ninguna moneda).
  const summary = { collectedThisMonth: 0, collectedThisMonthByCurrency: [] };
  const item = buildCobradoMesItem(summary);
  assert.equal(item.value, 0);
  assert.equal(item.valuesByCurrency, undefined);
});

test("buildCobradoMesItem — Bug #2 CRÍTICO: solo USD (array length=1) → usa valuesByCurrency (no escalar ARS)", () => {
  // Un mes donde TODOS los cobros son en dólares (un cliente pagó en USD).
  // Antes del fix: `length > 1` era false → caía al escalar ARS → mostraba "$ 3.400".
  // Después del fix: `length >= 1` es true → usa valuesByCurrency → muestra "US$ 3.400".
  const summary = {
    collectedThisMonth: 0, // escalar irrelevante
    collectedThisMonthByCurrency: [{ currency: "USD", amount: 3400 }],
  };
  const item = buildCobradoMesItem(summary);
  assert.ok(item.valuesByCurrency !== undefined, "Con length=1 debe usar valuesByCurrency, no el escalar");
  assert.equal(item.valuesByCurrency.length, 1);
  assert.equal(item.valuesByCurrency[0].currency, "USD");
  assert.equal(item.valuesByCurrency[0].value, 3400, "Debe mapear amount → value");
  assert.equal(item.value, undefined, "No debe tener `value` cuando usa valuesByCurrency");
});

test("buildCobradoMesItem — ARS y USD (array length=2) → usa valuesByCurrency con ambas monedas", () => {
  const summary = {
    collectedThisMonth: 150000,
    collectedThisMonthByCurrency: [
      { currency: "ARS", amount: 100000 },
      { currency: "USD", amount: 500 },
    ],
  };
  const item = buildCobradoMesItem(summary);
  assert.ok(item.valuesByCurrency !== undefined);
  assert.equal(item.valuesByCurrency.length, 2);

  const ars = item.valuesByCurrency.find((pm) => pm.currency === "ARS");
  const usd = item.valuesByCurrency.find((pm) => pm.currency === "USD");
  assert.equal(ars?.value, 100000);
  assert.equal(usd?.value, 500);
});

test("buildCobradoMesItem — only ARS (array length=1) → valuesByCurrency con ARS", () => {
  // Caso normal: mes con cobros solo en pesos → length=1 → usa valuesByCurrency (no escalar).
  const summary = {
    collectedThisMonth: 80000,
    collectedThisMonthByCurrency: [{ currency: "ARS", amount: 80000 }],
  };
  const item = buildCobradoMesItem(summary);
  assert.ok(item.valuesByCurrency !== undefined);
  assert.equal(item.valuesByCurrency[0].currency, "ARS");
  assert.equal(item.valuesByCurrency[0].value, 80000);
});

// ─── Tests: línea chica de saldo a favor (Bug #1) ────────────────────────────

test("buildCobradoMesItem — Bug #1 CRÍTICO: creditApplications con shape {currency,amount} → mapeados a {currency,value}", () => {
  // El backend manda `creditApplicationsThisMonthByCurrency: [{ currency, amount }]`.
  // FinanceMetricsGrid lee `pm.value`. Sin el mapeo, `pm.value` era undefined → la línea no aparecía.
  const summary = {
    collectedThisMonth: 0,
    collectedThisMonthByCurrency: [],
    creditApplicationsThisMonthByCurrency: [
      { currency: "ARS", amount: 15000 },
    ],
  };
  const item = buildCobradoMesItem(summary);
  assert.equal(item.creditApplicationsByCurrency.length, 1);
  assert.equal(item.creditApplicationsByCurrency[0].currency, "ARS");
  // El campo clave: debe estar como `value`, no como `amount`
  assert.equal(item.creditApplicationsByCurrency[0].value, 15000, "El mapeo amount→value es el fix del bug #1");
  assert.equal(item.creditApplicationsByCurrency[0].amount, undefined, "No debe quedar el campo `amount` sin mapear");
});

test("buildCobradoMesItem — creditApplications monto=0 → queda en el array pero FinanceMetricsGrid lo filtra con pm.value > 0", () => {
  // El grid tiene `.some((pm) => pm.value > 0)` como guard.
  // Con amount=0 el array llega al grid pero la línea chica no se renderiza (lo filtra el grid).
  const summary = {
    collectedThisMonth: 50000,
    collectedThisMonthByCurrency: [],
    creditApplicationsThisMonthByCurrency: [
      { currency: "ARS", amount: 0 },
    ],
  };
  const item = buildCobradoMesItem(summary);
  assert.equal(item.creditApplicationsByCurrency[0].value, 0, "Monto 0 se mapea como 0 (el grid lo filtra)");
});

test("buildCobradoMesItem — creditApplications ausente → array vacío (sin línea chica)", () => {
  const summary = { collectedThisMonth: 50000 };
  const item = buildCobradoMesItem(summary);
  assert.deepEqual(item.creditApplicationsByCurrency, []);
});

test("buildCobradoMesItem — creditApplications USD amount>0 → mapeado; FinanceMetricsGrid lo mostraría", () => {
  // Caso real: cliente tenía saldo a favor en dólares y se aplicó a esta reserva.
  const summary = {
    collectedThisMonth: 0,
    collectedThisMonthByCurrency: [{ currency: "USD", amount: 1200 }],
    creditApplicationsThisMonthByCurrency: [{ currency: "USD", amount: 300 }],
  };
  const item = buildCobradoMesItem(summary);
  const credit = item.creditApplicationsByCurrency.find((pm) => pm.currency === "USD");
  assert.ok(credit !== undefined);
  assert.equal(credit.value, 300, "El saldo a favor en USD debe mapearse correctamente");
});

// ─── Tests de integridad del item ────────────────────────────────────────────

test("buildCobradoMesItem — siempre incluye testId=kpi-cobrado-mes", () => {
  assert.equal(buildCobradoMesItem(null).testId, "kpi-cobrado-mes");
  assert.equal(buildCobradoMesItem({ collectedThisMonth: 0 }).testId, "kpi-cobrado-mes");
  assert.equal(
    buildCobradoMesItem({ collectedThisMonthByCurrency: [{ currency: "USD", amount: 10 }] }).testId,
    "kpi-cobrado-mes"
  );
});

test("buildCobradoMesItem — nunca tiene tanto `value` como `valuesByCurrency` al mismo tiempo", () => {
  // Invariante: los dos campos son mutuamente excluyentes en el item.
  const casosConByCurrency = [
    buildCobradoMesItem({ collectedThisMonthByCurrency: [{ currency: "ARS", amount: 1 }] }),
    buildCobradoMesItem({ collectedThisMonthByCurrency: [{ currency: "USD", amount: 1 }, { currency: "ARS", amount: 1 }] }),
  ];
  const casosSinByCurrency = [
    buildCobradoMesItem(null),
    buildCobradoMesItem({ collectedThisMonth: 999 }),
    buildCobradoMesItem({ collectedThisMonthByCurrency: [] }),
  ];

  for (const item of casosConByCurrency) {
    assert.equal(item.value, undefined, "Con valuesByCurrency no debe existir `value`");
    assert.ok(Array.isArray(item.valuesByCurrency));
  }
  for (const item of casosSinByCurrency) {
    assert.equal(item.valuesByCurrency, undefined, "Sin porMoneda no debe existir `valuesByCurrency`");
    assert.ok(typeof item.value === "number");
  }
});
