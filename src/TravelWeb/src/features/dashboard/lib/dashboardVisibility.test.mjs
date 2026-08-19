/**
 * Tests de dashboardVisibility.js — R1 de la spec del dashboard (2026-08-18).
 * Cómo correr: node --test src/features/dashboard/lib/dashboardVisibility.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";

import { calcularVisibilidadDashboard } from "./dashboardVisibility.js";

test("dueño/colaborador con cobranzas.see_cost: ve margen, caja proyectada y grid de 4", () => {
  const resultado = calcularVisibilidadDashboard({ puedeVerCostos: true, esAdmin: false });
  assert.equal(resultado.verMargenBruto, true);
  assert.equal(resultado.verCajaProyectada, true);
  assert.equal(resultado.columnasGridKpi, 4);
});

test("vendedor sin cobranzas.see_cost: NO ve margen ni caja proyectada, grid de 3", () => {
  const resultado = calcularVisibilidadDashboard({ puedeVerCostos: false, esAdmin: false });
  assert.equal(resultado.verMargenBruto, false);
  assert.equal(resultado.verCajaProyectada, false);
  assert.equal(resultado.columnasGridKpi, 3);
});

test("'Ver informes' depende de isAdmin, no de cobranzas.see_cost", () => {
  const conCostoSinAdmin = calcularVisibilidadDashboard({ puedeVerCostos: true, esAdmin: false });
  assert.equal(conCostoSinAdmin.verInformes, false);

  const admin = calcularVisibilidadDashboard({ puedeVerCostos: false, esAdmin: true });
  assert.equal(admin.verInformes, true);
});

test("valores undefined/null no rompen: se tratan como false", () => {
  const resultado = calcularVisibilidadDashboard({});
  assert.equal(resultado.verMargenBruto, false);
  assert.equal(resultado.verCajaProyectada, false);
  assert.equal(resultado.verInformes, false);
  assert.equal(resultado.columnasGridKpi, 3);
});
