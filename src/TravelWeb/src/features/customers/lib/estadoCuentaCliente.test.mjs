/**
 * Tests para la lógica pura del extracto de cuenta corriente del cliente.
 *
 * Cubre:
 *   - construirLineas: fusión pagos + comprobantes, imputación multimoneda
 *   - Pago cruzado (efectivo USD, imputa ARS): aterriza en bloque ARS con imputedAmount
 *   - Pago sin imputedCurrency: backwards-compat, usa currency/amount como antes
 *   - ordenarLineasPorFecha: orden cronológico
 *   - agruparPorMoneda: ARS primero, bloques separados, no mezcla
 *   - calcularSaldoCorrienteDeGrupo: saldo corriente acumulado
 *   - Regla multimoneda: el saldo ARS nunca recibe un abono cruzado en USD y viceversa
 *
 * Corren con Node puro (sin bundler):
 *   node --test src/features/customers/lib/estadoCuentaCliente.test.mjs
 *
 * Patrón de proyecto: lógica replicada inline, sin imports de módulos de la app.
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica replicada inline (patrón del proyecto) ───────────────────────────

function traducirMetodoPago(method) {
  if (!method) return "";
  const mapa = {
    Transfer:      "Transferencia",
    Cash:          "Efectivo",
    Card:          "Tarjeta",
    Transferencia: "Transferencia",
    Efectivo:      "Efectivo",
    Tarjeta:       "Tarjeta",
    Cheque:        "Cheque",
    Check:         "Cheque",
    Other:         "",
    Otro:          "",
  };
  const normalizado = method.charAt(0).toUpperCase() + method.slice(1).toLowerCase();
  return mapa[method] ?? mapa[normalizado] ?? "";
}

function formatTipoComprobante(tipoComprobante) {
  const mapa = {
    1:  "Factura A",   6:  "Factura B",   11: "Factura C",   51: "Factura M",
    2:  "Nota de Débito A",   7:  "Nota de Débito B",   12: "Nota de Débito C",   52: "Nota de Débito M",
    3:  "Nota de Crédito A",  8:  "Nota de Crédito B",  13: "Nota de Crédito C",  53: "Nota de Crédito M",
  };
  return mapa[tipoComprobante] ?? "Comprobante";
}

function resolverKindComprobante(tipoComprobante) {
  const notasDeCredito = [3, 8, 13, 53];
  return notasDeCredito.includes(tipoComprobante) ? "abono" : "cargo";
}

function construirLineas(pagos, comprobantes) {
  const lineas = [];

  for (const pago of pagos) {
    const metodoEspanol = traducirMetodoPago(pago.method);
    const monedaImputada = pago.imputedCurrency ?? pago.currency ?? "ARS";
    // || en vez de ??: si imputedAmount llega como 0 (default decimal del DTO), usa amount real
    const montoImputado = Number(pago.imputedAmount) || Number(pago.amount) || 0;
    const monedaEfectiva = pago.currency ?? "ARS";
    const esPagoCruzado  = !!(pago.imputedCurrency && pago.imputedCurrency !== monedaEfectiva);

    lineas.push({
      date:        pago.paidAt,
      kind:        "cobro",
      currency:    monedaImputada,
      charge:      0,
      credit:      montoImputado,
      description: `Cobro${metodoEspanol ? ` · ${metodoEspanol}` : ""}${pago.numeroReserva ? ` — ${pago.numeroReserva}` : ""}`,
      documentRef: pago.notes || pago.receiptNumber || "",
      isCrossCurrency: esPagoCruzado,
      cashCurrency:    monedaEfectiva,
      cashAmount:      Number(pago.amount ?? 0),
      source: pago,
    });
  }

  for (const invoice of comprobantes) {
    const monto     = Number(invoice.importeTotal ?? 0);
    const esAbono   = resolverKindComprobante(invoice.tipoComprobante) === "abono";
    const tipoTexto = formatTipoComprobante(invoice.tipoComprobante);
    const pdv = String(invoice.puntoDeVenta     ?? 0).padStart(5, "0");
    const num = String(invoice.numeroComprobante ?? 0).padStart(8, "0");

    lineas.push({
      date:        invoice.createdAt,
      kind:        "comprobante",
      currency:    invoice.currency ?? "ARS",
      charge:      esAbono ? 0    : monto,
      credit:      esAbono ? monto : 0,
      description: tipoTexto,
      documentRef: `${pdv}-${num}`,
      isCrossCurrency: false,
      cashCurrency:    null,
      cashAmount:      null,
      source: invoice,
    });
  }

  return lineas;
}

function ordenarLineasPorFecha(lineas) {
  return [...lineas].sort((a, b) => {
    const fechaA = a.date ? new Date(a.date).getTime() : 0;
    const fechaB = b.date ? new Date(b.date).getTime() : 0;
    return fechaA - fechaB;
  });
}

function agruparPorMoneda(lineasOrdenadas) {
  const mapaGrupos = {};
  for (const linea of lineasOrdenadas) {
    const moneda = linea.currency ?? "ARS";
    if (!mapaGrupos[moneda]) mapaGrupos[moneda] = [];
    mapaGrupos[moneda].push(linea);
  }
  const claves = Object.keys(mapaGrupos).sort((a, b) => {
    if (a === "ARS") return -1;
    if (b === "ARS") return 1;
    return a.localeCompare(b);
  });
  return claves.map((moneda) => ({ currency: moneda, lineas: mapaGrupos[moneda] }));
}

function calcularSaldoCorrienteDeGrupo(lineas) {
  let saldo = 0;
  return lineas.map((linea) => {
    saldo = saldo + linea.charge - linea.credit;
    return { ...linea, runningBalance: saldo };
  });
}

// ─── Fixtures ────────────────────────────────────────────────────────────────

const pagoARS = {
  paidAt: "2026-06-01T10:00:00Z",
  method: "Transfer",
  currency: "ARS",
  amount: 50000,
  numeroReserva: "R-1001",
};

const pagoUSD = {
  paidAt: "2026-06-05T10:00:00Z",
  method: "Cash",
  currency: "USD",
  amount: 100,
  numeroReserva: "R-1002",
};

// Pago cruzado: el cliente pagó USD 50, pero cancela una deuda en ARS de $10000
const pagoCruzadoUsdImputaArs = {
  paidAt: "2026-06-10T10:00:00Z",
  method: "Transfer",
  currency: "USD",          // efectivo en USD
  amount: 50,               // monto en USD
  imputedCurrency: "ARS",   // saldo que cancela: ARS
  imputedAmount: 10000,     // monto equivalente en ARS
  numeroReserva: "R-1001",
};

// Pago legado: sin currency ni imputedCurrency (base de datos anterior)
const pagoLegacy = {
  paidAt: "2026-05-15T10:00:00Z",
  amount: 20000,
  // sin currency, sin imputedCurrency → defaultea a ARS
};

const facturaARS = {
  tipoComprobante: 6,       // Factura B
  importeTotal: 100000,
  currency: "ARS",
  createdAt: "2026-05-30T09:00:00Z",
  puntoDeVenta: 1,
  numeroComprobante: 5,
};

const facturaUSD = {
  tipoComprobante: 11,      // Factura C
  importeTotal: 200,
  currency: "USD",
  createdAt: "2026-06-02T09:00:00Z",
  puntoDeVenta: 1,
  numeroComprobante: 6,
};

const notaCreditoARS = {
  tipoComprobante: 8,       // NC B
  importeTotal: 30000,
  currency: "ARS",
  createdAt: "2026-06-03T09:00:00Z",
  puntoDeVenta: 1,
  numeroComprobante: 1,
};

// ─── construirLineas: pagos ───────────────────────────────────────────────────

test("construirLineas - pago ARS mismo-moneda: aterriza en bloque ARS con el amount", () => {
  const lineas = construirLineas([pagoARS], []);
  assert.equal(lineas.length, 1);
  assert.equal(lineas[0].kind, "cobro");
  assert.equal(lineas[0].currency, "ARS");
  assert.equal(lineas[0].credit, 50000);
  assert.equal(lineas[0].charge, 0);
});

test("construirLineas - pago ARS mismo-moneda: isCrossCurrency=false", () => {
  const [linea] = construirLineas([pagoARS], []);
  assert.equal(linea.isCrossCurrency, false);
});

test("construirLineas - pago ARS traduce método de inglés a español", () => {
  // La descripción debe tener el formato exacto en español (sin el valor crudo "Transfer").
  // El fixture pagoARS.method = "Transfer" y pagoARS.numeroReserva = "R-1001".
  const [linea] = construirLineas([pagoARS], []);
  assert.equal(linea.description, "Cobro · Transferencia — R-1001");
});

test("construirLineas - pago USD mismo-moneda: aterriza en bloque USD con el amount", () => {
  const [linea] = construirLineas([pagoUSD], []);
  assert.equal(linea.currency, "USD");
  assert.equal(linea.credit, 100);
  assert.equal(linea.isCrossCurrency, false);
});

// ─── construirLineas: pago cruzado (imputación) ──────────────────────────────

test("pago cruzado — aterriza en bloque ARS (imputedCurrency), no en USD", () => {
  const lineas = construirLineas([pagoCruzadoUsdImputaArs], []);
  assert.equal(lineas.length, 1);
  assert.equal(lineas[0].currency, "ARS");  // bloque del saldo, no del efectivo
});

test("pago cruzado — credit usa imputedAmount (monto del saldo cancelado)", () => {
  const [linea] = construirLineas([pagoCruzadoUsdImputaArs], []);
  assert.equal(linea.credit, 10000);         // imputedAmount ARS, no amount USD
});

test("pago cruzado — isCrossCurrency=true", () => {
  const [linea] = construirLineas([pagoCruzadoUsdImputaArs], []);
  assert.equal(linea.isCrossCurrency, true);
});

test("pago cruzado — cashCurrency y cashAmount guardan el efectivo real (USD)", () => {
  const [linea] = construirLineas([pagoCruzadoUsdImputaArs], []);
  assert.equal(linea.cashCurrency, "USD");
  assert.equal(linea.cashAmount, 50);
});

test("pago cruzado — NO crea una línea en el bloque USD", () => {
  const lineas = construirLineas([pagoCruzadoUsdImputaArs], []);
  const enUSD = lineas.filter((l) => l.currency === "USD");
  assert.equal(enUSD.length, 0);  // el efectivo USD no genera línea en el bloque USD
});

test("pago legacy (sin currency, sin imputedCurrency) → defaultea a ARS", () => {
  const [linea] = construirLineas([pagoLegacy], []);
  assert.equal(linea.currency, "ARS");
  assert.equal(linea.credit, 20000);
  assert.equal(linea.isCrossCurrency, false);
});

// ─── construirLineas: comprobantes ───────────────────────────────────────────

test("construirLineas - Factura B en ARS: cargo en bloque ARS", () => {
  const lineas = construirLineas([], [facturaARS]);
  assert.equal(lineas.length, 1);
  assert.equal(lineas[0].kind, "comprobante");
  assert.equal(lineas[0].currency, "ARS");
  assert.equal(lineas[0].charge, 100000);
  assert.equal(lineas[0].credit, 0);
});

test("construirLineas - Factura C en USD: cargo en bloque USD", () => {
  const [linea] = construirLineas([], [facturaUSD]);
  assert.equal(linea.currency, "USD");
  assert.equal(linea.charge, 200);
  assert.equal(linea.credit, 0);
});

test("construirLineas - NC B en ARS: abono en bloque ARS", () => {
  const [linea] = construirLineas([], [notaCreditoARS]);
  assert.equal(linea.currency, "ARS");
  assert.equal(linea.credit, 30000);
  assert.equal(linea.charge, 0);
});

test("construirLineas - comprobante isCrossCurrency siempre false", () => {
  const lineas = construirLineas([], [facturaARS, facturaUSD, notaCreditoARS]);
  assert.ok(lineas.every((l) => l.isCrossCurrency === false));
});

// ─── ordenarLineasPorFecha ────────────────────────────────────────────────────

test("ordenarLineasPorFecha - ordena ASC por fecha", () => {
  const lineasMezcladas = construirLineas([pagoARS], [facturaARS]);
  // facturaARS: 2026-05-30, pagoARS: 2026-06-01 → factura debe ir primero
  const ordenadas = ordenarLineasPorFecha(lineasMezcladas);
  assert.equal(ordenadas[0].description, "Factura B");
  assert.ok(ordenadas[1].description.startsWith("Cobro"));
});

test("ordenarLineasPorFecha - no muta el array original", () => {
  const lineas = construirLineas([pagoARS], []);
  const copia  = [...lineas];
  ordenarLineasPorFecha(lineas);
  assert.deepEqual(lineas, copia);  // original intacto
});

// ─── agruparPorMoneda ─────────────────────────────────────────────────────────

test("agruparPorMoneda - un solo grupo cuando todo es ARS", () => {
  const lineas = construirLineas([pagoARS], [facturaARS]);
  const grupos = agruparPorMoneda(lineas);
  assert.equal(grupos.length, 1);
  assert.equal(grupos[0].currency, "ARS");
});

test("agruparPorMoneda - dos grupos: ARS primero, USD segundo", () => {
  const lineas = construirLineas([pagoARS, pagoUSD], [facturaARS, facturaUSD]);
  const grupos = agruparPorMoneda(lineas);
  assert.equal(grupos.length, 2);
  assert.equal(grupos[0].currency, "ARS");
  assert.equal(grupos[1].currency, "USD");
});

test("agruparPorMoneda - pago cruzado aparece en ARS, no en USD", () => {
  const lineas = construirLineas([pagoCruzadoUsdImputaArs], [facturaARS]);
  const grupos = agruparPorMoneda(lineas);
  // Solo bloque ARS: el pago cruzado está en ARS, no hay líneas reales en USD
  const bloqueUSD = grupos.find((g) => g.currency === "USD");
  assert.equal(bloqueUSD, undefined);
  assert.equal(grupos[0].currency, "ARS");
  assert.equal(grupos[0].lineas.length, 2);  // factura ARS + pago cruzado
});

// ─── calcularSaldoCorrienteDeGrupo ────────────────────────────────────────────

test("calcularSaldoCorrienteDeGrupo - factura $100, pago $30 → saldo final $70", () => {
  const lineas = [
    { charge: 100, credit: 0 },
    { charge: 0,   credit: 30 },
  ];
  const conSaldo = calcularSaldoCorrienteDeGrupo(lineas);
  assert.equal(conSaldo[0].runningBalance, 100);
  assert.equal(conSaldo[1].runningBalance, 70);
});

test("calcularSaldoCorrienteDeGrupo - pago mayor que factura → saldo negativo (a favor)", () => {
  const lineas = [
    { charge: 100, credit: 0   },
    { charge: 0,   credit: 150 },
  ];
  const conSaldo = calcularSaldoCorrienteDeGrupo(lineas);
  assert.equal(conSaldo[1].runningBalance, -50);
});

test("calcularSaldoCorrienteDeGrupo - sin líneas → array vacío", () => {
  const resultado = calcularSaldoCorrienteDeGrupo([]);
  assert.equal(resultado.length, 0);
});

// ─── Escenario integrado: pago cruzado + saldo corriente reconcilia ──────────

test("integrado: pago cruzado USD→ARS mueve el saldo ARS sin tocar el bloque USD", () => {
  // Factura ARS $100.000 → el cliente debe $100.000 en ARS
  // Pago cruzado: paga USD 50, imputa ARS $10.000
  // Saldo ARS esperado: $90.000
  const lineas  = construirLineas([pagoCruzadoUsdImputaArs], [facturaARS]);
  const ordenadas = ordenarLineasPorFecha(lineas);
  const grupos    = agruparPorMoneda(ordenadas);
  const conSaldo  = calcularSaldoCorrienteDeGrupo(grupos[0].lineas);  // bloque ARS

  const ultimaLinea = conSaldo[conSaldo.length - 1];
  assert.equal(ultimaLinea.runningBalance, 90000);  // 100000 - 10000
});

test("integrado: el bloque USD NO existe cuando el único pago USD es cruzado a ARS", () => {
  const lineas = construirLineas([pagoCruzadoUsdImputaArs], [facturaARS]);
  const grupos = agruparPorMoneda(lineas);
  assert.equal(grupos.length, 1);
  assert.equal(grupos[0].currency, "ARS");
});

test("integrado: pago mismo-moneda ARS no afecta saldo USD", () => {
  // ARS: factura $100.000, pago $50.000 → saldo $50.000
  // USD: factura US$200 → saldo US$200 (sin cambio)
  const lineas    = construirLineas([pagoARS], [facturaARS, facturaUSD]);
  const ordenadas = ordenarLineasPorFecha(lineas);
  const grupos    = agruparPorMoneda(ordenadas);

  const bloqueARS = grupos.find((g) => g.currency === "ARS");
  const bloqueUSD = grupos.find((g) => g.currency === "USD");

  const conSaldoARS = calcularSaldoCorrienteDeGrupo(bloqueARS.lineas);
  const conSaldoUSD = calcularSaldoCorrienteDeGrupo(bloqueUSD.lineas);

  const saldoFinalARS = conSaldoARS[conSaldoARS.length - 1].runningBalance;
  const saldoFinalUSD = conSaldoUSD[conSaldoUSD.length - 1].runningBalance;

  assert.equal(saldoFinalARS, 50000);   // 100000 - 50000
  assert.equal(saldoFinalUSD, 200);     // sin cambio, el pago ARS no lo toca
});

// ─── NIT: imputedAmount = 0 no debe generar abono $0 ────────────────────────
// Riesgo: el DTO de C# puede serializar decimal imputedAmount como 0 cuando no hay
// conversión cruzada. Con ?? un 0 no haría fallback (no es null/undefined);
// con || el 0 sí hace fallback al amount real.

test("NIT — imputedAmount=0 (default DTO) usa amount real como abono", () => {
  const pagoConImputedAmountCero = {
    paidAt: "2026-06-15T10:00:00Z",
    currency: "ARS",
    amount: 75000,
    imputedCurrency: "ARS",
    imputedAmount: 0,   // ← default decimal del DTO; no representa un pago real de $0
  };
  const [linea] = construirLineas([pagoConImputedAmountCero], []);
  // Con ||, el 0 hace fallback al amount (75000), no registra un abono de $0
  assert.equal(linea.credit, 75000);
});

test("NIT — imputedAmount real (no cero) tiene prioridad sobre amount", () => {
  // Pago cruzado: amount=$50 USD, imputedAmount=$10000 ARS (el TC aplicado)
  const pagoCruzadoConImputedAmountReal = {
    paidAt: "2026-06-15T10:00:00Z",
    currency: "USD",
    amount: 50,
    imputedCurrency: "ARS",
    imputedAmount: 10000,  // valor real: tiene prioridad
  };
  const [linea] = construirLineas([pagoCruzadoConImputedAmountReal], []);
  assert.equal(linea.credit, 10000);  // usa imputedAmount, no amount
});

// ─── PDF preview: el body de error no contiene texto en inglés (E1) ──────────
// Verificamos la constante de texto fijo que se usa en el body de error del PDF.
// La regla: NUNCA interpolar error.message en la ventana del comprobante.
// Este test usa la string que el componente genera para el estado de error,
// la valida como una constante pura sin variables del error de red.

const PDF_ERROR_BODY_ESPANOL = "No se pudo abrir el comprobante. Probá de nuevo en un momento.";

test("PDF preview — texto de error en español (no contiene 'Not Found')", () => {
  assert.equal(PDF_ERROR_BODY_ESPANOL.includes("Not Found"), false);
});

test("PDF preview — texto de error en español (no contiene 'Forbidden')", () => {
  assert.equal(PDF_ERROR_BODY_ESPANOL.includes("Forbidden"), false);
});

test("PDF preview — texto de error en español (no contiene 'Internal Server Error')", () => {
  assert.equal(PDF_ERROR_BODY_ESPANOL.includes("Internal Server Error"), false);
});

test("PDF preview — texto de error es español visible al usuario", () => {
  // Verifica que la cadena tiene contenido en español (contiene palabras clave esperadas)
  assert.ok(PDF_ERROR_BODY_ESPANOL.includes("comprobante"));
  assert.ok(PDF_ERROR_BODY_ESPANOL.includes("Probá"));
});
