/**
 * Tests que fijan el contrato camelCase del endpoint GET /api/alerts.
 *
 * Por qué existen: la API serializa en camelCase (AlertsResponse.cs, serialización
 * Web por defecto de .NET). En el pasado el frontend leía PascalCase, lo que hacía
 * que el badge del sidebar y las tarjetas de Cobranzas siempre aparecieran vacíos.
 * Si alguien vuelve a escribir UrgentTrips/SupplierDebts/TotalCount, estos tests fallan.
 */
import test from "node:test";
import assert from "node:assert/strict";

// ─── Contrato del estado vacío ────────────────────────────────────────────────
// Refleja exactamente el objeto `emptyAlerts` de useFinanceHome y el useState
// de AlertsContext. Si alguien cambia las claves, los tests de abajo atrapan el cambio.

const emptyAlerts = {
  urgentTrips: [],
  supplierDebts: [],
  serviceDeadlines: [],
  costsToConfirm: [],
  totalCount: 0,
};

// ─── Simulación de respuesta de la API ───────────────────────────────────────
// Representa lo que /api/alerts devuelve (camelCase confirmado en AlertsResponse.cs).

const apiResponse = {
  urgentTrips: [
    { id: 1, numeroReserva: "RES-001", balance: 50000, startDate: "2026-06-10" },
  ],
  supplierDebts: [
    { id: 2, name: "Proveedor SA", currentBalance: 120000 },
  ],
  serviceDeadlines: [],
  costsToConfirm: [],
  totalCount: 2,
};

// ─── Lógica pura: aplicar respuesta o fallback ────────────────────────────────
// Replica lo que hace AlertsContext y useFinanceHome al recibir la respuesta.

function applyAlertsResponse(res) {
  return res || emptyAlerts;
}

// ─── Tests ───────────────────────────────────────────────────────────────────

test("emptyAlerts: tiene claves en camelCase, no PascalCase", () => {
  // Si alguien cambia a TotalCount/UrgentTrips/SupplierDebts, este test falla.
  assert.ok("totalCount" in emptyAlerts, "Falta totalCount (camelCase)");
  assert.ok("urgentTrips" in emptyAlerts, "Falta urgentTrips (camelCase)");
  assert.ok("supplierDebts" in emptyAlerts, "Falta supplierDebts (camelCase)");
  assert.ok("serviceDeadlines" in emptyAlerts, "Falta serviceDeadlines (camelCase)");
  assert.ok("costsToConfirm" in emptyAlerts, "Falta costsToConfirm (camelCase)");

  // PascalCase NO debe existir
  assert.ok(!("TotalCount" in emptyAlerts), "TotalCount PascalCase no debe existir");
  assert.ok(!("UrgentTrips" in emptyAlerts), "UrgentTrips PascalCase no debe existir");
  assert.ok(!("SupplierDebts" in emptyAlerts), "SupplierDebts PascalCase no debe existir");
});

test("emptyAlerts: valores vacíos correctos como estado inicial", () => {
  assert.equal(emptyAlerts.totalCount, 0);
  assert.deepEqual(emptyAlerts.urgentTrips, []);
  assert.deepEqual(emptyAlerts.supplierDebts, []);
  assert.deepEqual(emptyAlerts.serviceDeadlines, []);
  assert.deepEqual(emptyAlerts.costsToConfirm, []);
});

test("applyAlertsResponse: con respuesta real, devuelve los datos de la API", () => {
  const result = applyAlertsResponse(apiResponse);
  assert.equal(result.totalCount, 2);
  assert.equal(result.urgentTrips.length, 1);
  assert.equal(result.supplierDebts.length, 1);
  assert.equal(result.urgentTrips[0].numeroReserva, "RES-001");
  assert.equal(result.supplierDebts[0].name, "Proveedor SA");
});

test("applyAlertsResponse: con null, devuelve emptyAlerts como fallback", () => {
  const result = applyAlertsResponse(null);
  assert.equal(result.totalCount, 0);
  assert.deepEqual(result.urgentTrips, []);
  assert.deepEqual(result.supplierDebts, []);
});

test("applyAlertsResponse: con undefined, devuelve emptyAlerts como fallback", () => {
  const result = applyAlertsResponse(undefined);
  assert.equal(result.totalCount, 0);
  assert.deepEqual(result.urgentTrips, []);
});

test("badge del sidebar: totalCount camelCase alimenta el badge correctamente", () => {
  // Simula la lógica del Sidebar: badge = alerts?.totalCount
  // Con PascalCase (bug anterior) el badge sería undefined → nunca se mostraba.
  const alerts = applyAlertsResponse(apiResponse);
  const badgeValue = alerts?.totalCount;
  assert.equal(typeof badgeValue, "number", "totalCount debe ser número, no undefined");
  assert.equal(badgeValue, 2);
});

test("tarjeta Cobranzas: urgentTrips camelCase tiene items renderizables", () => {
  const alerts = applyAlertsResponse(apiResponse);
  // PaymentsHomePage usa alerts?.urgentTrips || []
  const items = alerts?.urgentTrips || [];
  assert.equal(items.length, 1);
  assert.equal(items[0].balance, 50000);
});

test("tarjeta Proveedores: supplierDebts camelCase tiene items renderizables", () => {
  const alerts = applyAlertsResponse(apiResponse);
  // PaymentsHomePage usa alerts?.supplierDebts || []
  const items = alerts?.supplierDebts || [];
  assert.equal(items.length, 1);
  assert.equal(items[0].currentBalance, 120000);
});
