/**
 * Lógica pura de la tarjeta "Cobros pendientes" del dashboard (spec
 * `docs/ux/2026-08-18-spec-dashboard-y-cuentas-corrientes.md`, sección 1.3).
 *
 * Por qué hace falta agrupar: el backend arma `ReservasPendientes` como el TOP 5
 * deudoras DE CADA MONEDA por separado (ver `ReportService.GetDashboardAsync`,
 * un `foreach (var currency in Monedas.Soportadas)`) — así que una misma reserva
 * con deuda en pesos Y en dólares llega como DOS filas distintas con el mismo
 * `publicId`, cada una con su propio `balance`/`currency`. La maqueta pide UNA
 * fila por reserva con "una línea por moneda si la reserva tiene deuda en más de
 * una" (P-3) — este archivo hace ese agrupado antes de llegar al JSX.
 */

/**
 * @param {Array<{publicId: string, numeroReserva: string, name: string, balance: number, currency: string}>} reservasPendientes
 * @returns {Array<{ publicId: string, numeroReserva: string, name: string, lineas: Array<{currency: string, amount: number}> }>}
 *   Una entrada por reserva, en el mismo orden en que aparece por primera vez en
 *   la lista de origen (el backend ya la manda ordenada por monto descendente
 *   dentro de cada moneda).
 */
export function agruparCobrosPendientesPorReserva(reservasPendientes) {
  const lista = Array.isArray(reservasPendientes) ? reservasPendientes : [];
  const grupos = [];
  const indicePorPublicId = new Map();

  for (const fila of lista) {
    const publicId = fila?.publicId;
    if (!publicId) continue;

    if (!indicePorPublicId.has(publicId)) {
      indicePorPublicId.set(publicId, grupos.length);
      grupos.push({
        publicId,
        numeroReserva: fila.numeroReserva,
        name: fila.name,
        lineas: [],
      });
    }

    grupos[indicePorPublicId.get(publicId)].lineas.push({
      currency: fila.currency,
      amount: Number(fila.balance ?? 0),
    });
  }

  return grupos;
}
