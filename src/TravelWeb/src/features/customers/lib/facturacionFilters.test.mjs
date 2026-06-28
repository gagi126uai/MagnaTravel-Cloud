/**
 * Tests de lógica pura para los filtros de Facturación del cliente.
 *
 * Corren con Node puro sin bundler ni React:
 *   node --test src/features/customers/lib/facturacionFilters.test.mjs
 *
 * Cobertura:
 *   - calcularPeriodoPorDefecto: rango de fechas correcto (90 días)
 *   - formatTipoComprobante: mapeo código ARCA → texto español
 *   - resolverKindComprobante: Factura/ND → cargo, NC → abono
 *   - resolverEstadoFiscal: prioridad anulando > aprobado > rechazado > en_proceso
 *   - aplicarFiltros: sin filtros, por tipo, por estado, por fecha, por número, por moneda, combinados
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica replicada inline ─────────────────────────────────────────────────
// Patrón del proyecto: los tests .mjs replican la lógica sin importar el módulo.
// Si cambia facturacionFilters.js, actualizar también acá.

const DIAS_PERIODO_DEFECTO = 90;

function calcularPeriodoPorDefecto() {
  const hoy = new Date();
  const desde = new Date(hoy);
  desde.setDate(desde.getDate() - DIAS_PERIODO_DEFECTO);
  return {
    desde: desde.toISOString().slice(0, 10),
    hasta: hoy.toISOString().slice(0, 10),
  };
}

function formatTipoComprobante(tipoComprobante) {
  const mapa = {
    1:  "Factura A",
    6:  "Factura B",
    11: "Factura C",
    51: "Factura M",
    2:  "Nota de Débito A",
    7:  "Nota de Débito B",
    12: "Nota de Débito C",
    52: "Nota de Débito M",
    3:  "Nota de Crédito A",
    8:  "Nota de Crédito B",
    13: "Nota de Crédito C",
    53: "Nota de Crédito M",
  };
  return mapa[tipoComprobante] ?? "Comprobante";
}

function resolverKindComprobante(tipoComprobante) {
  const notasDeCredito = [3, 8, 13, 53];
  return notasDeCredito.includes(tipoComprobante) ? "abono" : "cargo";
}

function resolverEstadoFiscal(invoice) {
  if (invoice.annulmentStatus === "Pending") return "anulando";
  if (invoice.resultado === "A") return "aprobado";
  if (invoice.resultado === "R") return "rechazado";
  return "en_proceso";
}

function aplicarFiltros(invoices, filters) {
  if (!Array.isArray(invoices)) return [];
  const { desde, hasta, tipo, estado, moneda, buscarNumero } = filters || {};
  return invoices.filter((invoice) => {
    if (desde) {
      const fechaFactura = invoice.createdAt ? invoice.createdAt.slice(0, 10) : "";
      if (fechaFactura < desde) return false;
    }
    if (hasta) {
      const fechaFactura = invoice.createdAt ? invoice.createdAt.slice(0, 10) : "";
      if (fechaFactura > hasta) return false;
    }
    if (tipo) {
      if (String(invoice.tipoComprobante) !== tipo) return false;
    }
    if (estado) {
      if (resolverEstadoFiscal(invoice) !== estado) return false;
    }
    // Filtro por moneda: invoice.currency viene del backend ("ARS"/"USD").
    // Si el DTO no trae moneda (legacy), se asume ARS.
    if (moneda) {
      const monedaFactura = invoice.currency ?? "ARS";
      if (monedaFactura !== moneda) return false;
    }
    if (buscarNumero && buscarNumero.trim()) {
      const texto = buscarNumero.trim().toLowerCase();
      const puntoDeVenta = String(invoice.puntoDeVenta ?? 0).padStart(5, "0");
      const numero = String(invoice.numeroComprobante ?? 0).padStart(8, "0");
      if (!`${puntoDeVenta}-${numero}`.includes(texto)) return false;
    }
    return true;
  });
}

// ─── Fixtures ────────────────────────────────────────────────────────────────

// currency: campo ISO que el backend ahora expone en InvoiceListDto ("ARS"/"USD").
// Se usa para el filtro de Moneda y para la regla de nunca mezclar monedas.
const facturaA = {
  tipoComprobante: 1, puntoDeVenta: 1, numeroComprobante: 12345,
  createdAt: "2026-06-01T10:00:00Z", resultado: "A", annulmentStatus: "None",
  currency: "ARS",
};
const facturaB = {
  tipoComprobante: 6, puntoDeVenta: 1, numeroComprobante: 67890,
  createdAt: "2026-04-15T10:00:00Z", resultado: "A", annulmentStatus: "None",
  currency: "ARS",
};
const ncB = {
  tipoComprobante: 8, puntoDeVenta: 1, numeroComprobante: 111,
  createdAt: "2026-06-10T10:00:00Z", resultado: "A", annulmentStatus: "None",
  currency: "ARS",
};
const rechazada = {
  tipoComprobante: 6, puntoDeVenta: 1, numeroComprobante: 222,
  createdAt: "2026-06-05T10:00:00Z", resultado: "R", annulmentStatus: "None",
  currency: "ARS",
};
const anulando = {
  tipoComprobante: 6, puntoDeVenta: 1, numeroComprobante: 333,
  createdAt: "2026-06-12T10:00:00Z", resultado: "A", annulmentStatus: "Pending",
  currency: "ARS",
};
const enProceso = {
  tipoComprobante: 1, puntoDeVenta: 2, numeroComprobante: 444,
  createdAt: "2026-06-15T10:00:00Z", resultado: null, annulmentStatus: "None",
  currency: "ARS",
};
// Factura en dólares: para tests de filtro de moneda
const facturaUsd = {
  tipoComprobante: 1, puntoDeVenta: 1, numeroComprobante: 99999,
  createdAt: "2026-06-20T10:00:00Z", resultado: "A", annulmentStatus: "None",
  currency: "USD",
};
// Factura sin campo currency (caso legacy): debe tratarse como ARS
const facturaLegacy = {
  tipoComprobante: 6, puntoDeVenta: 1, numeroComprobante: 55555,
  createdAt: "2026-06-03T10:00:00Z", resultado: "A", annulmentStatus: "None",
  // sin campo currency
};

const listaCompleta = [facturaA, facturaB, ncB, rechazada, anulando, enProceso];

// ─── calcularPeriodoPorDefecto ────────────────────────────────────────────────

test("calcularPeriodoPorDefecto - hasta es hoy en formato YYYY-MM-DD", () => {
  const { hasta } = calcularPeriodoPorDefecto();
  const hoyStr = new Date().toISOString().slice(0, 10);
  assert.equal(hasta, hoyStr);
});

test("calcularPeriodoPorDefecto - desde es hace exactamente 90 días", () => {
  const { desde } = calcularPeriodoPorDefecto();
  const esperado = new Date();
  esperado.setDate(esperado.getDate() - 90);
  assert.equal(desde, esperado.toISOString().slice(0, 10));
});

// ─── formatTipoComprobante ────────────────────────────────────────────────────

test("formatTipoComprobante - Factura A (1)", () => {
  assert.equal(formatTipoComprobante(1), "Factura A");
});

test("formatTipoComprobante - Factura B (6)", () => {
  assert.equal(formatTipoComprobante(6), "Factura B");
});

test("formatTipoComprobante - Factura C (11)", () => {
  assert.equal(formatTipoComprobante(11), "Factura C");
});

test("formatTipoComprobante - NC A (3)", () => {
  assert.equal(formatTipoComprobante(3), "Nota de Crédito A");
});

test("formatTipoComprobante - NC B (8)", () => {
  assert.equal(formatTipoComprobante(8), "Nota de Crédito B");
});

test("formatTipoComprobante - ND A (2)", () => {
  assert.equal(formatTipoComprobante(2), "Nota de Débito A");
});

// B4: el fallback ya NO expone el entero al usuario; devuelve texto neutro "Comprobante"
test("formatTipoComprobante - tipo desconocido → 'Comprobante' sin el número", () => {
  const resultado = formatTipoComprobante(99);
  assert.equal(resultado, "Comprobante");
});

// ─── resolverKindComprobante ──────────────────────────────────────────────────

test("resolverKindComprobante - Factura A (1) es cargo", () => {
  assert.equal(resolverKindComprobante(1), "cargo");
});

test("resolverKindComprobante - Factura B (6) es cargo", () => {
  assert.equal(resolverKindComprobante(6), "cargo");
});

test("resolverKindComprobante - ND A (2) es cargo", () => {
  assert.equal(resolverKindComprobante(2), "cargo");
});

test("resolverKindComprobante - NC A (3) es abono", () => {
  assert.equal(resolverKindComprobante(3), "abono");
});

test("resolverKindComprobante - NC B (8) es abono", () => {
  assert.equal(resolverKindComprobante(8), "abono");
});

test("resolverKindComprobante - NC C (13) es abono", () => {
  assert.equal(resolverKindComprobante(13), "abono");
});

// ─── resolverEstadoFiscal ─────────────────────────────────────────────────────

test("resolverEstadoFiscal - anulando tiene prioridad sobre aprobado", () => {
  assert.equal(
    resolverEstadoFiscal({ annulmentStatus: "Pending", resultado: "A" }),
    "anulando"
  );
});

test("resolverEstadoFiscal - aprobado", () => {
  assert.equal(
    resolverEstadoFiscal({ annulmentStatus: "None", resultado: "A" }),
    "aprobado"
  );
});

test("resolverEstadoFiscal - rechazado", () => {
  assert.equal(
    resolverEstadoFiscal({ annulmentStatus: "None", resultado: "R" }),
    "rechazado"
  );
});

test("resolverEstadoFiscal - en proceso (resultado null)", () => {
  assert.equal(
    resolverEstadoFiscal({ annulmentStatus: "None", resultado: null }),
    "en_proceso"
  );
});

test("resolverEstadoFiscal - en proceso (resultado sin definir)", () => {
  assert.equal(
    resolverEstadoFiscal({ annulmentStatus: "None" }),
    "en_proceso"
  );
});

// ─── aplicarFiltros — casos básicos ──────────────────────────────────────────

test("aplicarFiltros - sin filtros devuelve toda la lista", () => {
  assert.equal(aplicarFiltros(listaCompleta, {}).length, 6);
});

test("aplicarFiltros - lista nula devuelve array vacío", () => {
  assert.deepEqual(aplicarFiltros(null, {}), []);
});

test("aplicarFiltros - lista no array devuelve array vacío", () => {
  assert.deepEqual(aplicarFiltros(undefined, {}), []);
});

// ─── aplicarFiltros — filtro por tipo ────────────────────────────────────────

test("aplicarFiltros - tipo=1 devuelve solo Factura A", () => {
  const resultado = aplicarFiltros(listaCompleta, { tipo: "1" });
  assert.equal(resultado.length, 2); // facturaA + enProceso son tipo 1
  assert.ok(resultado.every((f) => f.tipoComprobante === 1));
});

test("aplicarFiltros - tipo=8 devuelve solo NC B", () => {
  const resultado = aplicarFiltros(listaCompleta, { tipo: "8" });
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].numeroComprobante, 111);
});

// ─── aplicarFiltros — filtro por estado ──────────────────────────────────────

test("aplicarFiltros - estado=aprobado filtra aprobadas", () => {
  const resultado = aplicarFiltros(listaCompleta, { estado: "aprobado" });
  // facturaA, facturaB, ncB son aprobadas. rechazada=R, anulando=Pending, enProceso=null
  assert.equal(resultado.length, 3);
});

test("aplicarFiltros - estado=rechazado filtra rechazadas", () => {
  const resultado = aplicarFiltros(listaCompleta, { estado: "rechazado" });
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].numeroComprobante, 222);
});

test("aplicarFiltros - estado=anulando filtra en anulación", () => {
  const resultado = aplicarFiltros(listaCompleta, { estado: "anulando" });
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].numeroComprobante, 333);
});

test("aplicarFiltros - estado=en_proceso filtra en proceso", () => {
  const resultado = aplicarFiltros(listaCompleta, { estado: "en_proceso" });
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].numeroComprobante, 444);
});

// ─── aplicarFiltros — filtro por fecha ───────────────────────────────────────

test("aplicarFiltros - desde=2026-05-01 excluye facturaB (abril)", () => {
  const resultado = aplicarFiltros(listaCompleta, { desde: "2026-05-01" });
  assert.equal(resultado.some((f) => f.numeroComprobante === 67890), false);
  assert.equal(resultado.length, 5);
});

test("aplicarFiltros - hasta=2026-06-11 excluye anulando (2026-06-12)", () => {
  const resultado = aplicarFiltros(listaCompleta, { hasta: "2026-06-11" });
  assert.equal(resultado.some((f) => f.numeroComprobante === 333), false);
});

test("aplicarFiltros - rango completo que incluye todo", () => {
  const resultado = aplicarFiltros(listaCompleta, { desde: "2026-01-01", hasta: "2026-12-31" });
  assert.equal(resultado.length, 6);
});

test("aplicarFiltros - rango que excluye todo", () => {
  const resultado = aplicarFiltros(listaCompleta, { desde: "2027-01-01", hasta: "2027-12-31" });
  assert.equal(resultado.length, 0);
});

// ─── aplicarFiltros — filtro por número ──────────────────────────────────────

test("aplicarFiltros - buscarNumero encuentra por número exacto formateado", () => {
  const resultado = aplicarFiltros(listaCompleta, { buscarNumero: "00001-00012345" });
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].tipoComprobante, 1);
  assert.equal(resultado[0].numeroComprobante, 12345);
});

test("aplicarFiltros - buscarNumero busca parcialmente en el número", () => {
  const resultado = aplicarFiltros(listaCompleta, { buscarNumero: "00111" });
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].numeroComprobante, 111);
});

test("aplicarFiltros - buscarNumero vacío no filtra nada", () => {
  assert.equal(aplicarFiltros(listaCompleta, { buscarNumero: "" }).length, 6);
  assert.equal(aplicarFiltros(listaCompleta, { buscarNumero: "   " }).length, 6);
});

// ─── aplicarFiltros — filtros combinados ─────────────────────────────────────

test("aplicarFiltros - tipo + estado combinados", () => {
  // Factura B (tipo=6) aprobada: solo facturaB (67890). rechazada es tipo 6 pero rechazada. anulando es tipo 6 pero anulando.
  const resultado = aplicarFiltros(listaCompleta, { tipo: "6", estado: "aprobado" });
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].numeroComprobante, 67890);
});

test("aplicarFiltros - desde + tipo combinados", () => {
  // Desde 2026-05-01 + tipo=6: quedan rechazada(222) y anulando(333). facturaB(67890) quedó afuera por fecha.
  const resultado = aplicarFiltros(listaCompleta, { desde: "2026-05-01", tipo: "6" });
  assert.equal(resultado.length, 2);
  assert.ok(resultado.every((f) => f.tipoComprobante === 6));
});

// ─── aplicarFiltros — filtro por moneda ──────────────────────────────────────
// Regla multimoneda: NUNCA mezclar ARS y USD. El filtro separa estrictamente.

const listaConUsd = [...listaCompleta, facturaUsd];
const listaConLegacy = [...listaCompleta, facturaLegacy];

test("aplicarFiltros - moneda=ARS devuelve solo facturas en pesos", () => {
  const resultado = aplicarFiltros(listaConUsd, { moneda: "ARS" });
  // 6 de ARS, 1 de USD → quedan 6
  assert.equal(resultado.length, 6);
  assert.ok(resultado.every((f) => (f.currency ?? "ARS") === "ARS"));
});

test("aplicarFiltros - moneda=USD devuelve solo facturas en dólares", () => {
  const resultado = aplicarFiltros(listaConUsd, { moneda: "USD" });
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].numeroComprobante, 99999);
});

test("aplicarFiltros - moneda vacía devuelve todas las monedas (sin filtro)", () => {
  const resultado = aplicarFiltros(listaConUsd, { moneda: "" });
  assert.equal(resultado.length, 7); // 6 ARS + 1 USD
});

test("aplicarFiltros - factura sin campo currency se trata como ARS (legacy)", () => {
  // facturaLegacy no tiene `currency`: debe aparecer al filtrar por ARS
  const resultado = aplicarFiltros(listaConLegacy, { moneda: "ARS" });
  assert.ok(resultado.some((f) => f.numeroComprobante === 55555));
});

test("aplicarFiltros - factura legacy (sin currency) NO aparece al filtrar por USD", () => {
  const resultado = aplicarFiltros(listaConLegacy, { moneda: "USD" });
  assert.equal(resultado.some((f) => f.numeroComprobante === 55555), false);
});

test("aplicarFiltros - moneda + tipo combinados separan correctamente", () => {
  // USD + tipo=1: solo facturaUsd que es tipo 1 en USD
  const resultado = aplicarFiltros(listaConUsd, { moneda: "USD", tipo: "1" });
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].currency, "USD");
});

// ─── formatTipoComprobante — cobertura completa + sin exposición de entero ───
// B4: los tipos M (51/52/53) y el fallback no deben exponer el código numérico al usuario.

test("formatTipoComprobante - Factura M (51)", () => {
  assert.equal(formatTipoComprobante(51), "Factura M");
});

test("formatTipoComprobante - ND M (52)", () => {
  assert.equal(formatTipoComprobante(52), "Nota de Débito M");
});

test("formatTipoComprobante - NC M (53)", () => {
  assert.equal(formatTipoComprobante(53), "Nota de Crédito M");
});

test("formatTipoComprobante - tipo desconocido NO expone el número entero", () => {
  // El fallback debe ser texto neutro, nunca "Comprobante tipo 99" ni "99"
  const resultado = formatTipoComprobante(99);
  assert.equal(resultado, "Comprobante");
  assert.equal(resultado.includes("99"), false);
});

test("formatTipoComprobante - tipo 0 NO expone el número entero", () => {
  const resultado = formatTipoComprobante(0);
  assert.equal(resultado.includes("0"), false);
});

// ─── resolverKindComprobante — cobre tipo M ───────────────────────────────────

test("resolverKindComprobante - NC M (53) es abono", () => {
  assert.equal(resolverKindComprobante(53), "abono");
});

test("resolverKindComprobante - ND M (52) es cargo", () => {
  assert.equal(resolverKindComprobante(52), "cargo");
});

test("resolverKindComprobante - Factura M (51) es cargo", () => {
  assert.equal(resolverKindComprobante(51), "cargo");
});
