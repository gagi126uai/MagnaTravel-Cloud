/**
 * Qué estados de Reserva se muestran con el SELLO (ver `ReservaEstadoSello` en
 * `ReservaStatusBadge.jsx`) en vez del chip de color de siempre.
 *
 * Archivo `.js` PURO (sin JSX) a propósito — misma convención que
 * `reservaStatusLabels.js` (líneas 11-14 de ese archivo): así se puede testear
 * con `node --test` sin montar React.
 *
 * Fix bloqueante de review (2026-08-11, I1 — Tanda 1 del lavado de cara): el set
 * es EXPLÍCITO y de solo tres estados — Anulada (Cancelled) / Perdida (Lost) /
 * Finalizada (Closed) — y a propósito NO usa el helper `isReservaAnulada` de
 * `moneyStatus.js`. Ese helper agrupa TAMBIÉN "PendingOperatorRefund" (Esperando
 * reembolso) dentro de "anulada" porque para la PLATA da igual (en los dos casos
 * la venta quedó sin efecto) — pero para el ESTADO de la reserva no da igual:
 * "Esperando reembolso" es una reserva VIVA, con una multa del operador todavía
 * sin resolver. Sellarla como si ya no fuera a ningún lado (igual que una
 * Anulada de verdad) le mentiría al vendedor justo en el estado que más
 * necesita seguimiento.
 */
const ESTADOS_CON_SELLO = new Set(['Cancelled', 'Lost', 'Closed']);

/**
 * True si esta reserva tiene que mostrarse con el sello en vez del chip normal.
 * Acepta la reserva completa (no solo el status) para no romper la firma si en
 * el futuro hace falta mirar otro campo — hoy solo usa `reserva.status`.
 *
 * @param {{ status?: string } | null | undefined} reserva
 * @returns {boolean}
 */
export function debeMostrarComoSello(reserva) {
  if (!reserva) return false;
  return ESTADOS_CON_SELLO.has(reserva.status);
}
