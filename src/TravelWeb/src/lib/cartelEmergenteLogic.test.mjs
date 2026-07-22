/**
 * Tests de lógica pura del Cartel Emergente (spec docs/ux/2026-07-22-tratamiento-unico-avisos-bloqueo.md).
 * Corren con Node puro sin bundler: node --test src/lib/cartelEmergenteLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  CARTEL_EMERGENTE_VARIANTES,
  resolverTituloCartelEmergente,
  resolverTextoBotonSecundario,
} from "./cartelEmergenteLogic.js";

// ─── resolverTituloCartelEmergente ────────────────────────────────────────────

test("bloqueo sin título personalizado → título genérico 'No se puede todavía'", () => {
  assert.equal(
    resolverTituloCartelEmergente(CARTEL_EMERGENTE_VARIANTES.BLOQUEO, null),
    "No se puede todavía"
  );
});

test("confirmación sin título personalizado → título genérico 'Confirmá antes de seguir'", () => {
  assert.equal(
    resolverTituloCartelEmergente(CARTEL_EMERGENTE_VARIANTES.CONFIRMACION, undefined),
    "Confirmá antes de seguir"
  );
});

test("título personalizado no vacío → se respeta tal cual (recortando espacios)", () => {
  assert.equal(
    resolverTituloCartelEmergente(CARTEL_EMERGENTE_VARIANTES.BLOQUEO, "  Reserva bloqueada  "),
    "Reserva bloqueada"
  );
});

test("título personalizado vacío/solo espacios → cae al genérico igual", () => {
  assert.equal(
    resolverTituloCartelEmergente(CARTEL_EMERGENTE_VARIANTES.CONFIRMACION, "   "),
    "Confirmá antes de seguir"
  );
});

test("variante desconocida → no explota, cae al genérico de bloqueo (más conservador)", () => {
  assert.equal(resolverTituloCartelEmergente("algo-que-no-existe", null), "No se puede todavía");
});

// ─── resolverTextoBotonSecundario ─────────────────────────────────────────────

test("bloqueo sin texto personalizado → 'Entendido'", () => {
  assert.equal(resolverTextoBotonSecundario(CARTEL_EMERGENTE_VARIANTES.BLOQUEO, null), "Entendido");
});

test("confirmación sin texto personalizado → 'Volver'", () => {
  assert.equal(resolverTextoBotonSecundario(CARTEL_EMERGENTE_VARIANTES.CONFIRMACION, null), "Volver");
});

test("texto personalizado (ej. 'Volver a corregir') se respeta", () => {
  assert.equal(
    resolverTextoBotonSecundario(CARTEL_EMERGENTE_VARIANTES.CONFIRMACION, "Volver a corregir"),
    "Volver a corregir"
  );
});
