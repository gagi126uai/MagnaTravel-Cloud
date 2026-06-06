/**
 * Pill de fecha límite para la columna "Avisos" de la lista de servicios.
 *
 * Muestra una pill ámbar (vigente) o roja (vencida) con la fecha límite relevante
 * según el tipo de servicio:
 *   - Hotel / Paquete: operatorPaymentDeadline ("Señar antes del …")
 *   - Aéreo: ticketingDeadline ("Emitir antes del …")
 *   - Traslado / Asistencia / Genérico: nunca se muestra pill (devuelve "—")
 *
 * Solo se renderiza cuando el flag EnableCatalogFindOrCreate está ON.
 * Con flag OFF, el componente nunca se usa (el padre no lo llama).
 *
 * Condición de vencimiento: el día de la fecha deadline INCLUSIVE ya es vencida.
 * La comparación usa date-only local (sin zona horaria), lo que es correcto
 * para fechas administrativas de agencia.
 */

import { Clock } from "lucide-react";
import { SERVICE_RECORD_KIND } from "../lib/reservationServiceModel";

/**
 * Formatea una fecha ISO al formato "dd/MM" en español rioplatense.
 * Ejemplo: "2025-11-30" → "30/11"
 */
function formatearFechaDdMm(fechaIso) {
    // Tomamos solo la parte de fecha (YYYY-MM-DD) para evitar desfases por zona horaria.
    // Si usáramos new Date(fechaIso).toLocaleDateString(...), en ciertos timezones
    // una fecha "2025-11-30" podría mostrarse como "29/11".
    const solofecha = (fechaIso || "").split("T")[0];
    if (!solofecha) return "";
    const [anio, mes, dia] = solofecha.split("-");
    if (!anio || !mes || !dia) return "";
    return `${dia}/${mes}`;
}

/**
 * Devuelve true si la fecha deadline ya venció (hoy >= deadline, ambas date-only).
 * El día de la fecha inclusive ya se considera vencido.
 *
 * AJUSTABLE: si el criterio cambia (ej. "vence al día siguiente"), cambiar solo esta función.
 */
function estaVencida(fechaIso) {
    const solofecha = (fechaIso || "").split("T")[0];
    if (!solofecha) return false;

    // Comparamos como strings "YYYY-MM-DD" — funciona porque el formato es lexicográficamente ordenable.
    // "hoy" se construye con la fecha local del cliente (sin conversión UTC).
    const hoy = new Date();
    const hoyStr = [
        hoy.getFullYear(),
        String(hoy.getMonth() + 1).padStart(2, "0"),
        String(hoy.getDate()).padStart(2, "0"),
    ].join("-");

    // Vencida: hoy es igual o posterior al deadline
    return hoyStr >= solofecha;
}

/**
 * Extrae la fecha límite relevante del servicio según su tipo.
 * Devuelve null si el tipo no tiene deadline para mostrar.
 */
function obtenerDeadlineDelServicio(service) {
    if (service.recordKind === SERVICE_RECORD_KIND.HOTEL || service.recordKind === SERVICE_RECORD_KIND.PACKAGE) {
        return service.operatorPaymentDeadline || null;
    }
    if (service.recordKind === SERVICE_RECORD_KIND.FLIGHT) {
        return service.ticketingDeadline || null;
    }
    // Traslado, Asistencia, Genérico: sin deadline para mostrar en esta columna
    return null;
}

/**
 * Texto del label de la pill según tipo de servicio y si venció.
 */
function obtenerTextoPill(service, fechaFormateada, vencida) {
    if (service.recordKind === SERVICE_RECORD_KIND.FLIGHT) {
        return vencida
            ? `Venció emitir el ${fechaFormateada}`
            : `Emitir antes del ${fechaFormateada}`;
    }
    // Hotel y Paquete
    return vencida
        ? `Venció señar el ${fechaFormateada}`
        : `Señar antes del ${fechaFormateada}`;
}

const CLASES_VIGENTE = "inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-semibold bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400";
const CLASES_VENCIDA = "inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-semibold bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-400";
const CLASES_SIN_FECHA = "text-slate-300 dark:text-slate-600";

/**
 * Props:
 *   service      — servicio normalizado con recordKind y los campos de deadline
 *   mostrarGuion — si true, cuando no hay pill muestra "—" (para desktop);
 *                  si false, devuelve null (para mobile, donde el "—" no aplica)
 */
export function DeadlinePill({ service, mostrarGuion = true }) {
    // Servicios cancelados no muestran pill (regla de negocio: sin deadline en cancelados)
    const estaCancelado = service.workflowStatus === "Cancelado";

    const fechaIso = obtenerDeadlineDelServicio(service);

    // Sin fecha → dash o nada
    if (!fechaIso || estaCancelado) {
        if (mostrarGuion) {
            return <span className={CLASES_SIN_FECHA}>—</span>;
        }
        return null;
    }

    const fechaFormateada = formatearFechaDdMm(fechaIso);
    const vencida = estaVencida(fechaIso);
    const textoPill = obtenerTextoPill(service, fechaFormateada, vencida);

    return (
        <span
            className={vencida ? CLASES_VENCIDA : CLASES_VIGENTE}
            data-testid="pill-deadline"
        >
            {/* Ícono de reloj solo en la pill ámbar (vigente); la roja (vencida) va sin ícono */}
            {!vencida && <Clock className="w-3 h-3" />}
            {textoPill}
        </span>
    );
}

// Exportamos las funciones de lógica pura para los tests (sin DOM)
export { formatearFechaDdMm, estaVencida, obtenerDeadlineDelServicio, obtenerTextoPill };
