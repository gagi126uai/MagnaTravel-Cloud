/**
 * Lógica pura de la tarjeta "Ritmo de cobros y pagos" del dashboard (spec
 * `docs/ux/2026-08-18-spec-dashboard-y-cuentas-corrientes.md`, sección 1.3 + la
 * respuesta firmada de Gastón al final del documento, opción C: se usa la
 * TENDENCIA que ya calcula `GET /reports/cashflow`, con el título honesto "Ritmo
 * de cobros y pagos" — no promete un cronograma real de vencimientos).
 *
 * Arma, a partir de `CashFlowProjectionResponse` (30 días históricos + al menos 90
 * proyectados, ver `ReportService.GetCashFlowProjectionAsync`), las series de
 * cobros/pagos SEPARADAS POR MONEDA (P-3: nunca se suma ARS con USD, ni siquiera
 * en un gráfico) más las 4 marcas del eje X que pide la maqueta: Hoy / +30 / +60 / +90.
 *
 * Contrato asumido del backend (documentado para que un cambio ahí no rompa esto en
 * silencio): `historical` trae SIEMPRE 31 días (de -30 a hoy inclusive) y `projected`
 * trae SIEMPRE al menos 90 días — por eso "Hoy" es el último índice de `historical`
 * y "+30/+60/+90" son ese índice más 30/60/90.
 */

const MONEDAS_ORDEN_CANONICO = ["ARS", "USD"];

/**
 * @param {{ historical?: Array, projected?: Array }} cashflow - `CashFlowProjectionResponse`
 * @returns {{
 *   hayMovimiento: boolean,
 *   monedas: Array<{ currency: string, puntos: Array<{x: number, cobros: number, pagos: number}> }>,
 *   ejeXTicks: Array<{ x: number, etiqueta: string }>,
 * }}
 *   `hayMovimiento=false` cuando los 30 días históricos no tuvieron NINGÚN cobro ni
 *   pago real en ninguna moneda (estado vacío de la spec 1.6: "Todavía no hay
 *   movimientos para proyectar" en vez de un gráfico de líneas plano en cero).
 */
export function armarSeriesRitmoCobrosPagos(cashflow) {
  const historical = Array.isArray(cashflow?.historical) ? cashflow.historical : [];
  const projected = Array.isArray(cashflow?.projected) ? cashflow.projected : [];
  const dias = [...historical, ...projected];

  const monedas = monedasConMovimientoReal(historical);
  if (monedas.length === 0 || dias.length === 0) {
    return { hayMovimiento: false, monedas: [], ejeXTicks: [] };
  }

  // El último día de "historical" es hoy (ver contrato documentado arriba).
  const indiceHoy = historical.length - 1;

  return {
    hayMovimiento: true,
    monedas: monedas.map((currency) => ({
      currency,
      puntos: dias.map((dia, indice) => ({
        x: indice,
        cobros: montoDeMoneda(dia?.cashInByCurrency, currency),
        pagos: montoDeMoneda(dia?.cashOutByCurrency, currency),
      })),
    })),
    ejeXTicks: construirTicksEje(indiceHoy, dias.length),
  };
}

/** Qué monedas tuvieron al menos un cobro o pago real en la ventana histórica (no en la proyección, que es solo un promedio de esos mismos días). */
function monedasConMovimientoReal(historical) {
  const encontradas = new Set();
  for (const dia of historical) {
    for (const linea of dia?.cashInByCurrency ?? []) {
      if (Number(linea?.amount ?? 0) !== 0) encontradas.add(linea.currency);
    }
    for (const linea of dia?.cashOutByCurrency ?? []) {
      if (Number(linea?.amount ?? 0) !== 0) encontradas.add(linea.currency);
    }
  }

  // Orden estable "pesos primero" (mismo criterio que ordenarBloquesPesosPrimero
  // en el resto de la app), con cualquier moneda futura no contemplada al final.
  const canonicas = MONEDAS_ORDEN_CANONICO.filter((moneda) => encontradas.has(moneda));
  const otras = [...encontradas].filter((moneda) => !MONEDAS_ORDEN_CANONICO.includes(moneda));
  return [...canonicas, ...otras];
}

function montoDeMoneda(lineas, currency) {
  const linea = Array.isArray(lineas) ? lineas.find((l) => l?.currency === currency) : null;
  return linea ? Number(linea.amount) : 0;
}

function construirTicksEje(indiceHoy, totalPuntos) {
  return [
    { offset: 0, etiqueta: "Hoy" },
    { offset: 30, etiqueta: "+30" },
    { offset: 60, etiqueta: "+60" },
    { offset: 90, etiqueta: "+90" },
  ]
    .map(({ offset, etiqueta }) => ({ x: indiceHoy + offset, etiqueta }))
    .filter((tick) => tick.x >= 0 && tick.x < totalPuntos);
}
