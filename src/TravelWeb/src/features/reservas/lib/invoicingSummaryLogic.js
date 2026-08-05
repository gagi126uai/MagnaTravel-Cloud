/**
 * Lógica pura del KPI "Falta facturar" del Estado de Cuenta de la reserva
 * (barrido de PROD 2026-07-24, hallazgo #23).
 *
 * `disponibleParaFacturar` (campo que manda el backend) es lo que todavía se puede
 * facturar de lo vendido firme. Normalmente es positivo o cero. Pero puede dar
 * NEGATIVO cuando ya se facturó MÁS de lo vendido firme actual — por ejemplo, se
 * facturó un servicio y después se le bajó el precio, o se sacó un servicio de la
 * reserva sin tocar la factura ya emitida. En ese caso, mostrar el número pelado
 * (ej. "-$1.500,00") confunde: parece un error de cuenta, no una situación real.
 *
 * Esta función arma el texto que corresponde mostrar en cada caso, sin decidir
 * nada de negocio (el signo y el monto YA vienen calculados del backend — acá
 * solo se elige cómo explicarlo en criollo).
 *
 * Vive sin JSX para poder testearse con Node puro (mismo patrón que
 * costConfirmationGuard.js): node --test .../invoicingSummaryLogic.test.mjs
 */

import { formatCurrency } from "../../../lib/utils.js";

/**
 * @param {number|string|null|undefined} disponibleParaFacturar - campo del backend,
 *   puede venir negativo cuando se facturó de más.
 * @param {string} moneda - "ARS" | "USD" (la moneda de esta fila del KPI).
 * @param {{ withSymbol?: boolean }} [opciones] - withSymbol=false omite el "$"/"US$"
 *   del monto (se usa cuando al lado ya hay un CurrencyBadge mostrando el símbolo —
 *   fix símbolo duplicado, prueba integral 2026-08-05). Default true: comportamiento
 *   de siempre, para los call sites mono-moneda que no tienen badge.
 * @returns {{ texto: string, esExceso: boolean }}
 *   - texto: lo que hay que mostrar en pantalla.
 *   - esExceso: true si se facturó de más (para que el componente pueda usar un tono
 *     de color distinto al ámbar habitual de "falta facturar").
 */
export function formatearFaltaFacturar(disponibleParaFacturar, moneda, { withSymbol = true } = {}) {
  const valor = Number(disponibleParaFacturar ?? 0);

  if (valor < 0) {
    return {
      texto: `Facturaste ${formatCurrency(Math.abs(valor), moneda, { withSymbol })} de más`,
      esExceso: true,
    };
  }

  return {
    texto: formatCurrency(valor, moneda, { withSymbol }),
    esExceso: false,
  };
}

/**
 * Arma el texto del "Margen" del bloque Costo y margen (FIX 2026-08-05, prueba
 * integral: el margen negativo se pintaba violeta —el mismo color que una
 * ganancia— y mostraba el signo "-" pelado, ej. "-$ 600,00"). Cada moneda evalúa
 * su propio signo (regla P-3: nunca se suman pesos y dólares, así que puede haber
 * ganancia en una moneda y pérdida en otra al mismo tiempo).
 *
 * @param {number|string|null|undefined} margen - `margin`/`totalMargin` del backend
 *   (venta menos costo). Ya viene calculado; acá solo se decide cómo mostrarlo.
 * @param {string} moneda - "ARS" | "USD".
 * @param {{ withSymbol?: boolean }} [opciones] - ver formatearFaltaFacturar.
 * @returns {{ texto: string, esPerdida: boolean }}
 *   - texto: lo que hay que mostrar en pantalla ("Pérdida de $X" si dio negativo).
 *   - esPerdida: true si el margen es negativo (para pintarlo rojo, no violeta).
 */
export function formatearMargen(margen, moneda, { withSymbol = true } = {}) {
  const valor = Number(margen ?? 0);

  if (valor < 0) {
    return {
      texto: `Pérdida de ${formatCurrency(Math.abs(valor), moneda, { withSymbol })}`,
      esPerdida: true,
    };
  }

  return {
    texto: formatCurrency(valor, moneda, { withSymbol }),
    esPerdida: false,
  };
}
