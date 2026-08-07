import { formatCurrency, formatDate } from "../../../lib/utils.js";

/**
 * Arma el texto del renglón gris "Último precio: operador · precio · fecha" que aparece
 * debajo del campo de costo/venta cuando el vendedor elige un producto existente del
 * buscador (spec firmada 2026-08-06, §3.2 / P9=A).
 *
 * Reglas de la spec que este archivo respeta:
 *   - Solo aparece si HAY una venta real anterior (`catalogResult.lastSale`). El
 *     `rateFallback` (precio cargado a mano en el tarifario, sin ninguna venta todavía)
 *     NO cuenta como "aprendido de tus ventas" — por eso NO lo usamos acá.
 *   - Muestra el mismo tipo de monto que ve el vendedor en el campo de al lado: costo para
 *     quien tiene permiso de verlo, venta para el resto (nunca el costo a quien no puede verlo).
 *
 * Nota de alcance (2026-08-06): la spec pide la fecha en ÁMBAR cuando el precio tiene más
 * de 60 días (P10=A), pero el motor (endpoint /rates/catalog-search) todavía NO manda ese
 * dato calculado (isOldPrice/priceAgeText) para la última venta — a diferencia del Tarifario
 * nuevo, que sí lo trae. Mientras el backend no lo agregue acá, este helper devuelve el texto
 * SIN marca de antigüedad: pintarlo ámbar adivinando "más de 60 días" a mano, del lado del
 * front, duplicaría una regla de negocio que tiene que decidir el motor (T-13).
 */
export function buildLastSaleHintText(catalogResult, { canSeeCost = false } = {}) {
  const sale = catalogResult?.lastSale;
  if (!sale) return null;

  const monto = canSeeCost && sale.netCost != null ? sale.netCost : sale.salePrice;
  if (monto == null) return null;

  const precioTexto = formatCurrency(monto, sale.currency || "ARS");
  const fechaTexto = formatDate(sale.soldAt);

  const partes = [sale.supplierName, precioTexto];
  if (fechaTexto && fechaTexto !== "-") {
    partes.push(fechaTexto);
  }
  return partes.filter(Boolean).join(" · ");
}
