/**
 * Lógica pura del flujo "Usar saldo a favor del cliente".
 *
 * Funciones exportadas para poder testarlas con Node sin React.
 * El componente UsarSaldoAFavorInline las consume internamente.
 *
 * REGLA MULTIMONEDA: nunca mezclar ARS con USD.
 * Cada entry de crédito tiene su propia moneda; el retiro siempre
 * es en la moneda del entry seleccionado.
 *
 * DESTINOS DE RETIRO (enum ClientCreditWithdrawalKind del backend):
 *   0 = KeptAsCredit        → cierre sin movimiento (el saldo queda)
 *   1 = PhysicalCash        → devolución en efectivo (sujeto a tope Ley 25.345)
 *   2 = Transfer            → devolución por transferencia
 *   3 = AppliedToNewBooking → aplicar a otra reserva del mismo cliente (FC4 completo)
 *
 * El kind 3 usa un endpoint DIFERENTE: POST /customers/{id}/credit/apply
 * (no el endpoint de withdrawals). El backend drena los bolsillos en FIFO
 * y registra el pago en la reserva destino atómicamente.
 */

/**
 * Lista de destinos de retiro que se muestran al usuario.
 * El kind 3 "Aplicar a otra reserva" está disponible desde FC4:
 * el backend conecta el pago en la reserva destino correctamente.
 */
export const DESTINOS_RETIRO = [
  { kind: 2, label: "Devolver por transferencia" },
  { kind: 1, label: "Devolver en efectivo" },
  { kind: 3, label: "Aplicar a otra reserva" },
  { kind: 0, label: "Dejar como crédito (cerrar aviso)" },
];

/**
 * Valida el monto a retirar de un entry de crédito.
 *
 * @param {number|string} monto       - Monto ingresado por el usuario
 * @param {number}        saldoDisp   - Saldo disponible del entry (remainingBalance)
 * @returns {string|null}             - Mensaje de error en español, o null si es válido
 */
export function validarMontoRetiro(monto, saldoDisp) {
  const montoNum = parseFloat(monto);

  if (!monto || isNaN(montoNum) || montoNum <= 0) {
    return "El monto tiene que ser mayor a 0.";
  }

  if (montoNum > saldoDisp) {
    return `El monto no puede superar el saldo disponible (${saldoDisp}).`;
  }

  return null;
}

/**
 * Formatea un entry de crédito para mostrarlo en la lista.
 *
 * Ejemplo de salida: "Quedan $1.500,00 de $2.000,00 · ARS · origen: reserva 2024/001"
 *
 * @param {object} entry  - Objeto { remainingBalance, creditedAmount, currency, originReservaNumber }
 * @returns {string}
 */
export function formatearDescripcionEntry(entry) {
  if (!entry) return "";

  const { remainingBalance, creditedAmount, currency, originReservaNumber } = entry;

  // Símbolo visible según la moneda (no mezclar $ ARS con US$ USD)
  const simbolo = currency === "USD" ? "US$" : "$";

  const parteRemaining = `Quedan ${simbolo}${Number(remainingBalance || 0).toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  const parteDe        = `de ${simbolo}${Number(creditedAmount || 0).toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  const parteMoneda    = `· ${currency}`;
  const parteOrigen    = originReservaNumber ? `· origen: reserva ${originReservaNumber}` : "";

  return [parteRemaining, parteDe, parteMoneda, parteOrigen].filter(Boolean).join(" ");
}

/**
 * Arma el body del POST /api/client-credit-entries/{entryPublicId}/withdrawals.
 *
 * Para kind = 0 (KeptAsCredit): amount = 0, sin campos extra.
 * Para kind = 1 (PhysicalCash) o kind = 2 (Transfer): amount = monto numérico.
 * Para kind = 3: NO usar esta función — usar armarPayloadAplicacion().
 *
 * @param {number} kind    - Enum del destino (0, 1 o 2)
 * @param {number} amount  - Monto a retirar (ignorado cuando kind = 0)
 * @param {object} extras  - Campos opcionales: reference, paymentMethodOverride
 * @returns {object}       - Payload listo para el backend
 */
export function armarPayloadRetiro(kind, amount, extras = {}) {
  if (kind === 0) {
    // KeptAsCredit: no mueve plata, solo cierra el aviso
    return { kind: 0, amount: 0 };
  }

  const payload = {
    kind,
    amount: parseFloat(amount),
  };

  // Campos opcionales para transferencia (solo si el usuario los completó)
  if (extras.reference) {
    payload.reference = extras.reference;
  }
  if (extras.paymentMethodOverride) {
    payload.paymentMethodOverride = extras.paymentMethodOverride;
  }

  return payload;
}

/**
 * Valida los datos del flujo "Aplicar a otra reserva" (kind 3) ANTES de hacer el POST.
 *
 * Esta función es para kind 3 únicamente — para kinds 0/1/2 usar validarMontoRetiro().
 *
 * @param {number|string} monto               - Monto ingresado por el usuario
 * @param {number}        saldoDisponible      - Saldo disponible en la moneda (de ClientCreditOverviewDto)
 * @param {string|null}   targetReservaPublicId - PublicId de la reserva destino elegida
 * @returns {string|null}                      - Mensaje de error, o null si todo está bien
 */
export function validarAplicacion(monto, saldoDisponible, targetReservaPublicId) {
  // Primero validar que haya reserva destino elegida
  if (!targetReservaPublicId) {
    return "Elegí una reserva destino antes de confirmar.";
  }

  const montoNum = parseFloat(monto);
  if (!monto || isNaN(montoNum) || montoNum <= 0) {
    return "El monto tiene que ser mayor a 0.";
  }

  if (montoNum > saldoDisponible) {
    return `El monto no puede superar el saldo disponible (${saldoDisponible}).`;
  }

  return null;
}

/**
 * Arma el payload para POST /api/customers/{publicId}/credit/apply.
 *
 * Este endpoint es DIFERENTE al de withdrawals: drena bolsillos en FIFO
 * y registra el pago en la reserva destino automáticamente.
 *
 * Nota: el backend recibe Currency como string y TargetReservaPublicId como Guid.
 *
 * @param {string} currency              - "ARS" o "USD"
 * @param {number|string} amount         - Monto a aplicar
 * @param {string} targetReservaPublicId - PublicId de la reserva destino
 * @returns {object}                     - Payload listo para enviar como JSON
 */
export function armarPayloadAplicacion(currency, amount, targetReservaPublicId) {
  return {
    currency,
    amount: parseFloat(amount),
    targetReservaPublicId,
  };
}
