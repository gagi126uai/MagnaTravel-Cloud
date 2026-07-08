/**
 * Lógica pura para decidir qué solapa se abre primero en /pendientes-afip.
 *
 * Por qué existe (spec "fin de las bandejas", 2026-07-08): las 3 bandejas
 * back-office (multas/ND, notas de crédito por revisar, recibos por
 * reconciliar) se unificaron en una sola página con solapas. Cada solapa
 * requiere un permiso distinto, así que un mismo usuario puede ver 1, 2 o
 * las 3 según su rol — la primera solapa visible se elige acá, sin
 * dependencias de React, para poder testearla con Node solo (mismo patrón
 * que moneyStatus.js / invoiceCurrencyDefault.js en reservas).
 */

// Orden fijo de preferencia cuando no hay ?tab= en la URL (o el que vino no
// es válido/permitido): multas → notasCredito → recibos.
export const AFIP_PENDING_TABS = [
  { key: "multas", label: "Multas y cargos", permission: "cobranzas.invoice_annul" },
  { key: "notasCredito", label: "Notas de crédito", permission: "cobranzas.view_all" },
  { key: "recibos", label: "Recibos por regularizar", permission: "approvals.review" },
];

/**
 * Devuelve solo las solapas que el usuario puede ver, respetando el orden fijo.
 *
 * @param {(permission: string) => boolean} hasPermissionFn
 */
export function getAllowedAfipPendingTabs(hasPermissionFn) {
  return AFIP_PENDING_TABS.filter((tab) => hasPermissionFn(tab.permission));
}

/**
 * Resuelve la key de la solapa inicial.
 *
 * Reglas:
 *  - Si `?tab=<key>` vino en la URL Y esa key es una solapa permitida para
 *    el usuario, arranca ahí.
 *  - Si no vino, o vino pero el usuario no tiene permiso para esa solapa
 *    (o la key no existe), arranca en la primera permitida según el orden
 *    fijo (nunca rompe: cae con gracia a lo que SÍ puede ver).
 *  - Si el usuario no tiene ningún permiso, devuelve null (la página
 *    entera no debería ser accesible en ese caso — lo resuelve el guard
 *    de la ruta en App.jsx).
 *
 * @param {string | null | undefined} queryTabParam - valor crudo de `?tab=`
 * @param {(permission: string) => boolean} hasPermissionFn
 * @returns {string | null}
 */
export function resolveInitialAfipPendingTab(queryTabParam, hasPermissionFn) {
  const allowedTabs = getAllowedAfipPendingTabs(hasPermissionFn);
  if (allowedTabs.length === 0) return null;

  const tabPedidoEsValido = allowedTabs.some((tab) => tab.key === queryTabParam);
  if (queryTabParam && tabPedidoEsValido) {
    return queryTabParam;
  }

  return allowedTabs[0].key;
}
