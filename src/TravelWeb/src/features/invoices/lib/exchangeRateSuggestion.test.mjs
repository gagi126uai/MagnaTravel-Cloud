/**
 * Tests de lógica pura de la sugerencia de tipo de cambio al facturar en USD.
 * Spec: docs/ux/specs/2026-08-05-tc-sugerido-en-facturar.md.
 *
 * Cómo correr: node --test src/features/invoices/lib/exchangeRateSuggestion.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  interpretarRespuestaSugerenciaTC,
  debeMostrarJustificacionTC,
  faltaJustificacionTC,
  textoLeyendaTC,
  construirCamposUSDParaPayload,
  TEXTO_BUSCANDO_TC_SUGERIDO,
  TEXTO_SIN_TC_SUGERIDO,
} from "./exchangeRateSuggestion.js";

// ─── interpretarRespuestaSugerenciaTC ────────────────────────────────────────

test("interpretarRespuestaSugerenciaTC — 200 con dato → devuelve el número TAL CUAL, sin redondear", () => {
  const respuesta = { tipoCambio: 1234.567, fecha: "2026-08-05", esDeOtraFecha: false, leyenda: "Dólar oficial de hoy (5 de agosto)." };
  const resultado = interpretarRespuestaSugerenciaTC(respuesta);
  // Regla de oro de la spec: el motor decide comparando el número exacto — el front
  // no puede tocarlo ni un decimal, o una factura sin cambios quedaría "a mano".
  assert.equal(resultado.tipoCambioSugerido, 1234.567);
  assert.equal(resultado.leyenda, "Dólar oficial de hoy (5 de agosto).");
});

test("interpretarRespuestaSugerenciaTC — 204 (null de api.get) → sin sugerencia, sin error", () => {
  const resultado = interpretarRespuestaSugerenciaTC(null);
  assert.equal(resultado.tipoCambioSugerido, null);
  assert.equal(resultado.leyenda, null);
});

test("interpretarRespuestaSugerenciaTC — tipoCambio inválido (0 o negativo) → se trata como sin dato", () => {
  assert.equal(interpretarRespuestaSugerenciaTC({ tipoCambio: 0, leyenda: "x" }).tipoCambioSugerido, null);
  assert.equal(interpretarRespuestaSugerenciaTC({ tipoCambio: -5, leyenda: "x" }).tipoCambioSugerido, null);
});

// ─── debeMostrarJustificacionTC ──────────────────────────────────────────────

test("debeMostrarJustificacionTC — número escrito IGUAL al sugerido → no pide justificación", () => {
  const resultado = debeMostrarJustificacionTC({
    tipoCambioEscrito: "1234.5",
    tipoCambioSugerido: 1234.5,
    huboSugerencia: true,
  });
  assert.equal(resultado, false);
});

test("debeMostrarJustificacionTC — mismo número con formato distinto (1234.50 vs 1234.5) → sigue considerándose igual", () => {
  // La comparación es numérica, no de texto: agregar un cero de más no debe disparar
  // el pedido de justificación (regla T-13: compara como lo hace el motor).
  const resultado = debeMostrarJustificacionTC({
    tipoCambioEscrito: "1234.50",
    tipoCambioSugerido: 1234.5,
    huboSugerencia: true,
  });
  assert.equal(resultado, false);
});

test("debeMostrarJustificacionTC — número escrito DISTINTO del sugerido → pide justificación", () => {
  const resultado = debeMostrarJustificacionTC({
    tipoCambioEscrito: "1300",
    tipoCambioSugerido: 1234.5,
    huboSugerencia: true,
  });
  assert.equal(resultado, true);
});

test("debeMostrarJustificacionTC — sin sugerencia del motor (Momento C) → pide siempre, aunque el campo esté vacío", () => {
  const resultado = debeMostrarJustificacionTC({
    tipoCambioEscrito: "",
    tipoCambioSugerido: null,
    huboSugerencia: false,
  });
  assert.equal(resultado, true);
});

test("debeMostrarJustificacionTC — sin sugerencia, con un número cargado a mano → sigue pidiendo justificación", () => {
  const resultado = debeMostrarJustificacionTC({
    tipoCambioEscrito: "1300",
    tipoCambioSugerido: null,
    huboSugerencia: false,
  });
  assert.equal(resultado, true);
});

test("debeMostrarJustificacionTC — hubo sugerencia pero el casillero todavía está vacío → no pide de más (evita parpadeo)", () => {
  const resultado = debeMostrarJustificacionTC({
    tipoCambioEscrito: "",
    tipoCambioSugerido: 1234.5,
    huboSugerencia: true,
  });
  assert.equal(resultado, false);
});

test("debeMostrarJustificacionTC — el usuario borra y vuelve a escribir el mismo número sugerido → el campo desaparece", () => {
  // Regla explícita de la spec (§4 punto 5): "si el usuario borra y vuelve a
  // escribir el mismo número sugerido, el campo desaparece" — no importa que haya
  // habido un instante intermedio con el campo vacío.
  const tocado = debeMostrarJustificacionTC({ tipoCambioEscrito: "9999", tipoCambioSugerido: 1234.5, huboSugerencia: true });
  const vuelveAlOriginal = debeMostrarJustificacionTC({ tipoCambioEscrito: "1234.5", tipoCambioSugerido: 1234.5, huboSugerencia: true });
  assert.equal(tocado, true);
  assert.equal(vuelveAlOriginal, false);
});

// ─── Textos exactos (spec §5) ─────────────────────────────────────────────────

test("Textos exactos de la línea gris — buscando y sin dato", () => {
  assert.equal(TEXTO_BUSCANDO_TC_SUGERIDO, "Buscando el tipo de cambio del día…");
  assert.equal(TEXTO_SIN_TC_SUGERIDO, "No tenemos el tipo de cambio del día. Escribí el tipo de cambio a mano.");
});

// ─── textoLeyendaTC ───────────────────────────────────────────────────────────

test("textoLeyendaTC — cargando → texto de buscando, sin importar lo demás", () => {
  const resultado = textoLeyendaTC({ cargando: true, huboSugerencia: true, leyenda: "Dólar oficial de hoy." });
  assert.equal(resultado, TEXTO_BUSCANDO_TC_SUGERIDO);
});

test("textoLeyendaTC — hubo sugerencia → la leyenda del motor, tal cual", () => {
  const resultado = textoLeyendaTC({ cargando: false, huboSugerencia: true, leyenda: "Dólar oficial de hoy (5 de agosto)." });
  assert.equal(resultado, "Dólar oficial de hoy (5 de agosto).");
});

test("textoLeyendaTC — sin sugerencia → texto de sin dato", () => {
  const resultado = textoLeyendaTC({ cargando: false, huboSugerencia: false, leyenda: null });
  assert.equal(resultado, TEXTO_SIN_TC_SUGERIDO);
});

test("textoLeyendaTC (N3, defensa en profundidad) — hubo sugerencia pero la leyenda llegó vacía → fallback, no texto vacío", () => {
  // El fallback se decide con huboSugerencia (hay número o no), no con si la leyenda
  // vino vacía — aunque este caso no debería pasar del lado del motor, el front no
  // debe mostrar un renglón en blanco.
  const resultado = textoLeyendaTC({ cargando: false, huboSugerencia: true, leyenda: "" });
  assert.equal(resultado, TEXTO_SIN_TC_SUGERIDO);
});

// ─── faltaJustificacionTC (fix B1, review 2026-08-05) ─────────────────────────

test("faltaJustificacionTC — campo se muestra y está vacío → falta (bloquea)", () => {
  assert.equal(faltaJustificacionTC({ mostrar: true, texto: "" }), true);
});

test("faltaJustificacionTC — campo se muestra y tiene texto → no falta (habilita)", () => {
  assert.equal(faltaJustificacionTC({ mostrar: true, texto: "Cotización del operador" }), false);
});

test("faltaJustificacionTC — campo NO se muestra (sugerencia aceptada tal cual) y está vacío → no falta", () => {
  // Este es el caso exacto del bug B1: con la sugerencia aceptada, el campo de
  // justificación ni siquiera está en pantalla — no puede seguir bloqueando el botón.
  assert.equal(faltaJustificacionTC({ mostrar: false, texto: "" }), false);
});

test("faltaJustificacionTC — campo se muestra, texto solo espacios → falta (no cuenta como cargado)", () => {
  assert.equal(faltaJustificacionTC({ mostrar: true, texto: "   " }), true);
});

// ─── construirCamposUSDParaPayload (mismo patrón que confirmarMultaOperador.test.mjs:763) ─

test("construirCamposUSDParaPayload — sugerencia aceptada tal cual (mostrarJustificacion=false) → SIN exchangeRateSource/exchangeRateFetchedAt/exchangeRateJustification", () => {
  const payload = construirCamposUSDParaPayload({
    tipoCambio: "1234.5",
    justificacion: "",
    mostrarJustificacion: false,
  });
  assert.deepEqual(payload, { monId: "USD", monCotiz: 1234.5 });
  assert.equal("exchangeRateSource" in payload, false);
  assert.equal("exchangeRateFetchedAt" in payload, false);
  assert.equal("exchangeRateJustification" in payload, false);
});

test("construirCamposUSDParaPayload — número pisado (mostrarJustificacion=true) → exchangeRateJustification viaja, recortada; SIGUE sin exchangeRateSource/exchangeRateFetchedAt", () => {
  const payload = construirCamposUSDParaPayload({
    tipoCambio: "1300",
    justificacion: "  Cotización que me pasó el operador  ",
    mostrarJustificacion: true,
  });
  assert.equal(payload.monId, "USD");
  assert.equal(payload.monCotiz, 1300);
  assert.equal(payload.exchangeRateJustification, "Cotización que me pasó el operador");
  assert.equal("exchangeRateSource" in payload, false);
  assert.equal("exchangeRateFetchedAt" in payload, false);
});

test("construirCamposUSDParaPayload — el número viaja TAL CUAL, sin redondear", () => {
  const payload = construirCamposUSDParaPayload({
    tipoCambio: "1234.567",
    justificacion: "",
    mostrarJustificacion: false,
  });
  assert.equal(payload.monCotiz, 1234.567);
});
