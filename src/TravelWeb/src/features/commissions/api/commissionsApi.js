/**
 * Cliente de API para el módulo de comisiones de vendedor.
 *
 * Dos endpoints:
 *  - GET /commissions/summary?year=&month=  → resumen por vendedor del mes.
 *  - GET /commissions/accruals?sellerUserId=&from=&to= → detalle reserva por reserva.
 *
 * Solo el dueño/admin puede ver esta pantalla; el backend también lo valida.
 */
import { api } from "../../../api";

/**
 * Trae el resumen mensual de comisiones agrupado por vendedor.
 *
 * @param {number} year  - Año (ej. 2026).
 * @param {number} month - Mes en base 1 (ej. 6 = junio).
 * @returns {Promise<{ year: number, month: number, sellers: SellerSummaryDto[] }>}
 */
export async function fetchCommissionsSummary(year, month) {
  return api.get(`/commissions/summary?year=${year}&month=${month}`);
}

/**
 * Trae el detalle de acumulaciones de comisión de un vendedor en un rango de fechas.
 * El rango from/to se calcula como primer y último día del mes seleccionado.
 *
 * @param {string} sellerUserId - ID del usuario vendedor.
 * @param {string} from - Fecha ISO "YYYY-MM-DD" (primer día del mes).
 * @param {string} to   - Fecha ISO "YYYY-MM-DD" (último día del mes).
 * @returns {Promise<{ items: CommissionAccrualDto[], totalCount: number }>}
 */
export async function fetchCommissionsAccruals(sellerUserId, from, to) {
  const params = new URLSearchParams({ sellerUserId, from, to });
  return api.get(`/commissions/accruals?${params}`);
}
