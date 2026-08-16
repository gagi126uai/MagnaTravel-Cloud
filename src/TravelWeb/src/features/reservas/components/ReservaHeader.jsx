import React, { useState, useEffect, useRef } from 'react';
import { Link } from "react-router-dom";
import { ArrowLeft, AlertTriangle, Undo2, Lock, XCircle, RefreshCw, CornerUpLeft, FastForward, MoreHorizontal, Ban, FileText, Send, Loader2, Archive } from "lucide-react";
import { Button } from "../../../components/ui/button";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { getReservaArchiveBlockReason } from "../archiveRules";
import { isReservaEnEstadoVivo, tieneCandadoDeEdicionActivo, ReservaStatusBadge } from "./ReservaStatusBadge";
import { ReservaStatusChips } from "./ReservaStatusChips";
import { TripDatesRow } from "./TripDatesRow";
import { isAdmin } from "../../../auth";
import { faltaTitularConNombre } from "../lib/pasajeroHint";
import { isReservaAnulada } from "../moneyStatus";
import { armarLineaDestinoYPasajeros, armarAvisoPasajerosFaltantes } from "../lib/reservaDestinoFicha";
import { palabraTituloReserva, debeOcultarChapitaEstado } from "../lib/reservaHeaderTituloLogic";
import {
    MODO_PRECIO_PRESUPUESTO,
    queryParamPricingParaModo,
    porPersonaBooleanParaModo,
} from "../lib/budgetPdfLogic";

/**
 * Dispara la descarga de un blob ya recibido (mismo patrón que ReservaVoucherTab.jsx y
 * ReservaDocumentsTab.jsx — no hay un helper compartido todavía, se copia el mismo bloque
 * chico que ya usan esos dos componentes).
 */
function downloadBlob(blob, fileName) {
    const url = window.URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.setAttribute("download", fileName);
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.setTimeout(() => window.URL.revokeObjectURL(url), 1000);
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
            {/* Lavado de cara (2026-08-11, F-16 "la excepción es discreta, nunca un botón
                normal"): el "⋯" pasa de caja con borde a variant="ghost" — mismo molde
                terciario que "Perdida", sin fondo ni borde propio, 40px de alto igual
                que el resto de la fila (antes tenía su propia altura/relleno a mano). */}
            <Button
                type="button"
                ref={triggerRef}
                variant="ghost"
                size="icon"
                onClick={() => setAbierto((previo) => !previo)}
                aria-haspopup="menu"
                aria-expanded={abierto}
                aria-label="Más acciones"
                data-testid="reserva-menu-excepciones-trigger"
            >
                <MoreHorizontal className="h-4 w-4" aria-hidden="true" />
            </Button>
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
 * - Los callbacks onStatusChange, onArchive, onRevert, onReschedule son manejados por el padre
 * - onReschedule: callback que abre ReprogramarViajeModal; se muestra cuando capabilities.canReschedule.allowed === true (G5, 2026-06-24).
 * - onPromisedDatesChanged: callback que dispara el padre (ADR-053, 2026-08-13) para
 *   refrescar la ficha completa después de guardar/borrar la "fecha prometida al
 *   cliente" — ver TripDatesRow.jsx/PromisedDatesBlock.jsx.
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
    onReschedule,
    onPromisedDatesChanged,
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
    // esta cabecera (fecha prometida, Reprogramar viaje) se muestran "gris + candadito"
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

    // ─── Edición de "fecha prometida al cliente" (ADR-053, 2026-08-13) ───────────
    // Misma capability que antes gateaba el viejo botón "Editar fechas"
    // (canEditReservaData): Salida/Regreso pasaron a ser calculadas y de solo
    // lectura, pero el backend sigue usando este MISMO permiso/candado para la
    // "fecha prometida" nueva (PATCH /promised-dates usa la misma cadena de
    // compuertas que el viejo PATCH /dates — ver ADR-053 D3). Se oculta cuando
    // canEditReservaData.allowed === false (estados terminales) — feedback 2026-06-19.
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
    // "Reprogramar" es diferente de la "fecha prometida":
    //   - "Fecha prometida" es una nota aparte, nunca toca los servicios (ADR-053).
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
    // Fix bloqueante de review (2026-08-11, B2): el botón "Cerrar reserva" SOLO se
    // renderiza más abajo cuando `endHasPast` ya es true (`reserva.status === 'Traveling'
    // && endHasPast`) — la rama "El viaje todavía no terminó" nunca podía dispararse
    // adentro de ese botón, era código muerto. El único motivo real de bloqueo posible
    // acá es el saldo pendiente.
    const motivoCierreBloqueado = !todasLasMonedasSaldadas
        ? "No se puede cerrar con saldo pendiente"
        : null;

    // ─── Título dinámico de la cabecera (Lavado de cara, Tanda 2, 2026-08-11 —
    // ENMIENDA del dueño tras el review B1) ───────────────────────────────────
    // Cotización y Presupuesto son etapas DISTINTAS del ciclo (ADR-020): cada una
    // tiene su propia palabra en el título ("Cotización 2026-1067" / "Presupuesto
    // 2026-1067"), no se colapsan en una sola. La lógica vive en un helper puro
    // testeado aparte (reservaHeaderTituloLogic.js) para no repetir el mapeo acá
    // adentro del JSX. La chapita de estado se OMITE solo cuando repetiría la
    // palabra que el título ya dice (P-16) — eso pasa en Quotation y Budget.
    const palabraTitulo = palabraTituloReserva(reserva.status);
    const ocultarChapitaDeEstado = debeOcultarChapitaEstado(reserva.status);

    // ─── "Emitir PDF" / "Enviar por WhatsApp" (decisión del dueño, 2026-08-16 —
    // SUPERSEDE el interruptor suelto que había antes en esta cabecera) ────────────────
    // Ahora "Por persona" vs "Total del viaje" ya NO es un interruptor que el vendedor
    // deja seteado de antemano: se pregunta EN EL MOMENTO de emitir, con un renglón que
    // se despliega debajo de la botonera (ver más abajo, `eleccionPrecioPendiente`).
    // `eleccionPrecioPendiente` guarda CUÁL de los dos botones abrió la elección
    // ('pdf' | 'whatsapp' | null) — así, cuando el vendedor elige el formato, sabemos
    // qué acción disparar sin volver a preguntar nada.
    const [eleccionPrecioPendiente, setEleccionPrecioPendiente] = useState(null);
    // Si la reserva deja de ser Presupuesto con el renglón de elección abierto (ej. el
    // vendedor tocó "El cliente aceptó" en otra pestaña y esta se refrescó), los botones
    // que lo abren desaparecen pero el renglón quedaría huérfano en pantalla: se cierra.
    useEffect(() => {
        if (reserva?.status !== 'Budget') {
            setEleccionPrecioPendiente(null);
        }
    }, [reserva?.status]);
    // Candados anti doble click (mismo criterio que issuingId/sendingVoucherId en
    // ReservaVoucherTab.jsx): mientras uno de los dos está en curso, ese botón se apaga y
    // muestra spinner — un segundo click no dispara una segunda descarga/un segundo envío.
    const [generandoPdfPresupuesto, setGenerandoPdfPresupuesto] = useState(false);
    const [enviandoPresupuestoWhatsApp, setEnviandoPresupuestoWhatsApp] = useState(false);

    const handleEmitirPdfPresupuesto = async (modo) => {
        if (generandoPdfPresupuesto) return;
        setGenerandoPdfPresupuesto(true);
        try {
            const pricing = queryParamPricingParaModo(modo);
            const blob = await api.get(`/reservas/${reserva.publicId}/budget-pdf?pricing=${pricing}`, {
                responseType: "blob",
            });
            downloadBlob(blob, `Presupuesto ${reserva.numeroReserva}.pdf`);
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo generar el PDF del presupuesto."));
        } finally {
            setGenerandoPdfPresupuesto(false);
        }
    };

    // Mismo mecanismo YA construido para vouchers (ReservaVoucherTab.jsx → POST
    // /messages/…): acá el destinatario no es ambiguo (siempre el cliente/pagador de la
    // reserva, no hay selector de pasajero como en el voucher), así que el back lo
    // resuelve solo — un único click, sin ventana intermedia.
    const handleEnviarPresupuestoWhatsApp = async (modo) => {
        if (enviandoPresupuestoWhatsApp) return;
        setEnviandoPresupuestoWhatsApp(true);
        try {
            await api.post("/messages/budget", {
                reservaId: reserva.publicId,
                porPersona: porPersonaBooleanParaModo(modo),
            });
            showSuccess(`Presupuesto enviado por WhatsApp a ${reserva.customerName}.`);
        } catch (error) {
            // El backend ya arma el mensaje en criollo, listo para mostrar tal cual (ej.
            // "«Juan Pérez» no tiene teléfono cargado. Agregá uno y reintentá.").
            showError(getApiErrorMessage(error, "No se pudo enviar el presupuesto. Probá de nuevo."));
        } finally {
            setEnviandoPresupuestoWhatsApp(false);
        }
    };

    // Se dispara al tocar "Por persona" o "Total del viaje" en el renglón de elección.
    // Cierra el renglón ANTES de llamar al backend — así, aunque la emisión tarde, el
    // vendedor ve que su elección "se tomó" al toque (mismo criterio que el resto de
    // los flujos de esta ficha: la UI reacciona al click, no espera la respuesta del
    // servidor para dar feedback de que la acción arrancó).
    const handleElegirPrecioPresupuesto = (modo) => {
        const accionQueDisparoLaEleccion = eleccionPrecioPendiente;
        setEleccionPrecioPendiente(null);
        if (accionQueDisparoLaEleccion === 'pdf') {
            handleEmitirPdfPresupuesto(modo);
        } else if (accionQueDisparoLaEleccion === 'whatsapp') {
            handleEnviarPresupuestoWhatsApp(modo);
        }
    };

    return (
        // `sm:items-start` (antes `sm:items-center`, Tanda A UX 2026-08-16): con
        // `items-center` la botonera de la derecha se RE-CENTRABA verticalmente cada
        // vez que el bloque izquierdo crecía (ej. al abrir el form de "fecha
        // prometida"), lo que hacía "temblar" el header. Con `items-start` la
        // botonera queda anclada arriba y ya no se mueve — el `sm:mt-7` de la
        // botonera (más abajo) la alinea visualmente con el título en vez de con
        // el link chiquito "Volver al listado" que está por encima de este.
        <div className="mb-8 flex flex-col sm:flex-row sm:items-start sm:justify-between gap-4">
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
                        {palabraTitulo} <span className="text-primary">{reserva.numeroReserva}</span>
                    </h1>
                    {!ocultarChapitaDeEstado && (
                        <ReservaStatusBadge status={reserva.status} mostrarCandado size="lg" />
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

                {/* Cliente en negrita — mismo lugar de siempre. Tanda A UX 2026-08-16:
                    si hay customerPublicId, el nombre pasa a ser un link discreto a la
                    cuenta corriente del cliente (mismo destino y mismo gate que el link
                    "Ver cuenta del cliente" de EstadoCuentaResumen.jsx — ahí NO hay chequeo
                    de permiso adicional, solo se pide que el DTO traiga el publicId, así
                    que acá se replica exactamente eso, sin inventar un permiso que no
                    existe). Sin publicId queda como texto plano, igual que antes. */}
                {reserva.customerPublicId ? (
                    <Link
                        to={`/customers/${reserva.customerPublicId}/account`}
                        className="mt-2 inline-block text-base font-bold text-slate-900 transition-colors hover:text-primary hover:underline dark:text-white dark:hover:text-primary"
                    >
                        {reserva.customerName}
                    </Link>
                ) : (
                    <p className="text-base font-bold text-slate-900 dark:text-white mt-2">
                        {reserva.customerName}
                    </p>
                )}

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

                {/* Aviso discreto (Tanda A UX 2026-08-16): cuando lo DECLARADO (ADR-031,
                    "somos 4") todavía no coincide con los pasajeros que ya tienen nombre
                    cargado. Es información, no una leyenda decorativa (P-9/P-15) — texto
                    a la vista, sin tooltip, en una sola línea. No aplica en Anulada: ahí
                    la línea de arriba ya muestra el contador de servicios anulados, no
                    tiene sentido pedir pasajeros de un viaje que quedó sin efecto. */}
                {!isReservaAnulada(reserva) && armarAvisoPasajerosFaltantes(reserva) && (
                    <p
                        className="mt-0.5 truncate text-[11px] text-slate-400 dark:text-slate-500"
                        data-testid="aviso-pasajeros-faltantes"
                    >
                        {armarAvisoPasajerosFaltantes(reserva)}
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

                {/* Fechas del viaje (ADR-053, 2026-08-13): TripDatesRow pinta la Salida/Regreso
                    CALCULADA (solo lectura, ver ese componente) + el bloque opcional de "fecha
                    prometida al cliente" debajo. "Reprogramar viaje" se queda al lado, tal cual
                    estaba (spec §1: "esto ya estaba firmado y NO se toca"). items-start porque
                    el bloque de fechas ahora puede ocupar dos renglones. */}
                <div className="mt-3 flex items-start gap-3 flex-wrap">
                    <TripDatesRow
                        reserva={reserva}
                        canEditPromisedDates={canEditDates}
                        candadoDeEdicionActivo={candadoDeEdicionActivo}
                        onRequestEdit={onRequestEdit}
                        onPromisedDatesChanged={onPromisedDatesChanged}
                    />

                    {/* "Reprogramar viaje": mueve TODAS las fechas de los servicios desde una nueva fecha de salida.
                        Distinto de la "fecha prometida" (nota aparte, nunca toca servicios): este corre
                        todo el viaje en bloque. Visible cuando canEditServices.allowed=true → el backend
                        sabe si la reserva es editable. Candado C1 (2026-07-22): gris + candadito cuando
                        la reserva está bloqueada sin autorización viva. */}
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
                                // Residuo de coherencia (lavado de cara, 2026-08-11): antes era índigo
                                // suelto — mismo molde secundario que el resto de los botones de esta
                                // fila, único color de acción del sistema reservado al botón principal.
                                className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 hover:border-slate-300 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 dark:hover:bg-slate-800"
                                title="Mueve todas las fechas de los servicios"
                            >
                                <FastForward className="w-3.5 h-3.5" />
                                Reprogramar viaje
                            </button>
                        )
                    )}
                </div>
            </div>

            {/* Botonera de acciones + (si corresponde) el renglón de elección de precio.
                `sm:mt-7` compensa el cambio a `sm:items-start` de arriba: ese offset es
                aproximadamente el alto del link "Volver al listado" + su margen (texto
                chico + mb-2), así la botonera queda alineada con el título en vez de con
                ese link. `flex-col` apila la botonera y (cuando está abierto) el renglón
                de elección DEBAJO, cada uno en su propia línea — nunca se pisan. */}
            <div className="flex flex-col items-end gap-2 sm:mt-7">
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
                        <Button
                            type="button"
                            variant="default"
                            onClick={() => onStatusChange('Budget')}
                            data-testid="reserva-action-to-budget"
                            title="El borrador pasa a documento formal para el cliente"
                        >
                            Pasar a presupuesto
                        </Button>
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
                                {/* Lavado de cara (2026-08-11): el `title` con el motivo del apagado se
                                    saca de acá — ya está ESCRITO abajo, en el cartelito ámbar "Falta
                                    cargar el titular" (P-9: el motivo va a la vista, no en un tooltip).
                                    Sin motivo, el título repetía el mismo texto que dice el botón (P-16). */}
                                <Button
                                    type="button"
                                    variant="default"
                                    onClick={() => onStatusChange('InManagement')}
                                    disabled={faltaTitular}
                                    data-testid="reserva-action-client-accepted"
                                    data-disabled-reason={faltaTitular ? "sin-titular-con-nombre" : undefined}
                                    title={faltaTitular ? undefined : "Arranca la gestión con los operadores"}
                                >
                                    El cliente aceptó
                                </Button>
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

                    {/* "Emitir PDF" / "Enviar por WhatsApp" (SOLO en etapa Presupuesto,
                        inmediatamente después del botón principal "El cliente aceptó" y
                        ANTES del separador de acciones secundarias Perdida/Archivar/⋯).
                        Ninguno de los dos es relleno — la principal sigue siendo la única
                        con ese peso visual (B.3 regla de oro).

                        Decisión del dueño (16/08): "Por persona/Total del viaje" ya NO es
                        un interruptor que se deja seteado antes de tocar el botón — se
                        pregunta EN EL MOMENTO de emitir. Estos dos botones ya no ejecutan
                        la acción directo: abren el renglón de elección de más abajo
                        (`eleccionPrecioPendiente`), que decide el formato y recién ahí
                        dispara la emisión real. */}
                    {reserva.status === 'Budget' && (
                        <>
                            <Button
                                type="button"
                                variant="outline"
                                onClick={() => setEleccionPrecioPendiente('pdf')}
                                disabled={generandoPdfPresupuesto}
                                data-testid="reserva-action-emitir-pdf-presupuesto"
                            >
                                {generandoPdfPresupuesto ? (
                                    <Loader2 className="w-4 h-4 animate-spin" aria-hidden="true" />
                                ) : (
                                    <FileText className="w-4 h-4" aria-hidden="true" />
                                )}
                                {generandoPdfPresupuesto ? "Generando…" : "Emitir PDF"}
                            </Button>

                            <Button
                                type="button"
                                variant="outline"
                                onClick={() => setEleccionPrecioPendiente('whatsapp')}
                                disabled={enviandoPresupuestoWhatsApp}
                                data-testid="reserva-action-enviar-presupuesto-whatsapp"
                            >
                                {enviandoPresupuestoWhatsApp ? (
                                    <Loader2 className="w-4 h-4 animate-spin" aria-hidden="true" />
                                ) : (
                                    <Send className="w-4 h-4" aria-hidden="true" />
                                )}
                                {enviandoPresupuestoWhatsApp ? "Enviando…" : "Enviar por WhatsApp"}
                            </Button>
                        </>
                    )}

                    {/* En gestion: Confirmada es automatica al resolverse todos los servicios. */}
                    {/* Confirmada: En viaje tambien es automatica (job diario por fecha de salida). */}

                    {/* ADR-036: solo el boton "Cerrar reserva" en Traveling.
                        "Apartar para liquidar" y "Finalizar / Marcar liquidada" fueron eliminados
                        porque "A liquidar" ya no existe como estado. */}
                    {reserva.status === 'Traveling' && endHasPast && (
                        <>
                            {/* Fix bloqueante de review (2026-08-11, B2): un `title` sobre un botón
                                `disabled` NUNCA se ve — la clase base de Button trae
                                `disabled:pointer-events-none`, así que el navegador no puede
                                disparar el hover que muestra el tooltip nativo. Mismo patrón que
                                "El cliente aceptó" (más arriba): el motivo va ESCRITO al lado,
                                no en un tooltip fantasma. La enmienda de P-9 que permite tooltip
                                SOLO vale para los listados (Archivar en ReservaTable/
                                ReservaMobileList) — acá, en la cabecera de la ficha, sigue el
                                criterio original de P-9: el motivo a la vista. */}
                            <Button
                                type="button"
                                variant="default"
                                onClick={() => onStatusChange('Closed')}
                                disabled={!canClose}
                                data-testid="reserva-action-finalize-direct"
                            >
                                Cerrar reserva
                            </Button>
                            {motivoCierreBloqueado && (
                                <p
                                    className="text-xs text-amber-600 dark:text-amber-400 font-medium"
                                    data-testid="reserva-action-finalize-direct-motivo"
                                >
                                    {motivoCierreBloqueado}
                                </p>
                            )}
                        </>
                    )}

                    {/* ACCIONES SECUNDARIAS — Separador visual en sm: hacia arriba.
                        Feedback 2026-06-19: botones deshabilitados = solo gris, sin texto debajo.
                        Todos los botones tienen la misma altura/padding que las acciones primarias. */}
                    {/* Fix causa raíz del bug "Perdida más grande que El cliente aceptó" (Lavado
                        de cara 2026-08-11, ver auditoría A.0 del estándar visual): a este
                        contenedor le faltaba `items-center` — sin esa instrucción, los hijos se
                        ESTIRABAN al alto del más alto del grupo (el motivo de Archivar en 2
                        renglones). Con items-center todos los botones de la fila arrancan en la
                        misma línea y miden lo mismo, sin importar cuánto texto cuelgue debajo de
                        alguno. */}
                    <div className="flex flex-wrap items-center gap-2 sm:border-l sm:border-slate-200 sm:dark:border-slate-800 sm:pl-4">

                        {/* Boton "Perdida": discreto (nivel 3, fantasma), solo desde Cotizacion/Presupuesto.
                            Lavado de cara: pasa de caja gris rellena a variant="ghost" — texto
                            gris sin fondo propio, mismo molde que el resto de las terciarias. */}
                        {showMarkLostButton && (
                            <Button
                                type="button"
                                variant="ghost"
                                onClick={onMarkLost}
                                data-testid="reserva-action-mark-lost"
                                aria-label="Perdida"
                            >
                                <XCircle className="w-4 h-4" />
                                Perdida
                            </Button>
                        )}

                        {/* ── Boton "Anular reserva" (nivel 4, destructiva discreta) ──────────
                            F4-2 (2026-06-26): habilitado cuando canAnnul.allowed OR canCancel.allowed.
                            En gris (disabled) solo cuando NINGUNA de las dos lo permite.
                            ADR-035: SIEMPRE VISIBLE si el usuario tiene permiso (reservas.cancel).
                            Feedback 2026-06-19: SIN texto de motivo debajo, solo gris.
                            El cartel único en ReservaDetailPage explica el estado global.
                            P9 (2026-08-03): oculto en Anulada/Perdida (ver esAnuladaOPerdida
                            más arriba) — ahí no tiene sentido ofrecer anular de nuevo.
                            Lavado de cara (2026-08-11): variant="destructive" del sistema — letra
                            roja + contorno rosado, NUNCA relleno rojo (P-14 sigue confirmando en
                            el flujo que abre onCancelReserva). El emoji 🚫 se reemplaza por el
                            ícono Ban de lucide — B.3 regla 4, "se van todos los emojis". */}
                        {showCancelButton && (
                            <Button
                                type="button"
                                variant="destructive"
                                onClick={puedeAnular ? onCancelReserva : undefined}
                                disabled={!puedeAnular}
                                data-testid="btn-anular-reserva"
                                aria-label="Anular reserva"
                            >
                                <Ban className="w-4 h-4" aria-hidden="true" />
                                Anular reserva
                            </Button>
                        )}

                        {/* ADR-037: el botón "Reabrir para facturar" fue ELIMINADO.
                            La facturación se desacopló del estado: ahora se factura directo desde
                            Finalizada (y desde Confirmada/En viaje) sin reabrir ni destrabar nada.
                            El botón "Facturar" se habilita por capability del backend. */}

                        {/* Archivar: botón gris cuando no puede.
                            Motivo bloqueado (decisión del dueño, 11/08/2026 — REEMPLAZA la de
                            2026-08-03, enmienda P-9 el mismo día tras el review B1): va en el
                            `title`, no escrito debajo — mismo criterio que el listado de
                            escritorio (ReservaTable). Ese cambio de criterio queda documentado
                            en adr035FeedbackVisual.test.mjs.
                            Fix B1 (review): el `title` vive en el <span> que ENVUELVE al botón,
                            no en el <button> — un elemento deshabilitado no siempre dispara el
                            hover que necesita el navegador para mostrar el tooltip (mismo
                            problema que el Button de shadcn en ReservaTable.jsx); el envoltorio
                            sí lo recibe siempre.
                            Guía UX 2026-06-22: ocultar en Traveling — archivar es para estados
                            terminales (Finalizada/Perdida/Anulada), no para algo en curso.
                            El emoji 🗄 se reemplaza por el ícono Archive de lucide (Tanda A UX
                            2026-08-16, mismo patrón que "Anular reserva" con Ban). */}
                        {!esTraveling && (
                            <span title={archiveBlockReason || undefined}>
                                <button
                                    onClick={canArchive ? onArchive : undefined}
                                    disabled={!canArchive}
                                    aria-label="Archivar reserva"
                                    className={`inline-flex items-center gap-1.5 px-3 py-2.5 rounded-xl transition-colors text-sm font-semibold ${canArchive ? 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700' : 'bg-slate-50 text-slate-300 dark:bg-slate-900 dark:text-slate-700 cursor-not-allowed'}`}
                                >
                                    <Archive className="w-4 h-4" aria-hidden="true" />
                                    Archivar
                                </button>
                            </span>
                        )}

                        {/* Menú "⋯" de acciones de excepción (P9): agrupa "Volver atrás",
                            "Destrabar reserva" y "Sacar de viaje" — correcciones de último
                            recurso que antes competían con los botones de todos los días.
                            Si no hay ningún item que aplique, no se renderiza nada. */}
                        <MenuAccionesExcepcion items={menuAccionesExcepcion} />
                    </div>
                </div>
            )}

            {/* Renglón de elección "Por persona / Total del viaje" (decisión del dueño,
                16/08/2026): aparece SOLO cuando se tocó "Emitir PDF" o "Enviar por
                WhatsApp" en etapa Presupuesto. P-5: vive EN EL FLUJO del documento
                (renglón propio debajo de la botonera), nunca modal ni popover flotante.
                "Descartar" cierra sin emitir nada — el vendedor se arrepintió del click. */}
            {!isArchived && eleccionPrecioPendiente && (
                <div
                    className="flex flex-wrap items-center gap-2 rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 dark:border-slate-800 dark:bg-slate-800/40"
                    data-testid="reserva-eleccion-precio-presupuesto"
                >
                    <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">
                        Precios del presupuesto:
                    </span>
                    <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        onClick={() => handleElegirPrecioPresupuesto(MODO_PRECIO_PRESUPUESTO.PorPersona)}
                        data-testid="reserva-eleccion-precio-por-persona"
                    >
                        Por persona
                    </Button>
                    <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        onClick={() => handleElegirPrecioPresupuesto(MODO_PRECIO_PRESUPUESTO.Total)}
                        data-testid="reserva-eleccion-precio-total"
                    >
                        Total del viaje
                    </Button>
                    <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={() => setEleccionPrecioPendiente(null)}
                        data-testid="reserva-eleccion-precio-descartar"
                    >
                        Descartar
                    </Button>
                </div>
            )}
            </div>
        </div>
    );
}
