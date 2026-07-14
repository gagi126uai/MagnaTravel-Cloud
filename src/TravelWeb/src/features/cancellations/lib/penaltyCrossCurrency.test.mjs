/**
 * Tests de la lógica pura del bloque de conversión de moneda del panel "Corregir
 * monto y moneda" de la multa del operador (spec cerrada 2026-07-13, bug F-2026-1033).
 *
 * Corren con: node --test src/features/cancellations/lib/penaltyCrossCurrency.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  EXCHANGE_RATE_SOURCE_BNA,
  EXCHANGE_RATE_SOURCE_MANUAL,
  UMBRAL_AVISO_TC_LEJANO,
  hayCruceDeMoneda,
  tituloBloqueConversion,
  encabezadoBloqueConversion,
  explicacionMonedaFacturaCompleta,
  explicacionMonedaFacturaMinima,
  calcularMontoConvertido,
  debeMostrarAvisoTCLejano,
  resolverFuenteTC,
  validarBloqueConversion,
  bloqueConversionCompleto,
  construirCamposConversionParaPayload,
  formatFechaCorta,
  interpretarRespuestaBnaRate,
  textoEstadoDolarBna,
  debeAplicarRespuestaBna,
} from "./penaltyCrossCurrency.js";

// ============================================================================
// Sección 1: hayCruceDeMoneda — cuándo aparece el bloque
// ============================================================================

test("misma moneda (USD multa, USD factura) → NO hay cruce", () => {
  assert.equal(hayCruceDeMoneda("USD", "USD"), false);
});

test("misma moneda (ARS multa, ARS factura) → NO hay cruce", () => {
  assert.equal(hayCruceDeMoneda("ARS", "ARS"), false);
});

test("moneda cruzada (USD multa, ARS factura) → SÍ hay cruce", () => {
  assert.equal(hayCruceDeMoneda("USD", "ARS"), true);
});

test("moneda cruzada (ARS multa, USD factura) → SÍ hay cruce", () => {
  assert.equal(hayCruceDeMoneda("ARS", "USD"), true);
});

test("sin factura todavía (invoiceCurrency null) → NO hay cruce, se comporta como caso normal", () => {
  assert.equal(hayCruceDeMoneda("USD", null), false);
});

test("invoiceCurrency undefined (DTO viejo sin el campo nuevo) → NO hay cruce", () => {
  assert.equal(hayCruceDeMoneda("USD", undefined), false);
});

// ============================================================================
// Sección 2: textos del bloque — nunca "diferencia de cambio"
// ============================================================================

test("título: multa en USD, factura en ARS → 'la pasamos a pesos'", () => {
  const texto = tituloBloqueConversion("USD", "ARS");
  assert.match(texto, /dólares.*factura en pesos.*pasamos a pesos/i);
});

test("título: multa en ARS, factura en USD → 'la pasamos a dólares'", () => {
  const texto = tituloBloqueConversion("ARS", "USD");
  assert.match(texto, /pesos.*factura en dólares.*pasamos a dólares/i);
});

test("título: NUNCA contiene la frase prohibida 'diferencia de cambio'", () => {
  const textoA = tituloBloqueConversion("USD", "ARS");
  const textoB = tituloBloqueConversion("ARS", "USD");
  assert.doesNotMatch(textoA, /diferencia de cambio/i);
  assert.doesNotMatch(textoB, /diferencia de cambio/i);
});

test("encabezado: factura en pesos", () => {
  assert.equal(encabezadoBloqueConversion("ARS"), "La factura del cliente está en pesos ($)");
});

test("encabezado: factura en dólares", () => {
  assert.equal(encabezadoBloqueConversion("USD"), "La factura del cliente está en dólares (US$)");
});

// ============================================================================
// Sección 2b: explicacionMonedaFacturaCompleta / explicacionMonedaFacturaMinima
// (spec 2026-07-14 "explicación por qué la multa va en la moneda de la factura")
// ============================================================================

test("línea 1 (completa): factura en pesos, espejo dólares → texto exacto de la spec", () => {
  assert.equal(
    explicacionMonedaFacturaCompleta("ARS"),
    "La factura de esta reserva salió en pesos. Todo lo que se le cobra o se le devuelve al cliente va en esa moneda, incluida la multa — aunque el operador la haya cobrado en dólares."
  );
});

test("línea 1 (completa): factura en dólares → espejo pesos↔dólares", () => {
  assert.equal(
    explicacionMonedaFacturaCompleta("USD"),
    "La factura de esta reserva salió en dólares. Todo lo que se le cobra o se le devuelve al cliente va en esa moneda, incluida la multa — aunque el operador la haya cobrado en pesos."
  );
});

test("línea 1 (completa): NUNCA contiene la frase prohibida 'diferencia de cambio'", () => {
  assert.doesNotMatch(explicacionMonedaFacturaCompleta("ARS"), /diferencia de cambio/i);
  assert.doesNotMatch(explicacionMonedaFacturaCompleta("USD"), /diferencia de cambio/i);
});

test("línea 2 (mínima): factura en pesos → texto exacto de la spec", () => {
  assert.equal(
    explicacionMonedaFacturaMinima("ARS"),
    "La factura de esta reserva salió en pesos: el cargo al cliente va en pesos."
  );
});

test("línea 2 (mínima): factura en dólares → espejo pesos↔dólares", () => {
  assert.equal(
    explicacionMonedaFacturaMinima("USD"),
    "La factura de esta reserva salió en dólares: el cargo al cliente va en dólares."
  );
});

test("línea 2 (mínima): NUNCA contiene la frase prohibida 'diferencia de cambio'", () => {
  assert.doesNotMatch(explicacionMonedaFacturaMinima("ARS"), /diferencia de cambio/i);
  assert.doesNotMatch(explicacionMonedaFacturaMinima("USD"), /diferencia de cambio/i);
});

// ============================================================================
// Sección 3: calcularMontoConvertido — preview "→ Se le cobra al cliente $ X"
// ============================================================================

test("USD 200 multa, factura en ARS, TC 1200 → convertido = 240000 (monto x TC)", () => {
  const resultado = calcularMontoConvertido({ monto: "200", monedaMulta: "USD", invoiceCurrency: "ARS", tipoCambio: "1200" });
  assert.equal(resultado, 240000);
});

test("ARS 240000 multa, factura en USD, TC 1200 → convertido = 200 (monto / TC)", () => {
  const resultado = calcularMontoConvertido({ monto: "240000", monedaMulta: "ARS", invoiceCurrency: "USD", tipoCambio: "1200" });
  assert.equal(resultado, 200);
});

test("sin tipo de cambio cargado → null (nada que mostrar todavía, no es error)", () => {
  const resultado = calcularMontoConvertido({ monto: "200", monedaMulta: "USD", invoiceCurrency: "ARS", tipoCambio: "" });
  assert.equal(resultado, null);
});

test("sin monto cargado → null", () => {
  const resultado = calcularMontoConvertido({ monto: "", monedaMulta: "USD", invoiceCurrency: "ARS", tipoCambio: "1200" });
  assert.equal(resultado, null);
});

test("TC negativo o cero → null", () => {
  assert.equal(calcularMontoConvertido({ monto: "200", monedaMulta: "USD", invoiceCurrency: "ARS", tipoCambio: "0" }), null);
  assert.equal(calcularMontoConvertido({ monto: "200", monedaMulta: "USD", invoiceCurrency: "ARS", tipoCambio: "-5" }), null);
});

test("misma moneda (sin cruce real) → null, aunque venga un TC cargado", () => {
  const resultado = calcularMontoConvertido({ monto: "200", monedaMulta: "USD", invoiceCurrency: "USD", tipoCambio: "1200" });
  assert.equal(resultado, null);
});

test("recalcula en vivo: cambiar el monto cambia el resultado", () => {
  const r1 = calcularMontoConvertido({ monto: "100", monedaMulta: "USD", invoiceCurrency: "ARS", tipoCambio: "1000" });
  const r2 = calcularMontoConvertido({ monto: "150", monedaMulta: "USD", invoiceCurrency: "ARS", tipoCambio: "1000" });
  assert.equal(r1, 100000);
  assert.equal(r2, 150000);
});

// ============================================================================
// Sección 4: debeMostrarAvisoTCLejano — aviso suave, nunca bloquea
// ============================================================================

test("TC escrito igual al oficial → sin aviso", () => {
  assert.equal(debeMostrarAvisoTCLejano({ tipoCambioEscrito: 1200, tipoCambioReferenciaBNA: 1200 }), false);
});

test("TC escrito 10% arriba del oficial (dentro del umbral 20%) → sin aviso", () => {
  assert.equal(debeMostrarAvisoTCLejano({ tipoCambioEscrito: 1320, tipoCambioReferenciaBNA: 1200 }), false);
});

test("TC escrito exactamente en el umbral (20% de diferencia) → sin aviso (estrictamente mayor, no igual)", () => {
  assert.equal(debeMostrarAvisoTCLejano({ tipoCambioEscrito: 1440, tipoCambioReferenciaBNA: 1200 }), false);
});

test("TC escrito 30% arriba del oficial (más allá del umbral 20%) → SÍ dispara el aviso", () => {
  assert.equal(debeMostrarAvisoTCLejano({ tipoCambioEscrito: 1560, tipoCambioReferenciaBNA: 1200 }), true);
});

test("TC escrito muy por debajo del oficial (error de tipeo típico: faltó un cero) → SÍ dispara el aviso", () => {
  // Caso real que motivó el aviso: escribir 12 en vez de 1200.
  assert.equal(debeMostrarAvisoTCLejano({ tipoCambioEscrito: 12, tipoCambioReferenciaBNA: 1200 }), true);
});

test("sin cotización de referencia del BNA (caso real de la app hoy: no hay endpoint) → nunca dispara", () => {
  assert.equal(debeMostrarAvisoTCLejano({ tipoCambioEscrito: 1200, tipoCambioReferenciaBNA: null }), false);
  assert.equal(debeMostrarAvisoTCLejano({ tipoCambioEscrito: 1200, tipoCambioReferenciaBNA: undefined }), false);
});

test("sin TC escrito todavía → no dispara (nada para avisar)", () => {
  assert.equal(debeMostrarAvisoTCLejano({ tipoCambioEscrito: "", tipoCambioReferenciaBNA: 1200 }), false);
});

// ============================================================================
// Sección 5: resolverFuenteTC — la fuente se resuelve sola, no se pregunta
// ============================================================================

test("hubo sugerencia BNA y el usuario NO tocó el número → fuente BNA", () => {
  assert.equal(
    resolverFuenteTC({ fueTocadoPorElUsuario: false, huboSugerenciaBNA: true }),
    EXCHANGE_RATE_SOURCE_BNA
  );
});

test("hubo sugerencia BNA pero el usuario SÍ pisó el número → fuente Manual", () => {
  assert.equal(
    resolverFuenteTC({ fueTocadoPorElUsuario: true, huboSugerenciaBNA: true }),
    EXCHANGE_RATE_SOURCE_MANUAL
  );
});

test("no hubo sugerencia BNA (caso real de la app hoy) → siempre Manual, se haya tocado o no", () => {
  assert.equal(
    resolverFuenteTC({ fueTocadoPorElUsuario: false, huboSugerenciaBNA: false }),
    EXCHANGE_RATE_SOURCE_MANUAL
  );
  assert.equal(
    resolverFuenteTC({ fueTocadoPorElUsuario: true, huboSugerenciaBNA: false }),
    EXCHANGE_RATE_SOURCE_MANUAL
  );
});

// ============================================================================
// Sección 6: validarBloqueConversion — los 3 campos obligatorios
// ============================================================================

test("todo cargado, fuente BNA → sin errores, sin exigir justificación", () => {
  const errores = validarBloqueConversion({
    fecha: "2026-07-05",
    tipoCambio: "1200",
    fuente: EXCHANGE_RATE_SOURCE_BNA,
    justificacion: "",
  });
  assert.deepEqual(errores, { fecha: null, tipoCambio: null, justificacion: null });
});

test("sin fecha → error de fecha", () => {
  const errores = validarBloqueConversion({ fecha: "", tipoCambio: "1200", fuente: EXCHANGE_RATE_SOURCE_BNA, justificacion: "" });
  assert.notEqual(errores.fecha, null);
});

test("fecha futura → error de fecha (no puede guardar), mismo criterio que el resto del panel", () => {
  const errores = validarBloqueConversion({
    fecha: "2026-08-01",
    tipoCambio: "1200",
    fuente: EXCHANGE_RATE_SOURCE_BNA,
    justificacion: "",
    hoyIso: "2026-07-13",
  });
  assert.notEqual(errores.fecha, null);
  assert.match(errores.fecha, /futura/i);
});

test("fecha de hoy (no futura) → sin error de fecha", () => {
  const errores = validarBloqueConversion({
    fecha: "2026-07-13",
    tipoCambio: "1200",
    fuente: EXCHANGE_RATE_SOURCE_BNA,
    justificacion: "",
    hoyIso: "2026-07-13",
  });
  assert.equal(errores.fecha, null);
});

test("sin tipo de cambio → error de tipo de cambio", () => {
  const errores = validarBloqueConversion({ fecha: "2026-07-05", tipoCambio: "", fuente: EXCHANGE_RATE_SOURCE_BNA, justificacion: "" });
  assert.notEqual(errores.tipoCambio, null);
});

test("tipo de cambio cero o negativo → error de tipo de cambio", () => {
  const e1 = validarBloqueConversion({ fecha: "2026-07-05", tipoCambio: "0", fuente: EXCHANGE_RATE_SOURCE_BNA, justificacion: "" });
  const e2 = validarBloqueConversion({ fecha: "2026-07-05", tipoCambio: "-10", fuente: EXCHANGE_RATE_SOURCE_BNA, justificacion: "" });
  assert.notEqual(e1.tipoCambio, null);
  assert.notEqual(e2.tipoCambio, null);
});

test("fuente Manual sin justificación → error de justificación", () => {
  const errores = validarBloqueConversion({ fecha: "2026-07-05", tipoCambio: "1200", fuente: EXCHANGE_RATE_SOURCE_MANUAL, justificacion: "" });
  assert.notEqual(errores.justificacion, null);
});

test("fuente Manual con justificación cargada → sin error de justificación", () => {
  const errores = validarBloqueConversion({
    fecha: "2026-07-05",
    tipoCambio: "1200",
    fuente: EXCHANGE_RATE_SOURCE_MANUAL,
    justificacion: "El operador me pasó el ticket en dólares.",
  });
  assert.equal(errores.justificacion, null);
});

test("fuente Manual con justificación de solo espacios → sigue exigiendo (trim)", () => {
  const errores = validarBloqueConversion({ fecha: "2026-07-05", tipoCambio: "1200", fuente: EXCHANGE_RATE_SOURCE_MANUAL, justificacion: "   " });
  assert.notEqual(errores.justificacion, null);
});

// ============================================================================
// Sección 7: bloqueConversionCompleto → gatea "Guardar corrección"
// ============================================================================

test("sin errores → bloque completo (puede guardar)", () => {
  assert.equal(bloqueConversionCompleto({ fecha: null, tipoCambio: null, justificacion: null }), true);
});

test("con cualquier error → bloque incompleto (no puede guardar)", () => {
  assert.equal(bloqueConversionCompleto({ fecha: "obligatoria", tipoCambio: null, justificacion: null }), false);
  assert.equal(bloqueConversionCompleto({ fecha: null, tipoCambio: "obligatorio", justificacion: null }), false);
  assert.equal(bloqueConversionCompleto({ fecha: null, tipoCambio: null, justificacion: "obligatoria" }), false);
});

// ============================================================================
// Sección 8: construirCamposConversionParaPayload — el contrato con el backend
// ============================================================================

test("misma moneda (hayCruce=false) → payload BYTE-IDÉNTICO a hoy: objeto vacío, ningún campo nuevo", () => {
  const campos = construirCamposConversionParaPayload({
    hayCruce: false,
    tipoCambio: "1200",
    fuente: EXCHANGE_RATE_SOURCE_MANUAL,
    fecha: "2026-07-05",
    justificacion: "algo",
  });
  assert.deepEqual(campos, {});
  assert.equal(Object.keys(campos).length, 0);
});

test("cruce con fuente BNA → manda exchangeRate/exchangeRateSource/exchangeRateDate, SIN justificación", () => {
  const campos = construirCamposConversionParaPayload({
    hayCruce: true,
    tipoCambio: "1200",
    fuente: EXCHANGE_RATE_SOURCE_BNA,
    fecha: "2026-07-05",
    justificacion: "",
  });
  assert.equal(campos.exchangeRate, 1200);
  assert.equal(campos.exchangeRateSource, EXCHANGE_RATE_SOURCE_BNA);
  assert.equal(campos.exchangeRateDate, "2026-07-05");
  assert.equal("exchangeRateJustification" in campos, false);
});

test("cruce con fuente Manual → además manda exchangeRateJustification (trimmed)", () => {
  const campos = construirCamposConversionParaPayload({
    hayCruce: true,
    tipoCambio: "1500",
    fuente: EXCHANGE_RATE_SOURCE_MANUAL,
    fecha: "2026-07-05",
    justificacion: "  El operador me pasó el ticket en dólares.  ",
  });
  assert.equal(campos.exchangeRateJustification, "El operador me pasó el ticket en dólares.");
});

test("exchangeRate siempre es número, aunque tipoCambio venga como string del input", () => {
  const campos = construirCamposConversionParaPayload({
    hayCruce: true,
    tipoCambio: "1234.56",
    fuente: EXCHANGE_RATE_SOURCE_BNA,
    fecha: "2026-07-05",
    justificacion: "",
  });
  assert.strictEqual(typeof campos.exchangeRate, "number");
  assert.equal(campos.exchangeRate, 1234.56);
});

// ============================================================================
// Sección 9: constantes exportadas — enums verificados contra el contrato backend
// ============================================================================

test("EXCHANGE_RATE_SOURCE_BNA = 6 (BNA_VendedorDivisa, mismo valor que RegistrarCobroInline)", () => {
  assert.equal(EXCHANGE_RATE_SOURCE_BNA, 6);
});

test("EXCHANGE_RATE_SOURCE_MANUAL = 5 (Manual, mismo valor que penaltyPayload.js/RegistrarCobroInline)", () => {
  assert.equal(EXCHANGE_RATE_SOURCE_MANUAL, 5);
});

test("UMBRAL_AVISO_TC_LEJANO es 0.20 (20%) — default de frontend, no confirmado por negocio", () => {
  assert.equal(UMBRAL_AVISO_TC_LEJANO, 0.20);
});

// ============================================================================
// Sección 10: pre-carga del dólar BNA (endpoint conectado 2026-07-14)
// GET /cancellations/bna-usd-rate?date=YYYY-MM-DD
// ============================================================================

// ─── formatFechaCorta ─────────────────────────────────────────────────────────

test("formatFechaCorta: 'YYYY-MM-DD' → 'DD/MM'", () => {
  assert.equal(formatFechaCorta("2026-07-05"), "05/07");
});

test("formatFechaCorta: fecha con un solo dígito en día/mes conserva el cero", () => {
  assert.equal(formatFechaCorta("2026-01-03"), "03/01");
});

// ─── interpretarRespuestaBnaRate — qué hacer con la respuesta del endpoint ────

test("200 con rate y rateDate → tipoCambioSugerido y fechaSugeridaReal informados", () => {
  const resultado = interpretarRespuestaBnaRate({ rate: 1234.5, rateDate: "2026-07-03" });
  assert.equal(resultado.tipoCambioSugerido, 1234.5);
  assert.equal(resultado.fechaSugeridaReal, "2026-07-03");
});

test("200 con rateDate DISTINTA de la fecha pedida (cayó fin de semana) → se usa la rateDate real", () => {
  // El operador cobró un sábado (06/07); el BNA no cotiza ese día y el backend
  // devuelve el último día hábil anterior (03/07). El front tiene que reflejar
  // la fecha REAL del dato, no la que pidió el usuario.
  const resultado = interpretarRespuestaBnaRate({ rate: 1200, rateDate: "2026-07-03" });
  assert.equal(resultado.fechaSugeridaReal, "2026-07-03");
  assert.notEqual(resultado.fechaSugeridaReal, "2026-07-06");
});

test("204 (api.get devuelve null) → sin sugerencia, ningún dato inventado", () => {
  const resultado = interpretarRespuestaBnaRate(null);
  assert.equal(resultado.tipoCambioSugerido, null);
  assert.equal(resultado.fechaSugeridaReal, null);
});

test("respuesta defensiva: rate = 0 o negativo → se trata como sin dato (nunca prellena con basura)", () => {
  assert.equal(interpretarRespuestaBnaRate({ rate: 0, rateDate: "2026-07-03" }).tipoCambioSugerido, null);
  assert.equal(interpretarRespuestaBnaRate({ rate: -5, rateDate: "2026-07-03" }).tipoCambioSugerido, null);
});

test("respuesta defensiva: sin rateDate → se trata como sin dato", () => {
  const resultado = interpretarRespuestaBnaRate({ rate: 1200, rateDate: null });
  assert.equal(resultado.tipoCambioSugerido, null);
  assert.equal(resultado.fechaSugeridaReal, null);
});

// ─── textoEstadoDolarBna — el texto de ayuda debajo del casillero ─────────────

test("con cotización (200): 'Dólar oficial del BNA del {fecha REAL}...'", () => {
  const texto = textoEstadoDolarBna({ fechaPedida: "2026-07-06", fechaSugeridaReal: "2026-07-03" });
  assert.match(texto, /Dólar oficial del BNA del 03\/07/);
  // La fecha pedida (06/07, el sábado) NO debe aparecer en el texto — el rótulo
  // usa siempre la fecha real del dato.
  assert.doesNotMatch(texto, /06\/07/);
});

test("sin cotización (204) pero con fecha elegida: 'No tenemos el dólar del BNA para el {fecha pedida}...'", () => {
  const texto = textoEstadoDolarBna({ fechaPedida: "2026-07-05", fechaSugeridaReal: null });
  assert.match(texto, /No tenemos el dólar del BNA para el 05\/07/);
  assert.match(texto, /a mano/i);
});

test("sin fecha elegida todavía: invita a elegir la fecha", () => {
  const texto = textoEstadoDolarBna({ fechaPedida: "", fechaSugeridaReal: null });
  assert.match(texto, /Elegí la fecha/i);
});

// ─── debeAplicarRespuestaBna — guarda anti respuesta tardía (race condition) ──

test("la respuesta llega para la MISMA fecha que sigue vigente → se aplica", () => {
  assert.equal(debeAplicarRespuestaBna({ fechaPedida: "2026-07-05", fechaVigente: "2026-07-05" }), true);
});

test("respuesta tardía de una fecha VIEJA (el usuario ya cambió la fecha) → NO se aplica", () => {
  // Caso real: el usuario elige 05/07, después cambia rápido a 08/07. La consulta
  // del 05/07 puede responder DESPUÉS de que el usuario ya está en 08/07 — esa
  // respuesta vieja no debe pisar lo que el usuario eligió después.
  assert.equal(debeAplicarRespuestaBna({ fechaPedida: "2026-07-05", fechaVigente: "2026-07-08" }), false);
});

test("sin fecha pedida o sin fecha vigente (edge case defensivo) → no se aplica", () => {
  assert.equal(debeAplicarRespuestaBna({ fechaPedida: "", fechaVigente: "2026-07-05" }), false);
  assert.equal(debeAplicarRespuestaBna({ fechaPedida: "2026-07-05", fechaVigente: "" }), false);
});
