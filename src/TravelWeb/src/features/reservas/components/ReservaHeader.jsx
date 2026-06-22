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
 *   Cualquier etapa activa → [Anular] (con proceso fiscal)
 *   Quotation/Budget → [Perdido] (discreto, no hubo compra)
 *
 * ADR-036 (2026-06-21):
 *   - "Apartar para liquidar" (Traveling→ToSettle) ELIMINADO: ya no existe "A liquidar".
 *   - "Finalizar / Marcar liquidada" (ToSettle→Closed) ELIMINADO: idem.
 *   - El boton que antes decia "Cancelar" ahora dice "Anular" (anular = deshacer el viaje).
 *   - "Reabrir para facturar" destraba la Finalizada SIN cambiarla de estado (ya no manda a ToSettle).
 *   - Si el backend indica que no se puede eliminar (capability=false), el boton "Eliminar" no aparece.
 *
 * Feedback visual 2026-06-19 (dueño):
 *   - El boton primario de avance se integra en la fila de acciones (no flota suelto arriba).
 *   - Los botones deshabilitados van GRISES, sin texto de motivo debajo de cada uno.
 *   - "Editar fechas": visible solo cuando canEditReservaData.allowed === true.
 *   - "Reabrir para facturar": solo en Closed + sin factura (requiresInvoiceAnnulmentToCancel=false).
 *   - Un ÚNICO cartel de estado (en ReservaDetailPage) explica la restriccion global del estado.
 *
 * Props:
 * - reserva: objeto de la reserva cargada (incluye capabilities si es DTO ADR-035)
 * - canCancelReserva: si el usuario tiene el permiso reservas.cancel
 * - onCancelReserva: callback para abrir el flujo de anulacion en linea
 * - onRequestEdit: callback para abrir el modal de autorizacion de edicion (cuando hay candado)
 * - onMarkLost: callback para abrir el modal "Marcar como perdida"
 * - onReopenToSettle: callback para reabrir la reserva Finalizada para facturar (ADR-036)
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
    // ADR-036 (punto 5): si el backend manda canDelete.allowed === false (reserva con plata viva),
    // el boton "Eliminar" no aparece aunque la reserva sea temprana — solo se ofrece "Anular".
    const deleteCapability = getCapability('canDelete');
    const canDelete = (reserva.status === 'Quotation' || reserva.status === 'Budget')
        && deleteCapability.allowed;

    const archiveBlockReason = getReservaArchiveBlockReason(reserva);
    const canArchive = !archiveBlockReason;

    // ─── ADR-035: leer capabilities del DTO ──────────────────────────────────────
    // Si el backend no manda capabilities (DTO viejo), se cae en undefined y cada botón
    // usa su lógica local como fallback (degradación elegante).
    const capabilities = reserva.capabilities;

    // Helper local: extrae { allowed, reason } de un campo de capabilities.
    // Si no hay capabilities, devuelve { allowed: true, reason: null } para no bloquear.
    function getCapability(field) {
        if (!capabilities || !capabilities[field]) return { allowed: true, reason: null };
        return capabilities[field];
    }

    // ─── Botón "Editar fechas" ────────────────────────────────────────────────────
    // Feedback 2026-06-19: se oculta cuando canEditReservaData.allowed === false.
    // En estados terminales (Lost, Cancelled, Closed) el backend manda allowed=false.
    // Fallback (sin capabilities): lógica local por estado.
    const editReservaDataCap = getCapability('canEditReservaData');
    const canEditDates = editReservaDataCap.allowed
        // Fallback defensivo si el campo no vino: estados activos clásicos
        && !isArchived
        && reserva.status !== 'Cancelled'
        && reserva.status !== 'Lost'
        && reserva.status !== 'Closed';

    // ─── Boton "Anular reserva" (antes "Cancelar") ────────────────────────────────
    // ADR-035: siempre visible si hay permiso de usuario; apagado (gris) si capabilities.canCancel.allowed=false.
    // ADR-036: el boton dice "Anular" porque en este producto "anular" = deshacer el viaje.
    // "Cancelar" significa saldar una deuda — se reserva para botones de descarte de formularios.
    // Feedback 2026-06-19: SIN texto de motivo debajo, solo gris.
    // ADR-036: ToSettle eliminado del fallback (ya no existe ese estado en la UI).
    const CANCELLABLE_STATUSES_FALLBACK = ['InManagement', 'Confirmed', 'Traveling'];
    const cancelCapability = getCapability('canCancel');
    const showCancelButton = canCancelReserva && onCancelReserva && !isArchived && (
        capabilities
            ? true
            : CANCELLABLE_STATUSES_FALLBACK.includes(reserva.status)
    );

    // ─── Boton "Perdido" ─────────────────────────────────────────────────────────
    // "Perdido": solo desde Quotation o Budget (cuando el cliente no compro).
    const showMarkLostButton = ['Quotation', 'Budget'].includes(reserva.status)
        && !isArchived
        && onMarkLost;

    // ─── Reversion de estado ──────────────────────────────────────────────────────
    // ADR-036: ToSettle eliminado del fallback de reversion.
    const canRevertLocal = ['Budget', 'InManagement', 'Confirmed', 'Traveling', 'Closed', 'Lost'].includes(reserva.status);
    const canRevert = capabilities
        ? (Array.isArray(capabilities.allowedRevert) && capabilities.allowedRevert.length > 0)
        : canRevertLocal;

    // ADR-037: el botón "Reabrir para facturar" fue eliminado. La facturación se desacopló
    // del estado de la reserva: se factura directo desde Finalizada (sin reabrir). El botón
    // "Facturar" se gobierna por la capability del backend (canInvoiceSale), no por el estado.

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
                    {/* Chips de pago (Pagada / Saldo pendiente / En curso / Vencida).
                        Se muestran con tamaño más pequeño para que no compitan visualmente
                        con el badge de estado operativo de arriba (cambio 6 feedback 2026-06-19). */}
                    <ReservaStatusChips reserva={reserva} />
                </div>

                {/* Contador "N de M servicios cancelados" (ADR-025 DT.3.1 decision #1).
                    Solo aparece cuando hay AL MENOS UN servicio cancelado.
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
                    {/* "Editar fechas": visible solo cuando canEditReservaData.allowed === true.
                        Feedback 2026-06-19: en estados terminales (Lost/Cancelled/Closed) se oculta,
                        no se deshabilita, porque la reserva está en solo-lectura visual completa. */}
                    {canEditDates && onEditDates && (
                        <button
                            onClick={onEditDates}
                            type="button"
                            data-testid="reserva-action-edit-dates"
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
                /*
                  Feedback 2026-06-19: todos los botones van en UNA SOLA FILA (flex-wrap).
                  El botón primario de avance de etapa (ej. "El cliente aceptó") va PRIMERO,
                  con color lleno (primario). Las acciones secundarias (Cancelar, Volver, etc.)
                  van después, separadas por un border-l en sm: hacia arriba.
                  NO hay bloques flotantes sueltos arriba de la fila.
                  Los botones deshabilitados van grises sobrios SIN texto de motivo debajo.
                */
                <div className="flex flex-wrap items-center gap-2">

                    {/* =====================================================
                        BOTON PRIMARIO DE AVANCE — va PRIMERO en la fila
                        Quotation → [Pasar a presupuesto]
                        Budget    → [El cliente acepto]
                        Traveling → [Cerrar reserva]
                        ADR-036: "Apartar para liquidar" y "Finalizar/Marcar liquidada"
                                  eliminados (ya no existe "A liquidar").
                    ===================================================== */}

                    {reserva.status === 'Quotation' && (
                        <button
                            onClick={() => onStatusChange('Budget')}
                            data-testid="reserva-action-to-budget"
                            className="bg-blue-600 hover:bg-blue-700 text-white px-5 py-2.5 rounded-xl font-bold text-sm shadow-sm transition-all active:scale-95"
                            title="Pasar a Presupuesto — el borrador pasa a documento formal para el cliente"
                        >
                            Pasar a presupuesto
                        </button>
                    )}

                    {reserva.status === 'Budget' && (() => {
                        // P2 (ADR-031): el botón se apaga cuando no hay ningún pasajero declarado.
                        // totalPasajerosDeclarados viene del padre (suma adultCount + childCount + infantCount).
                        // Si el padre no lo pasa (null), asumimos que puede avanzar (graceful degradation).
                        const sinPasajeros = totalPasajerosDeclarados !== null && totalPasajerosDeclarados === 0;
                        return (
                            <>
                                <button
                                    onClick={() => onStatusChange('InManagement')}
                                    disabled={sinPasajeros}
                                    data-testid="reserva-action-client-accepted"
                                    data-disabled-reason={sinPasajeros ? "sin-pasajeros" : undefined}
                                    className={`px-5 py-2.5 rounded-xl font-bold text-sm shadow-sm transition-all active:scale-95 ${
                                        sinPasajeros
                                            ? 'bg-slate-300 dark:bg-slate-700 text-slate-500 dark:text-slate-400 cursor-not-allowed shadow-none'
                                            : 'bg-cyan-600 hover:bg-cyan-700 text-white'
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
                                    Feedback 2026-06-19: este cartelito bajo el BOTÓN PRIMARIO está
                                    permitido porque explica un requisito previo (no un bloqueo del estado).
                                    Los carteles de motivo de OTROS botones (Cancelar, Archivar) sí se eliminaron. */}
                                {sinPasajeros && (
                                    <p
                                        className="text-xs text-amber-600 dark:text-amber-400 font-medium"
                                        data-testid="reserva-action-client-accepted-hint"
                                    >
                                        Tiene que haber al menos 1 pasajero
                                    </p>
                                )}
                            </>
                        );
                    })()}

                    {/* En gestion: Confirmada es automatica al resolverse todos los servicios. */}
                    {/* Confirmada: En viaje tambien es automatica (job diario por fecha de salida). */}

                    {/* ADR-036: solo el boton "Cerrar reserva" en Traveling.
                        "Apartar para liquidar" y "Finalizar / Marcar liquidada" fueron eliminados
                        porque "A liquidar" ya no existe como estado. */}
                    {reserva.status === 'Traveling' && endHasPast && (
                        <button
                            onClick={() => onStatusChange('Closed')}
                            disabled={!canClose}
                            data-testid="reserva-action-finalize-direct"
                            className={`px-5 py-2.5 rounded-xl font-bold text-sm shadow-sm transition-all active:scale-95 ${canClose ? 'bg-slate-900 dark:bg-white dark:text-slate-900 text-white' : 'bg-slate-300 dark:bg-slate-700 text-slate-500 cursor-not-allowed shadow-none'}`}
                            title={closeTooltip}
                        >
                            Cerrar reserva
                        </button>
                    )}

                    {/* ACCIONES SECUNDARIAS — Separador visual en sm: hacia arriba.
                        Feedback 2026-06-19: botones deshabilitados = solo gris, sin texto debajo.
                        Todos los botones tienen la misma altura/padding que las acciones primarias. */}
                    <div className="flex flex-wrap gap-2 sm:border-l sm:border-slate-200 sm:dark:border-slate-800 sm:pl-4">

                        {/* Boton "Perdida": discreto, solo desde Cotizacion/Presupuesto */}
                        {showMarkLostButton && (
                            <button
                                onClick={onMarkLost}
                                data-testid="reserva-action-mark-lost"
                                aria-label="Perdida"
                                className="inline-flex items-center gap-1.5 px-3 py-2.5 bg-slate-100 text-slate-500 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700 rounded-xl transition-colors text-sm font-semibold"
                            >
                                <XCircle className="w-4 h-4" />
                                Perdida
                            </button>
                        )}

                        {/* ── Boton "Anular reserva" (antes "Cancelar") ─────────────────────────────
                            ADR-036: "Anular" = deshacer el viaje con proceso fiscal.
                            "Cancelar" en este producto significa saldar una deuda — NO se usa acá.
                            SIEMPRE VISIBLE si el usuario tiene permiso.
                            Feedback 2026-06-19: si no está permitido, solo gris (sin texto debajo).
                            El cartel único en ReservaDetailPage explica el motivo global. */}
                        {showCancelButton && (
                            <button
                                onClick={cancelCapability.allowed ? onCancelReserva : undefined}
                                disabled={!cancelCapability.allowed}
                                data-testid="reserva-action-cancel"
                                aria-label="Anular reserva"
                                className={`inline-flex items-center gap-1.5 px-3 py-2.5 rounded-xl transition-colors text-sm font-semibold ${
                                    cancelCapability.allowed
                                        ? 'bg-rose-50 text-rose-700 hover:bg-rose-100 dark:bg-rose-900/20 dark:text-rose-300'
                                        : 'bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-600 cursor-not-allowed'
                                }`}
                            >
                                <Ban className="w-4 h-4" />
                                Anular
                            </button>
                        )}

                        {/* ── Reversion de estado ──────────────────────────────────────────────── */}
                        {canRevert && onRevert && (
                            <button
                                onClick={onRevert}
                                aria-label="Volver atrás"
                                className="inline-flex items-center gap-1.5 px-3 py-2.5 bg-amber-50 text-amber-700 hover:bg-amber-100 dark:bg-amber-900/20 dark:text-amber-300 rounded-xl transition-colors text-sm font-semibold"
                            >
                                <Undo2 className="w-4 h-4" />
                                Volver atrás
                            </button>
                        )}

                        {/* ADR-037: el botón "Reabrir para facturar" fue ELIMINADO.
                            La facturación se desacopló del estado: ahora se factura directo desde
                            Finalizada (y desde Confirmada/En viaje) sin reabrir ni destrabar nada.
                            El botón "Facturar" se habilita por capability del backend. */}

                        {/* Eliminar: solo en etapas tempranas sin pagos */}
                        {canDelete && (
                            <button
                                onClick={onDelete}
                                aria-label="Eliminar reserva"
                                className="inline-flex items-center gap-1.5 px-3 py-2.5 bg-rose-50 text-rose-600 hover:bg-rose-100 dark:bg-rose-900/20 dark:text-rose-400 rounded-xl transition-colors text-sm font-semibold"
                            >
                                <Trash2 className="w-4 h-4" />
                                Eliminar
                            </button>
                        )}

                        {/* Archivar: botón siempre gris cuando no puede.
                            Feedback 2026-06-19: SIN texto de motivo debajo.
                            El cartel único en ReservaDetailPage explica el estado global. */}
                        <button
                            onClick={canArchive ? onArchive : undefined}
                            disabled={!canArchive}
                            aria-label="Archivar reserva"
                            className={`inline-flex items-center gap-1.5 px-3 py-2.5 rounded-xl transition-colors text-sm font-semibold ${canArchive ? 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700' : 'bg-slate-50 text-slate-300 dark:bg-slate-900 dark:text-slate-700 cursor-not-allowed'}`}
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
