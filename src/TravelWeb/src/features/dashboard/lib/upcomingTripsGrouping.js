/**
 * Lógica pura de la tarjeta "Salidas de los próximos 7 días" del dashboard
 * (spec `docs/ux/2026-08-18-spec-dashboard-y-cuentas-corrientes.md`, sección 1.3/1.4).
 *
 * Dos responsabilidades, separadas del JSX para poder testearlas sin montar React:
 *   1. Agrupar las salidas por día (no repetir la fecha si dos viajes salen el
 *      mismo día — mismo criterio que ya usan los separadores de fecha del resto
 *      de la app).
 *   2. Armar el chip de deuda de cada fila: rojo "Debe US$ X" si `PendingBalances`
 *      trae algo, verde "Saldada" si la lista viene vacía (R4 de la spec, YA
 *      resuelto en el backend — no hay más estado transitorio que contemplar).
 */

// Mismo patrón que FECHA_SOLO_DIA_REGEX de lib/utils.js: el backend manda
// `startDate` como fecha-solo-día (sin hora real), así que se lee el
// año/mes/día directo del texto ISO en vez de pasar por `new Date(...)` — eso
// evita el corrimiento de un día que causaría convertir a la zona horaria del
// navegador (mismo bug ya documentado en formatDate()).
const FECHA_SOLO_DIA_REGEX = /^(\d{4})-(\d{2})-(\d{2})/;
const DIAS_CORTOS = ["Dom", "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb"];

/**
 * Etiqueta de agrupador para una fecha-solo-día ("Lun 24/08"). Si el texto no
 * matchea el formato esperado, devuelve `null` (el llamador decide qué hacer:
 * nunca se inventa una fecha).
 *
 * @param {string} fechaIso
 * @returns {{ clave: string, etiqueta: string } | null}
 */
export function etiquetaDeDia(fechaIso) {
  const match = FECHA_SOLO_DIA_REGEX.exec(fechaIso || "");
  if (!match) return null;

  const [, anioTexto, mesTexto, diaTexto] = match;
  const anio = Number(anioTexto);
  const mes = Number(mesTexto);
  const dia = Number(diaTexto);

  // Date.UTC (no `new Date(fechaIso)`) para no depender de la hora local del
  // navegador al calcular el día de la semana.
  const diaDeLaSemana = new Date(Date.UTC(anio, mes - 1, dia)).getUTCDay();

  return {
    clave: `${anioTexto}-${mesTexto}-${diaTexto}`,
    etiqueta: `${DIAS_CORTOS[diaDeLaSemana]} ${diaTexto}/${mesTexto}`,
  };
}

/**
 * Agrupa la lista de `ProximosViajes` (ya ordenada por `startDate` desde el
 * backend) en bloques por día, listos para pintar un separador de fecha una
 * sola vez por grupo.
 *
 * @param {Array<{startDate: string}>} proximosViajes
 * @returns {Array<{ clave: string, etiqueta: string, viajes: Array }>}
 */
export function agruparSalidasPorDia(proximosViajes) {
  const lista = Array.isArray(proximosViajes) ? proximosViajes : [];
  const grupos = [];
  const indicePorClave = new Map();

  for (const viaje of lista) {
    const dia = etiquetaDeDia(viaje?.startDate);
    // Fecha rota/faltante (no debería pasar, pero no se inventa un grupo "sin
    // fecha" con datos falsos): la salida se descarta de la agrupación.
    if (!dia) continue;

    if (!indicePorClave.has(dia.clave)) {
      indicePorClave.set(dia.clave, grupos.length);
      grupos.push({ clave: dia.clave, etiqueta: dia.etiqueta, viajes: [] });
    }
    grupos[indicePorClave.get(dia.clave)].viajes.push(viaje);
  }

  return grupos;
}

/**
 * Chip de deuda de una fila de "Salidas próximas".
 *
 * `pendingBalances` es la lista `[{currency, amount}]` que manda `UpcomingTripDto`
 * (R4 de la spec): vacía = reserva saldada (chip verde), con algo = debe en esa(s)
 * moneda(s) (chip rojo, una línea por moneda — P-3, nunca se suman pesos y dólares).
 *
 * @param {Array<{currency: string, amount: number}>} pendingBalances
 * @returns {{ tone: "danger"|"success", lineas: Array<{currency: string, amount: number}> } }
 */
export function armarChipDeudaSalida(pendingBalances) {
  const lineas = Array.isArray(pendingBalances)
    ? pendingBalances.filter((linea) => Number(linea?.amount ?? 0) > 0)
    : [];

  if (lineas.length === 0) {
    return { tone: "success", lineas: [] };
  }

  return {
    tone: "danger",
    lineas: lineas.map((linea) => ({ currency: linea.currency, amount: Number(linea.amount) })),
  };
}
