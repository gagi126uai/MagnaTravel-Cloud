import React from 'react';
import { RESERVA_STATUS_LABELS, traducirEstadoReserva } from '../lib/reservaStatusLabels';

/**
 * Mapeo canonico de estados de Reserva al label en espanol y al color del badge.
 *
 * Los labels (el texto) viven en `reservaStatusLabels.js` (un archivo `.js` puro, sin
 * JSX) — Obra 6 (2026-07-27): ese archivo es ahora la FUENTE UNICA del texto de cada
 * estado, para que los dashboards (AgentDashboard/AdminDashboard) puedan reusar
 * exactamente el mismo mapeo sin duplicarlo, y para poder testear la traduccion con
 * `node --test` (este archivo .jsx, con JSX de verdad, no se puede importar desde un
 * test plano de Node). Colores e iconos siguen viviendo aca, son solo de presentacion.
 *
 * Los keys son los strings persistidos en la BD (en ingles, alineados con EstadoReserva.cs).
 *
 * `size` (Tanda 2 rediseño de la ficha, 2026-08-03, regla P7): "sm" (por defecto, sin
 * cambios) es el tamaño chico que ya usan las 17+ pantallas existentes. "lg" es el chip
 * grande al lado del título "Reserva #F-2026-XXXX" en la ficha de detalle — mismo
 * mapeo de color/texto/candado, solo más grande para que sea lo primero que se lea.
 *
 * Ciclo nuevo (ADR-020, ciclo unico sin flags):
 *   Quotation → Budget → InManagement → Confirmed → Traveling → Closed
 *   Lost: cotizacion/presupuesto que no prospero (queda en historial)
 *   Cancelled: la reserva fue anulada con proceso fiscal
 *
 * ADR-036 (2026-06-21):
 *   - "ToSettle" (A liquidar) eliminado de la UI. No aparece en ningun badge ni contador.
 *   - "Cancelada" → ahora se muestra como "Anulada" al usuario (el termino interno del backend
 *     sigue siendo "Cancelled", pero en la UI "Anular" = deshacer el viaje).
 *
 * "Sold" (Vendida) YA NO EXISTE desde ADR-020. Si llega del backend como legacy,
 * el fallback lo muestra igual pero sin color ni icono especial.
 */
export const statusConfig = {
    // Cotizacion: primer paso del ciclo. Borrador interno del vendedor.
    // Color gris claro — indica "todavia nada", borrador.
    Quotation: {
        label: RESERVA_STATUS_LABELS.Quotation,
        color: 'bg-slate-100 text-slate-500 border-slate-200 dark:bg-slate-800/60 dark:text-slate-400 dark:border-slate-700',
        icon: '📝',
    },
    // Presupuesto: documento armado que el cliente recibe y evalua.
    // Color azul claro — sigue siendo "en curso", pero ya mas formal.
    Budget: {
        label: RESERVA_STATUS_LABELS.Budget,
        color: 'bg-blue-50 text-blue-700 border-blue-200 dark:bg-blue-900/20 dark:text-blue-300 dark:border-blue-800',
        icon: '📋',
    },
    // En gestion: el cliente acepto; se solicitan servicios a los operadores.
    // Color celeste/cian — "en movimiento", diferente del azul del presupuesto.
    InManagement: {
        label: RESERVA_STATUS_LABELS.InManagement,
        color: 'bg-cyan-50 text-cyan-700 border-cyan-200 dark:bg-cyan-900/20 dark:text-cyan-300 dark:border-cyan-800',
        icon: '⚙️',
    },
    // Confirmada: todos los servicios resueltos. Se activa AUTOMATICAMENTE.
    // Color ambar/naranja — "lista pero en espera del viaje".
    Confirmed: {
        label: RESERVA_STATUS_LABELS.Confirmed,
        color: 'bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-900/20 dark:text-amber-300 dark:border-amber-800',
        icon: '🔒',
    },
    // En viaje: el cliente esta viajando. ADR-036: solo lectura.
    Traveling: {
        label: RESERVA_STATUS_LABELS.Traveling,
        color: 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/20 dark:text-emerald-300 dark:border-emerald-800',
        icon: '✈️',
    },
    // Finalizada: reserva cerrada, ciclo completo.
    Closed: {
        label: RESERVA_STATUS_LABELS.Closed,
        color: 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700',
        icon: '✅',
    },
    // Perdida: cotización o presupuesto que el cliente no compró. Queda en historial.
    // Decisión #10 (guia UX 2026-06-08): gris oscuro + tachado visual — indica "no prospero".
    Lost: {
        label: RESERVA_STATUS_LABELS.Lost,
        color: 'bg-slate-300 text-slate-600 border-slate-400 line-through dark:bg-slate-700 dark:text-slate-400 dark:border-slate-600',
        icon: '❌',
    },
    // Anulada (estado interno: Cancelled): la reserva fue deshecha con proceso fiscal.
    // ADR-036: el termino visible para el usuario es "Anulada" (anular = deshacer el viaje).
    // "Cancelar" en este producto significa "saldar una deuda"; por eso NO se usa "Cancelada".
    Cancelled: {
        label: RESERVA_STATUS_LABELS.Cancelled,
        color: 'bg-rose-50 text-rose-700 border-rose-200 dark:bg-rose-900/20 dark:text-rose-300 dark:border-rose-800',
        icon: '🚫',
    },
    // Esperando reembolso del operador: la reserva fue anulada y hay una multa del operador
    // pendiente de confirmar. Es un estado transitorio después de Cancelled.
    // Color rosa (mismo espectro que Cancelled/Anulada) — sigue siendo una reserva anulada.
    PendingOperatorRefund: {
        label: RESERVA_STATUS_LABELS.PendingOperatorRefund,
        color: 'bg-rose-100 text-rose-800 border-rose-300 dark:bg-rose-900/30 dark:text-rose-200 dark:border-rose-700',
        icon: '⏳',
    },
    // Archivada: solo lectura, fuera del ciclo activo.
    Archived: {
        label: RESERVA_STATUS_LABELS.Archived,
        color: 'bg-slate-100 text-slate-500 border-slate-300 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700',
        icon: '📦',
    },
};

/**
 * Estados que tienen el candado activo (confirmada o posterior).
 * Cuando una reserva esta en uno de estos estados, editar cualquier dato
 * requiere autorizacion explicita (ADR-020 F4).
 *
 * ADR-036: "ToSettle" eliminado — ya no existe como estado en la UI.
 */
export const LOCKED_STATUSES = new Set(['Confirmed', 'Traveling', 'Closed']);

/** Devuelve true si el status tiene candado activo. */
export function isStatusLocked(status) {
    return LOCKED_STATUSES.has(status);
}

/**
 * Candado de EDICION de la reserva (spec UX "candado coherente", 2026-07-22, decisión C1).
 *
 * Devuelve true cuando la reserva está en un estado con candado (Confirmed/Traveling/Closed)
 * Y NO hay una autorización de edición viva (`hasLiveEditAuthorization`, el mismo campo que
 * ya usa `ReservaLockBanner`). Cuando da true, los botones de edición (Editar fechas, Anular
 * servicio, Confirmar costo, etc.) se muestran "gris + candadito" en vez de encendidos: al
 * tocarlos, en vez de ejecutar la acción, se abre la ventana de destrabar (EditAuthorizationModal).
 *
 * En la práctica solo importa para Confirmed: en Traveling/Closed la capacidad de fondo del
 * backend (canEditServices, canEditPassengers, etc.) ya viene apagada por estado terminal, así
 * que esos botones ni llegan a evaluar el candado — quedan escondidos ("no aplica"), como manda
 * la spec. No se duplica ninguna fuente de verdad: reusa isStatusLocked + hasLiveEditAuthorization,
 * que ya vienen del DTO de la reserva.
 */
export function tieneCandadoDeEdicionActivo(reserva) {
    return isStatusLocked(reserva?.status) && !(reserva?.hasLiveEditAuthorization ?? false);
}

/**
 * Estados "vivos" del ciclo de la reserva: el viaje todavia esta en curso normal
 * (se esta gestionando, ya esta confirmado, o el cliente ya esta viajando).
 * Mismo criterio que usa la campanita de avisos del backend para decidir si
 * corresponde mostrar alertas de seguimiento sobre la reserva.
 *
 * Fuera de este conjunto quedan los estados de "borrador" (Quotation/Budget) y
 * los terminales (Closed/Lost/Cancelled/PendingOperatorRefund/Archived): en esos
 * casos no tiene sentido pedirle al vendedor que "revise" o "confirme" cambios,
 * porque el viaje ya no esta en curso (o todavia ni arranco a gestionarse).
 */
export const LIVE_RESERVA_STATUSES = new Set(['InManagement', 'Confirmed', 'Traveling']);

/** Devuelve true si la reserva esta en un estado vivo (ver LIVE_RESERVA_STATUSES). */
export function isReservaEnEstadoVivo(status) {
    return LIVE_RESERVA_STATUSES.has(status);
}

/**
 * Devuelve el label en espanol para mostrar en la UI.
 *
 * Fix bloqueante del reviewer (2026-07-27): antes, si el status no existia en el mapa,
 * esta funcion devolvia el string CRUDO del backend (`status ?? ''`) — jerga tecnica en
 * ingles filtrada a un usuario no programador (mismo problema que la Obra 6 encontro en
 * los dashboards). Ahora reusa `traducirEstadoReserva` (fuente unica del mapeo, ver
 * `reservaStatusLabels.js`), que cae a "—" para cualquier status desconocido, NUNCA la
 * clave interna.
 */
export function translateStatus(status) {
    return traducirEstadoReserva(status);
}

/** Devuelve la config completa (label + color + icon) para un status, con fallback a Budget. */
export function getStatusConfig(status) {
    return statusConfig[status] ?? statusConfig.Budget;
}

/**
 * Badge de estado de la reserva.
 * El color y el label se leen del statusConfig canonico.
 *
 * `mostrarCandado` (Tanda 1 rediseño listado, 2026-08-04, plan B4): opt-in, apagado
 * por default a propósito — este badge se usa en 17+ pantallas distintas y agregar
 * el candado 🔒 a TODAS rompería la maqueta firmada de cada una. Solo el listado de
 * Reservas lo prende explícitamente. El candado únicamente tiene sentido visual en
 * "Confirmada" (es el aviso de que editar la reserva pide autorización — ver
 * isStatusLocked): en Traveling/Closed, que también están "bloqueados" para editar,
 * el ícono normal de su propio estado (✈️/✅) ya cumple ese rol.
 */
export function ReservaStatusBadge({ status, mostrarCandado = false, size = "sm" }) {
    const cfg = getStatusConfig(status);
    // Fix bloqueante del reviewer (2026-07-27): mismo motivo que translateStatus — nunca
    // mostrar la clave cruda del backend si el status no esta en el mapa.
    const label = traducirEstadoReserva(status);
    const conCandado = mostrarCandado && status === "Confirmed";
    // "lg" = mismas clases que el chip grande dibujado a mano en ReservaHeader antes de
    // esta tanda (padding/tipografía más grandes, en mayúsculas). "sm" queda IDÉNTICO
    // a como estaba: no tocar el tamaño por defecto para no correr las 17+ pantallas
    // que ya usan este badge sin pedirlo.
    const sizeClasses = size === "lg"
        ? "px-3 py-1 text-xs font-bold uppercase tracking-wider"
        : "px-2.5 py-0.5 text-xs font-medium";
    return (
        <span className={`rounded-full border ${sizeClasses} ${cfg.color}`}>
            {label}{conCandado ? " 🔒" : ""}
        </span>
    );
}

/**
 * El "sello" de estado — la pieza de identidad propia de MagnaTravel (estándar visual
 * 2026-08-11, sección B.6, "prestado del sello del pasaporte"). Reemplaza al chip de
 * color SOLO en Anulada/Perdida/Finalizada — el set EXACTO de estados que lo llevan
 * vive en `reservaEstadoSelloLogic.js` (`debeMostrarComoSello`), un archivo .js puro
 * que NO se duplica acá (fix bloqueante de review 2026-08-11, I1/I6): quien quiera
 * decidir si una reserva va con sello o con chip importa esa función, no repite el
 * criterio a mano.
 *
 * Fix de review (2026-08-11, I2/I3): el texto va a opacidad PLENA (contraste ≥4.5:1
 * verificado contra fondo blanco y contra el fondo oscuro) — el estado de una reserva
 * es un dato crítico, no se difumina. El efecto "gastado/medio borroneado" de la
 * maqueta queda SOLO en el borde, con un `<span aria-hidden>` decorativo separado que
 * lleva el degradé — el texto nunca pasa por esa máscara.
 *
 * Colores y ángulo copiados tal cual de la maqueta firmada (docs/ux/2026-08-11-maqueta-
 * reservas-firmada.html, clase `.sello`) — no son un capricho de este componente.
 */
export function ReservaEstadoSello({ reserva, size = 'sm' }) {
    const label = traducirEstadoReserva(reserva?.status);
    const sizeClasses = size === 'lg'
        ? 'px-3.5 py-1 text-sm'
        : 'px-2.5 py-0.5 text-[11px]';

    return (
        // `leading-none` + padding chico: con las 3 etiquetas posibles (Anulada/Perdida/
        // Finalizada) el sello queda bajo, así el giro de -8deg no se come el renglón de
        // arriba/abajo en la tabla compacta (rotate no mueve el layout, pero un box más
        // bajo deja más margen visual antes de tocar la fila vecina).
        <span className="relative inline-block -rotate-[8deg] leading-none">
            {/* Pieza puramente decorativa (el "gastado" de la maqueta) — separada del
                texto a propósito, así el difuminado nunca le baja el contraste al dato. */}
            <span
                aria-hidden="true"
                className="pointer-events-none absolute inset-0 rounded-md border-2 border-dashed border-[#b4443c] dark:border-rose-400"
                style={{
                    maskImage: 'radial-gradient(circle at 30% 60%, black 55%, rgba(0,0,0,0.5) 78%, black 100%)',
                    WebkitMaskImage: 'radial-gradient(circle at 30% 60%, black 55%, rgba(0,0,0,0.5) 78%, black 100%)',
                }}
            />
            <span
                data-testid="reserva-estado-sello"
                className={`relative block whitespace-nowrap font-extrabold uppercase tracking-[0.2em] text-[#b4443c] dark:text-rose-400 ${sizeClasses}`}
            >
                {label}
            </span>
        </span>
    );
}
