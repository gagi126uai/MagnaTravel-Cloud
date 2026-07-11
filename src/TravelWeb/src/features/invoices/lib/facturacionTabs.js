/**
 * Lógica PURA de las solapas de la pantalla de Facturación (ADR-044 T4, fix F1 del
 * gate de frontend, 2026-07-10).
 *
 * Por qué existe: cuando "Pendientes con AFIP" se fusionó dentro de /facturacion, el
 * guard de la RUTA (App.jsx) pasó a exigir solo `cobranzas.view_all` — pero antes esa
 * bandeja también la veían un Vendedor con SOLO `cobranzas.invoice_annul` (bandeja de
 * multas) y un revisor con SOLO `approvals.review` (recibos por regularizar). Sin
 * corregir esto, esos dos roles perdían el acceso al monitor que antes tenían.
 *
 * Mismo patrón que `resolveInitialAfipPendingTab` (afip-pending/lib/resolveInitialTab.js):
 * orden fijo de preferencia, cada solapa se muestra solo si el usuario tiene SU propio
 * permiso, y `?tab=` en la URL solo se respeta si el usuario puede verla.
 */

export const FACTURACION_TAB_TODOS = "todos";
export const FACTURACION_TAB_COMPROBANTES = "comprobantes";
export const FACTURACION_TAB_RECIBOS = "recibos";

/**
 * "Todos los comprobantes" (la tabla global de siempre): requiere `cobranzas.view_all`
 * — sin cambios respecto de antes de la fusión.
 */
export function puedeVerTabTodos(hasPermissionFn) {
  return hasPermissionFn("cobranzas.view_all");
}

/**
 * "Comprobantes por resolver" (multas/cargos + NC por revisar, fusionados): visible si
 * el usuario tiene AL MENOS UNO de los dos permisos que fusiona (OR, no AND) — así el
 * Vendedor que antes solo veía la bandeja de multas (`cobranzas.invoice_annul`) sigue
 * viéndola acá, aunque no tenga `cobranzas.view_all`. Dentro de la solapa, cada fuente
 * (multas / NC) se fetchea SOLO si el usuario tiene el permiso específico de ESA fuente
 * (ver `puedeVerFuenteMultas` / `puedeVerFuenteNotasCredito`) — nunca se llama a un
 * endpoint sin el permiso que ese endpoint exige (evita el 403 documentado en
 * useDebitNotePendingList / usePendingCreditNoteReviewList).
 */
export function puedeVerTabComprobantes(hasPermissionFn) {
  return puedeVerFuenteMultas(hasPermissionFn) || puedeVerFuenteNotasCredito(hasPermissionFn);
}

/** Fuente "multas/cargos pendientes" dentro de "Comprobantes por resolver": `cobranzas.invoice_annul`. */
export function puedeVerFuenteMultas(hasPermissionFn) {
  return hasPermissionFn("cobranzas.invoice_annul");
}

/** Fuente "notas de crédito por revisar" dentro de "Comprobantes por resolver": `cobranzas.view_all`. */
export function puedeVerFuenteNotasCredito(hasPermissionFn) {
  return hasPermissionFn("cobranzas.view_all");
}

/** "Recibos por regularizar": requiere `approvals.review` — sin cambios. */
export function puedeVerTabRecibos(hasPermissionFn) {
  return hasPermissionFn("approvals.review");
}

/**
 * Todas las solapas que el usuario puede ver, en el orden fijo de preferencia
 * (Todos → Comprobantes por resolver → Recibos).
 *
 * @param {(permission: string) => boolean} hasPermissionFn
 * @returns {Array<{key: string, label: string}>}
 */
export function getAllowedFacturacionTabs(hasPermissionFn) {
  const tabs = [];
  if (puedeVerTabTodos(hasPermissionFn)) {
    tabs.push({ key: FACTURACION_TAB_TODOS, label: "Todos los comprobantes" });
  }
  if (puedeVerTabComprobantes(hasPermissionFn)) {
    tabs.push({ key: FACTURACION_TAB_COMPROBANTES, label: "Comprobantes por resolver" });
  }
  if (puedeVerTabRecibos(hasPermissionFn)) {
    tabs.push({ key: FACTURACION_TAB_RECIBOS, label: "Recibos por regularizar" });
  }
  return tabs;
}

/**
 * Resuelve la key de la solapa inicial.
 *
 * Reglas (idénticas a resolveInitialAfipPendingTab):
 *  - Si `?tab=<key>` vino en la URL Y esa key es una solapa permitida, arranca ahí.
 *  - Si no vino, o el usuario no tiene permiso para esa solapa (o la key no existe),
 *    arranca en la primera permitida según el orden fijo.
 *  - Si el usuario no tiene ningún permiso, devuelve null (no debería pasar: el guard
 *    de la ruta en App.jsx ya exige al menos uno de los 3 — esto es un resguardo).
 *
 * @param {string | null | undefined} queryTabParam
 * @param {(permission: string) => boolean} hasPermissionFn
 * @returns {string | null}
 */
export function resolveInitialFacturacionTab(queryTabParam, hasPermissionFn) {
  const allowedTabs = getAllowedFacturacionTabs(hasPermissionFn);
  if (allowedTabs.length === 0) return null;

  const tabPedidoEsValido = allowedTabs.some((tab) => tab.key === queryTabParam);
  if (queryTabParam && tabPedidoEsValido) {
    return queryTabParam;
  }

  return allowedTabs[0].key;
}
