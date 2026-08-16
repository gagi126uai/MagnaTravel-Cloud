/**
 * Deriva el texto "destino · N pasajeros" que se muestra en el encabezado de la
 * FICHA de una reserva (Tanda 2 del rediseño de Reservas, 2026-08-03, regla P7
 * firmada por el dueño). Reemplaza al nombre autogenerado tipo "File F-2026-…"
 * que se mostraba antes debajo del cliente.
 *
 * Es un archivo .js puro (sin JSX) a propósito, mismo criterio que avisosFicha.js
 * y reservaMoneyDisplay.js: así se puede testear con `node --test` y ReservaHeader.jsx
 * solo se ocupa de pintar el texto que este módulo ya calculó.
 *
 * MISMO CRITERIO que usa el listado de Reservas (Tanda 1, ver
 * ReservaService.FillDestinoForListAsync en el backend): ciudades REALES cargadas
 * en los servicios de la reserva, nunca inventadas. La diferencia es que acá no hay
 * un viaje al backend — se lee directo de los servicios que YA están cargados en el
 * detalle (reserva.flightSegments / hotelBookings / packageBookings), así que esta
 * es una aproximación del mismo criterio, no una copia exacta:
 *   - Vuelo: ciudad de llegada (destinationCity).
 *   - Hotel: ciudad (city).
 *   - Paquete: destino (destination).
 *   - Traslados y Asistencia/Seguro NO aportan destino (mismo motivo que el backend:
 *     un traslado dentro del mismo destino no agrega nada, y la zona de cobertura de
 *     un seguro no es una ciudad).
 *   - Servicios genéricos del tarifario (Asistencia/Excursión/Otro con tarifa propia)
 *     tampoco se leen acá: el detalle de la reserva no trae la ciudad/destino de la
 *     tarifa vinculada, solo el backend la tiene disponible. Limitación conocida y
 *     aceptada: en el peor caso, una reserva que SOLO tenga esos servicios genéricos
 *     con destino cargado en su tarifa se ve sin destino en la ficha (cae al fallback
 *     "solo pasajeros"), aunque el listado sí lo muestre.
 * Los servicios anulados no aportan destino (no tiene sentido mostrar la ciudad de
 * un vuelo que se dejó sin efecto).
 */

const ESTADO_SERVICIO_CANCELADO = "Cancelado";

/** Devuelve true si el servicio está anulado — no aporta destino. */
function estaAnulado(servicio) {
  const estado = servicio?.workflowStatus || servicio?.status;
  return estado === ESTADO_SERVICIO_CANCELADO;
}

/**
 * Lista de destinos (ciudades) reales cargados en los servicios de la reserva,
 * sin repetidos (comparación sin distinguir mayúsculas/minúsculas), en el orden
 * en que aparecen los servicios.
 *
 * @param {object} reserva - DTO de detalle de la reserva.
 * @returns {string[]}
 */
export function listarDestinosDeServiciosCargados(reserva) {
  if (!reserva) return [];

  const candidatos = [
    ...(reserva.flightSegments || [])
      .filter((s) => !estaAnulado(s))
      .map((s) => s.destinationCity),
    ...(reserva.hotelBookings || [])
      .filter((s) => !estaAnulado(s))
      .map((s) => s.city),
    ...(reserva.packageBookings || [])
      .filter((s) => !estaAnulado(s))
      .map((s) => s.destination),
  ];

  const destinosSinRepetir = [];
  const yaVistos = new Set();
  for (const candidato of candidatos) {
    const destino = (candidato || "").trim();
    if (!destino) continue;
    const clave = destino.toLowerCase();
    if (yaVistos.has(clave)) continue;
    yaVistos.add(clave);
    destinosSinRepetir.push(destino);
  }
  return destinosSinRepetir;
}

/**
 * Total de pasajeros DECLARADO por el vendedor (ADR-031: cantidad de adultos/
 * menores/infantes que se carga ANTES de tener el nombre de cada uno). Es
 * distinto de `reserva.passengers.length`, que es cuántos YA tienen nombre
 * cargado — ver PassengerList.jsx, que lee estos mismos tres campos del DTO.
 */
function totalPasajerosDeclarados(reserva) {
  const adultCount = reserva?.adultCount ?? 0;
  const childCount = reserva?.childCount ?? 0;
  const infantCount = reserva?.infantCount ?? 0;
  return adultCount + childCount + infantCount;
}

/**
 * Arma la línea "destino · N pasajeros" del encabezado de la ficha.
 * Si no hay ningún destino cargado, cae a "solo pasajeros" (nunca inventa una
 * ciudad — regla P7: "ciudades reales, sin inventar").
 *
 * Tanda A UX (2026-08-16): el "N" ahora prioriza lo DECLARADO (adultCount +
 * childCount + infantCount, ADR-031) por sobre la cantidad de pasajeros YA
 * cargados con nombre — así el encabezado muestra el compromiso real de la
 * venta, no solo lo que se alcanzó a tipear. Si no hay nada declarado (reserva
 * vieja, de antes de ADR-031), se cae al conteo de pasajeros cargados, como
 * siempre, para no mostrar "0 pasajeros" mintiendo.
 *
 * @param {object} reserva - DTO de detalle de la reserva.
 * @returns {string}
 */
export function armarLineaDestinoYPasajeros(reserva) {
  const destinos = listarDestinosDeServiciosCargados(reserva);
  const cantidadCargados = reserva?.passengers?.length ?? 0;
  const declarado = totalPasajerosDeclarados(reserva);
  const cantidadPasajeros = declarado > 0 ? declarado : cantidadCargados;
  const textoPasajeros = cantidadPasajeros === 1 ? "1 pasajero" : `${cantidadPasajeros} pasajeros`;

  if (destinos.length === 0) return textoPasajeros;
  return `${destinos.join(" · ")} · ${textoPasajeros}`;
}

/**
 * Aviso discreto cuando lo DECLARADO (ADR-031) todavía no coincide con los
 * pasajeros que YA tienen nombre cargado — ej. se cargó "somos 4" pero solo se
 * tipeó el nombre del titular. Devuelve `null` cuando no aplica (nada
 * declarado, o ya está todo cargado) — el llamador no muestra nada en ese caso.
 *
 * @param {object} reserva - DTO de detalle de la reserva.
 * @returns {string|null}
 */
export function armarAvisoPasajerosFaltantes(reserva) {
  const declarado = totalPasajerosDeclarados(reserva);
  const cargados = reserva?.passengers?.length ?? 0;
  if (declarado > 0 && cargados < declarado) {
    const faltan = declarado - cargados;
    return faltan === 1 ? "Falta cargar 1 pasajero" : `Faltan cargar ${faltan} pasajeros`;
  }
  return null;
}
