/**
 * Lógica PURA de las tarjetas KPI multimoneda del dashboard (`DashboardPage.jsx`,
 * vía `features/dashboard/components/MoneyKpiGrid.jsx`). Recibe una de las listas
 * de `dashboard.porMoneda`
 * (`DashboardByCurrencyDto`, ver `IReportService.cs`) y decide QUÉ líneas mostrar —
 * el mismo patrón que ya usa `ReportsPage.jsx` para las tarjetas de la solapa Finanzas.
 *
 * HALLAZGO B3 (revisión 2026-07-27): las tarjetas del dashboard sumaban pesos y dólares
 * como si fueran la misma moneda (ej. saldoPendiente = suma de reserva.Balance de TODAS
 * las reservas activas, sin importar su currency) y encima le pegaban el cartelito "ARS"
 * — un número mezclado con una etiqueta de moneda falsa (viola P-3: pesos y dólares
 * SIEMPRE separados). El dato correcto para separar por moneda YA viaja en
 * `dashboard.porMoneda` (backend, `DashboardByCurrencyDto`), este archivo solo decide
 * cómo pintarlo.
 */

/**
 * Arma las líneas por moneda de una tarjeta KPI a partir de una de las 6 listas de
 * `dashboard.porMoneda` (cobrosDelMes, ventasDelMes, saldoPendiente, etc — cada una
 * `[{currency, amount}]`).
 *
 * Reglas:
 *   - una línea por cada moneda que tuvo movimiento real (monto != 0);
 *   - si NINGUNA moneda tuvo movimiento, se muestra una única línea "$0" en ARS (nunca
 *     una tarjeta en blanco sin ningún número);
 *   - `negativoEsSaldoAFavor` (para "Saldo Pendiente"): un monto negativo en una moneda
 *     puntual no es un error — es que el saldo a favor de los clientes en ESA moneda
 *     supera lo que efectivamente deben. Esa línea se marca con `esSaldoAFavor: true` y
 *     el monto se devuelve en positivo (la tarjeta se encarga de mostrar la leyenda).
 *
 * @param {Array<{currency:string, amount:number}>} listaPorMoneda
 * @param {{negativoEsSaldoAFavor?: boolean}} [opciones]
 * @returns {Array<{currency:string, monto:number, esSaldoAFavor:boolean}>}
 */
export function construirLineasKpiPorMoneda(listaPorMoneda, { negativoEsSaldoAFavor = false } = {}) {
    const items = Array.isArray(listaPorMoneda) ? listaPorMoneda : [];
    const conMovimiento = items.filter((item) => Number(item?.amount ?? 0) !== 0);

    if (conMovimiento.length === 0) {
        return [{ currency: "ARS", monto: 0, esSaldoAFavor: false }];
    }

    return conMovimiento.map((item) => {
        const monto = Number(item.amount ?? 0);
        const esSaldoAFavor = negativoEsSaldoAFavor && monto < 0;
        return {
            currency: item.currency,
            monto: esSaldoAFavor ? Math.abs(monto) : monto,
            esSaldoAFavor,
        };
    });
}

/**
 * Arma las líneas por moneda de una tarjeta KPI, con reenganche a un escalar de
 * compatibilidad SOLO cuando la lista puntual no vino en la respuesta del backend
 * (deploy viejo en caché, sin `dashboard.porMoneda` todavía, o un campo puntual de
 * `porMoneda` que todavía no existe). Antes esta lógica vivía duplicada, sin test,
 * adentro del JSX del dashboard viejo (función `armarLineasKpi`, hoy reemplazado por
 * `DashboardPage.jsx`) — se centraliza acá para poder testearla una sola vez.
 *
 * FIX (revisión 2026-07-27, ítem 4 del re-review): la versión vieja cae al escalar
 * también cuando la lista puntual venía VACÍA — pero una lista vacía es un dato REAL
 * ("sin movimiento en ninguna moneda este mes"), no un dato faltante. Confundir ambos
 * casos hacía que un mes sin ventas mostrara el escalar viejo (potencialmente
 * desactualizado) en vez de "$0" ARS. La regla correcta: cae al escalar SOLO cuando
 * `listaPorMoneda` NO es un array (undefined/null) — eso sí significa "el backend no
 * mandó este dato todavía"; `construirLineasKpiPorMoneda` ya sabe devolver la línea
 * "$0" ARS cuando la lista es un array vacío o sin movimiento real.
 *
 * @param {Array<{currency:string, amount:number}>|null|undefined} listaPorMoneda
 * @param {number} valorEscalarDeCompatibilidad - el campo viejo del dashboard
 *   (ej. `dashboard.ventasDelMes`), usado SOLO si `listaPorMoneda` no vino.
 * @param {{negativoEsSaldoAFavor?: boolean}} [opciones]
 * @returns {Array<{currency:string, monto:number, esSaldoAFavor:boolean}>}
 */
export function construirLineasKpiConCompatibilidad(listaPorMoneda, valorEscalarDeCompatibilidad, opciones) {
    const lista = Array.isArray(listaPorMoneda)
        ? listaPorMoneda
        : [{ currency: "ARS", amount: Number(valorEscalarDeCompatibilidad) || 0 }];
    return construirLineasKpiPorMoneda(lista, opciones);
}
