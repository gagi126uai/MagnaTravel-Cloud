import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  buildVariantSuggestionFields,
  resolverCamposAlCambiarVariante,
} from "./variantPriceSuggestionLogic.js";

describe("buildVariantSuggestionFields", () => {
  it("sin sugerencia (producto nunca vendido): no precarga ni muestra renglón gris", () => {
    const campos = buildVariantSuggestionFields(null);
    assert.deepEqual(campos, { debeprecargarPrecio: false, price: "", currency: null, hintText: null });
  });

  it("isSameVariant=true: precarga precio y moneda (V9=A)", () => {
    const campos = buildVariantSuggestionFields({
      isSameVariant: true, price: 48, currency: "USD", suggestionText: "Último precio: Ola · US$ 48 · 22/05/2026",
    });
    assert.equal(campos.debeprecargarPrecio, true);
    assert.equal(campos.price, "48");
    assert.equal(campos.currency, "USD");
    assert.equal(campos.hintText, "Último precio: Ola · US$ 48 · 22/05/2026");
  });

  it("isSameVariant=false: casillero vacío, pero el renglón gris SÍ se arma (V9=A)", () => {
    const campos = buildVariantSuggestionFields({
      isSameVariant: false, price: 70, currency: "USD",
      suggestionText: "No hay precio de esa habitación. El de \"Triple\" es US$ 70 (Ola · 03/07/2026).",
    });
    assert.equal(campos.debeprecargarPrecio, false);
    assert.equal(campos.price, "");
    assert.equal(campos.currency, null);
    assert.match(campos.hintText, /Triple/);
  });
});

describe("resolverCamposAlCambiarVariante", () => {
  it("precio y moneda sin tocar: los dos se acomodan solos con la variante nueva", () => {
    const resultado = resolverCamposAlCambiarVariante({
      estaPrecioTocado: false,
      estaMonedaTocada: false,
      suggestion: { isSameVariant: true, price: 70, currency: "USD", suggestionText: "Último precio: Ola · US$ 70" },
    });
    assert.equal(resultado.debeActualizarPrecio, true);
    assert.equal(resultado.price, "70");
    assert.equal(resultado.debeActualizarMoneda, true);
    assert.equal(resultado.currency, "USD");
  });

  it("fix #4: moneda tocada a mano SIN tocar el precio — el precio se acomoda solo, la moneda NUNCA (V10=A)", () => {
    const resultado = resolverCamposAlCambiarVariante({
      estaPrecioTocado: false,
      estaMonedaTocada: true,
      suggestion: { isSameVariant: true, price: 70, currency: "USD", suggestionText: "Último precio: Ola · US$ 70" },
    });
    assert.equal(resultado.debeActualizarPrecio, true);
    assert.equal(resultado.price, "70");
    assert.equal(resultado.debeActualizarMoneda, false);
    assert.equal(resultado.currency, null);
  });

  it("precio tocado a mano SIN tocar la moneda: la moneda se acomoda sola, el precio NUNCA", () => {
    const resultado = resolverCamposAlCambiarVariante({
      estaPrecioTocado: true,
      estaMonedaTocada: false,
      suggestion: { isSameVariant: true, price: 70, currency: "USD" },
    });
    assert.equal(resultado.debeActualizarPrecio, false);
    assert.equal(resultado.price, null);
    assert.equal(resultado.debeActualizarMoneda, true);
    assert.equal(resultado.currency, "USD");
  });

  it("precio y moneda tocados: ninguno se toca, ni para vaciarlo (V10=A) — pero el hint sigue viajando", () => {
    const resultado = resolverCamposAlCambiarVariante({
      estaPrecioTocado: true,
      estaMonedaTocada: true,
      suggestion: { isSameVariant: false, price: 70, currency: "USD", suggestionText: "otra habitación..." },
    });
    assert.equal(resultado.debeActualizarPrecio, false);
    assert.equal(resultado.debeActualizarMoneda, false);
    // El renglón gris SÍ se sigue actualizando aunque los campos estén protegidos: es
    // solo información para que el vendedor compare, nunca pisa lo que ya escribió.
    assert.equal(resultado.hintText, "otra habitación...");
  });

  it("fix #5: campo VACÍO pero NUNCA tocado sigue siendo territorio del sistema (V9=A)", () => {
    // Este es el caso puntual del bug: un casillero vacío que el vendedor jamás editó
    // (no hay onChange disparado todavía) tiene que dejarse precargar igual que uno que
    // sí llegó a tener una sugerencia anterior — "vacío" y "tocado" NO son lo mismo.
    const resultado = resolverCamposAlCambiarVariante({
      estaPrecioTocado: false,
      estaMonedaTocada: false,
      suggestion: { isSameVariant: true, price: 48, currency: "USD", suggestionText: "Último precio: Ola · US$ 48" },
    });
    assert.equal(resultado.debeActualizarPrecio, true);
    assert.equal(resultado.price, "48");
    assert.equal(resultado.debeActualizarMoneda, true);
    assert.equal(resultado.currency, "USD");
  });

  it("tocados + sin sugerencia nueva: no revienta, el hint queda null", () => {
    const resultado = resolverCamposAlCambiarVariante({ estaPrecioTocado: true, estaMonedaTocada: true, suggestion: null });
    assert.equal(resultado.debeActualizarPrecio, false);
    assert.equal(resultado.debeActualizarMoneda, false);
    assert.equal(resultado.hintText, null);
  });

  it("fix ronda 3 (BLOQUEANTE): montaje en modo edición — precio ya tocado (seedeado por isEditing) + sin sugerencia todavía → NO TOCA NADA", () => {
    // Reproduce exactamente el bug reportado: un servicio YA GUARDADO se abre para
    // editar (HotelInlineForm/FlightInlineForm/TransferInlineForm siembran los flags en
    // `isEditing`, ver esos componentes), y el efecto corre en el montaje con
    // `sugerenciaVariante` todavía en null porque la consulta real recién se disparó y no
    // resolvió. Con el precio ya marcado como "tocado" (por venir de un dato guardado,
    // no de una sugerencia), el precio/moneda cargados NUNCA se pisan con "".
    const resultado = resolverCamposAlCambiarVariante({
      estaPrecioTocado: true,
      estaMonedaTocada: true,
      suggestion: null,
    });
    assert.equal(resultado.debeActualizarPrecio, false);
    assert.equal(resultado.price, null);
    assert.equal(resultado.debeActualizarMoneda, false);
    assert.equal(resultado.currency, null);
  });

  it("caso legítimo a preservar: precio SIN tocar que pasa de tener sugerencia (objeto) a no tenerla (null) → SÍ limpia", () => {
    // Este es el flujo que el fix de arriba NO debe romper: el vendedor elige un
    // producto (precio queda sin tocar, territorio del sistema), después cambia de
    // habitación a una sin precio conocido — ahí el casillero SÍ se tiene que vaciar,
    // porque nunca fue un dato guardado, fue una sugerencia que dejó de aplicar.
    const conSugerencia = resolverCamposAlCambiarVariante({
      estaPrecioTocado: false,
      estaMonedaTocada: false,
      suggestion: { isSameVariant: true, price: 70, currency: "USD", suggestionText: "Último precio: Ola · US$ 70" },
    });
    assert.equal(conSugerencia.debeActualizarPrecio, true);
    assert.equal(conSugerencia.price, "70");

    // Cambió de habitación: la sugerencia ahora es null (sin precio para esa combinación).
    const sinSugerencia = resolverCamposAlCambiarVariante({
      estaPrecioTocado: false,
      estaMonedaTocada: false,
      suggestion: null,
    });
    assert.equal(sinSugerencia.debeActualizarPrecio, true);
    assert.equal(sinSugerencia.price, "");
  });

  // ─── Fix #8 (auditoría de coherencia 2026-08-10) ────────────────────────────────
  // Repro real del bug: el vendedor elige un producto, `handleSelectExisting` precarga
  // el precio de la venta real (el casillero YA tiene un valor). Milisegundos después
  // llega la respuesta de useVariantPriceSuggestion para la habitación por default —
  // si esa combinación nunca se vendió, la sugerencia es null/vacía. Sin este fix, el
  // efecto pisaba el precio recién precargado con "".

  it("fix #8: sugerencia vacía pero el casillero YA tiene un precio con valor — NO se borra (solo el renglón gris habla)", () => {
    const resultado = resolverCamposAlCambiarVariante({
      estaPrecioTocado: false,
      estaMonedaTocada: false,
      suggestion: null,
      precioActual: "48000",
    });
    assert.equal(resultado.debeActualizarPrecio, false);
    assert.equal(resultado.price, null);
  });

  it("fix #8: mismo caso pero con suggestion.isSameVariant=false (otra habitación, con precio ajeno) — tampoco borra el actual", () => {
    const resultado = resolverCamposAlCambiarVariante({
      estaPrecioTocado: false,
      estaMonedaTocada: false,
      suggestion: { isSameVariant: false, price: 70, currency: "USD", suggestionText: "El de Triple es US$ 70" },
      precioActual: "48000",
    });
    assert.equal(resultado.debeActualizarPrecio, false);
    assert.equal(resultado.price, null);
    // El renglón gris SÍ se actualiza igual (es informativo, nunca pisa el casillero)
    assert.match(resultado.hintText, /Triple/);
  });

  it("fix #8: sugerencia vacía y el casillero YA está vacío — no hay nada que preservar, sigue 'actualizando' (a vacío, sin cambio real)", () => {
    const resultado = resolverCamposAlCambiarVariante({
      estaPrecioTocado: false,
      estaMonedaTocada: false,
      suggestion: null,
      precioActual: "",
    });
    assert.equal(resultado.debeActualizarPrecio, true);
    assert.equal(resultado.price, "");
  });

  it("fix #8: sugerencia CON valor real para la MISMA variante — se actualiza igual, aunque ya hubiera un precio distinto (nueva sugerencia legítima, no un borrado)", () => {
    const resultado = resolverCamposAlCambiarVariante({
      estaPrecioTocado: false,
      estaMonedaTocada: false,
      suggestion: { isSameVariant: true, price: 55, currency: "USD", suggestionText: "Último precio: Delfos · US$ 55" },
      precioActual: "48000",
    });
    assert.equal(resultado.debeActualizarPrecio, true);
    assert.equal(resultado.price, "55");
  });

  it("fix #8: precio tocado a mano + sugerencia vacía + precioActual con valor — sigue protegido por la regla de siempre (ni entra a evaluar el nuevo guard)", () => {
    const resultado = resolverCamposAlCambiarVariante({
      estaPrecioTocado: true,
      estaMonedaTocada: false,
      suggestion: null,
      precioActual: "48000",
    });
    assert.equal(resultado.debeActualizarPrecio, false);
    assert.equal(resultado.price, null);
  });
});

// ─── Secuencia (regresión #1+#6, re-review 2026-08-10) ────────────────────────────────
// La re-review encontró que `setPrecioTocadoPorElUsuario(false)` se llamaba por error en
// CADA tecleo del buscador (agregado junto con el fix #6) — eso dejaba la puerta abierta
// para que la sugerencia por variante pisara un precio que el vendedor acababa de tocar
// a mano, apenas escribía una letra más en el buscador. El fix: ese reset SOLO pasa al
// elegir/crear un producto (como siempre fue) o al `onChange` del propio campo — nunca
// por tipear en el buscador.

describe("Secuencia: tocar el precio a mano y seguir escribiendo en el buscador — la variante no lo pisa", () => {
  it("precioTocadoPorElUsuario en true, tal cual queda después de tocar el campo, bloquea la sugerencia aunque el vendedor siga escribiendo en el buscador", () => {
    // Paso 1: el vendedor tipea el precio a mano → el form (fuera de esta función) prende
    // `precioTocadoPorElUsuario = true` en el onChange de ESE campo — eso es lo único que
    // simulamos acá, porque es lo único que le importa a esta función pura.
    const precioTocadoPorElUsuario = true;

    // Paso 2 (lo que el bug rompía): el vendedor sigue escribiendo en el CASILLERO DE
    // BÚSQUEDA de producto (buscando otro hotel) — el fix #6 apaga `camposSugeridos`
    // ahí, pero `precioTocadoPorElUsuario` NO tiene que tocarse: sigue en `true`.
    // (No hay nada que llamar acá: el punto es que el flag NO cambió.)

    // Paso 3: llega una respuesta nueva de useVariantPriceSuggestion (por ejemplo, para
    // la habitación por default) — con el precio protegido, NUNCA se pisa, tenga o no
    // tenga sugerencia la variante nueva.
    const conSugerencia = resolverCamposAlCambiarVariante({
      estaPrecioTocado: precioTocadoPorElUsuario,
      estaMonedaTocada: false,
      suggestion: { isSameVariant: true, price: 999, currency: "USD", suggestionText: "Último precio: Ola · US$ 999" },
      precioActual: "48000",
    });
    assert.equal(conSugerencia.debeActualizarPrecio, false);
    assert.equal(conSugerencia.price, null);

    const sinSugerencia = resolverCamposAlCambiarVariante({
      estaPrecioTocado: precioTocadoPorElUsuario,
      estaMonedaTocada: false,
      suggestion: null,
      precioActual: "48000",
    });
    assert.equal(sinSugerencia.debeActualizarPrecio, false);
    assert.equal(sinSugerencia.price, null);
  });
});
