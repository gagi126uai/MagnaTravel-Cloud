/**
 * Tests de COMPONENTE de PaymentTermsCard.jsx (patrón existente en
 * features/ai-settings/components/AiSettingsTab.test.mjs): este repo no tiene jsdom ni
 * @testing-library instalado, así que "test de componente" es sobre la función de
 * ORQUESTACIÓN que el componente usa para decidir qué precargar —
 * `cargarTextoPrecargadoFormasDePago`, importada directo de ../lib/paymentTermsCardLogic.js,
 * la misma que importa PaymentTermsCard.jsx (nada se replica).
 *
 * Fix bloqueante (2026-08-13, hallazgo de frontend-reviewer): la card precargaba con
 * `GET /reports/settings` (Admin-only) — un vendedor/colaborador recibía 403 y el textarea
 * quedaba vacío. Estos tests cubren los 3 escenarios que pidió el reviewer, ya con el
 * endpoint nuevo `GET /reports/budget-payment-terms-template` (permiso base de reservas).
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/paymentTermsCard.test.mjs
 */

import { test } from "node:test";
import assert from "node:assert/strict";
import { cargarTextoPrecargadoFormasDePago } from "../lib/paymentTermsCardLogic.js";

// ─── (a) Precarga con texto propio de la reserva ────────────────────────────

test("(a) la reserva ya tiene texto propio: se muestra ESE, sin pedirle nada al endpoint de la plantilla", async () => {
  // Arrange: obtenerPlantilla representa la llamada real a
  // GET /reports/budget-payment-terms-template. La hacemos explotar a propósito — si el
  // componente la llamara igual, este test la detectaría.
  let sePidioLaPlantilla = false;
  const obtenerPlantilla = async () => {
    sePidioLaPlantilla = true;
    throw new Error("no debería llamarse: la reserva ya tiene texto propio");
  };

  // Act
  const texto = await cargarTextoPrecargadoFormasDePago("Seña 30% + saldo en 3 cuotas", obtenerPlantilla);

  // Assert: el texto propio gana tal cual, y el endpoint de la plantilla NUNCA se llamó
  // (ni siquiera un vendedor sin permiso para verla se ve afectado).
  assert.equal(texto, "Seña 30% + saldo en 3 cuotas");
  assert.equal(sePidioLaPlantilla, false);
});

// ─── (b) Precarga con la plantilla del endpoint nuevo (reserva sin texto propio) ──

test("(b) reserva sin texto propio: pide la plantilla al endpoint de lectura mínima y la usa como previsualización", async () => {
  // Arrange: simula la respuesta real de GET /reports/budget-payment-terms-template → { text }.
  const obtenerPlantilla = async () => "Seña del 30% al reservar. Saldo 21 días antes de la salida.";

  // Act
  const texto = await cargarTextoPrecargadoFormasDePago(null, obtenerPlantilla);

  // Assert
  assert.equal(texto, "Seña del 30% al reservar. Saldo 21 días antes de la salida.");
});

test("(b bis) reserva con texto propio en blanco (solo espacios) se trata como 'no tiene' — también cae a la plantilla", async () => {
  const obtenerPlantilla = async () => "Plantilla de la agencia";
  const texto = await cargarTextoPrecargadoFormasDePago("   ", obtenerPlantilla);
  assert.equal(texto, "Plantilla de la agencia");
});

// ─── (c) El GET de la plantilla falla: el textarea queda usable, no explota ────

test("(c) el GET de la plantilla falla (ej. 403 de un rol sin permiso, o caída de red): no explota, devuelve '' — textarea con placeholder", async () => {
  const obtenerPlantilla = async () => {
    const error = new Error("Forbidden");
    error.status = 403;
    throw error;
  };

  const texto = await cargarTextoPrecargadoFormasDePago(null, obtenerPlantilla);

  assert.equal(texto, "");
});

test("(c bis) la plantilla existe pero está vacía (agencia nunca cargó nada): también '', sin reventar", async () => {
  const obtenerPlantilla = async () => null;
  const texto = await cargarTextoPrecargadoFormasDePago(undefined, obtenerPlantilla);
  assert.equal(texto, "");
});
