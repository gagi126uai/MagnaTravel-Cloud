import React, { useState, useEffect, useRef } from 'react';
import { ArrowLeft, AlertTriangle, Undo2, Pencil, Lock, XCircle, RefreshCw, CornerUpLeft, FastForward, MoreHorizontal } from "lucide-react";
import { getReservaArchiveBlockReason } from "../archiveRules";
import { isReservaEnEstadoVivo, tieneCandadoDeEdicionActivo, ReservaStatusBadge } from "./ReservaStatusBadge";
import { ReservaStatusChips } from "./ReservaStatusChips";
import { isAdmin } from "../../../auth";
import { faltaTitularConNombre } from "../lib/pasajeroHint";
import { isReservaAnulada } from "../moneyStatus";
import { armarLineaDestinoYPasajeros } from "../lib/reservaDestinoFicha";

// Bug "fechas corridas un día" (2026-07-16): startDate/endDate de la reserva son
// fechas-solo-día (el usuario elige un día calendario, no una hora). El backend las
// guarda como medianoche UTC ("...T00:00:00Z"). Si las pasamos por new Date(value)
// y pedimos el día en hora LOCAL (UTC-3), la medianoche UTC del 23/05 cae a las
// 21:00 del 22/05 en Argentina y el usuario ve "22/05/2026" en vez de "23/05/2026".
// Por eso leemos el día/mes/año directo del texto (string-split), igual que
// MonthNavigator y ReprogramarViajeModal — nunca pasamos por new Date() para esto.
function formatTripDate(value) {
    if (!value) return null;
    const soloFecha = String(value).split("T")[0];
    // Validacion numerica estricta (mismo criterio que la formatDate central):
    // un valor que no sea yyyy-MM-dd de verdad devuelve null, jamas texto basura.
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(soloFecha);
    if (!match) return null;
    const [, anio, mes, dia] = match;
    return `${dia}/${mes}/${anio}`;
}

/**
 * Menú "⋯" con las acciones de EXCEPCIÓN de la ficha (regla P9, Tanda 2 del
 * rediseño de Reservas, 2026-08-03): "Volver atrás", "Destrabar reserva" y
 * "Sacar de viaje" son correcciones de último recurso (piden motivo, quedan
 * en el historial) — no acciones de todos los días, así que dejan de competir
 * en la fila principal de botones y pasan detrás de este menú desplegable.
 *
 * No inventa un patrón de dropdown nuevo: usa el mismo "click afuera cierra"
 * que ya tiene NotificationBell.jsx (useRef + listener de mousedown).
 *
 * Si no hay ningún item para mostrar, no se renderiza nada (ni el botón "⋯").
 */
function MenuAccionesExcepcion({ items }) {
    const [abierto, setAbierto] = useState(false);
    const contenedorRef = useRef(null);
    const triggerRef = useRef(null);

    // Cierra el menú al hacer click afuera, y con Escape (devolviendo el foco al
    // botón "⋯", como el resto de los desplegables de la app — review Tanda 2).
    useEffect(() => {
        function alClickearAfuera(evento) {
            if (contenedorRef.current && !contenedorRef.current.contains(evento.target)) {
                setAbierto(false);
            }
        }
        function alApretarTecla(evento) {
            if (evento.key === 'Escape') {
                setAbierto(false);
                triggerRef.current?.focus();
            }
        }
        document.addEventListener('mousedown', alClickearAfuera);
        document.addEventListener('keydown', alApretarTecla);
        return () => {
            document.removeEventListener('mousedown', alClickearAfuera);
            document.removeEventListener('keydown', alApretarTecla);
        };
    }, []);

    if (!items || items.length === 0) return null;

    return (
        <div className="relative" ref={contenedorRef}>
            <button
                type="button"
                ref={triggerRef}
                onClick={() => setAbierto((previo) => !previo)}
                aria-haspopup="menu"
                aria-expanded={abierto}
                aria-label="Más acciones"
                data-testid="reserva-menu-excepciones-trigger"
                className="inline-flex items-center justify-center rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-slate-500 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-400 dark:hover:bg-slate-800"
            >
                <MoreHorizontal className="h-4 w-4" aria-hidden="true" />
            </button>
            {abierto && (
                <div
                    role="menu"
                    data-testid="reserva-menu-excepciones"
                    className="absolute right-0 top-full z-20 mt-1 w-56 rounded-xl border border-slate-200 bg-white p-1.5 text-sm shadow-lg dark:border-slate-700 dark:bg-slate-900"
                >
                    {items.map((item) => (
                        <button
                            key={item.key}
                            type="button"
                            role="menuitem"
                            data-testid={item.testId}
                            onClick={() => {
                                setAbierto(false);
                                item.onClick();
                            }}
                            className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-slate-600 transition-colors hover:bg-slate-50 dark:text-slate-300 dark:hover:bg-slate-800"
                        >
                            {item.icon}
                            {item.label}
                        </button>
                    ))}
                </div>
            )}
        </div>
    );
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
 *   Cualquier etapa activa → [Anular reserva] (con proceso fiscal)
 *   Quotation/Budget → [Perdida] (discreto, no hubo compra)
 *
 * ADR-036 (2026-06-21):
 *   - "Apartar para liquidar" (Traveling→ToSettle) ELIMINADO: ya no existe "A liquidar".
 *   - "Finalizar / Marcar liquidada" (ToSettle→Closed) ELIMINADO: idem.
 *   - El boton que antes decia "Cancelar" ahora dice "Anular reserva" (anular = deshacer el
 *     viaje; texto aclarado 2026-07-22 — P2 firmado por Gaston, distingue este botón del de
 *     "Anular servicio" por fila y "Anular varios servicios" de la lista).
 *   - El botón "Eliminar reserva" fue SACADO de esta cabecera (Tanda 2 del rediseño,
 *     2026-08-03, regla absoluta del dueño: nada de negocio se borra — se anula o se
 *     archiva). El endpoint sigue existiendo en el hook, pero ya no hay puerta en la UI.
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
 * - Los callbacks onStatusChange, onArchive, onRevert, onEditDates, onReschedule son manejados por el padre
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
    onArchive,
    onRevert,
    onEditDates,
    onReschedule,
    canCancelReserva = false,
    onCancelReserva,
    onRequestEdit,
    onMarkLost,
    serviciosCancelados = null,
    onCorrectTraveling,
    // P8 (Tanda 3 del rediseño, 2026-08-03): callback que lleva a la solapa Pasajeros.
    // Lo usa el enlace "Cargar el titular →" de acá abajo — resuelve el "callejón sin
    // salida" que tenía el Presupuesto (el motivo del botón apagado no llevaba a ningún
    // lado porque esa solapa todavía no existía en esa etapa).
    onIrAPasajeros,
}) {
    const isArchived = reserva.status === 'Archived';

    // ─── Candado C1 (spec UX 2026-07-22) ─────────────────────────────────────────
    // Reserva Confirmada SIN autorización de edición viva: los botones de edición de
    // esta cabecera (Editar fechas, Reprogramar viaje) se muestran "gris + candadito"
    // en vez de encendidos. Al tocarlos, en vez de disparar la acción, abren la misma
    // ventana de destrabar que ya usa la franja ámbar (onRequestEdit → EditAuthorizationModal).
    const candadoDeEdicionActivo = tieneCandadoDeEdicionActivo(reserva);

    // ─── ADR-035: leer capabilities del DTO ──────────────────────────────────────
    // Si el backend no manda capabilities (DTO viejo), se cae en undefined y cada botón
    // usa su lógica local como fallback (degradación elegante).
    //
    // IMPORTANTE (fix TDZ 2026-06-22): este const y el helper getCapability van ANTES de
    // la primera llamada a getCapability (canEditReservaData, abajo). Aunque la function
    // declaration está hoisteada y se puede llamar antes, su cuerpo lee `capabilities`, que es un const:
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

    // Regla absoluta del dueño (firmada 2026-08-03, Tanda 2 del rediseño): nada de
    // negocio se borra, ni reservas ni presupuestos — se ANULAN (queda rastro) o se
    // ARCHIVAN. El botón "Eliminar reserva" que existía acá era un sobrante de antes
    // de esa regla y se saca de la UI. El endpoint/hook (handleDeleteReserva) no se
    // toca — esto es solo la puerta de la pantalla, no el motor.

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

    // P9 (Tanda 2 del rediseño, 2026-08-03): en una reserva ya Anulada o Perdida no
    // tiene sentido ofrecer "Anular reserva" de nuevo — antes quedaba gris/encendido
    // sobre una pantalla que ya dice "solo lectura" (bug reportado en la maqueta
    // firmada). Guarda defensiva en el front además de lo que ya decida el backend
    // en canAnnul/canCancel.
    const esAnuladaOPerdida = isReservaAnulada(reserva) || reserva.status === 'Lost';

    // Fix P14 (review Tanda 2, 2026-08-04): el "Anular" duplicado de Estado de Cuenta se
    // eliminó, así que este botón pasó a ser el ÚNICO acceso. El ocultamiento de pre-venta
    // (F4-2) cede cuando el motor dice explícitamente canAnnul.allowed=true: una pre-venta
    // puede tener plata viva (cobro o factura con CAE tras un "Volver atrás") y esa reserva
    // se ANULA con rastro — jamás puede quedar sin salida ("nada se borra", regla absoluta).
    const preSaleSinPlataViva = isPreSale && annulCapability.allowed !== true;
    const showCancelButton = !preSaleSinPlataViva && !esTraveling && !esAnuladaOPerdida && canCancelReserva && onCancelReserva && !isArchived && (
        capabilities
            ? true
            : CANCELLABLE_STATUSES_FALLBACK.includes(reserva.status)
    );

    // ─── Boton "Perdida" ─────────────────────────────────────────────────────────
    // "Perdida": solo desde Quotation o Budget (cuando el cliente no compró).
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

    // ─── Menú "⋯" de acciones de excepción (P9, Tanda 2 del rediseño) ───────────
    // "Destrabar reserva": mismo botón que ya ofrece la franja ámbar del candado
    // (ReservaLockBanner) — acá se agrega como atajo directo desde el encabezado.
    // Misma condición EXACTA que esa franja (reserva.status === 'Confirmed'), para
    // no ofrecer "destrabar" en Traveling/Closed, donde ese flujo no aplica.
    const showDestrabarMenuItem = reserva.status === 'Confirmed'
        && candadoDeEdicionActivo
        && typeof onRequestEdit === 'function';

    // Decisión de Gastón (2026-08-04, resolvió el choque entre la maqueta sección 11
    // "botonera = solo Archivar en Anulada" y ADR-050 "una anulación por error se puede
    // deshacer"): en ANULADA la botonera queda limpia como la maqueta, pero el "⋯" sigue
    // existiendo con UNA sola opción adentro, "Deshacer anulación" (mismo camino de
    // reversión del motor). En PERDIDA sí queda solo Archivar (no hay anulación que
    // deshacer). El rótulo cambia según el caso: acción de todos los días no es —
    // "Volver atrás" en vivas, "Deshacer anulación" en anuladas.
    const esAnulada = reserva.status === 'Cancelled' || reserva.status === 'PendingOperatorRefund';
    const menuAccionesExcepcion = [];
    if (canRevert && onRevert && (!esAnuladaOPerdida || esAnulada)) {
        menuAccionesExcepcion.push({
            key: 'volver-atras',
            label: esAnulada ? 'Deshacer anulación' : 'Volver atrás',
            icon: <Undo2 className="h-4 w-4" aria-hidden="true" />,
            onClick: onRevert,
            testId: 'reserva-menu-volver-atras',
        });
    }
    if (showDestrabarMenuItem) {
        menuAccionesExcepcion.push({
            key: 'destrabar',
            label: 'Destrabar reserva',
            icon: <Lock className="h-4 w-4" aria-hidden="true" />,
            onClick: onRequestEdit,
            testId: 'reserva-menu-destrabar',
        });
    }
    if (showCorrectTravelingButton) {
        menuAccionesExcepcion.push({
            key: 'sacar-de-viaje',
            label: 'Sacar de viaje',
            icon: <CornerUpLeft className="h-4 w-4" aria-hidden="true" />,
            onClick: onCorrectTraveling,
            testId: 'reserva-menu-sacar-de-viaje',
        });
    }

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
        ? "El viaje todavía no terminó"
        : !todasLasMonedasSaldadas
            ? "No se puede cerrar con saldo pendiente"
            : "Cerrar reserva";

    return (
        <div className="mb-8 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
            <div>
                <button
                    onClick={onBack}
                    className="flex items-center text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200 mb-2 transition-colors font-medium text-sm"
                >
                    {/* Texto exacto de la maqueta firmada (línea 688): "← Volver al listado". */}
                    <ArrowLeft className="w-4 h-4 mr-1.5" /> Volver al listado
                </button>
                {/* P7 (Tanda 2 del rediseño, 2026-08-03): el título se queda SOLO con el
                    número de reserva y el chip de estado grande — es lo primero que hay
                    que leer. "Con cambios" lo acompaña porque avisa sobre ESE estado
                    (hay una edición sin revisar), no es un dato de plata ni de destino. */}
                <div className="flex items-center gap-2 flex-wrap">
                    <h1 className="text-2xl font-extrabold text-slate-900 dark:text-white tracking-tight">
                        Reserva <span className="text-indigo-600 dark:text-indigo-400">#{reserva.numeroReserva}</span>
                    </h1>
                    <ReservaStatusBadge status={reserva.status} mostrarCandado size="lg" />
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

                {/* Cliente en negrita — mismo lugar de siempre. */}
                <p className="text-base font-bold text-slate-900 dark:text-white mt-2">
                    {reserva.customerName}
                </p>

                {/* P7: "MUERE" el nombre autogenerado tipo "File F-2026-…" (reserva.name) —
                    la palabra "file" no se ve más acá. En su lugar, esta línea gris muestra
                    destino + cantidad de pasajeros (derivado de los servicios YA cargados,
                    mismo criterio que el listado: ciudades reales, sin inventar — ver
                    reservaDestinoFicha.js). En una reserva Anulada con algún servicio
                    anulado, esta línea se REEMPLAZA por el contador "N de M servicios
                    anulados" (maqueta firmada, sección 11) — es el dato que importa ahí. */}
                {isReservaAnulada(reserva) && serviciosCancelados && serviciosCancelados.cancelados > 0 ? (
                    <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5" data-testid="ficha-destino-o-anulados">
                        {serviciosCancelados.cancelados} de {serviciosCancelados.totalConProveedor}{' '}
                        {serviciosCancelados.totalConProveedor === 1 ? 'servicio anulado' : 'servicios anulados'}
                    </p>
                ) : (
                    <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5" data-testid="ficha-destino-o-anulados">
                        {armarLineaDestinoYPasajeros(reserva)}
                    </p>
                )}

                {/* Reserva VIVA con servicios cancelados sueltos (no toda la reserva está
                    Anulada): esta info se conserva, solo que como línea secundaria — antes
                    era la única forma de verla y no puede desaparecer. */}
                {!isReservaAnulada(reserva) && serviciosCancelados && serviciosCancelados.cancelados > 0 && (
                    <p
                        className="mt-0.5 text-xs text-slate-400 dark:text-slate-500"
                        data-testid="contador-servicios-cancelados"
                    >
                        {serviciosCancelados.cancelados} de {serviciosCancelados.totalConProveedor}{' '}
                        {serviciosCancelados.totalConProveedor === 1 ? 'servicio anulado' : 'servicios anulados'}
                    </p>
                )}

                {/* P7: los cartelitos "Pago: … · Factura: …" bajan a su propio renglón,
                    debajo del destino — dejan de pelear con el título de arriba. Ningún
                    texto cambia (siguen siendo los mismos chips de ReservaStatusChips). */}
                <div className="mt-2">
                    <ReservaStatusChips reserva={reserva} />
                </div>

                {/* Fechas del viaje */}
                <div className="mt-3 flex items-center gap-3 flex-wrap">
                    <div className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-1.5 text-sm dark:border-slate-800 dark:bg-slate-900">
                        <span aria-hidden="true">📅</span>
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
                        no se deshabilita, porque la reserva está en solo-lectura visual completa.
                        Candado C1 (2026-07-22): con la reserva Confirmada y SIN autorización viva,
                        el botón queda gris + candadito y abre la ventana de destrabar en vez de
                        editar directo (antes ignoraba el candado — bug real, reserva 1052). */}
                    {canEditDates && onEditDates && (
                        candadoDeEdicionActivo ? (
                            <button
                                onClick={onRequestEdit}
                                type="button"
                                data-testid="reserva-action-edit-dates"
                                aria-label="Editar fechas — bloqueado, pedí autorización"
                                className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-slate-100 px-2.5 py-1.5 text-xs font-semibold text-slate-500 transition-colors hover:bg-slate-200 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700"
                            >
                                <Lock className="w-3.5 h-3.5" aria-hidden="true" />
                                Editar fechas
                            </button>
                        ) : (
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
                        )
                    )}

                    {/* "Reprogramar viaje": mueve TODAS las fechas de los servicios desde una nueva fecha de salida.
                        Distinto de "Editar fechas" (override de cabecera): este corre todo el viaje en bloque.
                        Visible cuando canEditServices.allowed=true → el backend sabe si la reserva es editable.
                        Candado C1 (2026-07-22): mismo tratamiento que "Editar fechas" — gris + candadito
                        cuando la reserva está bloqueada sin autorización viva. */}
                    {showRescheduleButton && (
                        candadoDeEdicionActivo ? (
                            <button
                                onClick={onRequestEdit}
                                type="button"
                                data-testid="reserva-action-reschedule"
                                aria-label="Reprogramar viaje — bloqueado, pedí autorización"
                                className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-slate-100 px-2.5 py-1.5 text-xs font-semibold text-slate-500 transition-colors hover:bg-slate-200 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700"
                            >
                                <Lock className="w-3.5 h-3.5" aria-hidden="true" />
                                Reprogramar viaje
                            </button>
                        ) : (
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
                        )
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
                        // H7 (2026-07-25, decisión firmada de Gastón): no alcanza con la CANTIDAD
                        // de pasajeros declarada (P2/ADR-031, lo que exigía antes) — hace falta que
                        // el TITULAR (primer pasajero de la lista) tenga el nombre cargado. Sin este
                        // gate se podía avanzar a "En gestión" con pasajeros "fantasma" sin nombre y
                        // recién chocaba más adelante al intentar confirmar un servicio con el
                        // operador (hallazgo #7 del barrido E2E 2026-07-25).
                        //
                        // faltaTitularConNombre (alias de calcularHintHotelTraslado, pasajeroHint.js)
                        // en vez de escribir el chequeo de nuevo acá: es EL MISMO criterio que ya usa
                        // el motor para confirmar hotel/traslado, así el front nunca diverge de esa
                        // regla. Cubre también el caso "sin pasajeros" (lista vacía → true).
                        const faltaTitular = faltaTitularConNombre(reserva.passengers);
                        return (
                            <>
                                <button
                                    onClick={() => onStatusChange('InManagement')}
                                    disabled={faltaTitular}
                                    data-testid="reserva-action-client-accepted"
                                    data-disabled-reason={faltaTitular ? "sin-titular-con-nombre" : undefined}
                                    className={`px-5 py-2.5 rounded-xl font-bold text-sm shadow-sm transition-all active:scale-95 ${
                                        faltaTitular
                                            ? 'bg-slate-300 dark:bg-slate-700 text-slate-500 dark:text-slate-400 cursor-not-allowed shadow-none'
                                            : 'bg-cyan-600 hover:bg-cyan-700 text-white'
                                    }`}
                                    title={
                                        faltaTitular
                                            ? "Tiene que haber un pasajero titular con el nombre cargado"
                                            : "El cliente aceptó el presupuesto — arranca la gestión con los operadores"
                                    }
                                >
                                    El cliente aceptó
                                </button>
                                {/* P8 (Tanda 3 del rediseño, 2026-08-03, maqueta sección 6 — "el callejón sin
                                    salida, resuelto"): antes este texto explicaba el motivo pero no llevaba a
                                    ningún lado — la solapa Pasajeros todavía no existía en Presupuesto, así
                                    que el vendedor se quedaba sin saber DÓNDE cargar el titular. Ahora es un
                                    enlace que abre esa solapa directo (mismo botón primario "abajo", este
                                    cartelito sigue permitido por el feedback 2026-06-19: explica un requisito
                                    previo, no un bloqueo del estado). */}
                                {faltaTitular && (
                                    <p className="text-xs text-amber-600 dark:text-amber-400 font-medium">
                                        Falta cargar el titular.{" "}
                                        <button
                                            type="button"
                                            onClick={onIrAPasajeros}
                                            data-testid="reserva-action-client-accepted-hint"
                                            className="font-bold underline underline-offset-2 hover:text-amber-700 dark:hover:text-amber-300"
                                        >
                                            Cargar el titular →
                                        </button>
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

                        {/* ── Boton "Anular reserva" (P9: estilo "peligro suave", con emoji
                            🚫 igual que la maqueta firmada) ──────────────────────────────
                            F4-2 (2026-06-26): habilitado cuando canAnnul.allowed OR canCancel.allowed.
                            En gris (disabled) solo cuando NINGUNA de las dos lo permite.
                            ADR-035: SIEMPRE VISIBLE si el usuario tiene permiso (reservas.cancel).
                            Feedback 2026-06-19: SIN texto de motivo debajo, solo gris.
                            El cartel único en ReservaDetailPage explica el estado global.
                            P9 (2026-08-03): oculto en Anulada/Perdida (ver esAnuladaOPerdida
                            más arriba) — ahí no tiene sentido ofrecer anular de nuevo. */}
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
                                <span aria-hidden="true">🚫</span>
                                Anular reserva
                            </button>
                        )}

                        {/* ADR-037: el botón "Reabrir para facturar" fue ELIMINADO.
                            La facturación se desacopló del estado: ahora se factura directo desde
                            Finalizada (y desde Confirmada/En viaje) sin reabrir ni destrabar nada.
                            El botón "Facturar" se habilita por capability del backend. */}

                        {/* Archivar: botón gris cuando no puede, y AHORA (P9, 2026-08-03) con el
                            motivo del motor escrito debajo — mismo patrón que ya usa el listado
                            (ReservaTable/ReservaMobileList, P-9/P-13⭐): nunca escondido en un
                            tooltip. Antes de esta tanda el motivo existía pero no se mostraba;
                            ese cambio queda documentado en adr035FeedbackVisual.test.mjs.
                            Guía UX 2026-06-22: ocultar en Traveling — archivar es para estados
                            terminales (Finalizada/Perdida/Anulada), no para algo en curso. */}
                        {!esTraveling && (
                            <div className="flex flex-col items-start gap-1">
                                <button
                                    onClick={canArchive ? onArchive : undefined}
                                    disabled={!canArchive}
                                    aria-label="Archivar reserva"
                                    className={`inline-flex items-center gap-1.5 px-3 py-2.5 rounded-xl transition-colors text-sm font-semibold ${canArchive ? 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700' : 'bg-slate-50 text-slate-300 dark:bg-slate-900 dark:text-slate-700 cursor-not-allowed'}`}
                                >
                                    <span aria-hidden="true">🗄</span>
                                    Archivar
                                </button>
                                {archiveBlockReason && (
                                    <span className="max-w-[220px] text-[11px] leading-tight text-slate-400 dark:text-slate-500">
                                        {archiveBlockReason}
                                    </span>
                                )}
                            </div>
                        )}

                        {/* Menú "⋯" de acciones de excepción (P9): agrupa "Volver atrás",
                            "Destrabar reserva" y "Sacar de viaje" — correcciones de último
                            recurso que antes competían con los botones de todos los días.
                            Si no hay ningún item que aplique, no se renderiza nada. */}
                        <MenuAccionesExcepcion items={menuAccionesExcepcion} />
                    </div>
                </div>
            )}
        </div>
    );
}
