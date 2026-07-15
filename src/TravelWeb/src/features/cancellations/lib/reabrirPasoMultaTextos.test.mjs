/**
 * Tests de los textos del "Reabrir el paso de la multa" (spec
 * docs/ux/2026-07-14-config-multas-proveedor.md, Pieza 3).
 *
 * Cómo correr:
 *   node --test src/features/cancellations/lib/reabrirPasoMultaTextos.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import {
  ENLACE_REABRIR_PASO_MULTA,
  TITULO_PANEL_REABRIR_PASO_MULTA,
  EXPLICACION_REABRIR_PASO_MULTA,
  textoConfirmacionReabrirPasoMulta,
} from "./reabrirPasoMultaTextos.js";

test("ENLACE_REABRIR_PASO_MULTA: texto exacto de la spec (reemplaza el viejo 'Deshacer: el operador sí cobró una multa')", () => {
  assert.equal(ENLACE_REABRIR_PASO_MULTA, "Reabrir el paso de la multa");
});

test("TITULO_PANEL_REABRIR_PASO_MULTA: texto exacto de la spec", () => {
  assert.equal(TITULO_PANEL_REABRIR_PASO_MULTA, "Reabrir el paso de la multa");
});

test("EXPLICACION_REABRIR_PASO_MULTA: texto exacto de la spec, aclara que no hay comprobante", () => {
  assert.equal(
    EXPLICACION_REABRIR_PASO_MULTA,
    "Volvés a la pregunta '¿el operador cobró una multa?'. No se toca ningún comprobante: este cierre nunca emitió ninguno."
  );
});

test("textoConfirmacionReabrirPasoMulta: incluye el número de reserva y la misma idea de la explicación", () => {
  const texto = textoConfirmacionReabrirPasoMulta(1234);
  assert.equal(
    texto,
    "Volvés a la pregunta '¿el operador cobró una multa?' de la reserva 1234. No se toca ningún comprobante: este cierre nunca emitió ninguno."
  );
});

test("textoConfirmacionReabrirPasoMulta: funciona con el número de reserva como string (formato que ya usa el resto de la ficha)", () => {
  const texto = textoConfirmacionReabrirPasoMulta("2026-0456");
  assert.match(texto, /de la reserva 2026-0456\./);
});

test("Los tres textos NUNCA mencionan 'nota de débito' ni jerga interna (regla de voz de la guía UX)", () => {
  const textos = [
    ENLACE_REABRIR_PASO_MULTA,
    TITULO_PANEL_REABRIR_PASO_MULTA,
    EXPLICACION_REABRIR_PASO_MULTA,
    textoConfirmacionReabrirPasoMulta(1),
  ];
  for (const texto of textos) {
    assert.doesNotMatch(texto.toLowerCase(), /nota de d[eé]bito/);
  }
});
