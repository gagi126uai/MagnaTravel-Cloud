/**
 * Helpers PUROS para decidir qué avisos INFORMATIVOS de la ficha de reserva van
 * plegados dentro de la barra "N avisos más" (spec UX 2026-07-05, respuesta 5A del
 * rediseño "arriba la foto, abajo solo lo que hay que hacer").
 *
 * Un aviso es "informativo" cuando NO le pide al vendedor hacer nada ahora mismo
 * (a diferencia del banner "con cambios" o la franja del candado, que sí piden una
 * acción). Por eso se pliegan por defecto: no compiten visualmente con lo accionable.
 *
 * Son funciones PURAS (reciben datos, devuelven datos, sin JSX) a propósito: así la
 * decisión "¿hay que mostrar este aviso?" y el componente que lo dibuja (Unconfirmed-
 * ServicesBanner / CapacityWarning) usan la MISMA regla — nunca pueden divergir entre
 * sí (evita el bug clásico de "el contador de la barra dice 2 pero adentro hay 3").
 */

// Estados de servicio que cuentan como "resuelto" (el operador ya contestó o se
// emitió el ticket). Un servicio en cualquier otro estado sigue "sin confirmar".
const ESTADOS_SERVICIO_RESUELTO = new Set(["Confirmado", "Emitido", "HK", "TK", "KK", "KL", "NoConfirmation"]);

/**
 * Lista de servicios de la reserva que todavía no tienen respuesta del proveedor.
 * Solo aplica en Confirmada: en En gestión ya existe otro resumen más específico
 * (ResumenServiciosResueltos) que cubre esta misma información.
 *
 * @param {object} reserva - DTO de la reserva (o null).
 * @returns {Array<{ nombre: string, workflowStatus: string|undefined }>}
 */
export function getServiciosSinConfirmar(reserva) {
  if (!reserva || reserva.status !== "Confirmed") return [];

  // Juntamos todos los tipos de servicio en una sola lista pareja (mismo shape)
  // para poder filtrar una sola vez, en lugar de repetir el filtro por tipo.
  const todosLosServicios = [
    ...(reserva.hotelBookings || []).map(b => ({ nombre: b.name || b.hotelName || "Hotel", workflowStatus: b.workflowStatus || b.status })),
    ...(reserva.transferBookings || []).map(b => ({ nombre: b.name || "Traslado", workflowStatus: b.workflowStatus || b.status })),
    ...(reserva.packageBookings || []).map(b => ({ nombre: b.name || b.packageName || "Paquete", workflowStatus: b.workflowStatus || b.status })),
    ...(reserva.flightSegments || []).map(b => ({ nombre: b.name || "Aéreo", workflowStatus: b.workflowStatus || b.status })),
    ...(reserva.assistanceBookings || []).map(b => ({ nombre: b.name || "Asistencia", workflowStatus: b.workflowStatus || b.status })),
    ...(reserva.servicios || []).map(b => ({ nombre: b.description || "Servicio adicional", workflowStatus: b.workflowStatus || b.status })),
  ];

  // Los cancelados no cuentan para la confirmación: ya quedaron fuera del viaje.
  return todosLosServicios.filter(
    s => s.workflowStatus !== "Cancelado" && !ESTADOS_SERVICIO_RESUELTO.has(s.workflowStatus)
  );
}

/**
 * Detalle de la advertencia de capacidad (más pasajeros que lugares contratados),
 * o null si no corresponde mostrarla.
 *
 * `capacity` puede venir como número plano (legacy, solo capacidad de hotel) o como
 * objeto `{ hotel, transfer, package, total }`.
 *
 * @param {number} paxCount
 * @param {number|{hotel:number,transfer:number,package:number,total:number}} capacity
 * @returns {{ hotel: number, transfer: number, package: number, total: number, detalle: string[] } | null}
 */
export function getAdvertenciaCapacidad(paxCount, capacity) {
  const cap = typeof capacity === "number"
    ? { hotel: capacity, transfer: 0, package: 0, total: capacity }
    : (capacity || { hotel: 0, transfer: 0, package: 0, total: 0 });

  if (paxCount <= 0 || cap.total <= 0 || paxCount <= cap.total) return null;

  const detalle = [];
  if (cap.hotel > 0 && paxCount > cap.hotel) detalle.push(`hotel para ${cap.hotel}`);
  if (cap.transfer > 0 && paxCount > cap.transfer) detalle.push(`transfer para ${cap.transfer}`);
  if (cap.package > 0 && paxCount > cap.package) detalle.push(`paquete para ${cap.package}`);

  return { ...cap, detalle };
}

/**
 * Arma la lista de avisos INFORMATIVOS que corresponde mostrar en la ficha (spec
 * 2026-07-05, respuesta 5A). Devuelve un array de claves — cada una presente SOLO
 * si su condición se cumple. El orden del array es el orden visual dentro del
 * plegado ("N avisos más").
 *
 * @param {{ reserva: object, paxCount: number, capacity: any }} params
 * @returns {Array<"serviciosSinConfirmar"|"capacidad">}
 */
export function construirAvisosInformativos({ reserva, paxCount, capacity }) {
  const avisos = [];
  if (getServiciosSinConfirmar(reserva).length > 0) avisos.push("serviciosSinConfirmar");
  if (getAdvertenciaCapacidad(paxCount, capacity)) avisos.push("capacidad");
  return avisos;
}

/**
 * Texto del contador de la barra plegada, con singular/plural correcto.
 * Ej: 1 → "1 aviso más"; 3 → "3 avisos más".
 *
 * @param {number} cantidad
 * @returns {string}
 */
export function formatearContadorAvisos(cantidad) {
  return cantidad === 1 ? "1 aviso más" : `${cantidad} avisos más`;
}
