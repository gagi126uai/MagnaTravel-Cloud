/**
 * Campanita de notificaciones con tres secciones apiladas:
 *   1. "PRÓXIMOS INICIOS" — reservas que arrancan pronto (flag EnableServiceDeadlineAlerts ON).
 *   2. "COSTOS A CONFIRMAR" — servicios sin costo conocido (flag EnableCatalogFindOrCreate ON).
 *   3. "NOTIFICACIONES" — notificaciones del sistema (SignalR + /notifications, siempre activo).
 *
 * Cada sección solo se renderiza si tiene items. Con flags OFF el panel queda
 * exactamente igual que antes (solo la sección de notificaciones, sin título).
 *
 * El badge suma: upcomingStarts visibles + costsToConfirm + notificaciones sin leer.
 * NO suma urgentTrips ni supplierDebts (esos viven en las tarjetas de Cobranzas).
 */

import React, { useState, useEffect, useRef } from "react";
import { Bell, CheckCircle2 } from "lucide-react";
import { api } from "../api";
import { Link } from "react-router-dom";
import { formatDistanceToNow } from "date-fns";
import { es } from "date-fns/locale";
import { useAlerts } from "../contexts/AlertsContext";

// ─── Helpers de fecha (sin new Date("yyyy-mm-dd") para evitar desfase UTC) ──────

/**
 * Formatea "2025-11-30" → "30/11" sin convertir a UTC.
 * Usa string-split porque el backend serializa firstStartDate como "...T00:00:00Z"
 * y new Date().toLocaleDateString() en UTC-3 podría devolver el día anterior.
 */
function formatearDdMm(fechaIso) {
    const soloFecha = (fechaIso || "").split("T")[0];
    if (!soloFecha) return "";
    const [, mes, dia] = soloFecha.split("-");
    if (!mes || !dia) return "";
    return `${dia}/${mes}`;
}

/**
 * Construye el texto de la línea 1 de un ítem de próximo inicio.
 *
 * Regla: daysLeft >= 1 → emoji ámbar + "en N días" (singular si N==1).
 *        daysLeft <= 0 → "Empieza HOY" en rojo, sin emoji.
 *        daysLeft < 0  → nunca debería llegar acá (el server filtra), pero
 *                         tratamos defensivamente igual a HOY.
 *
 * Exportada como helper puro para los tests (sin DOM).
 */
export function textoProximoInicio(daysLeft, firstStartDate) {
    const fecha = formatearDdMm(firstStartDate);
    // Defensivo: cubre daysLeft=0 (HOY) y negativos (server debería filtrarlos, pero por las dudas)
    if (daysLeft <= 0) {
        return `Empieza HOY ${fecha}`;
    }
    // Singular: "en 1 día", plural: "en N días"
    const diasTexto = daysLeft === 1 ? "en 1 día" : `en ${daysLeft} días`;
    return `⏰ Empieza el ${fecha} (${diasTexto})`;
}

// ─── Etiqueta de sección (título chico estilo "FECHAS LÍMITE") ────────────────

function TituloSeccion({ children }) {
    return (
        <div className="px-4 pt-3 pb-1 text-[11px] uppercase tracking-wider font-semibold text-slate-400">
            {children}
        </div>
    );
}

// ─── Sección 1: Próximos inicios ─────────────────────────────────────────────

/**
 * Lista de reservas por arrancar pronto.
 * El servidor ya ordena por firstStartDate ascendente; no hace falta sort local.
 * Solo se renderiza si hay items visibles (no descartados optimistamente).
 *
 * Props:
 *   items              — upcomingStarts[] del contexto, ya filtrados por descartadasOptimistas
 *   onClose            — cierra el panel (se llama al hacer clic en el Link)
 *   onDescartar        — callback(reservaPublicId) para el botón "Listo"
 *   descartando        — Set de publicIds cuyo botón ya está disabled (POST en vuelo)
 */
function SeccionProximosInicios({ items, onClose, onDescartar, descartando }) {
    if (!items || items.length === 0) return null;

    return (
        <div data-testid="bell-upcoming-section">
            <TituloSeccion>Próximos inicios</TituloSeccion>
            <ul role="list" className="divide-y divide-slate-100 dark:divide-slate-800/50">
                {items.map((item) => {
                    // Decidimos color según daysLeft: 0 = rojo, >= 1 = ámbar
                    const esHoy = item.daysLeft === 0;
                    const colorLinea1 = esHoy
                        ? "text-red-600 dark:text-red-400"
                        : "text-amber-600 dark:text-amber-400";
                    const colorPunto = esHoy ? "bg-red-500" : "bg-amber-500";

                    const linea1 = textoProximoInicio(item.daysLeft, item.firstStartDate);

                    // Línea 2: "Reserva {numero} · {titular}"
                    // Fallback holderName null → name; ambos vacíos → solo el número sin separador.
                    const titular = item.holderName || item.name || "";
                    const linea2 = titular
                        ? `Reserva ${item.numeroReserva} · ${titular}`
                        : `Reserva ${item.numeroReserva}`;

                    const estaDescartando = descartando.has(item.reservaPublicId);

                    return (
                        <li
                            key={item.reservaPublicId}
                            role="listitem"
                            data-testid="bell-upcoming-item"
                            data-today={esHoy ? "true" : "false"}
                        >
                            {/* Fila flex: zona clickeable (Link) + botón "Listo" separado.
                                NO anidamos el botón dentro del Link porque un button dentro
                                de un anchor es HTML inválido y rompe el click en algunos browsers. */}
                            <div className="flex items-start gap-0 hover:bg-slate-50 dark:hover:bg-slate-800/50 transition-colors">
                                {/* Zona izquierda: navega a la reserva y cierra el panel */}
                                <Link
                                    to={`/reservas/${item.reservaPublicId}`}
                                    onClick={onClose}
                                    className="flex items-start gap-3 px-4 py-3 flex-1 min-w-0"
                                >
                                    {/* Punto de color indicador de urgencia */}
                                    <div className="mt-1 flex-shrink-0">
                                        <div className={`h-2 w-2 rounded-full ${colorPunto}`} />
                                    </div>
                                    <div className="flex-1 min-w-0 space-y-0.5">
                                        <p className={`text-sm font-semibold ${colorLinea1}`}>
                                            {linea1}
                                        </p>
                                        <p className="text-xs text-slate-500 dark:text-slate-400 truncate">
                                            {linea2}
                                        </p>
                                    </div>
                                </Link>

                                {/* Botón "Listo": descarte optimista. Siempre visible, a la derecha.
                                    Decisión UX: permite descartar sin navegar ni cerrar el panel.
                                    Estilo: neutro/slate, mismo patrón que los botones de borde
                                    del sistema (border + hover gris). NO usa color ámbar. */}
                                <button
                                    type="button"
                                    onClick={() => onDescartar(item.reservaPublicId)}
                                    disabled={estaDescartando}
                                    aria-label={`Listo: reserva ${item.numeroReserva}`}
                                    data-testid="bell-upcoming-dismiss"
                                    className="self-center mr-3 px-2.5 py-1 text-[11px] font-medium border border-slate-300 dark:border-slate-600 text-slate-600 dark:text-slate-300 rounded-md hover:bg-slate-100 dark:hover:bg-slate-700 disabled:opacity-40 disabled:cursor-not-allowed transition-colors whitespace-nowrap flex-shrink-0"
                                >
                                    Listo
                                </button>
                            </div>
                        </li>
                    );
                })}
            </ul>
        </div>
    );
}

// ─── Sección 1b: Por caducar ─────────────────────────────────────────────────

/**
 * Q9 (2026-06-24): Lista de presupuestos/cotizaciones que están por caducar.
 *
 * El backend calcula el umbral y el texto del mensaje; el front solo lo muestra.
 * Ámbar cuando faltan días (daysLeft > 0); rojo cuando vence hoy (daysLeft === 0).
 * Navegar al aviso lleva a la reserva — mismo patrón que Próximos inicios.
 *
 * Sección APARTE de "Próximos inicios": aquella es sobre el inicio del viaje;
 * esta es sobre el vencimiento del presupuesto/cotización (spec 2026-06-24).
 *
 * Props:
 *   items   — expiringPreSales[] del contexto (null = bucket inactivo, no renderiza)
 *   onClose — cierra el panel al hacer clic en el Link
 */
function SeccionPorCaducar({ items, onClose }) {
    if (!items || items.length === 0) return null;

    return (
        <div data-testid="bell-expiring-presales-section">
            <TituloSeccion>Por caducar</TituloSeccion>
            <ul role="list" className="divide-y divide-slate-100 dark:divide-slate-800/50">
                {items.map((item) => {
                    // Rojo cuando vence hoy (daysLeft === 0), ámbar cuando faltan días.
                    const esHoy = item.daysLeft === 0;
                    const colorLinea = esHoy
                        ? "text-red-600 dark:text-red-400"
                        : "text-amber-600 dark:text-amber-400";
                    const colorPunto = esHoy ? "bg-red-500" : "bg-amber-500";

                    // B1 fix (2026-06-24): el backend devuelve la frase COMPLETA en item.message.
                    // Ej: "El presupuesto de Fam. García vence en 3 días."
                    // No construir la oración en el front — causaba duplicación del tipo y cliente.
                    const textoAviso = item.message;

                    return (
                        <li
                            key={item.reservaPublicId}
                            role="listitem"
                            data-testid="bell-expiring-presales-item"
                            data-today={esHoy ? "true" : "false"}
                        >
                            <Link
                                to={`/reservas/${item.reservaPublicId}`}
                                onClick={onClose}
                                className="flex items-start gap-3 px-4 py-3 hover:bg-slate-50 dark:hover:bg-slate-800/50 transition-colors"
                            >
                                {/* Punto de color indicador de urgencia */}
                                <div className="mt-1 flex-shrink-0">
                                    <div className={`h-2 w-2 rounded-full ${colorPunto}`} />
                                </div>
                                <div className="flex-1 min-w-0 space-y-0.5">
                                    <p className={`text-sm font-semibold ${colorLinea}`}>
                                        {textoAviso}
                                    </p>
                                    <p className="text-xs text-slate-500 dark:text-slate-400 truncate">
                                        Reserva {item.numeroReserva}
                                    </p>
                                </div>
                            </Link>
                        </li>
                    );
                })}
            </ul>
        </div>
    );
}

// ─── Sección 2: Costos a confirmar ───────────────────────────────────────────

/**
 * Construye el texto de la segunda línea según la razón del item.
 * NoKnownCost = producto nuevo; StaleReference = precio de una venta vieja.
 */
function textoRazonCosto(reason) {
    if (reason === "NoKnownCost") return "Producto nuevo, sin costo conocido";
    if (reason === "StaleReference") return "El costo viene de una venta vieja";
    return null; // null = no renderizar línea 2
}

/**
 * Título plural/singular: "TENÉS 1 COSTO A CONFIRMAR" / "TENÉS 3 COSTOS A CONFIRMAR"
 */
function tituloCostos(cantidad) {
    if (cantidad === 1) return "Tenés 1 costo a confirmar";
    return `Tenés ${cantidad} costos a confirmar`;
}

/**
 * Lista de costos a confirmar (flag EnableCatalogFindOrCreate + permiso cobranzas.see_cost).
 * Solo se renderiza si hay items.
 */
function SeccionCostosAConfirmar({ costos, onClose }) {
    if (!costos || costos.length === 0) return null;

    // Clases de la pill "A confirmar" — mismo estilo que CostConfirmCell.jsx (CLASES_PILL_AMBAR)
    const clasesPillAmbar = "inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-semibold bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400";

    return (
        <div data-testid="bell-costs-section">
            <TituloSeccion>{tituloCostos(costos.length)}</TituloSeccion>
            <ul role="list" className="divide-y divide-slate-100 dark:divide-slate-800/50">
                {costos.map((item, idx) => {
                    const lineaRazon = textoRazonCosto(item.reason);
                    return (
                        <li key={`costo-${item.reservaPublicId}-${item.serviceLabel}-${idx}`} role="listitem">
                            <Link
                                to={`/reservas/${item.reservaPublicId}`}
                                onClick={onClose}
                                className="flex items-start gap-3 px-4 py-3 hover:bg-slate-50 dark:hover:bg-slate-800/50 transition-colors"
                                data-testid="bell-cost-item"
                            >
                                {/* Punto ámbar */}
                                <div className="mt-1 flex-shrink-0">
                                    <div className="h-2 w-2 rounded-full bg-amber-500" />
                                </div>
                                <div className="flex-1 space-y-0.5">
                                    <p className="text-sm font-semibold text-slate-700 dark:text-slate-300 flex items-center gap-1.5 flex-wrap">
                                        {item.serviceLabel} · Reserva {item.numeroReserva}
                                        <span className={clasesPillAmbar}>A confirmar</span>
                                    </p>
                                    {/* Línea 2 solo si hay razón reconocida */}
                                    {lineaRazon && (
                                        <p className="text-xs text-slate-500 dark:text-slate-400">
                                            {lineaRazon}
                                        </p>
                                    )}
                                </div>
                            </Link>
                        </li>
                    );
                })}
            </ul>
        </div>
    );
}

// ─── Componente principal ─────────────────────────────────────────────────────

export default function NotificationBell() {
    const [isOpen, setIsOpen] = useState(false);
    const containerRef = useRef(null);

    // Set local de publicIds descartados optimistamente.
    // Al hacer clic en "Listo", el ítem desaparece al instante del panel y del badge.
    // Si el POST falla, el id se saca del set y el ítem reaparece con el próximo refresh.
    const [descartadasOptimistas, setDescartadasOptimistas] = useState(new Set());
    // Set de publicIds cuyo botón "Listo" está en vuelo (disabled mientras el POST va).
    const [descartando, setDescartando] = useState(new Set());

    // Todo viene del contexto compartido (Tanda 5): alertas + notificaciones + conexión SignalR única.
    // La campanita ya no hace su propio fetch ni abre su propia conexión (eso desincronizaba el badge con la página).
    const { alerts, refreshAlerts, notifications, unreadCount, markAsRead, markAllAsRead } = useAlerts();

    const upcomingStarts = alerts?.upcomingStarts || [];
    const costsToConfirm = alerts?.costsToConfirm || [];
    // Q9 (2026-06-24): presupuestos/cotizaciones por caducar.
    // null = el backend no envió el bucket (flag o configuración inactiva); [] = bucket activo pero vacío.
    const expiringPreSales = alerts?.expiringPreSales || [];

    // Poda del Set de descartados optimistas: cada vez que el servidor devuelve un nuevo
    // payload de alertas, eliminamos del Set los ids que el server ya no incluye.
    //
    // Por qué: si la fecha del primer servicio cambia y el server re-incluye un ítem
    // que el usuario había descartado, este cliente lo filtraría para siempre hasta recargar.
    // Con la poda, el id permanece en el Set solo mientras el server SIGUE devolviendo ese
    // ítem (cubre la ventana POST → refresh), y se libera cuando el server lo saca.
    //
    // Micro-carrera aceptada: si hay un poll en vuelo cuando se ejecuta la poda, el ítem
    // puede reaparecer brevemente en el próximo ciclo. La misma staleness que tiene todo
    // el contexto; no vale la pena resolverla con coordinación adicional.
    //
    // useEffect con [alerts]: corre cada vez que el contexto trae un nuevo payload del server.
    useEffect(() => {
        setDescartadasOptimistas((prev) => {
            if (prev.size === 0) return prev;
            const idsActuales = new Set(upcomingStarts.map((i) => i.reservaPublicId));
            const next = new Set([...prev].filter((id) => idsActuales.has(id)));
            // Solo actualizamos el estado si el Set efectivamente cambió para evitar re-renders
            return next.size === prev.size ? prev : next;
        });
    }, [alerts]); // eslint-disable-line react-hooks/exhaustive-deps -- `upcomingStarts` deriva de `alerts`; depender de `alerts` es correcto

    // Filtramos los descartados optimistamente para el render y el badge
    const upcomingStartsVisibles = upcomingStarts.filter(
        (item) => !descartadasOptimistas.has(item.reservaPublicId)
    );

    // Hay avisos activos si alguna sección tiene items (sin contar notificaciones del sistema).
    // Q9: los "por caducar" también cuentan como avisos activos.
    const hayAvisosNuevos = upcomingStartsVisibles.length > 0 || costsToConfirm.length > 0 || expiringPreSales.length > 0;

    // Badge = próximos inicios visibles + por caducar + costsToConfirm + notificaciones sin leer.
    // Decisión del dueño: urgentTrips y supplierDebts NO se suman (viven en Cobranzas).
    const totalBadge = upcomingStartsVisibles.length + expiringPreSales.length + costsToConfirm.length + unreadCount;

    /**
     * Handler del botón "Listo" de cada ítem de próximo inicio.
     *
     * Flujo optimista (decisión UX: respuesta inmediata):
     *   1. Deshabilita el botón y oculta el ítem al instante.
     *   2. Hace el POST dismiss al backend.
     *   3. Si tiene éxito: llama refreshAlerts() para sincronizar con el server.
     *   4. Si falla: saca el id del set de descartados → el ítem reaparece con el refresh.
     *      Sin cartel de error (el ítem reaparece solo, que es señal suficiente).
     */
    const handleDescartar = async (reservaPublicId) => {
        // 1. Efecto inmediato: ocultar el ítem y deshabilitar el botón
        setDescartadasOptimistas((prev) => new Set([...prev, reservaPublicId]));
        setDescartando((prev) => new Set([...prev, reservaPublicId]));

        try {
            // 2. Llamada al backend (204 idempotente)
            await api.post(`/alerts/upcoming-starts/${reservaPublicId}/dismiss`);
            // 3. Sincronizamos para que el poll/contexto quede limpio
            refreshAlerts();
        } catch {
            // 4. Si falla: revertimos el descarte optimista → el ítem reaparece
            setDescartadasOptimistas((prev) => {
                const next = new Set(prev);
                next.delete(reservaPublicId);
                return next;
            });
        } finally {
            // Siempre liberamos el estado "descartando" para que el botón no quede stuck
            setDescartando((prev) => {
                const next = new Set(prev);
                next.delete(reservaPublicId);
                return next;
            });
        }
    };

    // Click fuera del panel → cerrar
    useEffect(() => {
        const handleClickOutside = (event) => {
            if (containerRef.current && !containerRef.current.contains(event.target)) {
                setIsOpen(false);
            }
        };
        document.addEventListener("mousedown", handleClickOutside);
        return () => document.removeEventListener("mousedown", handleClickOutside);
    }, []);

    const cerrarPanel = () => setIsOpen(false);

    // Marcar una como leída: el contexto hace el POST y actualiza el estado compartido (campanita + página).
    const handleMarkAsRead = async (id, e) => {
        if (e) {
            e.preventDefault();
            e.stopPropagation();
        }
        await markAsRead(id);
    };

    // Marcar todas: delega en el contexto y cierra el panel.
    const handleMarkAllAsRead = async () => {
        await markAllAsRead();
        setIsOpen(false);
    };

    const getDotColor = (notification) => {
        if (notification.priority === "Urgent") return "bg-red-500 animate-pulse";
        if (notification.type === "Error") return "bg-red-500";
        if (notification.type === "Success") return "bg-emerald-500";
        if (notification.type === "Warning") return "bg-amber-500";
        return "bg-indigo-500";
    };

    const getRowHighlight = (notification) => {
        if (notification.priority === "Urgent") {
            return "bg-red-50/50 dark:bg-red-900/10 border-l-2 border-l-red-500";
        }
        return "";
    };

    return (
        <div className="relative" ref={containerRef}>
            <button
                onClick={() => setIsOpen(!isOpen)}
                className="relative p-2 text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200 transition-colors focus:outline-none"
                title="Notificaciones"
                aria-label={totalBadge > 0 ? `Notificaciones, ${totalBadge} pendientes` : "Notificaciones"}
                data-testid="notification-bell-button"
            >
                <Bell className="h-5 w-5" />
                {/* Badge: suma próximos inicios visibles + costos + notificaciones sin leer */}
                {totalBadge > 0 && (
                    <span className="absolute top-1 right-1 flex h-4 w-4 items-center justify-center rounded-full bg-rose-500 text-[10px] font-bold text-white ring-2 ring-white dark:ring-slate-900 animate-pulse">
                        {totalBadge > 9 ? "9+" : totalBadge}
                    </span>
                )}
            </button>

            {isOpen && (
                <div className="absolute right-0 mt-2 w-80 md:w-96 rounded-lg bg-white dark:bg-slate-900 shadow-xl ring-1 ring-slate-200 dark:ring-slate-800 z-50 animate-in fade-in slide-in-from-top-2">
                    {/* Cabecera del panel */}
                    <div className="flex items-center justify-between border-b border-slate-100 dark:border-slate-800 px-4 py-3">
                        <h3 className="font-semibold text-slate-800 dark:text-slate-200">Notificaciones</h3>
                        {unreadCount > 0 && (
                            <button
                                onClick={handleMarkAllAsRead}
                                className="text-xs font-medium text-indigo-600 dark:text-indigo-400 hover:underline"
                            >
                                Marcar todas leídas
                            </button>
                        )}
                    </div>

                    <div className="max-h-[60vh] overflow-y-auto">
                        {/* Sección 1: Próximos inicios (solo si hay items visibles) */}
                        <SeccionProximosInicios
                            items={upcomingStartsVisibles}
                            onClose={cerrarPanel}
                            onDescartar={handleDescartar}
                            descartando={descartando}
                        />

                        {/* Sección 1b: Por caducar — presupuestos/cotizaciones que vencen pronto.
                            Q9 (2026-06-24): sección APARTE de Próximos inicios (spec confirmada). */}
                        <SeccionPorCaducar items={expiringPreSales} onClose={cerrarPanel} />

                        {/* Sección 2: Costos a confirmar (solo si hay items) */}
                        <SeccionCostosAConfirmar costos={costsToConfirm} onClose={cerrarPanel} />

                        {/* Sección 3: Notificaciones del sistema.
                            Si hay avisos nuevos (próximos inicios o costos), agregamos el título de sección
                            para distinguir visualmente las notificaciones del sistema. Si no hay nada
                            nuevo, el panel queda exactamente como antes (sin título extra). */}
                        {hayAvisosNuevos && notifications.length > 0 && (
                            <TituloSeccion>Notificaciones</TituloSeccion>
                        )}

                        {notifications.length === 0 && !hayAvisosNuevos ? (
                            <div className="px-4 py-8 text-center text-slate-500 dark:text-slate-400">
                                <Bell className="mx-auto h-8 w-8 opacity-20 mb-2" />
                                <p className="text-sm">No tienes nuevas notificaciones</p>
                            </div>
                        ) : notifications.length > 0 ? (
                            <div className="divide-y divide-slate-100 dark:divide-slate-800/50">
                                {notifications.map((notification) => (
                                    <div
                                        key={notification.id}
                                        className={`relative flex items-start gap-3 p-4 hover:bg-slate-50 dark:hover:bg-slate-800/50 transition-colors group cursor-default ${getRowHighlight(notification)}`}
                                    >
                                        <div className="mt-1">
                                            <div className={`h-2 w-2 rounded-full ${getDotColor(notification)}`}></div>
                                        </div>
                                        <div className="flex-1 space-y-1">
                                            {notification.priority === "Urgent" && (
                                                <span className="inline-flex items-center gap-1 text-[9px] font-bold uppercase tracking-wider text-red-600 dark:text-red-400">
                                                    ⚡ Urgente
                                                </span>
                                            )}
                                            <p className="text-sm text-slate-700 dark:text-slate-300">
                                                {notification.message}
                                            </p>
                                            <p className="text-xs text-slate-400">
                                                {formatDistanceToNow(new Date(notification.createdAt), { addSuffix: true, locale: es })}
                                            </p>
                                        </div>
                                        <button
                                            onClick={(e) => handleMarkAsRead(notification.id, e)}
                                            className="opacity-0 group-hover:opacity-100 p-1 text-slate-400 hover:text-indigo-600 transition-opacity"
                                            title="Marcar como leída"
                                        >
                                            <CheckCircle2 className="h-4 w-4" />
                                        </button>
                                    </div>
                                ))}
                            </div>
                        ) : null}
                    </div>

                    <div className="border-t border-slate-100 dark:border-slate-800 px-4 py-2">
                        <Link
                            to="/notifications"
                            onClick={cerrarPanel}
                            className="block text-center text-xs font-medium text-slate-500 hover:text-slate-800 dark:hover:text-slate-300 py-1"
                        >
                            Ver todas las notificaciones
                        </Link>
                    </div>
                </div>
            )}
        </div>
    );
}
