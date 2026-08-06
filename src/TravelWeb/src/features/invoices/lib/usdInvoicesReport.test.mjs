/**
 * Tests de lógica pura de la solapa "Facturas en dólares" (Reportes).
 * Spec: docs/ux/specs/2026-08-06-ayuda-invisible-tc.md, Parte B.
 *
 * Cómo correr: node --test src/features/invoices/lib/usdInvoicesReport.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  formatMontoOGuion,
  formatDiferenciaConSigno,
  formatTotalesTabla,
  derivarVistaUsdInvoicesReport,
} from "./usdInvoicesReport.js";

// Formateador de prueba simple: no depende de Intl para que el test sea legible
// y no repita el formato real de formatCurrency (que ya se prueba en lib/utils).
const formatearDePrueba = (valor) => `$${valor}`;

// ─── formatMontoOGuion ────────────────────────────────────────────────────────

test("formatMontoOGuion — null (sin cobros todavía) → guion, no $0", () => {
  assert.equal(formatMontoOGuion(null, formatearDePrueba), "—");
});

test("formatMontoOGuion — undefined → guion (mismo criterio que null)", () => {
  assert.equal(formatMontoOGuion(undefined, formatearDePrueba), "—");
});

test("formatMontoOGuion — número real → lo formatea con la función recibida", () => {
  assert.equal(formatMontoOGuion(1500000, formatearDePrueba), "$1500000");
});

test("formatMontoOGuion — cero explícito SÍ se muestra (no es lo mismo que 'sin dato')", () => {
  assert.equal(formatMontoOGuion(0, formatearDePrueba), "$0");
});

// ─── formatDiferenciaConSigno ─────────────────────────────────────────────────

test("formatDiferenciaConSigno — null (sin cobros o diferencia exactamente cero) → guion", () => {
  assert.equal(formatDiferenciaConSigno(null, formatearDePrueba), "—");
});

test("formatDiferenciaConSigno — positivo (cobró más de lo facturado) → signo + adelante", () => {
  assert.equal(formatDiferenciaConSigno(265500, formatearDePrueba), "+ $265500");
});

test("formatDiferenciaConSigno — negativo (cobró menos de lo facturado) → signo − adelante, monto en valor absoluto", () => {
  assert.equal(formatDiferenciaConSigno(-1200, formatearDePrueba), "− $1200");
});

test("formatDiferenciaConSigno — cero explícito (defensivo, el backend no debería mandarlo) → sin signo", () => {
  assert.equal(formatDiferenciaConSigno(0, formatearDePrueba), "$0");
});

// ─── formatTotalesTabla ───────────────────────────────────────────────────────

test("formatTotalesTabla — arma los tres textos del pie de tabla con la misma regla que cada fila", () => {
  const resultado = formatTotalesTabla(
    { pesosDeLaFactura: 4366800, pesosCobrados: 2053500, diferencia: 265500 },
    formatearDePrueba
  );
  assert.deepEqual(resultado, {
    pesosDeLaFactura: "$4366800",
    pesosCobrados: "$2053500",
    diferencia: "+ $265500",
  });
});

test("formatTotalesTabla — sin cobros en el período → pesosCobrados y diferencia en guion", () => {
  const resultado = formatTotalesTabla(
    { pesosDeLaFactura: 553500, pesosCobrados: null, diferencia: null },
    formatearDePrueba
  );
  assert.deepEqual(resultado, {
    pesosDeLaFactura: "$553500",
    pesosCobrados: "—",
    diferencia: "—",
  });
});

// ─── derivarVistaUsdInvoicesReport (item 10: "tests de componente" — vacío/con
// datos/totales. Este proyecto no tiene jsdom/Testing Library, así que la decisión
// de qué-se-ve vive acá, en una función pura que SÍ se puede probar con Node,
// mismo patrón que resolverEstadoFiscal en EmitirFacturaInline.jsx) ──────────────

test("derivarVistaUsdInvoicesReport — respuesta VACÍA (sin facturas en el período) → hayFilas=false, sin pie de tabla", () => {
  const resultado = derivarVistaUsdInvoicesReport(
    { filas: [], totales: { pesosDeLaFactura: 0, pesosCobrados: null, diferencia: null } },
    formatearDePrueba
  );
  assert.equal(resultado.hayFilas, false, "Con la tabla vacía, el componente muestra el texto 'No hay facturas...'");
  // El pie SÍ se calcula si vino `totales` (aunque esté en cero) — es el componente
  // quien decide no dibujarlo cuando hayFilas es false, no esta función.
  assert.deepEqual(resultado.totalesFormateados, { pesosDeLaFactura: "$0", pesosCobrados: "—", diferencia: "—" });
});

test("derivarVistaUsdInvoicesReport — CON facturas y con cobros → hayFilas=true, pie de tabla con los tres montos", () => {
  const resultado = derivarVistaUsdInvoicesReport(
    {
      filas: [{ comprobanteId: "abc-123" }],
      totales: { pesosDeLaFactura: 4366800, pesosCobrados: 2053500, diferencia: 265500 },
    },
    formatearDePrueba
  );
  assert.equal(resultado.hayFilas, true);
  assert.deepEqual(resultado.totalesFormateados, {
    pesosDeLaFactura: "$4366800",
    pesosCobrados: "$2053500",
    diferencia: "+ $265500",
  });
});

test("derivarVistaUsdInvoicesReport — CON facturas pero SIN cobros todavía → hayFilas=true, pie con guiones", () => {
  const resultado = derivarVistaUsdInvoicesReport(
    {
      filas: [{ comprobanteId: "abc-123" }, { comprobanteId: "def-456" }],
      totales: { pesosDeLaFactura: 553500, pesosCobrados: null, diferencia: null },
    },
    formatearDePrueba
  );
  assert.equal(resultado.hayFilas, true);
  assert.deepEqual(resultado.totalesFormateados, { pesosDeLaFactura: "$553500", pesosCobrados: "—", diferencia: "—" });
});

test("derivarVistaUsdInvoicesReport — defensivo: filas sin totales (no debería pasar, pero no debe reventar) → totalesFormateados null", () => {
  const resultado = derivarVistaUsdInvoicesReport({ filas: [{ comprobanteId: "x" }], totales: null }, formatearDePrueba);
  assert.equal(resultado.hayFilas, true);
  assert.equal(resultado.totalesFormateados, null);
});

test("derivarVistaUsdInvoicesReport — defensivo: filas no es un array (respuesta rara del backend) → hayFilas=false, no revienta", () => {
  const resultado = derivarVistaUsdInvoicesReport({ filas: undefined, totales: null }, formatearDePrueba);
  assert.equal(resultado.hayFilas, false);
});
