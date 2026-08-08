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
});
