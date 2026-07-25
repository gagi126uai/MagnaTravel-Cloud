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
 * @returns {{ texto: string, esExceso: boolean }}
 *   - texto: lo que hay que mostrar en pantalla.
 *   - esExceso: true si se facturó de más (para que el componente pueda usar un tono
 *     de color distinto al ámbar habitual de "falta facturar").
 */
export function formatearFaltaFacturar(disponibleParaFacturar, moneda) {
  const valor = Number(disponibleParaFacturar ?? 0);

  if (valor < 0) {
    return {
      texto: `Facturaste ${formatCurrency(Math.abs(valor), moneda)} de más`,
      esExceso: true,
    };
  }

  return {
    texto: formatCurrency(valor, moneda),
    esExceso: false,
  };
}
