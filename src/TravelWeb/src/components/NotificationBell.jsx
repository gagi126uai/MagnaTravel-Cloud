/**
 * Campanita de notificaciones con tres secciones apiladas:
 *   1. "FECHAS LÍMITE" — deadlines de seña/emisión (flag EnableServiceDeadlineAlerts ON).
 *   2. "COSTOS A CONFIRMAR" — servicios sin costo conocido (flag EnableCatalogFindOrCreate ON).
 *   3. "NOTIFICACIONES" — notificaciones del sistema (SignalR + /notifications, siempre activo).
 *
 * Cada sección solo se renderiza si tiene items. Con flags OFF el panel queda
 * exactamente igual que antes (solo la sección de notificaciones, sin título).
 *
 * El badge suma: deadlines + costsToConfirm + notificaciones sin leer.
 * NO suma urgentTrips ni supplierDebts (esos viven en las tarjetas de Cobranzas).
 */

import React, { useState, useEffect, useRef } from "react";
import { Bell, CheckCircle2 } from "lucide-react";
import * as signalR from "@microsoft/signalr";
import { api, buildAppUrl } from "../api";
import { Link } from "react-router-dom";
import { formatDistanceToNow } from "date-fns";
import { es } from "date-fns/locale";
import { useAlerts } from "../contexts/AlertsContext";

// ─── Helpers de fecha (sin new Date("yyyy-mm-dd") para evitar desfase UTC) ──────

/**
 * Formatea "2025-11-30" → "30/11" sin convertir a UTC.
 * Mismo patrón que DeadlinePill.jsx (formatearFechaDdMm).
 */
function formatearDdMm(fechaIso) {
    const soloFecha = (fechaIso || "").split("T")[0];
    if (!soloFecha) return "";
    const [, mes, dia] = soloFecha.split("-");
    if (!mes || !dia) return "";
    return `${dia}/${mes}`;
}

/**
 * Construye la línea 1 del item de deadline según tipo y si vencio.
 * Mismo criterio de texto que DeadlinePill.jsx (obtenerTextoPill).
 */
function textoDeadline(deadlineKind, fechaIso, isOverdue) {
    const fecha = formatearDdMm(fechaIso);
    if (deadlineKind === "OperatorPayment") {
        return isOverdue ? `Venció señar el ${fecha}` : `⏰ Señar antes del ${fecha}`;
    }
    // deadlineKind === "Ticketing"
    return isOverdue ? `Venció emitir el ${fecha}` : `⏰ Emitir antes del ${fecha}`;
}

// ─── Etiqueta de sección (título chico estilo "FECHAS LÍMITE") ────────────────

function TituloSeccion({ children }) {
    return (
        <div className="px-4 pt-3 pb-1 text-[11px] uppercase tracking-wider font-semibold text-slate-400">
            {children}
        </div>
    );
}

// ─── Sección 1: Fechas límite ─────────────────────────────────────────────────

/**
 * Lista de deadlines de seña/emisión.
 * Solo se renderiza si hay items. Ordenada por deadline ascendente (vencidas quedan arriba).
 */
function SeccionFechasLimite({ deadlines, onClose }) {
    if (!deadlines || deadlines.length === 0) return null;

    // Ordenar por deadline ascendente para que las vencidas queden arriba (fecha menor = más vieja)
    const ordenadas = [...deadlines].sort((a, b) => {
        const fa = (a.deadline || "").split("T")[0];
        const fb = (b.deadline || "").split("T")[0];
        if (fa < fb) return -1;
        if (fa > fb) return 1;
        return 0;
    });

    return (
        <div data-testid="bell-deadlines-section">
            <TituloSeccion>Fechas límite</TituloSeccion>
            <ul role="list" className="divide-y divide-slate-100 dark:divide-slate-800/50">
                {ordenadas.map((item, idx) => {
                    const linea1 = textoDeadline(item.deadlineKind, item.deadline, item.isOverdue);
                    const colorLinea1 = item.isOverdue
                        ? "text-red-600 dark:text-red-400"
                        : "text-amber-600 dark:text-amber-400";
                    const colorPunto = item.isOverdue ? "bg-red-500" : "bg-amber-500";

                    return (
                        <li key={`deadline-${item.reservaPublicId}-${item.deadlineKind}-${item.deadline}-${idx}`} role="listitem">
                            <Link
                                to={`/reservas/${item.reservaPublicId}`}
                                onClick={onClose}
                                className="flex items-start gap-3 px-4 py-3 hover:bg-slate-50 dark:hover:bg-slate-800/50 transition-colors"
                                data-testid="bell-deadline-item"
                                data-overdue={item.isOverdue ? "true" : "false"}
                            >
                                {/* Punto de color indicador de urgencia */}
                                <div className="mt-1 flex-shrink-0">
                                    <div className={`h-2 w-2 rounded-full ${colorPunto}`} />
                                </div>
                                <div className="flex-1 space-y-0.5">
                                    <p className={`text-sm font-semibold ${colorLinea1}`}>
                                        {linea1}
                                    </p>
                                    <p className="text-xs text-slate-500 dark:text-slate-400">
                                        {item.serviceLabel} · Reserva {item.numeroReserva}
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
    const [notifications, setNotifications] = useState([]);
    const [unreadCount, setUnreadCount] = useState(0);
    const [isOpen, setIsOpen] = useState(false);
    const containerRef = useRef(null);
    const connectionRef = useRef(null);

    // Consumimos alertas del contexto compartido (serviceDeadlines + costsToConfirm).
    // El contexto ya hace el fetch y el poll por su cuenta.
    const { alerts } = useAlerts();

    const serviceDeadlines = alerts?.serviceDeadlines || [];
    const costsToConfirm = alerts?.costsToConfirm || [];

    // Las secciones nuevas están activas (hay items para mostrar)
    const hayAvisosNuevos = serviceDeadlines.length > 0 || costsToConfirm.length > 0;

    // Badge = deadlines + costsToConfirm + notificaciones sin leer.
    // Decisión del dueño: urgentTrips y supplierDebts NO se suman (viven en Cobranzas).
    const totalBadge = serviceDeadlines.length + costsToConfirm.length + unreadCount;

    // Initial load de notificaciones del sistema (separadas del contexto de alertas)
    useEffect(() => {
        const fetchNotifications = async () => {
            try {
                const data = await api.get("/notifications?unreadOnly=true");
                setNotifications(data || []);
                setUnreadCount(data?.length || 0);
            } catch (error) {
                console.error("Error fetching notifications:", error);
            }
        };

        fetchNotifications();
    }, []);

    // SignalR: recibe notificaciones en tiempo real
    useEffect(() => {
        const url = buildAppUrl("/hubs/notifications");

        const newConnection = new signalR.HubConnectionBuilder()
            .withUrl(url, { withCredentials: true })
            .withAutomaticReconnect()
            .build();

        newConnection.on("ReceiveNotification", (notification) => {
            setNotifications((prev) => [notification, ...prev]);
            setUnreadCount((prev) => prev + 1);
        });

        newConnection.start().catch((err) => console.error("SignalR Connection Error: ", err));
        connectionRef.current = newConnection;

        return () => {
            if (connectionRef.current) {
                connectionRef.current.stop();
            }
        };
    }, []);

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

    const markAsRead = async (id, e) => {
        if (e) {
            e.preventDefault();
            e.stopPropagation();
        }
        try {
            await api.post(`/notifications/${id}/read`);
            setNotifications((prev) => prev.filter((n) => n.id !== id));
            setUnreadCount((prev) => Math.max(0, prev - 1));
        } catch (error) {
            console.error("Error marking notification as read:", error);
        }
    };

    const markAllAsRead = async () => {
        try {
            await Promise.all(notifications.map(n => api.post(`/notifications/${n.id}/read`)));
            setNotifications([]);
            setUnreadCount(0);
            setIsOpen(false);
        } catch (error) {
            console.error("Error marking all as read:", error);
        }
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
                {/* Badge: suma deadlines + costos + notificaciones sin leer */}
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
                                onClick={markAllAsRead}
                                className="text-xs font-medium text-indigo-600 dark:text-indigo-400 hover:underline"
                            >
                                Marcar todas leídas
                            </button>
                        )}
                    </div>

                    <div className="max-h-[60vh] overflow-y-auto">
                        {/* Sección 1: Fechas límite (solo si hay items) */}
                        <SeccionFechasLimite deadlines={serviceDeadlines} onClose={cerrarPanel} />

                        {/* Sección 2: Costos a confirmar (solo si hay items) */}
                        <SeccionCostosAConfirmar costos={costsToConfirm} onClose={cerrarPanel} />

                        {/* Sección 3: Notificaciones del sistema.
                            Si hay avisos nuevos (deadlines o costos), agregamos el título de sección
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
                                            onClick={(e) => markAsRead(notification.id, e)}
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
