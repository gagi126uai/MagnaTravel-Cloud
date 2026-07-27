/**
 * Lógica pura de los botones "Marcar confirmado" / "Marcar emitido" / "No requiere
 * confirmación" que resuelven un servicio pendiente DESDE LA FICHA de la reserva
 * (fix #34, Tanda 3, 2026-07-24). Spec completa: docs/ux/guia-ux-gaston.md, sección
 * "Confirmar un servicio DESDE LA FICHA de la reserva (2026-07-24, respuestas de
 * Gastón P1..P4)".
 *
 * Separada del JSX (ResolverServicioInline.jsx) para poder testear con node:test,
 * sin montar React — mismo criterio que el resto de los archivos de lib/.
 *
 * P4=A (unificación): esta MISMA lógica la usan tanto ServiceList.jsx (ficha de la
 * reserva) como SupplierAccountPage.jsx ("Servicios comprados" de la cuenta del
 * operador) — un solo lenguaje para avanzar en toda la app.
 */

import { SERVICE_RECORD_KIND } from "./reservationServiceModel.js";

// Mapeo recordKind -> segmento de URL de los endpoints PATCH .../status que ya existen
// en el backend (hotel-bookings, package-bookings, assistance-bookings, transfer-bookings).
// El aéreo NO está acá a propósito: "Marcar emitido" usa mark-issued (ver más abajo),
// no el PATCH genérico — el aéreo se resuelve por TicketIssuedAt, no por Status.
const ENDPOINT_STATUS_POR_RECORD_KIND = Object.freeze({
    hotel: "hotel-bookings",
    package: "package-bookings",
    assistance: "assistance-bookings",
    transfer: "transfer-bookings",
});

/**
 * Devuelve los botones que corresponde mostrar en la fila de un servicio PENDIENTE
 * (todavía no resuelto), según su tipo (P3=A de la spec, texto único por tipo).
 *
 * - Hotel / Paquete / Asistencia / Traslado (rama "confirmado por el operador"):
 *   "Marcar confirmado", con casillero para el N° de confirmación.
 * - Aéreo: "Marcar emitido", con casillero para el N° de ticket.
 * - Traslado además siempre suma "No requiere confirmación" (traslado "mudo", que se
 *   destraba a mano): único click, SIN casillero — no hay número de operador que cargar.
 * - Genérico: sin botón (nunca tuvo flujo de confirmación con operador).
 *
 * @param {string} recordKind - "flight"|"hotel"|"transfer"|"assistance"|"package"|"generic"
 * @returns {Array<{ tipo: string, etiqueta: string, necesitaCasillero: boolean }>}
 */
export function resolverAccionesParaServicioPendiente(recordKind) {
    if (recordKind === "flight") {
        return [{ tipo: "mark-issued", etiqueta: "Marcar emitido", necesitaCasillero: true }];
    }
    if (recordKind === "transfer") {
        return [
            { tipo: "confirm-status", etiqueta: "Marcar confirmado", necesitaCasillero: true },
            { tipo: "no-confirmation", etiqueta: "No requiere confirmación", necesitaCasillero: false },
        ];
    }
    if (recordKind === "hotel" || recordKind === "package" || recordKind === "assistance") {
        return [{ tipo: "confirm-status", etiqueta: "Marcar confirmado", necesitaCasillero: true }];
    }
    // "generic" u otro tipo desconocido: nunca tuvo confirmación de operador desde acá.
    return [];
}

/**
 * Arma el pedido HTTP (método + URL + body) para resolver un servicio hacia adelante.
 * El componente solo ejecuta esto — la decisión de QUÉ endpoint/payload usar vive acá,
 * separada y testeada aparte.
 *
 * Los tres endpoints:
 *   - "mark-issued" (aéreo): POST /reservas/{reservaId}/flights/{id}/mark-issued
 *     { ticketNumber } — el ÚNICO camino que estampa TicketIssuedAt (lo que resuelve
 *     el aéreo). El PATCH genérico .../status NO alcanza para esto.
 *   - "no-confirmation" (traslado mudo): POST /reservas/{reservaId}/transfers/{id}/no-confirmation
 *     — sin body, un solo click.
 *   - "confirm-status" (hotel/paquete/asistencia/traslado confirmado): PATCH
 *     /{tipo}-bookings/{id}/status { status: "Confirmado", confirmationNumber } — mismo
 *     endpoint absoluto que ya usa la cuenta del operador (P4=A, unificación).
 *
 * @param {{ tipo: string, recordKind: string, reservaId: string, servicePublicId: string, numero?: string|null }} params
 * @returns {{ method: "post"|"patch", url: string, body: object|undefined }|null} null si el tipo no se reconoce
 */
export function construirRequestResolverServicio({ tipo, recordKind, reservaId, servicePublicId, numero }) {
    // Un casillero vacío significa "sin número" — el backend acepta null (P2=B: es opcional).
    const numeroLimpio = (numero || "").trim() || null;

    if (tipo === "mark-issued") {
        return {
            method: "post",
            url: `/reservas/${reservaId}/flights/${servicePublicId}/mark-issued`,
            body: { ticketNumber: numeroLimpio },
        };
    }

    if (tipo === "no-confirmation") {
        return {
            method: "post",
            url: `/reservas/${reservaId}/transfers/${servicePublicId}/no-confirmation`,
            body: undefined,
        };
    }

    if (tipo === "confirm-status") {
        const endpoint = ENDPOINT_STATUS_POR_RECORD_KIND[recordKind];
        if (!endpoint) return null; // recordKind sin endpoint de status conocido
        return {
            method: "patch",
            url: `/${endpoint}/${servicePublicId}/status`,
            body: { status: "Confirmado", confirmationNumber: numeroLimpio },
        };
    }

    return null;
}

/**
 * Mensaje de éxito por tipo de acción (toast que se muestra al confirmar).
 * Separado para no repetir el texto en cada callsite y para poder testearlo.
 *
 * @param {string} tipo
 * @returns {string}
 */
export function resolverMensajeExito(tipo) {
    if (tipo === "mark-issued") return "Vuelo marcado como emitido.";
    if (tipo === "no-confirmation") return "Traslado marcado como que no requiere confirmación.";
    if (tipo === "confirm-status") return "Servicio confirmado.";
    return "Listo.";
}

// Umbral de longitud para decidir si un rechazo del motor va al Cartel emergente único
// (spec docs/ux/2026-07-22-tratamiento-unico-avisos-bloqueo.md: "rechazo de negocio largo"
// vs "error corto de un campo"). 80 caracteres es el mismo umbral que ya usa AuditPage.jsx
// para truncar texto largo — lo reusamos acá como línea divisoria entre "cabe en la fila" y
// "necesita ventana".
const UMBRAL_MENSAJE_LARGO = 80;

/**
 * True si el mensaje de rechazo del motor es lo bastante largo como para merecer el
 * Cartel emergente único en vez de quedar como texto chico pegado al casillero.
 *
 * @param {string|null|undefined} mensaje
 * @returns {boolean}
 */
export function debeMostrarCartelEmergente(mensaje) {
    return Boolean(mensaje) && mensaje.length > UMBRAL_MENSAJE_LARGO;
}

// La cuenta del operador (SupplierAccountPage) recibe el tipo de servicio en ESPAÑOL
// (Type del backend: "Hotel", "Vuelo", "Traslado", "Paquete", "Asistencia") — distinto
// del recordKind en inglés que usa la ficha de la reserva. P4=A (unificación, 2026-07-24):
// para reusar el MISMO ResolverServicioInline en las dos pantallas hace falta este mapeo.
const RECORD_KIND_POR_TIPO_ESPANOL = Object.freeze({
    Hotel: "hotel",
    Vuelo: "flight",
    Aereo: "flight",
    Traslado: "transfer",
    Paquete: "package",
    Asistencia: "assistance",
});

/**
 * Convierte el `Type` en español que manda la cuenta del operador al `recordKind` en
 * inglés que usa ResolverServicioInline / la ficha de la reserva. Tipo desconocido (o
 * un servicio genérico sin tipo específico) cae en "generic" — que no ofrece ningún
 * botón (ver resolverAccionesParaServicioPendiente).
 *
 * @param {string|null|undefined} tipoEspanol
 * @returns {string}
 */
export function mapearTipoEspanolARecordKind(tipoEspanol) {
    return RECORD_KIND_POR_TIPO_ESPANOL[tipoEspanol] || "generic";
}

/**
 * Elegibilidad del botón primario ("Marcar confirmado"/"Marcar emitido") en la fila de
 * "Servicios comprados" de la cuenta del operador (P4=A, EstadoServicioCell en
 * SupplierAccountPage.jsx). Si esto da `false`, la celda muestra el desplegable de
 * siempre (ServiceStatusEditor) en vez del botón nuevo.
 *
 * Condiciones (TODAS tienen que cumplirse):
 *   - El usuario tiene permiso de editar (mismo gate que ya protege el desplegable).
 *   - El servicio está "Solicitado" (pendiente) — si ya está Confirmado/Cancelado no hay
 *     nada que "avanzar".
 *   - El tipo tiene un recordKind conocido (no "generic": esos nunca tuvieron flujo de
 *     confirmación con operador).
 *   - El servicio tiene una reserva asociada (reservaPublicId) — los endpoints
 *     "mark-issued"/"no-confirmation" son reserva-scoped.
 *
 * @param {{ canEdit: boolean, status: string, recordKind: string, reservaPublicId: string|null|undefined }} params
 * @returns {boolean}
 */
export function debeMostrarBotonPrimarioEnCuentaOperador({ canEdit, status, recordKind, reservaPublicId }) {
    return Boolean(canEdit) && status === "Solicitado" && recordKind !== "generic" && Boolean(reservaPublicId);
}

/**
 * H19 (barrido E2E 2026-07-25, decisión firmada 9): el aviso "Para: Todos — cargá los
 * nombres para elegir" del control de asignación (ControlAsignacionServicio) antes salía
 * en la fila de CUALQUIER servicio sin pasajeros con nombre todavía, incluidos hotel,
 * paquete y asistencia — ruido en filas donde elegir un pasajero puntual (asiento de avión,
 * lugar en el traslado) no es una necesidad real hoy. Se restringe a los dos tipos donde sí
 * lo es: aéreo y traslado. En el resto, sin nombres cargados, el control simplemente no se
 * muestra todavía (una vez que hay nombres, el control aparece igual para todos los tipos).
 *
 * @param {string} recordKind - "flight"|"hotel"|"transfer"|"assistance"|"package"|"generic"
 * @returns {boolean}
 */
export function debeMostrarAvisoSinNombresParaElegir(recordKind) {
    return recordKind === SERVICE_RECORD_KIND.FLIGHT || recordKind === SERVICE_RECORD_KIND.TRANSFER;
}
