/**
 * Pill de "próximo inicio" para la columna "Avisos" de la lista de servicios.
 *
 * Muestra una pill ámbar (faltan N días) o roja (arranca HOY) con la fecha de inicio
 * del servicio, siempre que esté dentro de la ventana de alertas (windowDays).
 *
 * Criterios:
 *   - workflowStatus "Cancelado" → siempre "—"
 *   - Sin fecha (service.date) o sin windowDays → "—"
 *   - diff (días hasta la fecha) en [0, windowDays] → pill visible
 *   - diff < 0 (ya pasó) o diff > windowDays (muy lejos) → "—"
 *   - diff >= 1 → pill ámbar con ícono Clock + "Empieza el {dd/MM} (en N días)" (singular si N==1)
 *   - diff === 0 → pill roja sin ícono + "Empieza HOY {dd/MM}"
 *
 * NOTA: este componente NO recibe ni evalúa el Status de la RESERVA.
 * Esa es una decisión deliberada del dueño: el aviso aparece para reservas
 * en cualquier estado (presupuesto, en viaje, etc.).
 *
 * La columna se muestra cuando el flag enableServiceDeadlineAlerts está ON,
 * independientemente del flag enableCatalogFindOrCreate.
 */

import { Clock } from "lucide-react";

/**
 * Formatea una fecha ISO al formato "dd/MM".
 * Usa string-split para evitar desfases de zona horaria.
 * Exportada para tests.
 */
export function formatearFechaDdMm(fechaIso) {
    const soloFecha = (fechaIso || "").split("T")[0];
    if (!soloFecha) return "";
    const [, mes, dia] = soloFecha.split("-");
    if (!mes || !dia) return "";
    return `${dia}/${mes}`;
}

/**
 * Calcula la diferencia en días entre "hoy" y la fecha dada.
 * Usa Date.UTC para que la comparación sea date-only sin zona horaria.
 * Devuelve null si la fecha es inválida.
 *
 * diff < 0  → fecha ya pasó
 * diff === 0 → es hoy
 * diff > 0  → faltan N días
 *
 * Exportada para tests.
 */
export function calcularDiffDias(fechaIso) {
    const soloFecha = (fechaIso || "").split("T")[0];
    if (!soloFecha) return null;

    const partes = soloFecha.split("-");
    if (partes.length !== 3) return null;

    const [anio, mes, dia] = partes;
    if (!anio || !mes || !dia) return null;

    // Guardamos contra cadenas no numéricas ("no-es-fecha" → NaN)
    const anioNum = Number(anio);
    const mesNum = Number(mes);
    const diaNum = Number(dia);
    if (isNaN(anioNum) || isNaN(mesNum) || isNaN(diaNum)) return null;

    // Date.UTC evita conversiones de zona horaria: compara date-only
    const fechaMs = Date.UTC(anioNum, mesNum - 1, diaNum);
    const hoy = new Date();
    const hoyMs = Date.UTC(hoy.getFullYear(), hoy.getMonth(), hoy.getDate());

    const diffMs = fechaMs - hoyMs;
    return Math.round(diffMs / (1000 * 60 * 60 * 24));
}

/**
 * Devuelve true si el servicio está dentro de la ventana de alertas y tiene pill visible.
 *
 * Exportada para que ServiceList.jsx pueda decidir si renderizar el div de pills en mobile
 * sin instanciar el componente completo. Centraliza la lógica de "hay pill" en un solo lugar.
 *
 * Replica exactamente las condiciones de render de UpcomingStartPill (sin el caso "Cancelado",
 * que se chequea por fuera en ServiceList).
 */
export function estaEnVentana(fechaIso, windowDays) {
    if (windowDays == null) return false;
    if (!fechaIso) return false;
    const diff = calcularDiffDias(fechaIso);
    if (diff === null) return false;
    // Dentro de ventana: hoy (0) o hasta windowDays días en el futuro
    return diff >= 0 && diff <= windowDays;
}

// Clases de la pill ámbar (vigente, faltan días) — mismo patrón que DeadlinePill.jsx
const CLASES_VIGENTE = "inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-semibold bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400";
// Clases de la pill roja (arranca hoy)
const CLASES_HOY = "inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-semibold bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-400";
const CLASES_SIN_FECHA = "text-slate-300 dark:text-slate-600";

/**
 * Props:
 *   service       — servicio normalizado; usamos service.date como fecha de inicio
 *   windowDays    — int, días de ventana configurados en el backend (upcomingStartsWindowDays)
 *   mostrarGuion  — si true, cuando no hay pill muestra "—" (desktop); si false, devuelve null (mobile)
 */
export function UpcomingStartPill({ service, windowDays, mostrarGuion = true }) {
    // Servicios cancelados nunca muestran pill
    if (service.workflowStatus === "Cancelado") {
        if (mostrarGuion) return <span className={CLASES_SIN_FECHA}>—</span>;
        return null;
    }

    // Sin windowDays (flag OFF o respuesta vacía del server) → sin pill
    if (windowDays == null) {
        if (mostrarGuion) return <span className={CLASES_SIN_FECHA}>—</span>;
        return null;
    }

    // Usamos service.date como fecha de inicio del servicio
    const fechaIso = service.date || null;
    if (!fechaIso) {
        if (mostrarGuion) return <span className={CLASES_SIN_FECHA}>—</span>;
        return null;
    }

    const diff = calcularDiffDias(fechaIso);
    if (diff === null) {
        if (mostrarGuion) return <span className={CLASES_SIN_FECHA}>—</span>;
        return null;
    }

    // Fuera de la ventana: antes del inicio (diff < 0) o muy lejos (diff > windowDays)
    // Regla: NO existe estado "vencido" — si ya pasó, tampoco se muestra pill.
    if (diff < 0 || diff > windowDays) {
        if (mostrarGuion) return <span className={CLASES_SIN_FECHA}>—</span>;
        return null;
    }

    const fechaFormateada = formatearFechaDdMm(fechaIso);

    if (diff === 0) {
        // Arranca HOY: pill roja, sin ícono
        return (
            <span
                className={CLASES_HOY}
                data-testid="pill-upcoming-start"
                data-today="true"
            >
                Empieza HOY {fechaFormateada}
            </span>
        );
    }

    // Faltan N días: pill ámbar, con ícono Clock
    // Singular: "en 1 día", plural: "en N días"
    const diasTexto = diff === 1 ? "en 1 día" : `en ${diff} días`;

    return (
        <span
            className={CLASES_VIGENTE}
            data-testid="pill-upcoming-start"
            data-today="false"
        >
            <Clock className="w-3 h-3" />
            Empieza el {fechaFormateada} ({diasTexto})
        </span>
    );
}
