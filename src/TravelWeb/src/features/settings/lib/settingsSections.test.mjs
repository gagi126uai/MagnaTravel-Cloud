import { test } from "node:test";
import assert from "node:assert/strict";
import {
  SETTINGS_GROUPS,
  SETTINGS_SECTIONS,
  esSeccionVisible,
  agruparSeccionesVisibles,
  encontrarSeccionVisiblePorSlug,
  chipWhatsApp,
  chipFacturacion,
} from "./settingsSections.js";

// Contexto de ayuda: un vendedor sin permisos especiales, ni admin.
const contextoVendedorSinPermisos = { esAdmin: false, tienePermiso: () => false };
const contextoAdmin = { esAdmin: true, tienePermiso: () => true };

// ─── Visibilidad por sección (§3.1, misma regla que isTabVisible de siempre) ──────────

test("esSeccionVisible: Agencia/Operativa/Facturación/Presupuestos/WhatsApp siempre visibles, sin admin ni permiso", () => {
  const siempreVisibles = ["agencia", "facturacion", "operativa-caja", "presupuestos-pdf", "whatsapp"];
  for (const slug of siempreVisibles) {
    const seccion = SETTINGS_SECTIONS.find((s) => s.slug === slug);
    assert.equal(esSeccionVisible(seccion, contextoVendedorSinPermisos), true, `${slug} debería ser siempre visible`);
  }
});

test("esSeccionVisible: Inteligencia artificial solo para Admin", () => {
  const ia = SETTINGS_SECTIONS.find((s) => s.slug === "ia");
  assert.equal(esSeccionVisible(ia, contextoVendedorSinPermisos), false);
  assert.equal(esSeccionVisible(ia, contextoAdmin), true);
});

test("esSeccionVisible: Logs solo para Admin (rama logsAdminEstricto, no puedeVerConfiguracionIa)", () => {
  const logs = SETTINGS_SECTIONS.find((s) => s.slug === "logs");
  assert.equal(esSeccionVisible(logs, contextoVendedorSinPermisos), false);
  assert.equal(esSeccionVisible(logs, contextoAdmin), true);
});

test("esSeccionVisible: Aprobaciones depende del permiso approvals.policies, no de ser Admin", () => {
  const aprobaciones = SETTINGS_SECTIONS.find((s) => s.slug === "aprobaciones");
  assert.equal(esSeccionVisible(aprobaciones, contextoVendedorSinPermisos), false);
  assert.equal(
    esSeccionVisible(aprobaciones, { esAdmin: false, tienePermiso: (p) => p === "approvals.policies" }),
    true
  );
});

// ─── Agrupamiento (§3.2: grupo vacío desaparece entero) ────────────────────────────────

test("agruparSeccionesVisibles: Admin ve los 3 grupos completos, 8 secciones en total", () => {
  const grupos = agruparSeccionesVisibles(contextoAdmin);
  assert.equal(grupos.length, 3);
  const totalItems = grupos.reduce((acumulado, g) => acumulado + g.items.length, 0);
  assert.equal(totalItems, 8);
});

test("agruparSeccionesVisibles: mismo orden de grupos que la portada (TU EMPRESA, LO QUE VE EL CLIENTE, REGLAS Y SISTEMA)", () => {
  const grupos = agruparSeccionesVisibles(contextoAdmin);
  assert.deepEqual(
    grupos.map((g) => g.grupo),
    [SETTINGS_GROUPS.TU_EMPRESA, SETTINGS_GROUPS.LO_QUE_VE_EL_CLIENTE, SETTINGS_GROUPS.REGLAS_Y_SISTEMA]
  );
});

test("agruparSeccionesVisibles: orden de tarjetas dentro de TU EMPRESA es Agencia, Facturación, Operativa y Caja", () => {
  const grupos = agruparSeccionesVisibles(contextoAdmin);
  const tuEmpresa = grupos.find((g) => g.grupo === SETTINGS_GROUPS.TU_EMPRESA);
  assert.deepEqual(tuEmpresa.items.map((s) => s.slug), ["agencia", "facturacion", "operativa-caja"]);
});

test("agruparSeccionesVisibles: ejemplo real de la spec — vendedor sin approvals.policies y sin ser Admin no ve el grupo REGLAS Y SISTEMA (ni vacío)", () => {
  const grupos = agruparSeccionesVisibles(contextoVendedorSinPermisos);
  const nombresDeGrupo = grupos.map((g) => g.grupo);
  assert.ok(!nombresDeGrupo.includes(SETTINGS_GROUPS.REGLAS_Y_SISTEMA));
  // Los otros dos grupos sí quedan, con todas sus secciones (ninguna de esas 6 requiere permiso especial).
  assert.equal(grupos.length, 2);
});

// ─── Resolución de slug (deep-link, §6) ────────────────────────────────────────────────

test("encontrarSeccionVisiblePorSlug: slug real y visible devuelve la sección", () => {
  const seccion = encontrarSeccionVisiblePorSlug("facturacion", contextoVendedorSinPermisos);
  assert.equal(seccion?.slug, "facturacion");
});

test("encontrarSeccionVisiblePorSlug: slug inventado devuelve null", () => {
  assert.equal(encontrarSeccionVisiblePorSlug("no-existe", contextoAdmin), null);
});

test("encontrarSeccionVisiblePorSlug: slug real pero sin permiso (vendedor pidiendo /settings/logs) devuelve null", () => {
  assert.equal(encontrarSeccionVisiblePorSlug("logs", contextoVendedorSinPermisos), null);
});

// ─── Chip WhatsApp (§2.4) ───────────────────────────────────────────────────────────────

test("chipWhatsApp: READY -> CONECTADO verde", () => {
  assert.deepEqual(chipWhatsApp("READY"), { texto: "CONECTADO", tono: "verde" });
});

test("chipWhatsApp: OFFLINE/STARTING/SCAN_QR -> DESCONECTADO neutro (nunca ámbar)", () => {
  for (const estado of ["OFFLINE", "STARTING", "SCAN_QR"]) {
    assert.deepEqual(chipWhatsApp(estado), { texto: "DESCONECTADO", tono: "neutro" });
  }
});

test("chipWhatsApp: todavía sin respuesta de la API (undefined/null) -> sin chip", () => {
  assert.equal(chipWhatsApp(undefined), null);
  assert.equal(chipWhatsApp(null), null);
});

// ─── Chip Facturación (§2.4) ────────────────────────────────────────────────────────────

test("chipFacturacion: isProduction true -> PRODUCCIÓN verde", () => {
  assert.deepEqual(chipFacturacion(true), { texto: "PRODUCCIÓN", tono: "verde" });
});

test("chipFacturacion: isProduction false -> HOMOLOGACIÓN ámbar", () => {
  assert.deepEqual(chipFacturacion(false), { texto: "HOMOLOGACIÓN", tono: "ambar" });
});

test("chipFacturacion: todavía sin respuesta de la API (undefined/null) -> sin chip, nunca 'Cargando...'", () => {
  assert.equal(chipFacturacion(undefined), null);
  assert.equal(chipFacturacion(null), null);
});
