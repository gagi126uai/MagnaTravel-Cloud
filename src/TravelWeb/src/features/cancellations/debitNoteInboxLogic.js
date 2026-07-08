/**
 * Lógica PURA de la bandeja pasiva "Cargos de cancelación pendientes" (Parte D de la
 * spec "el paso de multa vive en la ficha", 2026-07-08).
 *
 * Desde esta tanda la bandeja YA NO tiene un botón de acción por fila: cada fila es
 * un link a la ficha de la reserva, que es donde ahora vive todo el paso de la multa
 * (ver operatorPenaltyBanner.js). Esta bandeja solo AVISA qué reservas necesitan
 * atención y qué les falta, en criollo — nunca el status técnico crudo del backend
 * (regla del gate de exposición de datos: un enum interno no es algo que un vendedor
 * tenga que leer).
 */

// Traduce el `debitNoteStatus` de cada fila a un texto en criollo de "qué falta".
// Tabla explícita (no un switch gigante) para que agregar un estado nuevo sea
// cambiar una sola línea acá, no tocar el JSX de la página.
// Regla de voz (2026-07-08): el término fiscal ("nota de débito") vive SOLO en las
// pantallas de facturación. Acá, como en cualquier aviso, se habla de "la multa" o
// "el cargo" — nunca de comprobantes ni de AFIP/ARCA.
const QUE_FALTA_POR_ESTADO = {
  EstimatedPendingConfirmation: "Falta confirmar el monto de la multa",
  ConfirmedWithoutDebitNote: "Falta cobrarle la multa al cliente",
  Pending: "El cobro de la multa está en proceso",
  Failed: "El cobro de la multa no salió — hay que reintentar",
  ManualReview: "Falta corregir el monto y la moneda",
};

/**
 * Texto en criollo de "qué falta" para una fila de la bandeja.
 *
 * Si el backend manda un `debitNoteStatus` que todavía no contemplamos acá (por
 * ejemplo, una versión más nueva de la API), devolvemos un texto genérico — NUNCA
 * el string técnico crudo directo al usuario.
 *
 * @param {string} debitNoteStatus
 * @returns {string}
 */
export function textoQueFalta(debitNoteStatus) {
  return QUE_FALTA_POR_ESTADO[debitNoteStatus] || "Hay que revisar esta cancelación";
}

const UN_MINUTO_EN_MS = 60 * 1000;
const UNA_HORA_EN_MS = 60 * UN_MINUTO_EN_MS;
const UN_DIA_EN_MS = 24 * UNA_HORA_EN_MS;

/**
 * Texto relativo simple ("hace 3 días", "hace 2 horas", "recién") a partir de una
 * fecha. No usamos una librería de fechas para esto: es un texto chico de la fila y
 * la regla es simple (redondeamos hacia abajo, siempre en el pasado).
 *
 * @param {string|Date|null|undefined} fecha
 * @param {Date} ahora - inyectable para poder testear sin depender del reloj real.
 * @returns {string}
 */
export function textoTiempoRelativo(fecha, ahora = new Date()) {
  if (!fecha) return "—";

  const fechaDate = fecha instanceof Date ? fecha : new Date(fecha);
  const diffMs = ahora.getTime() - fechaDate.getTime();
  if (Number.isNaN(diffMs)) return "—";

  if (diffMs < UN_MINUTO_EN_MS) return "recién";

  if (diffMs < UNA_HORA_EN_MS) {
    const minutos = Math.floor(diffMs / UN_MINUTO_EN_MS);
    return minutos === 1 ? "hace 1 minuto" : `hace ${minutos} minutos`;
  }

  if (diffMs < UN_DIA_EN_MS) {
    const horas = Math.floor(diffMs / UNA_HORA_EN_MS);
    return horas === 1 ? "hace 1 hora" : `hace ${horas} horas`;
  }

  const dias = Math.floor(diffMs / UN_DIA_EN_MS);
  return dias === 1 ? "hace 1 día" : `hace ${dias} días`;
}
