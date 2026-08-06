/**
 * Tests de lógica pura de la sugerencia de tipo de cambio al facturar en USD.
 * Spec base: docs/ux/specs/2026-08-05-tc-sugerido-en-facturar.md.
 * Ampliada por: docs/ux/specs/2026-08-06-ayuda-invisible-tc.md (A3 "el motor
 * completa solo" y A4 "acomodo al techo").
 *
 * Cómo correr: node --test src/features/invoices/lib/exchangeRateSuggestion.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  interpretarRespuestaSugerenciaTC,
  debeMostrarJustificacionTC,
  acomodarAlTope,
  faltaJustificacionTC,
  textoLeyendaTC,
  textoAcomodadoAlTope,
  construirCamposUSDParaPayload,
  TEXTO_BUSCANDO_TC_SUGERIDO,
  TEXTO_SIN_TC_SUGERIDO,
} from "./exchangeRateSuggestion.js";

// ─── interpretarRespuestaSugerenciaTC ────────────────────────────────────────

test("interpretarRespuestaSugerenciaTC — 200 con dato → devuelve el número TAL CUAL, sin redondear", () => {
  const respuesta = {
    tipoCambio: 1234.567,
    fecha: "2026-08-05",
    esDeOtraFecha: false,
    leyenda: "Dólar oficial del 5 de agosto.",
    topeDelDia: 1235.5,
    loCompletaElSistema: false,
  };
  const resultado = interpretarRespuestaSugerenciaTC(respuesta);
  // Regla de oro de la spec: el motor decide comparando el número exacto — el front
  // no puede tocarlo ni un decimal, o una factura sin cambios quedaría "a mano".
  assert.equal(resultado.tipoCambioSugerido, 1234.567);
  assert.equal(resultado.leyenda, "Dólar oficial del 5 de agosto.");
  assert.equal(resultado.topeDelDia, 1235.5);
  assert.equal(resultado.loCompletaElSistema, false);
});

test("interpretarRespuestaSugerenciaTC — 204 (null de api.get) → sin sugerencia, sin error, sin techo", () => {
  const resultado = interpretarRespuestaSugerenciaTC(null);
  assert.equal(resultado.tipoCambioSugerido, null);
  assert.equal(resultado.leyenda, null);
  assert.equal(resultado.topeDelDia, null);
  assert.equal(resultado.loCompletaElSistema, false);
});

test("interpretarRespuestaSugerenciaTC — tipoCambio inválido (0 o negativo) → se trata como sin dato", () => {
  assert.equal(interpretarRespuestaSugerenciaTC({ tipoCambio: 0, leyenda: "x" }).tipoCambioSugerido, null);
  assert.equal(interpretarRespuestaSugerenciaTC({ tipoCambio: -5, leyenda: "x" }).tipoCambioSugerido, null);
});

test("interpretarRespuestaSugerenciaTC — A3 loCompletaElSistema=true → nada que precargar ni mostrar, ni siquiera el número", () => {
  // El motor manda tipoCambio: null y leyenda: "" a propósito (spec A3): el número
  // de práctica no es plata de verdad y no debe llegar a la pantalla en absoluto.
  const respuesta = { tipoCambio: null, fecha: "2026-08-05", esDeOtraFecha: false, leyenda: "", topeDelDia: null, loCompletaElSistema: true };
  const resultado = interpretarRespuestaSugerenciaTC(respuesta);
  assert.equal(resultado.tipoCambioSugerido, null);
  assert.equal(resultado.leyenda, null);
  assert.equal(resultado.topeDelDia, null);
  assert.equal(resultado.loCompletaElSistema, true);
});

test("interpretarRespuestaSugerenciaTC — topeDelDia inválido (0 o negativo) → se trata como techo desconocido", () => {
  const respuesta = { tipoCambio: 1234.5, leyenda: "x", topeDelDia: 0, loCompletaElSistema: false };
  assert.equal(interpretarRespuestaSugerenciaTC(respuesta).topeDelDia, null);
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

test("debeMostrarJustificacionTC — A4 fueAcomodadoAlTope=true → NUNCA pide justificación, aunque el número difiera del sugerido", () => {
  // El vendedor no eligió el número que quedó en el casillero (se lo puso el
  // sistema al bajarlo al techo) — no tiene nada que explicar.
  const resultado = debeMostrarJustificacionTC({
    tipoCambioEscrito: "1235.5",
    tipoCambioSugerido: 1234.5,
    huboSugerencia: true,
    fueAcomodadoAlTope: true,
  });
  assert.equal(resultado, false);
});

test("debeMostrarJustificacionTC — A4 fueAcomodadoAlTope=true incluso sin sugerencia previa → sigue sin pedir nada", () => {
  const resultado = debeMostrarJustificacionTC({
    tipoCambioEscrito: "1235.5",
    tipoCambioSugerido: null,
    huboSugerencia: false,
    fueAcomodadoAlTope: true,
  });
  assert.equal(resultado, false);
});

// ─── acomodarAlTope (A4, spec 2026-08-06) ─────────────────────────────────────

test("acomodarAlTope — número escrito por encima del techo → devuelve el techo", () => {
  assert.equal(acomodarAlTope("1500", 1235.5), 1235.5);
});

test("acomodarAlTope — número escrito dentro del techo → no toca nada (null)", () => {
  assert.equal(acomodarAlTope("1200", 1235.5), null);
});

test("acomodarAlTope — número escrito IGUAL al techo → no toca nada (ya entra exacto)", () => {
  assert.equal(acomodarAlTope("1235.5", 1235.5), null);
});

test("acomodarAlTope — sin techo conocido (null) → nunca acomoda nada", () => {
  assert.equal(acomodarAlTope("999999", null), null);
});

test("acomodarAlTope — casillero vacío → no hay nada que acomodar", () => {
  assert.equal(acomodarAlTope("", 1235.5), null);
});

// ─── Textos exactos (tabla A6, spec 2026-08-06) ───────────────────────────────

test("Textos exactos de la línea gris — buscando y sin dato", () => {
  assert.equal(TEXTO_BUSCANDO_TC_SUGERIDO, "Buscando el tipo de cambio…");
  assert.equal(TEXTO_SIN_TC_SUGERIDO, "Escribí el tipo de cambio.");
});

test("textoAcomodadoAlTope — arma el texto exacto de la tabla A6, con el monto en formato pesos", () => {
  assert.equal(textoAcomodadoAlTope(1235.5), "En la factura entra hasta $ 1.235,50.");
});

// ─── textoLeyendaTC ───────────────────────────────────────────────────────────

test("textoLeyendaTC — cargando → texto de buscando, sin importar lo demás", () => {
  const resultado = textoLeyendaTC({ cargando: true, huboSugerencia: true, leyenda: "Dólar oficial del 5 de agosto." });
  assert.equal(resultado, TEXTO_BUSCANDO_TC_SUGERIDO);
});

test("textoLeyendaTC — hubo sugerencia → la leyenda del motor, tal cual", () => {
  const resultado = textoLeyendaTC({ cargando: false, huboSugerencia: true, leyenda: "Dólar oficial del 5 de agosto." });
  assert.equal(resultado, "Dólar oficial del 5 de agosto.");
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

test("textoLeyendaTC — A4 fueAcomodadoAlTope=true → el acomodo pisa a la leyenda normal", () => {
  // Aunque hubo sugerencia (y el número escrito difiere de ella), el momento del
  // acomodo es más importante para el vendedor que "qué dólar es".
  const resultado = textoLeyendaTC({
    cargando: false,
    huboSugerencia: true,
    leyenda: "Dólar oficial del 5 de agosto.",
    fueAcomodadoAlTope: true,
    topeDelDia: 1235.5,
  });
  assert.equal(resultado, "En la factura entra hasta $ 1.235,50.");
});

test("textoLeyendaTC — fueAcomodadoAlTope=true pero SIN topeDelDia (dato defensivo faltante) → cae a la leyenda normal", () => {
  const resultado = textoLeyendaTC({
    cargando: false,
    huboSugerencia: true,
    leyenda: "Dólar oficial del 5 de agosto.",
    fueAcomodadoAlTope: true,
    topeDelDia: null,
  });
  assert.equal(resultado, "Dólar oficial del 5 de agosto.");
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

// ─── Contrato A4 completo (fix bloqueante, review post-implementación) ────────
//
// BUG que esto evita: la primera implementación pisaba el ESTADO del casillero con
// el techo apenas se acomodaba, así que el payload terminaba mandando el techo
// (1235.50) en vez de lo que el vendedor tipeó (1500). El motor comparaba ese
// 1235.50 contra la sugerencia, no coincidía, y lo trataba como carga MANUAL sin
// justificar → rebotaba pidiendo una explicación que la pantalla nunca mostró
// (verificado por 3 reviewers). La regla correcta: el casillero MUESTRA el techo,
// pero el payload manda el número ORIGINAL — es el motor quien clampea server-side,
// guarda el rastro (RequestedExchangeRate) y escribe su propia justificación.

test("Contrato A4 — usuario tipea 1500 (por encima del techo 1235.50): el casillero muestra el techo, PERO el payload manda 1500 tal cual, sin justificación", () => {
  const tipeado = "1500";
  const topeDelDia = 1235.5;

  // 1. La pantalla detecta que hay que acomodar — este valor es SOLO para pintar
  //    el casillero, nunca para reemplazar lo que se manda.
  const valorParaMostrarEnPantalla = acomodarAlTope(tipeado, topeDelDia);
  assert.equal(valorParaMostrarEnPantalla, 1235.5, "El casillero debe MOSTRAR el techo");

  // 2. Con el acomodo activo, nunca se pide justificación (A5.4) — el vendedor no
  //    eligió el número que terminó en el comprobante.
  const requiereJustificacion = debeMostrarJustificacionTC({
    tipoCambioEscrito: tipeado,
    tipoCambioSugerido: 1234.5,
    huboSugerencia: true,
    fueAcomodadoAlTope: true,
  });
  assert.equal(requiereJustificacion, false);

  // 3. El payload manda el número TIPEADO (1500), NO el techo mostrado en pantalla.
  const payload = construirCamposUSDParaPayload({
    tipoCambio: tipeado, // el estado real del componente, nunca pisado por el acomodo
    justificacion: "",
    mostrarJustificacion: requiereJustificacion,
  });
  assert.deepEqual(payload, { monId: "USD", monCotiz: 1500 });
  assert.equal("exchangeRateJustification" in payload, false);
});

test("Contrato A4 — usuario tipea A MANO exactamente el techo (sin pasarse) → NO hay acomodo, la justificación se pide como siempre", () => {
  const tipeadoIgualAlTecho = "1235.5";
  const topeDelDia = 1235.5;

  // acomodarAlTope no debe activarse: el número ya entra tal cual.
  assert.equal(acomodarAlTope(tipeadoIgualAlTecho, topeDelDia), null);

  // Como no hubo acomodo (fueAcomodadoAlTope=false) y el número difiere del
  // sugerido, la regla de siempre sigue pidiendo justificación.
  const requiereJustificacion = debeMostrarJustificacionTC({
    tipoCambioEscrito: tipeadoIgualAlTecho,
    tipoCambioSugerido: 1234.5,
    huboSugerencia: true,
    fueAcomodadoAlTope: false,
  });
  assert.equal(requiereJustificacion, true);
});

test("construirCamposUSDParaPayload — A3 loCompletaElSistema=true → ignora tipoCambio/justificación, manda solo un placeholder que el backend descarta", () => {
  // No hubo casillero que el vendedor haya llenado: aunque por error llegara un
  // tipoCambio o justificación en los params, esta rama los ignora por completo.
  const payload = construirCamposUSDParaPayload({
    tipoCambio: "999",
    justificacion: "esto no debería viajar",
    mostrarJustificacion: true,
    loCompletaElSistema: true,
  });
  assert.deepEqual(payload, { monId: "USD", monCotiz: 1 });
});
