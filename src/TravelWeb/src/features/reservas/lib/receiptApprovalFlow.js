/**
 * Lógica pura de la Tanda 2 ("contrato pantalla-motor", spec docs/ux/2026-07-18-t1-t2-contrato-pantalla-motor.md):
 * qué hacer cuando falla emitir o anular el comprobante de un cobro desde la
 * ficha de la reserva.
 *
 * Si el motor devuelve 409 con requiresApproval=true, quiere decir que el
 * vendedor no tiene permiso para esa acción y hace falta que un supervisor
 * autorice. En ese caso la ficha NO se queda con un cartel de error sin
 * salida: abre directo el mismo modal "Solicitar aprobación" que ya usa
 * Cobranzas → Movimientos para el mismo caso (RequestApprovalModal). Para
 * cualquier otro error, la ficha sigue mostrando el cartel de siempre.
 *
 * Vive en un .js sin JSX (mismo patrón que moneyStatus.js o
 * t5ResolverLegacyLogic.js) para poder testearlo con Node puro, sin bundler.
 */

import { formatDate } from "../../../lib/utils.js";

/**
 * Decide si el error de emitir/anular un comprobante es un pedido de
 * autorización del motor o un error de negocio común.
 *
 * @param {object} error - error lanzado por el cliente HTTP (api.js). Trae
 *   `status` (número HTTP) y, si el backend mandó body, `payload` con el JSON.
 * @returns {{ requiereAutorizacion: boolean, requestType?: string, entityType?: string, entityId?: string|number }}
 */
export function resolverAccionAlFallarComprobante(error) {
  const payload = error?.payload;
  const requiereAutorizacion = error?.status === 409 && payload?.requiresApproval === true;

  if (!requiereAutorizacion) {
    return { requiereAutorizacion: false };
  }

  return {
    requiereAutorizacion: true,
    requestType: payload.requestType,
    entityType: payload.entityType,
    entityId: payload.entityId,
  };
}

/**
 * Arma el texto "Sobre:" que ve el vendedor en el modal de autorización,
 * identificando el comprobante en criollo — nunca con IDs internos ni
 * códigos técnicos. Ej: "Comprobante del cobro US$ 500,00 · 15/07/2026".
 *
 * @param {object|null} payment - el cobro (usa payment.amount, payment.currency, payment.paidAt)
 * @param {(amount: number, currency: string) => string} formatCurrency - formateador de moneda del proyecto
 */
export function armarEtiquetaComprobante(payment, formatCurrency) {
  if (!payment || !Number.isFinite(Number(payment.amount))) {
    return "Comprobante de pago";
  }

  const monto = formatCurrency(payment.amount, payment.currency);

  // paidAt viene del backend como fecha-solo-día en medianoche UTC; formatDate
  // es el formateador canónico que evita el bug de "fecha corrida un día".
  // Si la fecha no se puede interpretar, se omite antes que mostrar basura.
  const fechaParseable = payment.paidAt && !Number.isNaN(new Date(payment.paidAt).getTime());
  const fecha = fechaParseable ? formatDate(payment.paidAt) : null;

  return fecha ? `Comprobante del cobro ${monto} · ${fecha}` : `Comprobante del cobro ${monto}`;
}
