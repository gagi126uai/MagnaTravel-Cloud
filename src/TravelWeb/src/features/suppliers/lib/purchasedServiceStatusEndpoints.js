/**
 * Constantes del editor de estado de "Servicios comprados" (cuenta del operador).
 * Extraídas de `SupplierAccountPage.jsx` (Tanda T5, 2026-08-18) para que
 * `ServiceStatusEditor` y `ServiceConfirmationEditor` (que sigue viviendo en la
 * página) puedan usar el MISMO mapeo sin duplicarlo.
 */

// Mapeo de Type (en espanol, viene del backend) -> endpoint de status update.
// Si no esta mapeado (servicios genericos), no se permite editar inline aca.
export const STATUS_ENDPOINT_BY_TYPE = {
    "Hotel": "hotel-bookings",
    "Vuelo": "flight-segments",
    "Traslado": "transfer-bookings",
    "Paquete": "package-bookings",
    "Asistencia": "assistance-bookings",
};

export const STATUS_OPTIONS = ["Solicitado", "Confirmado", "Cancelado"];
