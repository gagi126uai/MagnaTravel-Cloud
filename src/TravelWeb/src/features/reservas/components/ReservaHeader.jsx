import React from 'react';
import { ArrowLeft, Trash2, Archive, AlertTriangle, Undo2, Calendar, Pencil, Ban, Lock, XCircle, RefreshCw, CornerUpLeft, FastForward } from "lucide-react";
import { getReservaArchiveBlockReason } from "../archiveRules";
import { getStatusConfig, translateStatus, isStatusLocked, isReservaEnEstadoVivo } from "./ReservaStatusBadge";
import { ReservaStatusChips } from "./ReservaStatusChips";
import { isAdmin } from "../../../auth";

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
 *   - Si el backend indica que no se puede eliminar (capability=false), el boton "Eliminar" no aparece.
 *
 * ADR-037 (2026-06-21):
 *   - "Reabrir para facturar" ELIMINADO: la facturacion se desacoplo del estado. Se factura
 *     directo desde Finalizada (boton "Emitir factura" en la solapa Cuenta, gobernado por la
 *     capability canInvoiceSale). Ya no se reabre ni se destraba nada.
 *
 * Feedback visual 2026-06-19 (dueño):
 *   - El boton primario de avance se integra en la fila de acciones (no flota suelto arriba).
 *   - Los botones deshabilitados van GRISES, sin texto de motivo debajo de cada uno.
 *   - "Editar fechas": visible solo cuando canEditReservaData.allowed === true.
 *   - Un ÚNICO cartel de estado (en ReservaDetailPage) explica la restriccion global del estado.
 *
 * Props:
 * - reserva: objeto de la reserva cargada (incluye capabilities si es DTO ADR-035)
 * - canCancelReserva: si el usuario tiene el permiso reservas.cancel
 * - onCancelReserva: callback para abrir el flujo de anulacion en linea
 * - onRequestEdit: callback para abrir el modal de autorizacion de edicion (cuando hay candado)
 * - onMarkLost: callback para abrir el modal "Marcar como perdida"
 * - Los callbacks onStatusChange, onDelete, onArchive, onRevert, onEditDates, onReschedule son manejados por el padre
 * - onReschedule: callback que abre ReprogramarViajeModal; se muestra cuando capabilities.canReschedule.allowed === true (G5, 2026-06-24).
 * - serviciosCancelados: { cancelados: number, totalConProveedor: number } — para el contador "N de M".
 *   El padre lo calcula con calculateServiciosCanceladosResumen(allServices).
 *   Si viene null/undefined no se muestra nada (diseño conservador).
 * - onCorrectTraveling: callback que abre el modal "Sacar de viaje" (solo Admin + Traveling + capability).
 */
export function ReservaHeader({
    reserva,
    onBack,
    onStatusChange,
    onDelete,
    onArchive,
    onRevert,
    onEditDates,
    onReschedule,
    canCancelReserva = false,
    onCancelReserva,
    onRequestEdit,
    onMarkLost,
    serviciosCancelados = null,
    totalPasajerosDeclarados = null,
    onCorrectTraveling,
}) {
    const isArchived = reserva.status === 'Archived';
    const locked = isStatusLocked(reserva.status);

    // ─── ADR-035: leer capabilities del DTO ──────────────────────────────────────
    // Si el backend no manda capabilities (DTO viejo), se cae en undefined y cada botón
    // usa su lógica local como fallback (degradación elegante).
    //
    // IMPORTANTE (fix TDZ 2026-06-22): este const y el helper getCapability van ANTES de
    // la primera llamada a getCapability (canDelete, abajo). Aunque la function declaration
    // está hoisteada y se puede llamar antes, su cuerpo lee `capabilities`, que es un const:
    // usarlo antes de su línea de declaración lanza un TDZ ("Cannot access 'capabilities'
    // before initialization") que en el bundle de producción dejaba la pantalla en blanco al
    // abrir cualquier reserva. Declarar antes de usar lo resuelve de raíz.
    const capabilities = reserva.capabilities;

    // Helper local: extrae { allowed, reason } de un campo de capabilities.
    // Si no hay capabilities, devuelve { allowed: true, reason: null } para no bloquear.
    function getCapability(field) {
        if (!capabilities || !capabilities[field]) return { allowed: true, reason: null };
        return capabilities[field];
    }

    // Solo se puede eliminar en Quotation/Budget (sin pagos, sin servicios resueltos).
    // ADR-036 (punto 5): si el backend manda canDelete.allowed === false (reserva con plata viva),
    // el boton "Eliminar" no aparece aunque la reserva sea temprana — solo se ofrece "Anular".
    const deleteCapability = getCapability('canDelete');
    const canDelete = (reserva.status === 'Quotation' || reserva.status === 'Budget')
        && deleteCapability.allowed;

    const archiveBlockReason = getReservaArchiveBlockReason(reserva);
    const canArchive = !archiveBlockReason;

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

    // ─── Botón "Reprogramar viaje" ────────────────────────────────────────────────
    // G5 (2026-06-24): ahora se gate-a por canReschedule (capability específica del backend),
    // no por canEditServices. canReschedule=true solo en {Confirmada, En viaje}.
    // En pre-venta (Quotation/Budget) y en estados terminales = false.
    // Fallback a canEditServices si el backend aún no manda canReschedule (DTO viejo).
    // "Reprogramar" es diferente de "Editar fechas":
    //   - "Editar fechas" overridea la cabecera de la reserva (manual, fecha a fecha).
    //   - "Reprogramar" mueve TODOS los servicios el mismo delta desde una nueva salida.
    // Se oculta en estados archivados — la reserva archivada es historial.
    const rescheduleCap = capabilities?.canReschedule ?? getCapability('canEditServices');
    const showRescheduleButton = rescheduleCap.allowed && !isArchived && typeof onReschedule === 'function';

    // ─── Guarda "En viaje" = inmutable ───────────────────────────────────────────
    // Guía UX 2026-06-22: en Traveling la reserva es inmutable por diseño (no por candado
    // destrababl). Los botones "Volver atrás", "Archivar" y "Anular" no se muestran.
    // Experto ERP confirmado: un documento in-transit no se des-confirma con un botón libre.
    // Nota: el backend ya devuelve canCancel.allowed=false y allowedRevert=[] para Traveling,
    // pero agregamos una guarda defensiva en el front para el caso de DTO viejo.
    const esTraveling = reserva.status === 'Traveling';

    // ─── Boton "Anular reserva" ─────────────────────────────────────────────────
    // F4-2 (2026-06-26): el botón lee `canAnnul` como capacidad PRIMARIA.
    //   canAnnul.allowed=true  → reserva con plata viva (factura con CAE o cobros).
    //                             CancelarReservaInline emite NC formal.
    //   canCancel.allowed=true → baja simple sin documentos fiscales vivos (PreSale/DirectCancel).
    //                            Mismo botón, distinto camino dentro de CancelarReservaInline.
    //   Ambas false → botón gris (ADR-035: siempre visible si el usuario tiene permiso,
    //                             apagado cuando ninguna capacidad lo permite).
    //
    // ADR-036: "anular" = deshacer el viaje. "Cancelar" = saldar deuda (otro concepto).
    // Guía UX 2026-06-22: ocultar en Traveling (en viaje no se anula).
    // Feedback 2026-06-19: SIN texto de motivo debajo, solo gris.
    const CANCELLABLE_STATUSES_FALLBACK = ['InManagement', 'Confirmed'];
    const annulCapability = getCapability('canAnnul');
    const cancelCapability = getCapability('canCancel');
    // Botón habilitado cuando CUALQUIERA de las dos capacidades lo permite.
    const puedeAnular = annulCapability.allowed || cancelCapability.allowed;

    // F4-2 fix (2026-06-26): ocultar "Anular reserva" en pre-venta (Quotation/Budget).
    // En esos estados el botón "Perdida (⊗)" cubre el camino natural — el cliente no compró.
    // "Anular" es solo para reservas en firme (con servicios, cobros o factura viva).
    // Sin esto, canCancel.allowed=true en pre-venta hacía que el botón quedara habilitado ahí también.
    const isPreSale = reserva.status === 'Quotation' || reserva.status === 'Budget';

    const showCancelButton = !isPreSale && !esTraveling && canCancelReserva && onCancelReserva && !isArchived && (
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
    // Guía UX 2026-06-22: guarda defensiva para Traveling — el backend ya devuelve
    // allowedRevert=[], pero si por algún bug mandara algo, lo ignoramos igual.
    const canRevertLocal = ['Budget', 'InManagement', 'Confirmed', 'Closed', 'Lost'].includes(reserva.status);
    const canRevert = !esTraveling && (
        capabilities
            ? (Array.isArray(capabilities.allowedRevert) && capabilities.allowedRevert.length > 0)
            : canRevertLocal
    );

    // ─── Botón "Sacar de viaje" (corrección de entrada errónea) ──────────────────
    // Spec UX 2026-06-22 "Tanda 2": acción de EXCEPCIÓN solo para Admin.
    // Se renderiza SOLAMENTE si se cumplen LAS TRES condiciones:
    //   1) La reserva está En viaje (Traveling)
    //   2) El backend lo permite: canCorrectTravelingEntry.allowed === true
    //      (solo llega true si no hay factura viva ni voucher vivo)
    //   3) El usuario es Admin (isAdmin() del store de auth)
    // Si falta cualquiera de las tres → NO se renderiza (ni gris, ni mensaje).
    // El botón va discreto y SEPARADO de los botones normales (no en la fila principal).
    const correctTravelingCapability = getCapability('canCorrectTravelingEntry');
    const showCorrectTravelingButton =
        esTraveling &&
        correctTravelingCapability.allowed === true &&
        isAdmin() &&
        typeof onCorrectTraveling === 'function';

    // ADR-037: el botón "Reabrir para facturar" fue eliminado. La facturación se desacopló
    // del estado de la reserva: se factura directo desde Finalizada (sin reabrir). El botón
    // "Facturar" se gobierna por la capability del backend (canInvoiceSale), no por el estado.

    const startLabel = formatTripDate(reserva.startDate);
    const endLabel = formatTripDate(reserva.endDate);

    // Gate de cierre: el viaje tiene que haber terminado y no quedar saldo.
    //
    // Fix C5 (Tanda 6, 2026-07-05): antes solo miraba reserva.balance (el escalar), que en
    // una reserva MULTIMONEDA suma ARS + USD — podía dar ~0 y dejar cerrar la reserva aunque
    // quedara deuda real en una sola moneda (ej: debe USD 500 pero tiene a favor un ARS
    // equivalente). Ahora exige que TODAS las líneas de porMoneda estén saldadas o a favor.
    // Fallback al escalar si el DTO no trae porMoneda (reserva vieja sin filas materializadas).
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const endHasPast = reserva.endDate ? new Date(reserva.endDate) < today : false;
    const todasLasMonedasSaldadas = Array.isArray(reserva.porMoneda) && reserva.porMoneda.length > 0
        ? reserva.porMoneda.every((linea) => (linea.balance ?? 0) <= 0)
        : (reserva.balance ?? 0) <= 0;
    const canClose = endHasPast && todasLasMonedasSaldadas;
    const closeTooltip = !endHasPast
        ? "El viaje todavia no termino"
        : !todasLasMonedasSaldadas
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
                            en una reserva viva y el dueño todavía no acusó el cambio.

                            Bug fix 2026-07-03: el flag hasUnacknowledgedChanges puede quedar en
                            true por error del backend en reservas Anuladas / Esperando reembolso.
                            Exigimos ademas que el estado sea "vivo" para no mostrar la etiqueta
                            sobre un viaje que ya quedo sin efecto. */}
                        {reserva.hasUnacknowledgedChanges && isReservaEnEstadoVivo(reserva.status) && (
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

                {/* Contador "N de M servicios anulados" (ADR-025 DT.3.1 decision #1).
                    Solo aparece cuando hay AL MENOS UN servicio anulado.
                    Vocabulario del negocio (2026-07-16): "anular" = dejar sin efecto; "cancelar" = el cliente abona el total.
                    El dato viene del padre, calculado con calculateServiciosCanceladosResumen(allServices). */}
                {serviciosCancelados && serviciosCancelados.cancelados > 0 && (
                    <p
                        className="mt-1 text-xs text-slate-400 dark:text-slate-500"
                        data-testid="contador-servicios-cancelados"
                    >
                        {serviciosCancelados.cancelados} de {serviciosCancelados.totalConProveedor}{' '}
                        {serviciosCancelados.totalConProveedor === 1 ? 'servicio anulado' : 'servicios anulados'}
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

                    {/* "Reprogramar viaje": mueve TODAS las fechas de los servicios desde una nueva fecha de salida.
                        Distinto de "Editar fechas" (override de cabecera): este corre todo el viaje en bloque.
                        Visible cuando canEditServices.allowed=true → el backend sabe si la reserva es editable. */}
                    {showRescheduleButton && (
                        <button
                            onClick={onReschedule}
                            type="button"
                            data-testid="reserva-action-reschedule"
                            className="inline-flex items-center gap-1.5 rounded-lg border border-indigo-200 bg-indigo-50 px-2.5 py-1.5 text-xs font-semibold text-indigo-700 hover:bg-indigo-100 hover:border-indigo-300 dark:border-indigo-800 dark:bg-indigo-950/30 dark:text-indigo-300 dark:hover:bg-indigo-900/50"
                            title="Reprogramar viaje — mueve todas las fechas de los servicios"
                        >
                            <FastForward className="w-3.5 h-3.5" />
                            Reprogramar viaje
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

                        {/* ── Boton "Anular reserva" ──────────────────────────────────────────────
                            F4-2 (2026-06-26): habilitado cuando canAnnul.allowed OR canCancel.allowed.
                            En gris (disabled) solo cuando NINGUNA de las dos lo permite.
                            ADR-035: SIEMPRE VISIBLE si el usuario tiene permiso (reservas.cancel).
                            Feedback 2026-06-19: SIN texto de motivo debajo, solo gris.
                            El cartel único en ReservaDetailPage explica el estado global. */}
                        {showCancelButton && (
                            <button
                                onClick={puedeAnular ? onCancelReserva : undefined}
                                disabled={!puedeAnular}
                                data-testid="btn-anular-reserva"
                                aria-label="Anular reserva"
                                className={`inline-flex items-center gap-1.5 px-3 py-2.5 rounded-xl transition-colors text-sm font-semibold ${
                                    puedeAnular
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
                            El cartel único en ReservaDetailPage explica el estado global.
                            Guía UX 2026-06-22: ocultar en Traveling — archivar es para estados
                            terminales (Finalizada/Perdida/Anulada), no para algo en curso. */}
                        {!esTraveling && (
                            <button
                                onClick={canArchive ? onArchive : undefined}
                                disabled={!canArchive}
                                aria-label="Archivar reserva"
                                className={`inline-flex items-center gap-1.5 px-3 py-2.5 rounded-xl transition-colors text-sm font-semibold ${canArchive ? 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700' : 'bg-slate-50 text-slate-300 dark:bg-slate-900 dark:text-slate-700 cursor-not-allowed'}`}
                            >
                                <Archive className="w-4 h-4" />
                                Archivar
                            </button>
                        )}
                    </div>

                    {/* ── "Sacar de viaje" — separado de la botonera normal ───────────────
                        Acción de EXCEPCIÓN (spec UX 2026-06-22 "Tanda 2"):
                        - Solo para Admin, solo En viaje, solo si no hay factura ni voucher vivo.
                        - Va discreto: separador visual + estilo terciario gris sobrio.
                        - NO se muestra gris/deshabilitado si no cumple las condiciones: directamente no aparece.
                        - Al click abre el modal CorregirEntradaViajeModal (NO ejecuta directo). */}
                    {showCorrectTravelingButton && (
                        <div className="sm:border-l sm:border-slate-200 sm:dark:border-slate-800 sm:pl-4">
                            <button
                                onClick={onCorrectTraveling}
                                data-testid="reserva-action-correct-traveling"
                                aria-label="Sacar de viaje — corrección de entrada errónea"
                                className="inline-flex items-center gap-1.5 px-3 py-2.5 bg-slate-100 text-slate-500 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700 rounded-xl transition-colors text-sm font-semibold"
                                title="Acción de corrección — solo si la reserva entró en viaje por error"
                            >
                                <CornerUpLeft className="w-4 h-4" />
                                Sacar de viaje
                            </button>
                        </div>
                    )}
                </div>
            )}
        </div>
    );
}
