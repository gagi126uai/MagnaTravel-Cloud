/**
 * Qué piezas del dashboard "Inicio" se muestran, según los permisos del usuario
 * logueado (spec firmada `docs/ux/2026-08-18-spec-dashboard-y-cuentas-corrientes.md`,
 * sección 1.5 "Variantes por rol").
 *
 * R1 de la spec: el dashboard deja de rutear por `isAdmin` (un solo componente para
 * todos los roles) y en cambio arma su layout mirando permisos puntuales. Este
 * archivo es la ÚNICA fuente de esa decisión — así se puede testear sin montar
 * ningún componente de React (node --test, sin JSX).
 *
 * OJO: "Salidas próximas" y "Cobros pendientes" NO se gatean acá. El backend YA
 * filtra esas listas a la cartera propia cuando el usuario no tiene
 * `reservas.view_all` (ver `IReportService.cs`, `ResolveUserScopeAsync`) — el
 * frontend nunca vuelve a filtrar un dato que el backend ya recortó (P-13).
 */

/**
 * @param {{ puedeVerCostos: boolean, esAdmin: boolean }} permisos
 *   - puedeVerCostos: `hasPermission("cobranzas.see_cost")` — margen, pagos a
 *     operadores y todo lo que revele cuánto le cuesta un servicio a la agencia.
 *   - esAdmin: `isAdmin()` — "Ver informes" navega a `/analytics`, que hoy sigue
 *     detrás de endpoints `[Authorize(Roles="Admin")]` duros (sellers/destinations/
 *     yoy no migraron a permisos todavía). Mientras eso no cambie, se gatea por rol
 *     tal cual pide la spec (sección 1.5, última fila de la tabla).
 * @returns {{
 *   verMargenBruto: boolean,
 *   verCajaProyectada: boolean,
 *   verInformes: boolean,
 *   columnasGridKpi: 3|4,
 * }}
 */
export function calcularVisibilidadDashboard({ puedeVerCostos, esAdmin }) {
  const verCostos = Boolean(puedeVerCostos);
  return {
    // Margen bruto revela costo (venta - margen = costo): mismo criterio que ya
    // usa el backend para vaciar la lista PorMoneda.MargenBruto sin el permiso.
    verMargenBruto: verCostos,
    // "Por pagar a operadores" es, literalmente, información de costo.
    verCajaProyectada: verCostos,
    verInformes: Boolean(esAdmin),
    // Sin Margen bruto el grid pasa de 2x2 a una fila de 3 (spec 1.5, nota debajo
    // de la tabla): nunca 2+1 desparejo ni un hueco vacío donde iba la 4ta tarjeta.
    columnasGridKpi: verCostos ? 4 : 3,
  };
}
