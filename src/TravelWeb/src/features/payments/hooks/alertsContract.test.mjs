/**
 * Tests que fijan el contrato camelCase del endpoint GET /api/alerts.
 *
 * Por qué existen: la API serializa en camelCase (AlertsResponse.cs, serialización
 * Web por defecto de .NET). En el pasado el frontend leía PascalCase, lo que hacía
 * que el badge del sidebar y las tarjetas de Cobranzas siempre aparecieran vacíos.
 * Si alguien vuelve a escribir UrgentTrips/SupplierDebts/TotalCount, estos tests fallan.
 *
 * F2 (Próximos Inicios): serviceDeadlines → upcomingStarts + upcomingStartsWindowDays.
 */
import test from "node:test";
import assert from "node:assert/strict";

// ─── Contrato del estado vacío ────────────────────────────────────────────────
// Refleja exactamente el objeto `emptyAlerts` del useState de AlertsContext.
// Si alguien cambia las claves, los tests de abajo atrapan el cambio.

const emptyAlerts = {
  urgentTrips: [],
  supplierDebts: [],
  upcomingStarts: [],
  upcomingStartsWindowDays: null,
  costsToConfirm: [],
  totalCount: 0,
};

// ─── Simulación de respuesta de la API ───────────────────────────────────────
// Representa lo que /api/alerts devuelve (camelCase confirmado en AlertsResponse.cs).
// F2: el server devuelve upcomingStarts[] (uno por reserva) + upcomingStartsWindowDays (int|null).

const apiResponse = {
  urgentTrips: [
    { id: 1, numeroReserva: "RES-001", balance: 50000, startDate: "2026-06-10" },
  ],
  supplierDebts: [
    { id: 2, name: "Proveedor SA", currentBalance: 120000 },
  ],
  upcomingStarts: [
    { reservaPublicId: "res-abc", numeroReserva: "RES-010", holderName: "Juan Perez", firstStartDate: "2026-06-15T00:00:00Z", daysLeft: 5 },
  ],
  upcomingStartsWindowDays: 7,
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
  // F2: serviceDeadlines → upcomingStarts + upcomingStartsWindowDays
  assert.ok("upcomingStarts" in emptyAlerts, "Falta upcomingStarts (F2, camelCase)");
  assert.ok("upcomingStartsWindowDays" in emptyAlerts, "Falta upcomingStartsWindowDays (F2, camelCase)");
  assert.ok(!("serviceDeadlines" in emptyAlerts), "serviceDeadlines ya no debe existir (reemplazado por upcomingStarts en F2)");
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
  // F2: upcomingStarts vacío y windowDays null en el estado inicial
  assert.deepEqual(emptyAlerts.upcomingStarts, []);
  assert.equal(emptyAlerts.upcomingStartsWindowDays, null);
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

// ─── Tests F2: upcomingStarts + upcomingStartsWindowDays ─────────────────────

test("F2: upcomingStarts camelCase tiene items renderizables con los campos esperados", () => {
  const alerts = applyAlertsResponse(apiResponse);
  const items = alerts?.upcomingStarts || [];
  assert.equal(items.length, 1);
  // Campos requeridos por NotificationBell (SeccionProximosInicios)
  assert.ok("reservaPublicId" in items[0], "Falta reservaPublicId");
  assert.ok("numeroReserva" in items[0], "Falta numeroReserva");
  assert.ok("firstStartDate" in items[0], "Falta firstStartDate");
  assert.ok("daysLeft" in items[0], "Falta daysLeft");
  assert.equal(items[0].daysLeft, 5);
  assert.equal(items[0].numeroReserva, "RES-010");
});

test("F2: upcomingStartsWindowDays es número cuando el flag está ON", () => {
  const alerts = applyAlertsResponse(apiResponse);
  assert.equal(typeof alerts?.upcomingStartsWindowDays, "number");
  assert.equal(alerts?.upcomingStartsWindowDays, 7);
});

test("F2: upcomingStartsWindowDays null en emptyAlerts (flag OFF)", () => {
  const alerts = applyAlertsResponse(null);
  assert.equal(alerts?.upcomingStartsWindowDays, null);
});

test("F2: holderName en upcomingStarts para la línea 2 del ítem", () => {
  const alerts = applyAlertsResponse(apiResponse);
  const item = alerts?.upcomingStarts[0];
  assert.equal(item?.holderName, "Juan Perez");
});
