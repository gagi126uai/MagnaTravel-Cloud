/**
 * Lógica pura para el envío de voucher por WhatsApp desde la solapa Vouchers.
 *
 * Las funciones de este archivo NO tienen efectos secundarios ni llaman al API:
 * solo calculan a partir de los datos que ya están en la UI.
 *
 * Se separan del componente para poder testearse fácilmente con node:test.
 */

/**
 * Determina si el botón "Enviar al pasajero" debe mostrarse para un voucher dado.
 *
 * Reglas:
 *  - El voucher debe estar en estado Issued o UploadedExternal (ya tiene contenido real).
 *  - El voucher debe tener canSend=true (el backend indica que está listo para enviar).
 *  - No se muestra en soloLectura: "enviar" es una acción que inicia comunicación;
 *    aunque no modifica el voucher en sí, se preserva la coherencia del estado de lectura.
 *  - No se muestra si el voucher está en Revoked (la solapa ya filtra esto, pero
 *    la función es defensiva).
 *
 * @param {object} voucher - DTO normalizado del voucher.
 * @param {boolean} soloLectura - Si la pestaña está en modo solo lectura (estado congelado).
 * @returns {boolean}
 */
export function puedeEnviarVoucher(voucher, soloLectura) {
  if (soloLectura) return false;
  if (voucher.status === "Revoked") return false;

  // Solo los estados que tienen un documento real adjunto permiten envío.
  const estadoEnviable = voucher.status === "Issued" || voucher.status === "UploadedExternal";
  if (!estadoEnviable) return false;

  // canSend es la señal del backend: tiene en cuenta si el PDF existe, si el
  // voucher fue aprobado, etc. Respetamos esa decisión sin reimplementarla.
  return Boolean(voucher.canSend);
}

/**
 * Resuelve el destinatario por defecto para enviar el voucher.
 *
 * Estrategia (alineada con cómo MessagesPage resuelve recipients):
 *  - Si el voucher tiene exactamente un pasajero con publicId conocido → ese pasajero.
 *  - Si el voucher tiene varios pasajeros → devuelve null (el componente muestra
 *    un mini-selector para que el operador elija).
 *  - Si no tiene pasajeros (scope ReservaCompleta) → el titular (customer) de la reserva.
 *  - Si no hay customerPublicId en la reserva → null (no se puede enviar sin destinatario).
 *
 * @param {object} voucher - DTO normalizado del voucher (con passengerPublicIds[]).
 * @param {object} reserva - DTO de la reserva (con customerPublicId, customerName).
 * @param {Array} passengers - Lista de pasajeros de la reserva (para resolver nombre).
 * @returns {{ personType: 'passenger'|'customer', personId: string, displayName: string }|null}
 */
export function resolverDestinatarioPorDefecto(voucher, reserva, passengers) {
  const passengerIds = voucher?.passengerPublicIds ?? [];

  if (passengerIds.length === 1) {
    // Caso frecuente: voucher de pasajero individual.
    const passengerId = passengerIds[0];

    // Buscamos el nombre del pasajero en la lista de la reserva para mostrarlo en el toast.
    const passengerEncontrado = (passengers ?? []).find(
      (p) => (p.publicId || p.PublicId) === passengerId
    );
    const displayName =
      passengerEncontrado?.fullName ||
      passengerEncontrado?.FullName ||
      voucher.passengerNames?.[0] ||
      "Pasajero";

    return { personType: "passenger", personId: passengerId, displayName };
  }

  if (passengerIds.length > 1) {
    // Hay varios pasajeros: el componente debe pedir al operador que elija.
    // Devolvemos null como señal de "necesita selección".
    return null;
  }

  // Sin pasajeros asociados: se envía al titular (customer/payer) de la reserva.
  const customerPublicId = reserva?.customerPublicId;
  if (!customerPublicId) return null;

  const displayName =
    reserva?.customerName ||
    reserva?.client?.fullName ||
    "Cliente";

  return { personType: "customer", personId: customerPublicId, displayName };
}

/**
 * Construye los candidatos a destinatario para un voucher con varios pasajeros.
 * Se usa cuando resolverDestinatarioPorDefecto devuelve null por haber >1 pasajero.
 *
 * @param {object} voucher - DTO normalizado del voucher.
 * @param {Array} passengers - Lista de pasajeros de la reserva.
 * @returns {Array<{ personType: 'passenger', personId: string, displayName: string }>}
 */
export function resolverCandidatosDestinatario(voucher, passengers) {
  const passengerIds = voucher?.passengerPublicIds ?? [];

  return passengerIds.map((passengerId) => {
    const passengerEncontrado = (passengers ?? []).find(
      (p) => (p.publicId || p.PublicId) === passengerId
    );

    // Fallback al array de nombres si no encontramos el objeto completo del pasajero.
    const index = passengerIds.indexOf(passengerId);
    const displayName =
      passengerEncontrado?.fullName ||
      passengerEncontrado?.FullName ||
      voucher.passengerNames?.[index] ||
      `Pasajero ${index + 1}`;

    return { personType: "passenger", personId: passengerId, displayName };
  });
}
