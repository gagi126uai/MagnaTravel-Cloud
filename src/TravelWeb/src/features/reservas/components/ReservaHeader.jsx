import React from 'react';
import { ArrowLeft, Trash2, Archive, AlertTriangle, Undo2, Calendar, Pencil, Ban, Lock, XCircle, RefreshCw } from "lucide-react";
import { getReservaArchiveBlockReason } from "../archiveRules";
import { getStatusConfig, translateStatus, isStatusLocked } from "./ReservaStatusBadge";
import { ReservaStatusChips } from "./ReservaStatusChips";

function formatTripDate(value) {
    if (!value) return null;
    try {
        const d = new Date(value);
        if (isNaN(d.getTime())) return null;
        return d.toLocaleDateString("es-AR", { day: "2-digit", month: "2-digit", year: "numeric" });
    } catch {
        return null;
    }
}

/**
 * Cabecera de la pagina de detalle de una Reserva.
 * Muestra: nombre, numero, estado (con icono de candado si aplica), fechas de viaje y botonera de acciones.
 *
 * ADR-020 (ciclo unico, sin flags):
 *   Quotation → [Pasar a presupuesto] → Budget
 *   Budget    → [El cliente acepto]   → InManagement
 *   InManagement → (automatico al resolverse todos los servicios) → Confirmed
 *   Confirmed (candado) → (automatico al llegar la fecha de salida) → Traveling
 *   Traveling → [Cerrar reserva] → Closed
 *   Traveling → [Apartar para liquidar] → ToSettle (desvio opcional)
 *   ToSettle  → [Finalizar] → Closed
 *   Cualquier etapa activa → [Cancelar] (con proceso fiscal)
 *   Quotation/Budget → [Perdido] (discreto, no hubo compra)
 *
 * Props:
 * - reserva: objeto de la reserva cargada
 * - canCancelReserva: si el usuario tiene el permiso reservas.cancel
 * - onCancelReserva: callback para abrir el flujo de cancelacion
 * - onRequestEdit: callback para abrir el modal de autorizacion de edicion (cuando hay candado)
 * - onMarkLost: callback para abrir el modal "Marcar como perdida"
 * - Los callbacks onStatusChange, onDelete, onArchive, onRevert, onEditDates son manejados por el padre
 * - serviciosCancelados: { cancelados: number, totalConProveedor: number } — para el contador "N de M".
 *   El padre lo calcula con calculateServiciosCanceladosResumen(allServices).
 *   Si viene null/undefined no se muestra nada (diseño conservador).
 */
export function ReservaHeader({
    reserva,
    onBack,
    onStatusChange,
    onDelete,
    onArchive,
    onRevert,
    onEditDates,
    canCancelReserva = false,
    onCancelReserva,
    onRequestEdit,
    onMarkLost,
    serviciosCancelados = null,
    totalPasajerosDeclarados = null,
}) {
    const isArchived = reserva.status === 'Archived';
    const locked = isStatusLocked(reserva.status);

    // Solo se puede eliminar en Quotation/Budget (sin pagos, sin servicios resueltos).
    const canDelete = (reserva.status === 'Quotation' || reserva.status === 'Budget');

    const archiveBlockReason = getReservaArchiveBlockReason(reserva);
    const canArchive = !archiveBlockReason;

    // Reversion: permitida desde estados post-gestion hacia atras.
    // Closed puede revertir a Traveling; Lost puede revertir a su origen.
    const canRevert = ['Budget', 'InManagement', 'Confirmed', 'Traveling', 'ToSettle', 'Closed', 'Lost'].includes(reserva.status);

    // Las fechas se pueden editar en estados activos (no archivada/cancelada/perdida).
    const canEditDates = !isArchived && reserva.status !== 'Cancelled' && reserva.status !== 'Lost';

    // Boton "Cancelar reserva": visible solo en estados operativos con implicancias fiscales.
    // Quotation/Budget/Lost/InManagement sin pagos → preferiria usar "eliminar" o "perder".
    // El backend re-valida el permiso; esto es solo UI.
    const CANCELLABLE_STATUSES = ['InManagement', 'Confirmed', 'Traveling', 'ToSettle'];
    const showCancelButton = canCancelReserva
        && onCancelReserva
        && CANCELLABLE_STATUSES.includes(reserva.status)
        && !isArchived;

    // "Perdido": solo desde Quotation o Budget (cuando el cliente no compro).
    // En etapas mas avanzadas se usa "Cancelar" (hay implicancias fiscales).
    const showMarkLostButton = ['Quotation', 'Budget'].includes(reserva.status)
        && !isArchived
        && onMarkLost;

    const startLabel = formatTripDate(reserva.startDate);
    const endLabel = formatTripDate(reserva.endDate);

    // Gate de cierre: el viaje tiene que haber terminado y no quedar saldo.
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const endHasPast = reserva.endDate ? new Date(reserva.endDate) < today : false;
    const canClose = endHasPast && reserva.balance <= 0;
    const closeTooltip = !endHasPast
        ? "El viaje todavia no termino"
        : reserva.balance > 0
            ? "No se puede cerrar con saldo pendiente"
            : "Cerrar reserva";

    const statusCfg = getStatusConfig(reserva.status);

    return (
        <div className="mb-8 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
            <div>
                <button
                    onClick={onBack}
                    className="flex items-center text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200 mb-2 transition-colors font-medium text-sm"
                >
                    <ArrowLeft className="w-4 h-4 mr-1.5" /> Volver a Lista
                </button>
                <div className="flex items-center gap-3 flex-wrap">
                    <h1 className="text-3xl font-extrabold text-slate-900 dark:text-white tracking-tight">
                        Reserva <span className="text-indigo-600 dark:text-indigo-400">#{reserva.numeroReserva}</span>
                    </h1>
                    {/* Badge de estado con icono de candado si aplica (decision 1 de UX) */}
                    <div className="flex items-center gap-1.5">
                        <span className={`px-3 py-1 rounded-full text-xs font-bold uppercase tracking-wider border ${statusCfg.color}`}>
                            {translateStatus(reserva.status)}
                        </span>
                        {locked && (
                            <Lock
                                className="w-3.5 h-3.5 text-amber-600 dark:text-amber-400"
                                title="Esta reserva tiene el candado activo. Para editar necesitas autorizacion."
                                aria-label="Reserva bloqueada"
                            />
                        )}
                        {/* ADR-027: etiqueta "Con cambios" al lado del estado.
                            Aparece cuando el vendedor editó precio/costo de un servicio
                            en una reserva viva y el dueño todavía no acusó el cambio. */}
                        {reserva.hasUnacknowledgedChanges && (
                            <span
                                className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-black uppercase tracking-wider bg-amber-100 text-amber-800 border border-amber-300 dark:bg-amber-900/40 dark:text-amber-200 dark:border-amber-700"
                                data-testid="badge-con-cambios"
                                title="Hay cambios de precio/costo pendientes de revisión"
                            >
                                <RefreshCw className="w-2.5 h-2.5" aria-hidden="true" />
                                Con cambios
                            </span>
                        )}
                    </div>
                    <ReservaStatusChips reserva={reserva} />
                </div>

                {/* Contador "N de M servicios cancelados" (ADR-025 DT.3.1 decision #1).
                    Solo aparece cuando hay AL MENOS UN servicio cancelado.
                    Es informativo: no cambia el estado ni el color de la reserva.
                    El dato viene del padre, calculado con calculateServiciosCanceladosResumen(allServices). */}
                {serviciosCancelados && serviciosCancelados.cancelados > 0 && (
                    <p
                        className="mt-1 text-xs text-slate-400 dark:text-slate-500"
                        data-testid="contador-servicios-cancelados"
                    >
                        {serviciosCancelados.cancelados} de {serviciosCancelados.totalConProveedor}{' '}
                        {serviciosCancelados.totalConProveedor === 1 ? 'servicio cancelado' : 'servicios cancelados'}
                    </p>
                )}

                <p className="text-xl text-slate-900 dark:text-white mt-2 font-bold flex items-center gap-2">
                    {reserva.customerName}
                </p>
                {reserva.name && reserva.name !== `Reserva ${reserva.numeroReserva}` && (
                    <p className="text-lg text-slate-500 dark:text-slate-400 font-medium italic">{reserva.name}</p>
                )}

                {/* Fechas del viaje */}
                <div className="mt-3 flex items-center gap-3 flex-wrap">
                    <div className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-1.5 text-sm dark:border-slate-800 dark:bg-slate-900">
                        <Calendar className="w-4 h-4 text-indigo-500" />
                        <span className="font-medium text-slate-500 dark:text-slate-400">Salida:</span>
                        <span className={startLabel ? "font-bold text-slate-900 dark:text-white" : "italic text-slate-400"}>
                            {startLabel || "sin cargar"}
                        </span>
                        <span className="text-slate-300 dark:text-slate-700">·</span>
                        <span className="font-medium text-slate-500 dark:text-slate-400">Regreso:</span>
                        <span className={endLabel ? "font-bold text-slate-900 dark:text-white" : "italic text-slate-400"}>
                            {endLabel || "sin cargar"}
                        </span>
                    </div>
                    {canEditDates && onEditDates && (
                        <button
                            onClick={onEditDates}
                            type="button"
                            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 hover:border-slate-300 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 dark:hover:bg-slate-800"
                            title="Editar fechas del viaje"
                        >
                            <Pencil className="w-3.5 h-3.5" />
                            Editar fechas
                        </button>
                    )}
                </div>
            </div>

            {/* Botonera de acciones */}
            {isArchived ? (
                <div className="flex items-center gap-2 px-4 py-3 bg-slate-100 dark:bg-slate-800 border border-slate-200 dark:border-slate-700 rounded-xl">
                    <AlertTriangle className="w-4 h-4 text-slate-500" />
                    <span className="text-sm font-medium text-slate-600 dark:text-slate-400">Solo lectura — Reserva archivada</span>
                </div>
            ) : (
                <div className="flex flex-wrap gap-3">
                    {/* =====================================================
                        BOTONES DE AVANCE DE ETAPA — ciclo unico (ADR-020)

                        Quotation → [Pasar a presupuesto]
                        Budget    → [El cliente acepto]
                        InManagement: Confirmada es AUTOMATICA (no hay boton)
                        Confirmed: En viaje es AUTOMATICA (no hay boton)
                        Traveling → [Cerrar reserva] + [Apartar para liquidar]
                        ToSettle  → [Finalizar / Marcar liquidada]
                    ===================================================== */}

                    {reserva.status === 'Quotation' && (
                        <button
                            onClick={() => onStatusChange('Budget')}
                            data-testid="reserva-action-to-budget"
                            className="bg-blue-600 hover:bg-blue-700 text-white px-5 py-2.5 rounded-xl font-bold text-sm shadow-lg shadow-blue-200 dark:shadow-none transition-all active:scale-95"
                            title="Pasar a Presupuesto — el borrador pasa a documento formal para el cliente"
                        >
                            Pasar a presupuesto
                        </button>
                    )}

                    {reserva.status === 'Budget' && (() => {
                        // P2 (ADR-031): el botón se apaga cuando no hay ningún pasajero declarado.
                        // El backend también valida (suma >= 1 en /transition-readiness),
                        // pero bloqueamos en la UI para dar feedback inmediato antes de enviar nada.
                        //
                        // totalPasajerosDeclarados viene del padre (suma adultCount + childCount + infantCount).
                        // Si el padre no lo pasa (null), asumimos que puede avanzar (graceful degradation).
                        const sinPasajeros = totalPasajerosDeclarados !== null && totalPasajerosDeclarados === 0;
                        return (
                            <div className="flex flex-col items-start gap-1">
                                <button
                                    onClick={() => onStatusChange('InManagement')}
                                    disabled={sinPasajeros}
                                    data-testid="reserva-action-client-accepted"
                                    data-disabled-reason={sinPasajeros ? "sin-pasajeros" : undefined}
                                    className={`px-5 py-2.5 rounded-xl font-bold text-sm shadow-lg transition-all active:scale-95 ${
                                        sinPasajeros
                                            ? 'bg-slate-300 dark:bg-slate-700 text-slate-500 dark:text-slate-400 cursor-not-allowed shadow-none'
                                            : 'bg-cyan-600 hover:bg-cyan-700 text-white shadow-cyan-200 dark:shadow-none'
                                    }`}
                                    title={
                                        sinPasajeros
                                            ? "Tiene que haber al menos 1 pasajero declarado"
                                            : "El cliente acepto el presupuesto — empieza la gestion con los operadores"
                                    }
                                >
                                    El cliente acepto
                                </button>
                                {/* Cartelito informativo: solo cuando no hay pasajeros.
                                    No reemplaza el disabled — es ayuda adicional para que el usuario
                                    sepa exactamente qué tiene que hacer antes de poder avanzar. */}
                                {sinPasajeros && (
                                    <p
                                        className="text-xs text-amber-600 dark:text-amber-400 font-medium"
                                        data-testid="reserva-action-client-accepted-hint"
                                    >
                                        Tiene que haber al menos 1 pasajero
                                    </p>
                                )}
                            </div>
                        );
                    })()}

                    {/* En gestion: Confirmada es automatica al resolverse todos los servicios.
                        No hay boton manual de "Confirmar". */}

                    {/* Confirmada: En viaje tambien es automatica (job diario por fecha de salida).
                        No hay boton manual para evitar que el vendedor adelante el estado. */}

                    {reserva.status === 'Traveling' && (
                        <>
                            {/* Cierre directo: solo disponible cuando el viaje ya termino */}
                            {endHasPast && (
                                <button
                                    onClick={() => onStatusChange('Closed')}
                                    disabled={!canClose}
                                    data-testid="reserva-action-finalize-direct"
                                    className={`px-5 py-2.5 rounded-xl font-bold text-sm shadow-lg transition-all active:scale-95 ${canClose ? 'bg-slate-900 dark:bg-white dark:text-slate-900 text-white' : 'bg-slate-300 dark:bg-slate-700 text-slate-500 cursor-not-allowed shadow-none'}`}
                                    title={closeTooltip}
                                >
                                    Cerrar reserva
                                </button>
                            )}
                            {/* Desvio opcional: apartar para liquidar con el operador */}
                            <button
                                onClick={() => onStatusChange('ToSettle')}
                                data-testid="reserva-action-tosettle"
                                className="bg-white text-emerald-700 border border-emerald-300 hover:bg-emerald-50 dark:bg-slate-900 dark:text-emerald-300 dark:border-emerald-700 dark:hover:bg-emerald-900/20 px-5 py-2.5 rounded-xl font-bold text-sm shadow-sm transition-all active:scale-95"
                                title="Apartar para liquidar con el operador (opcional). Queda en la bandeja A liquidar."
                            >
                                Apartar para liquidar
                            </button>
                        </>
                    )}

                    {reserva.status === 'ToSettle' && (
                        <button
                            onClick={() => onStatusChange('Closed')}
                            disabled={!canClose}
                            data-testid="reserva-action-finalize"
                            className={`px-5 py-2.5 rounded-xl font-bold text-sm shadow-lg transition-all active:scale-95 ${canClose ? 'bg-slate-900 dark:bg-white dark:text-slate-900 text-white' : 'bg-slate-300 dark:bg-slate-700 text-slate-500 cursor-not-allowed shadow-none'}`}
                            title={closeTooltip}
                        >
                            Finalizar / Marcar liquidada
                        </button>
                    )}

                    {/* ACCIONES SECUNDARIAS — cada botón muestra icono + palabra siempre visible (spec UX 2026-06-08).
                        El separador border-l solo se muestra en sm: hacia arriba para que no quede raro al envolver en mobile. */}
                    <div className="flex flex-wrap gap-2 ml-2 pl-4 sm:border-l border-slate-200 dark:border-slate-800">

                        {/* Boton "Perdida": discreto, solo desde Cotizacion/Presupuesto */}
                        {showMarkLostButton && (
                            <button
                                onClick={onMarkLost}
                                data-testid="reserva-action-mark-lost"
                                aria-label="Perdida"
                                className="inline-flex items-center gap-1.5 px-3 py-2 bg-slate-100 text-slate-500 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700 rounded-xl transition-colors text-sm font-semibold"
                                title="Marcar como Perdida (el cliente no compro)"
                            >
                                <XCircle className="w-4 h-4" />
                                Perdida
                            </button>
                        )}

                        {/* Boton "Cancelar reserva": proceso fiscal, solo en estados operativos.
                            El title mantiene la advertencia AFIP porque el texto corto "Cancelar" no la comunica. */}
                        {showCancelButton && (
                            <button
                                onClick={onCancelReserva}
                                data-testid="reserva-action-cancel"
                                aria-label="Cancelar reserva"
                                className="inline-flex items-center gap-1.5 px-3 py-2 bg-rose-50 text-rose-700 hover:bg-rose-100 dark:bg-rose-900/20 dark:text-rose-300 rounded-xl transition-colors text-sm font-semibold"
                                title="Cancelar reserva (emite nota de credito en AFIP/ARCA)"
                            >
                                <Ban className="w-4 h-4" />
                                Cancelar
                            </button>
                        )}

                        {/* Reversion de estado: disponible en varios estados */}
                        {canRevert && onRevert && (
                            <button
                                onClick={onRevert}
                                aria-label="Volver atrás"
                                className="inline-flex items-center gap-1.5 px-3 py-2 bg-amber-50 text-amber-700 hover:bg-amber-100 dark:bg-amber-900/20 dark:text-amber-300 rounded-xl transition-colors text-sm font-semibold"
                                title="Revertir estado (requiere autorizacion si la reserva tiene candado)"
                            >
                                <Undo2 className="w-4 h-4" />
                                Volver atrás
                            </button>
                        )}

                        {/* Eliminar: solo en etapas tempranas sin pagos */}
                        {canDelete && (
                            <button
                                onClick={onDelete}
                                aria-label="Eliminar reserva"
                                className="inline-flex items-center gap-1.5 px-3 py-2 bg-rose-50 text-rose-600 hover:bg-rose-100 dark:bg-rose-900/20 dark:text-rose-400 rounded-xl transition-colors text-sm font-semibold"
                            >
                                <Trash2 className="w-4 h-4" />
                                Eliminar
                            </button>
                        )}

                        {/* Archivar: el title conserva el motivo de bloqueo cuando está deshabilitado,
                            porque "Archivar" solo no explica por qué no se puede. */}
                        <button
                            onClick={canArchive ? onArchive : undefined}
                            disabled={!canArchive}
                            aria-label="Archivar reserva"
                            className={`inline-flex items-center gap-1.5 px-3 py-2 rounded-xl transition-colors text-sm font-semibold ${canArchive ? 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700' : 'bg-slate-50 text-slate-300 dark:bg-slate-900 dark:text-slate-700 cursor-not-allowed'}`}
                            title={archiveBlockReason || "Archivar"}
                        >
                            <Archive className="w-4 h-4" />
                            Archivar
                        </button>
                    </div>
                </div>
            )}
        </div>
    );
}
