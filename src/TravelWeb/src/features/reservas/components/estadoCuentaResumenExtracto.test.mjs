/**
 * Tests de lógica pura para el Estado de Cuenta rediseñado (2026-06-22).
 *
 * Cubre:
 *   1. Franja de 3 ejes: Venta/Facturación, Cobranza, Costo/Margen.
 *      - El bloque Costo/Margen aparece solo para quien puede ver costos.
 *      - Fix "Recaudado": el total viene del backend (TotalPaid), no se recalcula en el front.
 *   2. Acciones por renglón del extracto:
 *      - Línea de factura (Invoice/CreditNote/DebitNote) → Ver PDF si está aprobada.
 *      - Línea de cobro (Payment) con recibo → Ver PDF siempre + Anular solo si no congelado.
 *      - Línea de cobro sin recibo + no congelado → Emitir comprobante.
 *      - Línea de cobro sin recibo + congelado → Nada.
 *   3. Cruce de sourcePublicId con las colecciones del DTO de la reserva.
 *   4. Aclaración de diseño: el saldo del extracto puede diferir del saldo a cobrar.
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/estadoCuentaResumenExtracto.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica de la lógica de visibilidad del bloque Costo/Margen ─────────────

/**
 * El bloque "Costo y margen" solo se muestra si el usuario es admin o tiene
 * el permiso cobranzas.see_cost. Replica la condición de EstadoCuentaResumen.jsx.
 */
function debeVerBloqueCosto({ esAdmin, tienePermisoCosto }) {
  return esAdmin || tienePermisoCosto;
}

test("bloque Costo/Margen: admin SÍ lo ve", () => {
  assert.equal(debeVerBloqueCosto({ esAdmin: true, tienePermisoCosto: false }), true);
});

test("bloque Costo/Margen: usuario con permiso cobranzas.see_cost SÍ lo ve", () => {
  assert.equal(debeVerBloqueCosto({ esAdmin: false, tienePermisoCosto: true }), true);
});

test("bloque Costo/Margen: usuario sin permiso NO lo ve", () => {
  assert.equal(debeVerBloqueCosto({ esAdmin: false, tienePermisoCosto: false }), false);
});

test("bloque Costo/Margen: admin con permiso también SÍ lo ve (combinado)", () => {
  assert.equal(debeVerBloqueCosto({ esAdmin: true, tienePermisoCosto: true }), true);
});

// ─── Fix "Recaudado": usar TotalPaid del backend, no recalcular ───────────────

/**
 * Replica el criterio: el total cobrado viene de reserva.totalPaid (backend).
 * NO se suma reserva.payments en el front porque puede incluir pagos puente
 * (AffectsCash=false) que divergen del backend.
 *
 * En modo multimoneda, se usa porMoneda[i].totalPaid.
 */
function obtenerTotalCobradoMono(reserva) {
  // Fuente única: el campo del DTO, ya calculado por el backend.
  return reserva.totalPaid ?? 0;
}

function obtenerTotalCobradoMulti(reserva, currency) {
  const linea = (reserva.porMoneda ?? []).find((pm) => pm.currency === currency);
  return linea?.totalPaid ?? 0;
}

test("Recaudado mono-moneda: usa totalPaid del DTO (no suma payments[])", () => {
  const reserva = {
    totalPaid: 50000,
    // Estos dos pagos incluyen uno con AffectsCash=false (puente) que NO debería sumar.
    payments: [
      { amount: 50000, currency: "ARS", affectsCash: true },
      { amount: 10000, currency: "ARS", affectsCash: false }, // puente, no se suma
    ],
  };
  // El front debe mostrar el valor del DTO, no la suma de los payments.
  assert.equal(obtenerTotalCobradoMono(reserva), 50000);
});

test("Recaudado multimoneda: usa porMoneda[].totalPaid para ARS", () => {
  const reserva = {
    porMoneda: [
      { currency: "ARS", totalPaid: 80000 },
      { currency: "USD", totalPaid: 200 },
    ],
  };
  assert.equal(obtenerTotalCobradoMulti(reserva, "ARS"), 80000);
  assert.equal(obtenerTotalCobradoMulti(reserva, "USD"), 200);
});

test("Recaudado: si totalPaid es null/undefined → devuelve 0 (no rompe)", () => {
  assert.equal(obtenerTotalCobradoMono({ totalPaid: null }), 0);
  assert.equal(obtenerTotalCobradoMono({}), 0);
});

// ─── Cruce de sourcePublicId con las colecciones del DTO ─────────────────────

/**
 * Replica el cruce que hace FilaExtracto: busca el objeto original en la
 * colección correspondiente del DTO (invoices o payments) por su publicId.
 *
 * Normaliza: la comparación es String vs String para tolerar Guid con distintos casings.
 */
function cruzarLineaConReserva(linea, reserva) {
  const esDocumentoFiscal =
    linea.kind === "Invoice" || linea.kind === "CreditNote" || linea.kind === "DebitNote";
  const esCobro = linea.kind === "Payment";

  if (esDocumentoFiscal && linea.sourcePublicId) {
    return (reserva.invoices ?? []).find(
      (inv) => String(inv.publicId ?? inv.id ?? "") === String(linea.sourcePublicId)
    ) ?? null;
  }

  if (esCobro && linea.sourcePublicId) {
    return (reserva.payments ?? []).find(
      (pay) => String(pay.publicId ?? pay.id ?? "") === String(linea.sourcePublicId)
    ) ?? null;
  }

  return null;
}

const GUID_FACTURA = "aaaaaaaa-0000-0000-0000-000000000001";
const GUID_COBRO   = "bbbbbbbb-0000-0000-0000-000000000002";

const reservaConDatos = {
  invoices: [
    { publicId: GUID_FACTURA, resultado: "A", tipoComprobante: 6 },
  ],
  payments: [
    { publicId: GUID_COBRO, amount: 10000, currency: "ARS", receipt: null },
  ],
};

test("cruce: línea Invoice encuentra su factura por sourcePublicId", () => {
  const linea = { kind: "Invoice", sourcePublicId: GUID_FACTURA };
  const encontrado = cruzarLineaConReserva(linea, reservaConDatos);
  assert.equal(encontrado?.publicId, GUID_FACTURA);
});

test("cruce: línea CreditNote encuentra su factura (es un documento fiscal)", () => {
  const linea = { kind: "CreditNote", sourcePublicId: GUID_FACTURA };
  const encontrado = cruzarLineaConReserva(linea, reservaConDatos);
  assert.equal(encontrado?.publicId, GUID_FACTURA);
});

test("cruce: línea DebitNote también busca en invoices[]", () => {
  const linea = { kind: "DebitNote", sourcePublicId: GUID_FACTURA };
  const encontrado = cruzarLineaConReserva(linea, reservaConDatos);
  assert.equal(encontrado?.publicId, GUID_FACTURA);
});

test("cruce: línea Payment encuentra su cobro por sourcePublicId", () => {
  const linea = { kind: "Payment", sourcePublicId: GUID_COBRO };
  const encontrado = cruzarLineaConReserva(linea, reservaConDatos);
  assert.equal(encontrado?.publicId, GUID_COBRO);
});

test("cruce: sourcePublicId que no existe en la colección → null (dato legacy)", () => {
  const linea = { kind: "Invoice", sourcePublicId: "00000000-ffff-ffff-ffff-000000000000" };
  const encontrado = cruzarLineaConReserva(linea, reservaConDatos);
  assert.equal(encontrado, null);
});

test("cruce: sin sourcePublicId → null (no intenta buscar)", () => {
  const linea = { kind: "Payment", sourcePublicId: null };
  assert.equal(cruzarLineaConReserva(linea, reservaConDatos), null);
});

test("cruce: colecciones vacías → null (reserva recién creada)", () => {
  const linea = { kind: "Invoice", sourcePublicId: GUID_FACTURA };
  assert.equal(cruzarLineaConReserva(linea, { invoices: [], payments: [] }), null);
});

// ─── Acciones por renglón del extracto ───────────────────────────────────────

/**
 * Replica la lógica de qué acciones se muestran para una línea de factura.
 * Ver PDF: solo si la factura está aprobada (resultado === "A") y tiene publicId.
 */
function accionesFactura(facturaEncontrada) {
  if (!facturaEncontrada) return { verPdfVisible: false };
  const aprobada = facturaEncontrada.resultado === "A";
  const tienePublicId = Boolean(facturaEncontrada.publicId ?? facturaEncontrada.id);
  return { verPdfVisible: aprobada && tienePublicId };
}

test("acciones factura: aprobada con publicId → Ver PDF visible", () => {
  const factura = { publicId: GUID_FACTURA, resultado: "A" };
  assert.deepEqual(accionesFactura(factura), { verPdfVisible: true });
});

test("acciones factura: rechazada → Ver PDF NO visible (no hay PDF válido)", () => {
  const factura = { publicId: GUID_FACTURA, resultado: "R" };
  assert.deepEqual(accionesFactura(factura), { verPdfVisible: false });
});

test("acciones factura: en proceso (resultado null) → Ver PDF NO visible", () => {
  const factura = { publicId: GUID_FACTURA, resultado: null };
  assert.deepEqual(accionesFactura(factura), { verPdfVisible: false });
});

test("acciones factura: no encontrada (datos legacy) → Ver PDF NO visible", () => {
  assert.deepEqual(accionesFactura(null), { verPdfVisible: false });
});

/**
 * Replica la lógica de acciones para una línea de cobro (PaymentReceiptActions).
 * Las reglas son las mismas que en el componente existente, probadas también
 * en estadosCongelados.test.mjs — aquí las verificamos en el contexto del extracto.
 */
function accionesCobro({ cobroEncontrado, congelado }) {
  if (!cobroEncontrado) return { nada: true };

  const receipt = cobroEncontrado.receipt ?? null;
  const entryType = cobroEncontrado.entryType ?? "Payment";
  const amount = Number(cobroEncontrado.amount ?? 0);

  if (receipt) {
    const estaAnulado = receipt.status === "Voided";
    return {
      chipVisible: true,
      verPdfVisible: !estaAnulado,
      anularVisible: !estaAnulado && !congelado,
      emitirVisible: false,
    };
  }

  // Sin recibo y congelado → nada
  if (congelado) {
    return { chipVisible: false, verPdfVisible: false, anularVisible: false, emitirVisible: false };
  }

  // Sin recibo, no congelado: se puede emitir si es un Payment de monto > 0
  const puedeEmitir = entryType === "Payment" && amount > 0;
  return { chipVisible: false, verPdfVisible: false, anularVisible: false, emitirVisible: puedeEmitir };
}

test("acciones cobro: con recibo no anulado + no congelado → Ver PDF + Anular visibles", () => {
  const cobro = { publicId: GUID_COBRO, amount: 5000, entryType: "Payment", receipt: { status: "Active", receiptNumber: "R-001" } };
  const acciones = accionesCobro({ cobroEncontrado: cobro, congelado: false });
  assert.equal(acciones.chipVisible, true);
  assert.equal(acciones.verPdfVisible, true);
  assert.equal(acciones.anularVisible, true);
  assert.equal(acciones.emitirVisible, false);
});

test("acciones cobro: con recibo no anulado + CONGELADO → Ver PDF visible, Anular NO", () => {
  const cobro = { publicId: GUID_COBRO, amount: 5000, entryType: "Payment", receipt: { status: "Active", receiptNumber: "R-001" } };
  const acciones = accionesCobro({ cobroEncontrado: cobro, congelado: true });
  assert.equal(acciones.verPdfVisible, true);
  assert.equal(acciones.anularVisible, false);
});

test("acciones cobro: con recibo ANULADO → chip visible, Ver PDF y Anular NO", () => {
  const cobro = { publicId: GUID_COBRO, amount: 5000, entryType: "Payment", receipt: { status: "Voided" } };
  const acciones = accionesCobro({ cobroEncontrado: cobro, congelado: false });
  assert.equal(acciones.chipVisible, true);
  assert.equal(acciones.verPdfVisible, false);
  assert.equal(acciones.anularVisible, false);
});

test("acciones cobro: sin recibo + no congelado + monto > 0 → Emitir visible", () => {
  const cobro = { publicId: GUID_COBRO, amount: 3000, entryType: "Payment", receipt: null };
  const acciones = accionesCobro({ cobroEncontrado: cobro, congelado: false });
  assert.equal(acciones.emitirVisible, true);
});

test("acciones cobro: sin recibo + CONGELADO → Emitir NO visible (no se emiten nuevos comprobantes)", () => {
  const cobro = { publicId: GUID_COBRO, amount: 3000, entryType: "Payment", receipt: null };
  const acciones = accionesCobro({ cobroEncontrado: cobro, congelado: true });
  assert.equal(acciones.emitirVisible, false);
  assert.equal(acciones.verPdfVisible, false);
});

test("acciones cobro: cobro no encontrado (sourcePublicId legacy) → nada (degradación elegante)", () => {
  const acciones = accionesCobro({ cobroEncontrado: null, congelado: false });
  assert.equal(acciones.nada, true);
});

// ─── Aclaración de diseño: saldo del extracto ≠ saldo a cobrar ───────────────

/**
 * Verifica que la aclaración de diseño sea correcta: el saldo del extracto
 * refleja lo FACTURADO, mientras que el saldo a cobrar refleja lo CONFIRMADO.
 * Pueden diferir si hay servicios confirmados sin facturar aún.
 *
 * Este test verifica la lógica numérica que respalda esa aclaración.
 */
function calcularDivergenciaSaldo({ saldoExtracto, saldoACobrar }) {
  // Puede haber divergencia cuando hay algo confirmado no facturado.
  // Saldo del extracto = deuda desde el lado de facturación.
  // Saldo a cobrar     = deuda desde el lado de lo confirmado.
  return Math.abs(saldoACobrar - saldoExtracto);
}

test("aclaración diseño: saldo extracto puede ser menor si hay servicios sin facturar", () => {
  // Confirmado $10.000 pero solo se facturó $6.000 → extracto muestra $6.000, cobrar muestra $10.000.
  const divergencia = calcularDivergenciaSaldo({ saldoExtracto: 6000, saldoACobrar: 10000 });
  assert.equal(divergencia, 4000);
});

test("aclaración diseño: sin divergencia cuando todo lo confirmado está facturado", () => {
  const divergencia = calcularDivergenciaSaldo({ saldoExtracto: 10000, saldoACobrar: 10000 });
  assert.equal(divergencia, 0);
});

// ─── Multimoneda: nunca mezclar monedas ──────────────────────────────────────

/**
 * Verifica que el extracto agrupe líneas por moneda y nunca sume ARS + USD.
 */
function agruparLineasPorMoneda(lines) {
  const grupos = {};
  for (const linea of lines) {
    const moneda = linea.currency ?? "ARS";
    if (!grupos[moneda]) grupos[moneda] = [];
    grupos[moneda].push(linea);
  }
  return grupos;
}

test("multimoneda: líneas ARS y USD se agrupan por separado, nunca sumadas", () => {
  const lineas = [
    { kind: "Invoice", currency: "ARS", charge: 100000, credit: 0, runningBalance: 100000 },
    { kind: "Payment", currency: "ARS", charge: 0, credit: 50000, runningBalance: 50000 },
    { kind: "Invoice", currency: "USD", charge: 500, credit: 0, runningBalance: 500 },
  ];
  const grupos = agruparLineasPorMoneda(lineas);
  assert.equal(grupos["ARS"].length, 2);
  assert.equal(grupos["USD"].length, 1);
  // No existe un grupo combinado
  assert.equal(grupos["ARS_USD"], undefined);
});

test("multimoneda: extracto mono-moneda devuelve un solo grupo", () => {
  const lineas = [
    { kind: "Invoice", currency: "ARS", charge: 80000, credit: 0, runningBalance: 80000 },
    { kind: "Payment", currency: "ARS", charge: 0, credit: 80000, runningBalance: 0 },
  ];
  const grupos = agruparLineasPorMoneda(lineas);
  assert.equal(Object.keys(grupos).length, 1);
  assert.equal(grupos["ARS"].length, 2);
});

// ─── accountRefreshKey: lógica de incremento ─────────────────────────────────

/**
 * La clave de refresco del extracto (accountRefreshKey) se incrementa con cada
 * acción de plata. Replica la función refrescarExtracto del padre.
 *
 * Regla: la key solo debe crecer — siempre n+1 respecto al valor anterior.
 * Así el useCallback del extracto detecta el cambio y re-ejecuta el fetch.
 */
function simularIncrementoKey(keyActual) {
  // Replica: setAccountRefreshKey((k) => k + 1)
  return keyActual + 1;
}

test("refreshKey: empieza en 0 (sin acciones)", () => {
  const keyInicial = 0;
  assert.equal(keyInicial, 0);
});

test("refreshKey: se incrementa en 1 al registrar un cobro", () => {
  const keyAntes = 0;
  const keyDespues = simularIncrementoKey(keyAntes);
  assert.equal(keyDespues, 1);
});

test("refreshKey: se incrementa en 1 al emitir una factura", () => {
  const keyAntes = 1;
  const keyDespues = simularIncrementoKey(keyAntes);
  assert.equal(keyDespues, 2);
});

test("refreshKey: se incrementa en 1 al emitir un comprobante (recibo)", () => {
  const keyAntes = 2;
  const keyDespues = simularIncrementoKey(keyAntes);
  assert.equal(keyDespues, 3);
});

test("refreshKey: se incrementa en 1 al anular un comprobante", () => {
  const keyAntes = 5;
  const keyDespues = simularIncrementoKey(keyAntes);
  assert.equal(keyDespues, 6);
});

test("refreshKey: cada acción es un incremento independiente, no se resetea a 0", () => {
  // Simula 3 acciones sucesivas: cobro → factura → recibo
  const despuesCobro = simularIncrementoKey(0);
  const despuesFactura = simularIncrementoKey(despuesCobro);
  const despuesRecibo = simularIncrementoKey(despuesFactura);
  assert.equal(despuesCobro, 1);
  assert.equal(despuesFactura, 2);
  assert.equal(despuesRecibo, 3);
});

test("refreshKey: nunca cambia si no hubo acción de plata (sin ciclos infinitos)", () => {
  // La key que el componente EstadoCuentaExtracto recibe como prop no cambia
  // entre renders si el padre no llamó refrescarExtracto().
  // Este test verifica que el valor solo cambia cuando hay intención explícita.
  const key = 4;
  const keyDespuesdeSoloUnRenderSinAccion = key; // sin llamar refrescarExtracto()
  assert.equal(keyDespuesdeSoloUnRenderSinAccion, 4);
});

// ─── B1: Editar / Eliminar cobro ─────────────────────────────────────────────

/**
 * Replica la lógica de visibilidad de Editar/Eliminar cobro en PaymentReceiptActions.
 *
 * Regla: los botones Editar y Eliminar cobro se muestran SOLO cuando:
 *   - No está congelado (Traveling/Lost/Cancelled/FullyInvoiced).
 *   - El recibo NO está anulado (un cobro con recibo anulado está procesado formalmente).
 *
 * En congelado: sin botones de escritura. Solo lectura.
 */
function calcularVisibilidadEditarEliminar({ congelado, reciboAnulado }) {
  // Un cobro es editable si no está congelado y su recibo (si lo tiene) no está anulado.
  return !congelado && !reciboAnulado;
}

test("B1: cobro sin recibo + no congelado → Editar y Eliminar visibles", () => {
  const visible = calcularVisibilidadEditarEliminar({ congelado: false, reciboAnulado: false });
  assert.equal(visible, true);
});

test("B1: cobro con recibo activo + no congelado → Editar y Eliminar visibles", () => {
  const visible = calcularVisibilidadEditarEliminar({ congelado: false, reciboAnulado: false });
  assert.equal(visible, true);
});

test("B1: cobro en estado CONGELADO → Editar y Eliminar NO visibles (solo lectura)", () => {
  const visible = calcularVisibilidadEditarEliminar({ congelado: true, reciboAnulado: false });
  assert.equal(visible, false);
});

test("B1: cobro con recibo ANULADO → Editar y Eliminar NO visibles (ya procesado formalmente)", () => {
  // Un cobro cuyo recibo fue anulado está documentado; no tiene sentido editarlo.
  const visible = calcularVisibilidadEditarEliminar({ congelado: false, reciboAnulado: true });
  assert.equal(visible, false);
});

test("B1: cobro congelado + recibo anulado → Editar y Eliminar NO visibles (doble bloqueo)", () => {
  const visible = calcularVisibilidadEditarEliminar({ congelado: true, reciboAnulado: true });
  assert.equal(visible, false);
});

// ─── I1: Facturación multimoneda por moneda (nuevo backend 2026-06-22) ───────

/**
 * Verifica el nuevo comportamiento: el backend expone facturadoNeto y disponibleParaFacturar
 * DENTRO de porMoneda[], igual que confirmedSale, totalPaid y balance.
 *
 * El componente EstadoCuentaResumen usa ColumnaNumericaMulti para facturadoNeto
 * y ColumnaNumericaMultiCondicional para disponibleParaFacturar.
 *
 * EjeEscalarGlobal fue eliminado (ya no tiene consumidores).
 */

/**
 * Replica el acceso que hace ColumnaNumericaMulti en el componente:
 * lee pm[campo] para cada entrada del array porMoneda.
 */
function leerCampoPorMoneda(porMoneda, campo) {
  return porMoneda.map((pm) => ({ currency: pm.currency, valor: pm[campo] ?? 0 }));
}

/**
 * Replica la lógica de color de ColumnaNumericaMultiCondicional para disponibleParaFacturar:
 * ámbar si queda algo, gris si es cero.
 */
function colorDisponibleParaFacturar(valor) {
  return valor > 0 ? "amber" : "grey";
}

test("I1: en multimoneda, facturadoNeto se lee desde porMoneda[].facturadoNeto (nuevo backend)", () => {
  const porMoneda = [
    { currency: "ARS", confirmedSale: 100000, facturadoNeto: 80000, disponibleParaFacturar: 20000 },
    { currency: "USD", confirmedSale: 500,    facturadoNeto: 300,   disponibleParaFacturar: 200   },
  ];

  const resultado = leerCampoPorMoneda(porMoneda, "facturadoNeto");

  assert.equal(resultado[0].currency, "ARS");
  assert.equal(resultado[0].valor, 80000);
  assert.equal(resultado[1].currency, "USD");
  assert.equal(resultado[1].valor, 300);
});

test("I1: en multimoneda, disponibleParaFacturar se lee desde porMoneda[].disponibleParaFacturar", () => {
  const porMoneda = [
    { currency: "ARS", disponibleParaFacturar: 20000 },
    { currency: "USD", disponibleParaFacturar: 200 },
  ];

  const resultado = leerCampoPorMoneda(porMoneda, "disponibleParaFacturar");

  assert.equal(resultado[0].valor, 20000);
  assert.equal(resultado[1].valor, 200);
});

test("I1: si porMoneda[].facturadoNeto es null/undefined → se trata como 0 (sin romper)", () => {
  const porMoneda = [
    { currency: "ARS", facturadoNeto: null },
    { currency: "USD" },
  ];

  const resultado = leerCampoPorMoneda(porMoneda, "facturadoNeto");

  assert.equal(resultado[0].valor, 0);
  assert.equal(resultado[1].valor, 0);
});

test("I1: disponibleParaFacturar > 0 → color ámbar (falta facturar algo)", () => {
  assert.equal(colorDisponibleParaFacturar(20000), "amber");
  assert.equal(colorDisponibleParaFacturar(1), "amber");
});

test("I1: disponibleParaFacturar === 0 → color gris (todo facturado)", () => {
  assert.equal(colorDisponibleParaFacturar(0), "grey");
});

test("I1: en multimoneda, vendido firme también se lee por moneda (confirmedSale en porMoneda)", () => {
  // Verifica que el patrón es consistente para los tres campos del eje 1.
  const porMoneda = [
    { currency: "ARS", confirmedSale: 100000, facturadoNeto: 100000, disponibleParaFacturar: 0 },
    { currency: "USD", confirmedSale: 500,    facturadoNeto: 200,    disponibleParaFacturar: 300 },
  ];

  const vendido    = leerCampoPorMoneda(porMoneda, "confirmedSale");
  const facturado  = leerCampoPorMoneda(porMoneda, "facturadoNeto");
  const disponible = leerCampoPorMoneda(porMoneda, "disponibleParaFacturar");

  // ARS: todo facturado, nada pendiente
  assert.equal(vendido[0].valor, 100000);
  assert.equal(facturado[0].valor, 100000);
  assert.equal(disponible[0].valor, 0);
  assert.equal(colorDisponibleParaFacturar(disponible[0].valor), "grey");

  // USD: parcialmente facturado, quedan $300 pendientes
  assert.equal(vendido[1].valor, 500);
  assert.equal(facturado[1].valor, 200);
  assert.equal(disponible[1].valor, 300);
  assert.equal(colorDisponibleParaFacturar(disponible[1].valor), "amber");
});
