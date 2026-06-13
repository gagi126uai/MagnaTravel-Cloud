/**
 * Tests de lógica pura para EmitirFacturaInline.
 *
 * Testea las funciones exportadas del componente que encapsulan decisiones
 * críticas de facturación fiscal. Corren con Node puro sin bundler.
 *
 * Decisiones cubiertas:
 *   - elegirGrupoPrecarga: la regla de seguridad B1 (nunca cargar USD como ARS)
 *   - hayDescuadre: si el total del formulario difiere del sugerido
 *   - validarCamposUSD: validación del tipo de cambio para facturas en dólares
 *
 * Cómo correr: node --test src/features/reservas/components/emitirFacturaInline.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura: copiada de EmitirFacturaInline.jsx ─────────────────────────
// El runner es Node puro (sin Vite/JSX), así que replicamos las funciones aquí.
// Si cambia la función original en el JSX, actualizar acá también.
// (Este patrón es el mismo que usan el resto de los tests .mjs del proyecto.)

/**
 * Elige qué grupo de items precargar al abrir el formulario.
 *
 * Regla de seguridad fiscal (B1):
 *   Con flag OFF la moneda efectiva es ARS → SOLO cargar grupo ARS.
 *   Si no hay grupo ARS → null (no cargar USD como ARS).
 *   Con flag ON → ARS preferido, si no existe el primero disponible.
 */
function elegirGrupoPrecarga(grupos, flagMultimonedaOn) {
  if (!Array.isArray(grupos) || grupos.length === 0) return null;

  if (!flagMultimonedaOn) {
    return grupos.find((g) => g.currency === "ARS") ?? null;
  }

  const grupoARS = grupos.find((g) => g.currency === "ARS");
  return grupoARS ?? grupos[0];
}

/**
 * Determina si hay un descuadre entre el total armado y el sugerido.
 */
function hayDescuadre(totalItems, suggestedTotal, tolerancia = 0.5) {
  if (typeof suggestedTotal !== "number" || suggestedTotal <= 0) return false;
  const diferencia = Math.abs(totalItems - suggestedTotal);
  return diferencia > tolerancia;
}

/**
 * Valida los campos de tipo de cambio para facturas en USD.
 * Devuelve string de error, o null si todo está bien.
 */
function validarCamposUSD(tipoCambio, justificacion) {
  const tcNum = Number(tipoCambio);
  if (!tipoCambio || isNaN(tcNum) || tcNum <= 0) {
    return "Ingresá el tipo de cambio para facturas en dólares.";
  }
  if (tcNum === 1) {
    return "El tipo de cambio no puede ser 1. Ingresá el valor en pesos del dólar (ej: 1200).";
  }
  if (!String(justificacion ?? "").trim()) {
    return "Ingresá la justificación del tipo de cambio.";
  }
  return null;
}

// ─── Tests: elegirGrupoPrecarga ───────────────────────────────────────────────

// Caso B1 crítico: con flag OFF y reserva solo con servicios en USD,
// la función debe devolver null en lugar del grupo USD.
// Devolverlo habría cargado montos en dólares en un comprobante ARS.
test("elegirGrupoPrecarga — B1 CRÍTICO: flag OFF + solo USD → null (no cargar USD como ARS)", () => {
  const grupos = [
    { currency: "USD", items: [{ description: "Vuelo", unitPrice: 500 }], suggestedTotal: 500 },
  ];
  const resultado = elegirGrupoPrecarga(grupos, false);
  assert.equal(resultado, null, "Con flag OFF y solo USD, debe devolver null para no facturar USD como pesos");
});

test("elegirGrupoPrecarga — flag OFF + grupos ARS y USD → devuelve solo el ARS", () => {
  const grupoARS = { currency: "ARS", items: [{ description: "Hotel", unitPrice: 80000 }], suggestedTotal: 80000 };
  const grupoUSD = { currency: "USD", items: [{ description: "Vuelo", unitPrice: 500 }], suggestedTotal: 500 };
  const grupos = [grupoARS, grupoUSD];

  const resultado = elegirGrupoPrecarga(grupos, false);
  assert.equal(resultado?.currency, "ARS", "Con flag OFF debe devolver el grupo ARS aunque haya USD también");
  assert.equal(resultado?.suggestedTotal, 80000);
});

test("elegirGrupoPrecarga — flag OFF + solo ARS → devuelve el grupo ARS", () => {
  const grupos = [
    { currency: "ARS", items: [{ description: "Paquete Bariloche", unitPrice: 120000 }], suggestedTotal: 120000 },
  ];
  const resultado = elegirGrupoPrecarga(grupos, false);
  assert.equal(resultado?.currency, "ARS");
});

test("elegirGrupoPrecarga — flag OFF + lista vacía → null", () => {
  const resultado = elegirGrupoPrecarga([], false);
  assert.equal(resultado, null);
});

test("elegirGrupoPrecarga — flag OFF + null → null (no lanza)", () => {
  const resultado = elegirGrupoPrecarga(null, false);
  assert.equal(resultado, null);
});

test("elegirGrupoPrecarga — flag ON + ARS y USD → devuelve ARS (preferencia)", () => {
  // Con flag ON se mantiene el comportamiento original: ARS preferido.
  const grupoARS = { currency: "ARS", items: [], suggestedTotal: 80000 };
  const grupoUSD = { currency: "USD", items: [], suggestedTotal: 500 };
  const grupos = [grupoUSD, grupoARS]; // ARS no es el primero en el array

  const resultado = elegirGrupoPrecarga(grupos, true);
  assert.equal(resultado?.currency, "ARS", "Con flag ON debe preferir ARS aunque no sea el primero");
});

test("elegirGrupoPrecarga — flag ON + solo USD → devuelve USD (no hay ARS, es válido con flag ON)", () => {
  // Con flag ON el usuario puede elegir emitir en USD explícitamente,
  // así que precargar el único grupo disponible (USD) es correcto.
  const grupos = [
    { currency: "USD", items: [{ description: "Vuelo", unitPrice: 1000 }], suggestedTotal: 1000 },
  ];
  const resultado = elegirGrupoPrecarga(grupos, true);
  assert.equal(resultado?.currency, "USD", "Con flag ON y solo USD, precargar USD es correcto (el usuario puede cambiarlo)");
});

test("elegirGrupoPrecarga — flag ON + lista vacía → null", () => {
  const resultado = elegirGrupoPrecarga([], true);
  assert.equal(resultado, null);
});

// ─── Tests: hayDescuadre ─────────────────────────────────────────────────────

test("hayDescuadre — dentro de tolerancia → false (sin aviso)", () => {
  // El total puede diferir hasta $0.50 sin mostrar el aviso (redondeos de decimales).
  const resultado = hayDescuadre(1000.30, 1000.00, 0.5);
  assert.equal(resultado, false, "Diferencia de $0.30 está dentro de la tolerancia de $0.50");
});

test("hayDescuadre — exactamente en el límite → false", () => {
  const resultado = hayDescuadre(1000.50, 1000.00, 0.5);
  // 1000.50 - 1000.00 = 0.50 que es igual al límite, no mayor → no hay descuadre
  assert.equal(resultado, false, "Diferencia exactamente en el límite no debe mostrar aviso");
});

test("hayDescuadre — supera tolerancia → true (mostrar franja)", () => {
  const resultado = hayDescuadre(1001.00, 1000.00, 0.5);
  assert.equal(resultado, true, "Diferencia de $1.00 supera la tolerancia de $0.50 → aviso");
});

test("hayDescuadre — total menor al sugerido supera tolerancia → true", () => {
  // El usuario editó los renglones y factura menos de lo vendido.
  const resultado = hayDescuadre(95000, 100000, 0.5);
  assert.equal(resultado, true, "Diferencia de $5000 por debajo del sugerido → aviso");
});

test("hayDescuadre — total mayor al sugerido supera tolerancia → true", () => {
  // El usuario agregó un renglón extra que no estaba en los servicios.
  const resultado = hayDescuadre(105000, 100000, 0.5);
  assert.equal(resultado, true, "Diferencia de $5000 por encima del sugerido → aviso");
});

test("hayDescuadre — suggestedTotal 0 → false (sin sugerido no hay descuadre posible)", () => {
  // Si no hay servicios confirmados, suggestedTotal es 0 o null. No mostrar aviso.
  const resultado = hayDescuadre(1000, 0, 0.5);
  assert.equal(resultado, false, "Sin sugerido (0) no se puede hablar de descuadre");
});

test("hayDescuadre — suggestedTotal negativo → false", () => {
  const resultado = hayDescuadre(1000, -100, 0.5);
  assert.equal(resultado, false, "Sugerido negativo no tiene sentido, no mostrar aviso");
});

test("hayDescuadre — tolerancia default 0.5 aplicada correctamente", () => {
  // Sin pasar tolerancia, usa 0.5 por defecto.
  assert.equal(hayDescuadre(100.40, 100, undefined), false, "0.40 < 0.50 → no hay descuadre");
  assert.equal(hayDescuadre(100.60, 100, undefined), true, "0.60 > 0.50 → hay descuadre");
});

test("hayDescuadre — totales iguales → false", () => {
  const resultado = hayDescuadre(50000, 50000, 0.5);
  assert.equal(resultado, false);
});

// ─── Tests: validarCamposUSD ──────────────────────────────────────────────────

test("validarCamposUSD — todo válido → null (sin error)", () => {
  // TC 1200 y justificación completa → puede emitirse.
  const resultado = validarCamposUSD("1200", "Dólar BNA vendedor divisa del 13/06/2026");
  assert.equal(resultado, null);
});

test("validarCamposUSD — TC vacío → error de TC faltante", () => {
  const resultado = validarCamposUSD("", "Justificación válida");
  assert.ok(typeof resultado === "string" && resultado.length > 0, "Debe devolver error");
  assert.ok(resultado.includes("tipo de cambio"), `El error debe mencionar tipo de cambio: '${resultado}'`);
});

test("validarCamposUSD — TC cero → error", () => {
  const resultado = validarCamposUSD("0", "Justificación válida");
  assert.ok(resultado !== null);
});

test("validarCamposUSD — TC negativo → error", () => {
  const resultado = validarCamposUSD("-100", "Justificación válida");
  assert.ok(resultado !== null);
});

test("validarCamposUSD — TC = 1 → error específico (no puede ser 1)", () => {
  // TC = 1 significaría 1 peso por dólar, que es claramente un error de tipeo.
  const resultado = validarCamposUSD("1", "Justificación válida");
  assert.ok(typeof resultado === "string");
  assert.ok(resultado.includes("no puede ser 1"), `El error debe explicar por qué no puede ser 1: '${resultado}'`);
});

test("validarCamposUSD — TC válido pero justificación vacía → error", () => {
  const resultado = validarCamposUSD("1200", "");
  assert.ok(typeof resultado === "string");
  assert.ok(resultado.includes("justificación"), `El error debe mencionar la justificación: '${resultado}'`);
});

test("validarCamposUSD — justificación solo espacios → error (no se acepta)", () => {
  const resultado = validarCamposUSD("1200", "   ");
  assert.ok(resultado !== null, "Solo espacios no es una justificación válida");
});

test("validarCamposUSD — TC como número (no string) → válido si es > 1", () => {
  // El campo llega como string del input pero la función debe tolerar number también.
  const resultado = validarCamposUSD(1200, "Justificación completa del TC");
  assert.equal(resultado, null, "TC como número también debe funcionar");
});

test("validarCamposUSD — TC = 1.5 (dólar oficial no es 1) → válido, sin error", () => {
  // Aunque 1.5 parece bajo, la función solo rechaza exactamente 1.
  // La validación de rango de mercado es responsabilidad del operador y del backend.
  const resultado = validarCamposUSD("1.5", "Tipo de cambio oficial fijado");
  assert.equal(resultado, null);
});

// ─── Test de integración de regla B1 (flujo completo de precarga) ─────────────

test("regla B1 end-to-end: flag OFF + solo USD → soloServiciosUSD=true + items vacío", () => {
  // Simula la lógica completa que ejecuta el useEffect de carga de sugeridos:
  //   1. grupos = solo USD
  //   2. elegirGrupoPrecarga(grupos, false) = null
  //   3. El componente arranca con item genérico en cero
  //   4. soloServiciosUSD = true → el aviso se muestra

  const grupos = [
    { currency: "USD", items: [{ description: "Paquete Caribe", unitPrice: 2000 }], suggestedTotal: 2000 },
  ];
  const flagMultimonedaOn = false;

  const grupoPrecarga = elegirGrupoPrecarga(grupos, flagMultimonedaOn);
  assert.equal(grupoPrecarga, null, "No debe precargar el grupo USD");

  // La lógica del componente usa el grupo null para saber que debe mostrar el aviso.
  // soloServiciosUSD = !flag && grupos.length > 0 && todos son NOT ARS
  const soloServiciosUSD =
    !flagMultimonedaOn &&
    grupos.length > 0 &&
    grupos.every((g) => g.currency !== "ARS");
  assert.equal(soloServiciosUSD, true, "Con flag OFF y solo USD, soloServiciosUSD debe ser true");

  // Al no precargar, los items quedan en blanco (item genérico en 0).
  // El total del formulario sería 0, que no coincide con los 2000 USD del grupo.
  // PERO la franja de descuadre NO debe aparecer (hayDescuadre compara contra
  // el grupo de la moneda EFECTIVA, que es ARS, no USD → suggestedTotal ARS = 0).
  const suggestedTotalARS = 0; // No hay grupo ARS
  assert.equal(hayDescuadre(0, suggestedTotalARS, 0.5), false, "Sin sugerido ARS no hay descuadre falso");
});
