/**
 * Mapa canónico ESTADO → label en español para reservas (Obra 6, firma de Gastón
 * 2026-07-27: "Estados de reserva en inglés en los dashboards ('Confirmed',
 * 'InManagement') se traducen con las mismas etiquetas criollas de las pestañas de
 * Reservas").
 *
 * Es la fuente ÚNICA del TEXTO de cada estado: `ReservaStatusBadge.jsx` (el badge de
 * color que ya se usa en el listado y la ficha de Reservas) importa estos labels para no
 * mantener una segunda copia del mismo mapeo que se pueda desincronizar con el tiempo.
 *
 * Se separa en un archivo `.js` PURO (sin JSX) a propósito: así se puede testear el
 * mapeo con `node --test` sin montar React — un archivo `.jsx` con JSX de verdad (como
 * `ReservaStatusBadge.jsx`) no se puede importar desde un test plano de Node, que no
 * entiende sintaxis JSX.
 *
 * Los keys son los strings persistidos en la BD, alineados con `EstadoReserva.cs`
 * (motor). "Archived" es un estado lateral (soft-delete de reservas viejas) que no está
 * como constante en `EstadoReserva.cs`, pero SÍ llega como string en `reserva.status`.
 */
export const RESERVA_STATUS_LABELS = {
  Quotation: "Cotizacion",
  Budget: "Presupuesto",
  InManagement: "En gestion",
  Confirmed: "Confirmada",
  Traveling: "En viaje",
  Closed: "Finalizada",
  Lost: "Perdido",
  Cancelled: "Anulada",
  PendingOperatorRefund: "Esperando reembolso",
  Archived: "Archivada",
};

/**
 * Traduce un status de reserva a su label en español. Un status desconocido cae en "—"
 * (dato neutro), NUNCA la clave técnica cruda — importante para pantallas de RESUMEN
 * (dashboards) donde no hay contexto adicional para que un usuario no programador
 * entienda una clave suelta como "Quotation" o "InManagement".
 *
 * Fix bloqueante del reviewer (2026-07-27): `translateStatus` (en `ReservaStatusBadge.jsx`)
 * ahora DELEGA acá — antes tenía su propio fallback que devolvía el string crudo del
 * backend como último recurso, mismo problema que esta función siempre evitó. Con la
 * delegación, todo el árbol de Reservas (listado, ficha, badges, dashboards) usa esta
 * MISMA función y el mismo criterio de fallback, sin una segunda fuente que pueda
 * desincronizarse.
 *
 * @param {string|null|undefined} status
 * @returns {string}
 */
export function traducirEstadoReserva(status) {
  return RESERVA_STATUS_LABELS[status] || "—";
}
