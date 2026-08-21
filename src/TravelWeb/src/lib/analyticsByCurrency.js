/**
 * Lógica PURA de las solapas "Vendedores", "Destinos" e "Interanual" de
 * `AnalyticsPage.jsx`. El backend (`ReportService.GetSellerRankingAsync`,
 * `GetDestinationAnalyticsAsync`, `GetYearOverYearAsync`) agregó campos ADITIVOS
 * por moneda (`totalSalesByCurrency`, `totalRevenueByCurrency`, `salesByCurrency`,
 * etc — cada uno `[{currency, amount}]`) sin tocar los escalares legacy que ya
 * mezclaban ARS y USD en un solo número.
 *
 * Reglas de la guía UX que este archivo aplica (`docs/ux/guia-ux-gaston.md`):
 *   - línea 538 / Constitución P-3: las monedas nunca se suman ni convierten.
 *   - línea 554: los rankings son DOS listas separadas, una por moneda.
 *   - línea 649: solo se listan las monedas que se usan de verdad (sin "US$ 0"
 *     fantasma) — como el backend ya arma las listas por moneda agrupando SOLO
 *     filas reales (`ReportService.SumByCurrency`), acá no hace falta filtrar de
 *     nuevo por monto distinto de cero: si una moneda aparece en la lista es
 *     porque tuvo al menos una reserva real en esa moneda.
 *   - patrón firmado "una sola moneda = la pantalla se ve IGUAL que hoy": si todos
 *     los datos están en una sola moneda (o el backend todavía no manda el campo
 *     por moneda, deploy viejo en caché), se devuelve un único bloque sin título
 *     de moneda, usando el mismo orden que YA trae el backend.
 *   - F-14: costo y margen vienen en listas VACÍAS (no en $0) cuando el usuario no
 *     tiene `cobranzas.see_cost` — acá eso se traduce en `margenPercent: null`
 *     (la fila oculta la columna de margen en vez de mostrar "0%").
 */

const MONEDAS_ORDEN_CANONICO = ["ARS", "USD"];

/** Qué monedas aparecen de verdad en alguna de las listas `[{currency, amount}]` de `items`, pesos primero. */
function monedasPresentes(items, extractorListaPorMoneda) {
  const encontradas = new Set();
  for (const item of items) {
    for (const linea of extractorListaPorMoneda(item) ?? []) {
      if (linea?.currency) encontradas.add(linea.currency);
    }
  }
  const canonicas = MONEDAS_ORDEN_CANONICO.filter((moneda) => encontradas.has(moneda));
  const otras = [...encontradas].filter((moneda) => !MONEDAS_ORDEN_CANONICO.includes(moneda));
  return [...canonicas, ...otras];
}

function montoDeMoneda(lineas, currency) {
  const linea = Array.isArray(lineas) ? lineas.find((l) => l?.currency === currency) : null;
  return linea ? Number(linea.amount) : 0;
}

/** true si `currency` tiene una línea propia en la lista — false para lista vacía (F-14, sin permiso de costo) o moneda sin movimiento. */
function tieneLinea(lineas, currency) {
  return Array.isArray(lineas) && lineas.some((l) => l?.currency === currency);
}

function maximoOUno(numeros) {
  const valores = numeros.filter((n) => Number.isFinite(n));
  return valores.length > 0 ? Math.max(...valores, 1) : 1;
}

/**
 * Ranking de vendedores por moneda (solapa "Vendedores").
 *
 * `filesCreated` es un conteo GLOBAL (todas las monedas del vendedor juntas): con una
 * sola moneda viaja tal cual (es honesto), pero en los bloques multi-moneda viene
 * `null` — mostrar el mismo total en cada bloque de moneda haría que se cuente doble
 * a quien sume los bloques (hallazgo de review 2026-08-20).
 *
 * @param {Array} sellers - `SellerRankingDto[]`
 * @returns {{ hayMasDeUnaMoneda: boolean, bloques: Array<{ currency: string, maxMonto: number,
 *   vendedores: Array<{ userId, sellerName, monto, filesCreated: number|null, margenPercent: number|null }> }> }}
 */
export function armarRankingVendedoresPorMoneda(sellers) {
  const items = Array.isArray(sellers) ? sellers : [];
  const monedas = monedasPresentes(items, (s) => s?.totalSalesByCurrency);

  if (monedas.length <= 1) {
    // Una sola moneda (o backend viejo sin el campo por moneda): mismo orden que ya
    // trae el backend (`.OrderByDescending(s => s.TotalSales)`), no se reordena acá.
    const vendedores = items.map((s) => ({
      userId: s.userId,
      sellerName: s.sellerName,
      monto: Number(s.totalSales ?? 0),
      filesCreated: s.filesCreated,
      margenPercent: s.marginPercent != null ? Number(s.marginPercent) : null,
    }));
    return {
      hayMasDeUnaMoneda: false,
      bloques: [{ currency: monedas[0] ?? "ARS", maxMonto: maximoOUno(vendedores.map((v) => v.monto)), vendedores }],
    };
  }

  const bloques = monedas.map((currency) => {
    const vendedores = items
      .filter((s) => tieneLinea(s.totalSalesByCurrency, currency))
      .map((s) => {
        const monto = montoDeMoneda(s.totalSalesByCurrency, currency);
        const margenMonto = tieneLinea(s.grossMarginByCurrency, currency)
          ? montoDeMoneda(s.grossMarginByCurrency, currency)
          : null;
        return {
          userId: s.userId,
          sellerName: s.sellerName,
          monto,
          // `filesCreated` es un conteo GLOBAL del vendedor (todas las monedas juntas):
          // repetirlo idéntico en cada bloque de moneda haría que alguien que suma los
          // bloques cuente el mismo file dos veces (bloqueante de review, 2026-08-20).
          // Partirlo por moneda tampoco sirve: un file con servicios en ARS Y USD
          // contaría doble igual. Se oculta directamente en el camino multi-moneda.
          filesCreated: null,
          margenPercent: margenMonto != null && monto > 0 ? (margenMonto / monto) * 100 : null,
        };
      })
      .sort((a, b) => b.monto - a.monto);

    return { currency, maxMonto: maximoOUno(vendedores.map((v) => v.monto)), vendedores };
  });

  return { hayMasDeUnaMoneda: true, bloques };
}

/**
 * Ranking de destinos por moneda (solapa "Destinos").
 *
 * `bookingCount`/`passengerCount` son conteos GLOBALES del destino (todas las
 * monedas juntas): con una sola moneda viajan tal cual (es honesto), pero en los
 * bloques multi-moneda vienen `null` — mismo motivo que `filesCreated` en
 * vendedores (ver `armarRankingVendedoresPorMoneda`, hallazgo de review 2026-08-20).
 *
 * @param {Array} destinations - `DestinationAnalyticsDto[]`
 * @returns {{ hayMasDeUnaMoneda: boolean, bloques: Array<{ currency: string, maxMonto: number,
 *   destinos: Array<{ destination, monto, margenMonto: number|null, bookingCount: number|null, passengerCount: number|null }> }> }}
 */
export function armarRankingDestinosPorMoneda(destinations) {
  const items = Array.isArray(destinations) ? destinations : [];
  const monedas = monedasPresentes(items, (d) => d?.totalRevenueByCurrency);

  if (monedas.length <= 1) {
    // Mismo orden que ya trae el backend (`.OrderByDescending(d => d.TotalRevenue).Take(15)`).
    const destinos = items.map((d) => ({
      destination: d.destination,
      monto: Number(d.totalRevenue ?? 0),
      margenMonto: d.margin != null ? Number(d.margin) : null,
      bookingCount: d.bookingCount,
      passengerCount: d.passengerCount,
    }));
    return {
      hayMasDeUnaMoneda: false,
      bloques: [{ currency: monedas[0] ?? "ARS", maxMonto: maximoOUno(destinos.map((d) => d.monto)), destinos }],
    };
  }

  const bloques = monedas.map((currency) => {
    const destinos = items
      .filter((d) => tieneLinea(d.totalRevenueByCurrency, currency))
      .map((d) => ({
        destination: d.destination,
        monto: montoDeMoneda(d.totalRevenueByCurrency, currency),
        margenMonto: tieneLinea(d.marginByCurrency, currency) ? montoDeMoneda(d.marginByCurrency, currency) : null,
        // bookingCount/passengerCount son conteos GLOBALES del destino (todas las
        // monedas juntas) — mismo motivo que filesCreated en vendedores: repetirlos
        // en cada bloque de moneda duplicaría el conteo para quien sume los bloques.
        // Se ocultan en el camino multi-moneda (bloqueante de review, 2026-08-20).
        bookingCount: null,
        passengerCount: null,
      }))
      .sort((a, b) => b.monto - a.monto);

    return { currency, maxMonto: maximoOUno(destinos.map((d) => d.monto)), destinos };
  });

  return { hayMasDeUnaMoneda: true, bloques };
}

/**
 * Comparativa interanual por moneda (KPI "Crecimiento" + solapa "Interanual").
 * `YearOverYearResponse` NO trae totales anuales por moneda (solo cada mes trae
 * `salesByCurrency`) — acá se suman los 12 meses de cada moneda para armar el
 * total anual y el % de crecimiento. Sumar meses DENTRO de una misma moneda está
 * permitido por P-3 (lo prohibido es sumar entre monedas distintas).
 *
 * @param {{ currentYear: Array, previousYear: Array, currentYearTotal: number,
 *   previousYearTotal: number, growthPercent: number }} yoy - `YearOverYearResponse`
 * @returns {{ hayMasDeUnaMoneda: boolean, bloques: Array<{ currency: string, totalActual: number,
 *   totalAnterior: number, crecimientoPercent: number, maxMonto: number,
 *   meses: Array<{ month: string, actual: number, anterior: number }> }> }}
 */
export function armarComparativaInteranualPorMoneda(yoy) {
  const currentYear = Array.isArray(yoy?.currentYear) ? yoy.currentYear : [];
  const previousYear = Array.isArray(yoy?.previousYear) ? yoy.previousYear : [];
  const monedas = monedasPresentes([...currentYear, ...previousYear], (mes) => mes?.salesByCurrency);

  if (monedas.length <= 1) {
    const meses = currentYear.map((mes, idx) => ({
      month: mes.month,
      actual: Number(mes.sales ?? 0),
      anterior: Number(previousYear[idx]?.sales ?? 0),
    }));
    return {
      hayMasDeUnaMoneda: false,
      bloques: [{
        currency: monedas[0] ?? "ARS",
        totalActual: Number(yoy?.currentYearTotal ?? 0),
        totalAnterior: Number(yoy?.previousYearTotal ?? 0),
        crecimientoPercent: Number(yoy?.growthPercent ?? 0),
        maxMonto: maximoOUno(meses.flatMap((m) => [m.actual, m.anterior])),
        meses,
      }],
    };
  }

  const bloques = monedas.map((currency) => {
    const meses = currentYear.map((mes, idx) => ({
      month: mes.month,
      actual: montoDeMoneda(mes.salesByCurrency, currency),
      anterior: montoDeMoneda(previousYear[idx]?.salesByCurrency, currency),
    }));
    const totalActual = meses.reduce((suma, m) => suma + m.actual, 0);
    const totalAnterior = meses.reduce((suma, m) => suma + m.anterior, 0);
    // Mismo redondeo a 1 decimal que usa el backend para growthPercent (ReportService.GetYearOverYearAsync).
    const crecimientoPercent = totalAnterior > 0
      ? Math.round(((totalActual - totalAnterior) / totalAnterior) * 1000) / 10
      : 0;

    return {
      currency,
      totalActual,
      totalAnterior,
      crecimientoPercent,
      maxMonto: maximoOUno(meses.flatMap((m) => [m.actual, m.anterior])),
      meses,
    };
  });

  return { hayMasDeUnaMoneda: true, bloques };
}
