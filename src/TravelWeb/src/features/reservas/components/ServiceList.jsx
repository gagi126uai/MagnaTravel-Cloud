/**
 * Lista de servicios contratados de una reserva.
 *
 * Muestra los servicios en dos layouts:
 *   - Desktop: tabla con columnas (Tipo, Descripción, Fecha/Estancia, Estado, Costo, Precio Venta, [Avisos], Acciones)
 *   - Mobile: tarjetas apiladas con la información clave
 *
 * Columna "Avisos" (UpcomingStartPill):
 *   Gateada por la prop isServiceDeadlineAlertsEnabled (flag EnableServiceDeadlineAlerts del backend).
 *   Decisión del dueño: catálogo OFF + avisos ON → la columna APARECE igual.
 *   Son dos flags independientes y la columna solo depende del flag de avisos.
 *
 * El gate de costo (quién ve el costo neto) cambia según el flag catálogo:
 *   - Flag OFF: isAdmin() (comportamiento original)
 *   - Flag ON:  hasPermission("cobranzas.see_cost") (admin sigue pasando porque admin tiene todo)
 *
 * Papelera borrar vs anular (decisión #9 guia UX 2026-06-08):
 *   - Servicio NO confirmado por el operador → "¿Borrar?" → desaparece de la reserva.
 *   - Servicio YA confirmado por el operador → "¿Anular?" → queda tachado (con motivo opcional).
 *   La decisión la toma el sistema solo según esServicioResuelto(svc).
 *   Un servicio YA anulado (esServicioAnulado(svc)) no ofrece ni Editar ni Borrar/Anular:
 *   ya quedó sin efecto y es historia de la reserva (2026-07-16).
 *
 * Botón "Anular varios" (ADR-025):
 *   Permite anular múltiples servicios de una vez con un único motivo.
 *   Despliega una sección INLINE debajo de la lista (no modal).
 *   Gateada por: el usuario debe tener permiso reservas.cancel.
 *   El bloqueo fiscal (serviceCancellationBlockReason) se propaga a la sección inline.
 *
 * Vocabulario "Cancelar" vs "Anular" (regla del dueño, 2026-07-16): en este producto
 * "Cancelar" significa que el cliente pagó el total del servicio/viaje; "Anular"
 * significa dejarlo sin efecto. Los textos visibles de este componente usan "Anular"
 * para lo segundo — los identificadores de código (CancelarVariosServiciosInline,
 * handleModalCancelar, etc.) se mantienen como están por ahora, son internos.
 */

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from "react-router-dom";
import { AlertTriangle, Plus, Plane, Hotel, Car, Package, ShieldCheck, Edit2, Trash2, CheckCircle2, Clock, X, Loader2, FileText, Ban, XSquare, UserX, Lock } from "lucide-react";
import { isAdmin, hasPermission } from "../../../auth";
import { tieneCandadoDeEdicionActivo } from "./ReservaStatusBadge";
import { CancelarVariosServiciosInline } from "./CancelarVariosServiciosInline";
import { PasajeroInlineForm } from "./PasajeroInlineForm";
import { ControlAsignacionServicio } from "./ControlAsignacionServicio";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import {
    SERVICE_RECORD_KIND,
    getReservationServicePublicId
} from "../lib/reservationServiceModel";
import { sugerirFacturaParaServicios } from "../lib/serviceInvoiceMatch";
import { calcularHintPorTipo, calcularSlotsFaltantesDelSet } from "../lib/pasajeroHint";
import { resolverBloqueoAnularServicio, resolverRechazoAnularServicio } from "../lib/serviceCancellationGuard";
import { useServiceNominalCoverage } from "../lib/useServiceNominalCoverage";
import { UpcomingStartPill, estaEnVentana } from "./UpcomingStartPill";
import { CostConfirmCell, CostConfirmCellMobile } from "./CostConfirmCell";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { formatCurrency } from "../../../lib/utils";
import { useReservaSupplierPaymentStatus } from "../lib/useReservaSupplierPaymentStatus";
import { buscarEstadoPagoServicio, puedenVerseMontos } from "../lib/supplierPaymentStatusLogic";
import { OperadorPagoStatusBadge } from "./OperadorPagoStatusBadge";
import { CancellationPenaltyLabel } from "./CancellationPenaltyLabel";

/**
 * Convierte el recordKind del frontend al serviceType que espera el endpoint de nominal-coverage.
 *
 * El backend usa los valores: "Hotel", "Flight", "Transfer", "Package", "Assistance", "Generic".
 * El frontend usa: "hotel", "flight", "transfer", "package", "assistance", "generic".
 *
 * @param {string} recordKind - valor del frontend (minúscula)
 * @returns {string} - valor del backend (capitalizado)
 */
function recordKindAServiceType(recordKind) {
    const mapa = {
        flight: "Flight",
        hotel: "Hotel",
        transfer: "Transfer",
        package: "Package",
        assistance: "Assistance",
        generic: "Generic",
    };
    return mapa[recordKind] || "Generic";
}

/**
 * Wrapper por servicio que encapsula el hook de nominal-coverage.
 *
 * Necesitamos un componente separado porque los hooks de React no se pueden llamar
 * dentro de un .map() — cada servicio necesita su propia instancia del hook.
 * Este componente solo maneja el estado de coverage y delega el render al padre.
 *
 * Props:
 *   reservaId           — publicId de la reserva
 *   svc                 — objeto del servicio normalizado
 *   pasajerosConNombre  — array de pasajeros con fullName cargado
 *   children            — función render prop: (coverage, coverageLoading, updateCoverage, serviceType, servicePublicId) => JSX
 *
 * NOTA D1 (follow-up, no bloqueante): en reservas con muchos servicios y pasajeros con nombre
 * se hacen N llamadas GET /nominal-coverage al mismo tiempo al renderizar la lista.
 * Una mejora futura sería cargar la coverage solo al abrir el panel (lazy), pero eso
 * requiere cambiar el texto "Para: X de N" del botón cerrado (hoy depende de coverage).
 * No resuelto ahora — se anota como follow-up.
 *
 * Props adicional:
 *   reservaStatus — necesario para saber si cargar coverage aunque no haya nombres
 *                   (en InManagement el mini-form la necesita para los slots faltantes).
 */
function ConCoverageDeServicio({ reservaId, svc, pasajerosConNombre, reservaStatus, children }) {
    const servicePublicId = getReservationServicePublicId(svc);
    const serviceType = recordKindAServiceType(svc.recordKind);

    const hayNombres = Array.isArray(pasajerosConNombre) && pasajerosConNombre.length > 0;

    // Habilitamos la llamada al backend cuando:
    //   1. Hay pasajeros con nombre → necesario para el control "Para: X de N".
    //   2. La reserva está en InManagement → necesario para el mini-form de slots faltantes
    //      (aunque aún no haya ningún nombre cargado, el backend dice qué slots faltan).
    const necesitaCoverageParaMiniForm = reservaStatus === 'InManagement' && !esServicioResuelto(svc);
    const habilitarCoverage = (hayNombres || necesitaCoverageParaMiniForm) && Boolean(servicePublicId);

    const { coverage, loading: coverageLoading, updateCoverage } = useServiceNominalCoverage({
        reservaId,
        serviceType,
        servicePublicId,
        enabled: habilitarCoverage,
    });

    // updateCoverage: función que permite pisar la coverage localmente con el DTO que devuelve
    // el PUT atómico de assignments (B2). Así evitamos re-pedir GET nominal-coverage tras guardar.
    return children(coverage, coverageLoading, updateCoverage, serviceType, servicePublicId);
}

/**
 * Calcula el resumen de servicios cancelados para el contador "N de M" del ReservaHeader.
 *
 * M = cantidad de servicios con proveedor (los "cancelables": vuelo, hotel, traslado, paquete,
 *     asistencia). Los servicios genéricos sin supplierId también se cuentan si tienen proveedor.
 * N = cuántos de esos tienen workflowStatus === "Cancelado".
 *
 * Se exporta para que el padre (ReservaDetailPage) lo calcule sobre allServices y lo pase
 * al ReservaHeader sin que este último tenga que conocer el array completo de servicios.
 *
 * @param {Array} services - Lista de servicios normalizados (salida de normalizeReservaServices).
 * @returns {{ cancelados: number, totalConProveedor: number }}
 */
export function calculateServiciosCanceladosResumen(services) {
    // Consideramos "con proveedor" a todos los servicios que tienen supplierId/supplierPublicId
    // o que son de un tipo específico (no genérico sin supplier). Los servicios genéricos sin
    // proveedor (solo descripción) no son "cancelables" en el sentido del ADR-025.
    const serviciosConProveedor = (services || []).filter(svc => {
        // Un servicio con proveedor asignado siempre es cancelable.
        const tieneProveedor = Boolean(svc.supplierPublicId || svc.supplierId || svc.supplierName);
        // Los tipos específicos (vuelo, hotel, traslado, paquete, asistencia) son cancelables
        // por definición aunque en algún caso raro no tengan el campo supplier poblado en front.
        const esTipoEspecifico = svc.recordKind && svc.recordKind !== 'generic';
        return tieneProveedor || esTipoEspecifico;
    });

    const cancelados = serviciosConProveedor.filter(
        svc => (svc.workflowStatus || svc.status) === 'Cancelado'
    ).length;

    return {
        cancelados,
        totalConProveedor: serviciosConProveedor.length,
    };
}

/**
 * Determina si un servicio esta "resuelto" para el ciclo ADR-020.
 * Definicion por tipo (guia UX 2026-06-08):
 *   - Aereo: ticket EMITIDO (workflowStatus === "Emitido" o status === "TK"/"KL")
 *   - Hotel / Paquete: confirmado por el operador (workflowStatus === "Confirmado" o status HK/KK)
 *   - Asistencia: voucher emitido (workflowStatus === "Emitido")
 *   - Traslado: confirmado o marcado "no requiere confirmacion" (workflowStatus "Confirmado" | "NoConfirmation")
 */
function esServicioResuelto(svc) {
    const status = svc.workflowStatus || svc.status || '';
    const estadosResueltos = ['Confirmado', 'Emitido', 'HK', 'TK', 'KK', 'KL', 'NoConfirmation'];
    return estadosResueltos.includes(status);
}

/**
 * Texto de la pelotita de estado para un servicio que todavia NO esta resuelto.
 * Indica que falta para que ese servicio quede listo (decision 4 de UX).
 */
function textoFaltante(svc) {
    if (svc.recordKind === SERVICE_RECORD_KIND.FLIGHT) return "Falta emitir";
    if (svc.recordKind === SERVICE_RECORD_KIND.TRANSFER) return "Sin confirmar";
    if (svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE) return "Falta voucher";
    return "Pendiente";
}

/**
 * Determina si un servicio esta "confirmado por el operador" para la lógica borrar-vs-cancelar.
 *
 * Usa los mismos estados que esServicioResuelto porque un servicio resuelto implica
 * que el operador ya confirmó el compromiso (emitió ticket, confirmó hotel, etc.).
 * Decisión #9 guia UX: si está confirmado → cancelar (queda tachado); si no → borrar.
 */
export function esServicioConfirmadoPorOperador(svc) {
    return esServicioResuelto(svc);
}

/**
 * Determina si un servicio ya está ANULADO (lo que el backend todavía llama
 * workflowStatus "Cancelado" — nombre histórico del campo; la UI ya no debe decir
 * "Cancelado" para esto: ver etiquetaEstadoServicio más abajo).
 *
 * Un servicio anulado es una TERCERA categoría, aparte de "resuelto" (confirmado por
 * el operador) y "borrador" (todavía sin confirmar): es historia cerrada de la reserva.
 * Puede tener una multa, un ajuste de tipo de cambio o una nota de crédito asociados
 * (mismo criterio que usa el backend en DeleteGuards.cs para rechazar el borrado físico).
 * Por eso nunca debe ofrecer Editar, Borrar, ni volver a Anular/Cancelar — y tampoco
 * botones para "resolverlo" (Marcar Emitido, etc.), que no tienen sentido sobre algo
 * que ya quedó sin efecto.
 */
function esServicioAnulado(svc) {
    return (svc.workflowStatus || svc.status) === 'Cancelado';
}

/**
 * Devuelve la etiqueta de texto del badge de estado del servicio.
 *
 * Regla de negocio (Gaston 2026-06-08):
 *   - En Cotizacion (Quotation) y Presupuesto (Budget) todavia no se le solicito
 *     nada al operador — el badge dice "En espera", no "Solicitado".
 *   - A partir de "En gestion" (InManagement) en adelante, si el workflowStatus
 *     esta vacio o es el valor por defecto, el badge dice "Solicitado" (ya se pidio).
 *   - "Confirmado" siempre se muestra tal cual (llega del backend).
 *
 * ADR-036 (2026-06-21): cuando la reserva entera está deshecha (Lost o Cancelled),
 * TODOS sus servicios muestran "Anulado" — es SOLO presentación, no muta datos.
 * ADR-035 usaba "Cancelado" para Cancelled; ADR-036 unifica ambas en "Anulado"
 * porque en ambos casos la reserva quedó deshecha. La distincion entre Lost/Cancelled
 * la maneja el cartel de estado de la reserva, no los servicios.
 *
 * 2026-06-24 (G1): cuando la reserva pasa a Closed (Finalizada), el backend marca
 * los servicios como "Finalizado". Este estado se muestra como "Finalizado" en verde
 * (completado), NO se tacha (no es cancelación sino cierre exitoso del viaje).
 *
 * Vocabulario "Cancelar" vs "Anular" (2026-07-16, regla del dueño):
 *   El backend todavía llama "Cancelado" al workflowStatus interno de un servicio
 *   individual (nombre histórico del campo), pero en la UI un servicio que se dejó
 *   sin efecto se dice "Anulado" — "Cancelar" en este producto significa que el
 *   cliente pagó el total, y no es el caso acá. Mapeamos el texto ACÁ (mismo patrón
 *   que el override de Lost/Cancelled más arriba), sin tocar el dato del backend.
 *
 * @param {string|null} workflowStatus - Estado workflow del servicio (puede ser null/undefined)
 * @param {string} reservaStatus - Estado de la reserva (ej. "Quotation", "Budget", "InManagement")
 */
function etiquetaEstadoServicio(workflowStatus, reservaStatus) {
    // Override visual para reservas terminales (display-derived — no muta datos del backend).
    // ADR-036: Lost Y Cancelled → "Anulado" (ambas = reserva deshecha, unificamos el termino).
    if (reservaStatus === 'Lost') return 'Anulado';
    if (reservaStatus === 'Cancelled') return 'Anulado';

    // "Finalizado": el backend lo marca cuando la reserva se cierra (Closed).
    // No lo tratamos como cancelado: el viaje fue exitoso, no se tacha.
    if (workflowStatus === 'Finalizado') return 'Finalizado';

    // Servicio individual anulado (workflowStatus "Cancelado" del backend): mostramos
    // "Anulado", igual que hacemos arriba con la reserva completa Lost/Cancelled.
    if (workflowStatus === 'Cancelado') return 'Anulado';

    // "Confirmado" y otros estados concretos del operador (ej. "Emitido", "HK")
    // se muestran tal cual.
    if (workflowStatus && workflowStatus !== 'Solicitado') {
        return workflowStatus;
    }

    // "Solicitado" o vacio: en Cotizacion/Presupuesto todavia no se pidio
    // nada al operador, asi que el texto correcto es "En espera".
    const estaEnEtapaPrevia = reservaStatus === 'Quotation' || reservaStatus === 'Budget';
    return estaEnEtapaPrevia ? 'En espera' : 'Solicitado';
}

/**
 * Color del badge segun la ETIQUETA visible (no solo el workflowStatus).
 * "En espera" (cotizacion/presupuesto, nada pedido aun) va gris neutro, distinto
 * del ambar de "Solicitado" (ya pedido al operador, esperando confirmacion).
 * "Anulado" tiene DOS orígenes con color distinto a propósito:
 *   - Reserva entera Lost/Cancelled: gris sobrio (la reserva no prosperó, nada para
 *     remarcar puntualmente — se chequea PRIMERO, antes de mirar la etiqueta).
 *   - Servicio individual anulado (workflowStatus "Cancelado") dentro de una reserva
 *     que sigue viva: rosa/alerta, como ya era antes de renombrar el texto — suele
 *     implicar plata en juego (multa o nota de crédito) que vale la pena remarcar.
 * "Finalizado" (2026-06-24): verde/slate — viaje exitoso, no es cancelación.
 *   Verde claro en vez del verde intenso de "Confirmado" para distinguirlos.
 */
function claseColorEstadoServicio(workflowStatus, reservaStatus) {
    // Reserva entera deshecha: gris sobrio, sin importar el workflowStatus de este
    // servicio en particular (etiquetaEstadoServicio también le da prioridad a esto).
    if (reservaStatus === 'Lost' || reservaStatus === 'Cancelled') {
        return 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-500';
    }

    const etiqueta = etiquetaEstadoServicio(workflowStatus, reservaStatus);
    if (etiqueta === 'Confirmado') return 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400';
    // Servicio anulado individualmente (antes decía "Cancelado" acá): mismo rosa/alerta
    // de siempre, solo cambió el texto que se muestra.
    if (etiqueta === 'Anulado') return 'bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-400';
    if (etiqueta === 'En espera') return 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400';
    // "Finalizado": verde pálido / slate — el servicio se cumplió, el viaje terminó bien.
    // Se diferencia de "Confirmado" (verde intenso) para no confundir "en camino" con "cerrado".
    if (etiqueta === 'Finalizado') return 'bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-400';
    return 'bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400';
}

/**
 * Formatea una fecha para mostrar en la tabla. Devuelve '-' si el valor
 * no existe o no es una fecha válida (evita que aparezca "Invalid Date").
 *
 * Se usa en la columna Fecha/Estancia para fechas de inicio Y fin,
 * porque Paquete puede no tener fecha de fin informada por el operador.
 */
function formatFechaSegura(valor) {
    if (!valor) return '-';
    const fecha = new Date(valor);
    // getTime() devuelve NaN si la fecha no es válida
    if (Number.isNaN(fecha.getTime())) return '-';
    return fecha.toLocaleDateString('es-AR');
}

/**
 * Modal de confirmación para borrar o anular un servicio.
 *
 * Decisión #9 (guia UX 2026-06-08):
 * - Servicio NO confirmado: texto "¿Borrar?" → llama onBorrar.
 * - Servicio CONFIRMADO: texto "¿Anular?" + campo motivo obligatorio (min 10 chars) → llama onCancelar.
 *
 * El backend exige Reason entre 10 y 1000 caracteres. La validación se hace acá: el botón
 * permanece deshabilitado hasta que el motivo cumpla el mínimo. No se rellena texto automático.
 *
 * Accesibilidad: foco al textarea al montar (camino cancelación), Escape cierra, role="dialog".
 *
 * Factura de la devolución (2026-07-16): si hay 2+ facturas activas y este servicio
 * está adentro de los renglones de UNA sola de ellas (según InvoiceDto.ServicePublicIds),
 * se preselecciona sola con un texto aclaratorio — ver sugerirFacturaParaServicios en
 * lib/serviceInvoiceMatch.js. El usuario siempre puede cambiarla a mano.
 *
 * Props:
 * - service: objeto del servicio
 * - onBorrar: () => void — callback cuando el usuario confirma borrar
 * - onCancelar: (motivo: string) => void — callback cuando confirma cancelar (motivo ya valido)
 * - onClose: () => void
 */
function ModalBorrarVsCancelar({ service, saleInvoices = [], onBorrar, onCancelar, onClose }) {
    const estaConfirmado = esServicioConfirmadoPorOperador(service);
    const [motivo, setMotivo] = useState('');
    const [loading, setLoading] = useState(false);

    // Trazabilidad de origen (2026-07-16): si ESTE servicio aparece en los renglones
    // de una única factura activa, la sugerimos sola (el usuario la puede cambiar).
    // Con una sola factura activa no hace falta sugerencia: ya se autocompletaba sola
    // (regla anterior, sin ambigüedad posible). Con varias, antes el usuario tenía que
    // adivinar; ahora, si sabemos el origen, se lo evitamos.
    // Usamos el MISMO helper de identidad que CancelarVariosServiciosInline (cubre
    // servicios que traen el GUID en otra propiedad, ej. id/servicePublicId).
    const publicIdFacturaSugerida = sugerirFacturaParaServicios(
        [getReservationServicePublicId(service)],
        saleInvoices
    );
    const [targetInvoicePublicId, setTargetInvoicePublicId] = useState(
        saleInvoices.length === 1 ? saleInvoices[0].publicId : (publicIdFacturaSugerida || '')
    );
    const motivoInputRef = useRef(null);

    // Regla del backend: el motivo de cancelación debe tener entre 10 y 1000 caracteres.
    // Validamos acá para no mandar texto inventado — el hook ya no rellena automáticamente.
    const MOTIVO_MIN_CHARS = 10;
    const motivoValido = motivo.trim().length >= MOTIVO_MIN_CHARS;

    // Al abrir el modal de cancelación (servicio confirmado), el foco va directo al textarea
    // para que el usuario pueda tipear el motivo sin hacer clic primero.
    // Solo corre cuando estaConfirmado=true porque en el camino "borrar" no hay textarea.
    useEffect(() => {
        if (estaConfirmado && motivoInputRef.current) {
            motivoInputRef.current.focus();
        }
    // useEffect con [] corre solo al montar el componente (una sola vez).
    // estaConfirmado no cambia durante la vida del modal; motivoInputRef es estable.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    const handleCerrarConEscape = (e) => {
        if (e.key === 'Escape') onClose();
    };

    const handleConfirmar = async () => {
        setLoading(true);
        try {
            if (estaConfirmado) {
                await onCancelar(motivo.trim(), {
                    targetInvoicePublicId: targetInvoicePublicId || undefined,
                    confirmedGrossCreditAmount: Number(service?.salePrice || 0),
                });
            } else {
                await onBorrar();
            }
        } finally {
            setLoading(false);
        }
    };

    return (
        <div
            className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200"
            role="dialog"
            aria-modal="true"
            aria-label={estaConfirmado ? 'Anular servicio' : 'Borrar servicio'}
            onKeyDown={handleCerrarConEscape}
        >
            <div className="w-full max-w-sm rounded-2xl border bg-white shadow-2xl dark:bg-slate-900 dark:border-slate-800">
                {/* Header */}
                <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
                    <div className="flex items-center gap-2">
                        <Trash2 className="h-4 w-4 text-slate-500" />
                        <h3 className="font-bold text-slate-900 dark:text-white">
                            {estaConfirmado ? 'Anular servicio' : 'Borrar servicio'}
                        </h3>
                    </div>
                    <button
                        type="button"
                        onClick={onClose}
                        aria-label="Cerrar"
                        className="text-slate-400 hover:text-slate-600 transition-colors dark:hover:text-slate-200"
                    >
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <div className="p-6 space-y-4">
                    {estaConfirmado ? (
                        // Servicio ya confirmado por el operador: NO se puede borrar, solo anular.
                        // Quedará tachado en la lista con quién/cuándo.
                        <>
                            <p className="text-sm text-slate-600 dark:text-slate-300">
                                <span className="font-bold">Este servicio ya está confirmado.</span>{' '}
                                Al anularlo, queda tachado en la reserva (no desaparece) — hubo un compromiso real con el operador y puede haber penalidad o saldo a devolver al cliente.
                            </p>
                            <div>
                                <label
                                    htmlFor="motivo-cancelacion-servicio"
                                    className="mb-1 block text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400"
                                >
                                    Motivo
                                </label>
                                <textarea
                                    id="motivo-cancelacion-servicio"
                                    ref={motivoInputRef}
                                    value={motivo}
                                    onChange={(e) => setMotivo(e.target.value)}
                                    placeholder="¿Por qué se anula este servicio?"
                                    rows={2}
                                    className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                                />
                                {/* Helper visible solo cuando el usuario empezó a tipear pero aún no llega al mínimo.
                                    No lo mostramos desde el inicio para no ser intrusivos antes de que el usuario toque el campo. */}
                                {motivo.length > 0 && !motivoValido && (
                                    <p className="mt-1 text-xs text-amber-600 dark:text-amber-400">
                                        Mínimo {MOTIVO_MIN_CHARS} caracteres ({motivo.trim().length}/{MOTIVO_MIN_CHARS})
                                    </p>
                                )}
                            </div>
                            {saleInvoices.length > 1 && (
                                <div>
                                    <label htmlFor="factura-destino-devolucion" className="mb-1 block text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                                        Factura de la devolución
                                    </label>
                                    <select
                                        id="factura-destino-devolucion"
                                        value={targetInvoicePublicId}
                                        onChange={(e) => setTargetInvoicePublicId(e.target.value)}
                                        className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                                        data-testid="select-factura-devolucion"
                                        aria-describedby={
                                            // Accesibilidad: el lector de pantalla anuncia la aclaración
                                            // de la sugerencia como descripción del propio desplegable.
                                            publicIdFacturaSugerida && targetInvoicePublicId === publicIdFacturaSugerida
                                                ? "hint-factura-sugerida-cancelar-servicio"
                                                : undefined
                                        }
                                    >
                                        <option value="">Elegí una factura</option>
                                        {saleInvoices.map((invoice) => <option key={invoice.publicId} value={invoice.publicId}>{invoice.label}</option>)}
                                    </select>
                                    {/* Solo mientras la selección actual siga siendo la sugerida: si el
                                        usuario elige otra factura a mano, el texto deja de aplicar. */}
                                    {publicIdFacturaSugerida && targetInvoicePublicId === publicIdFacturaSugerida && (
                                        <p id="hint-factura-sugerida-cancelar-servicio" className="mt-1 text-xs text-emerald-600 dark:text-emerald-400" data-testid="hint-factura-sugerida">
                                            Este servicio está incluido en esta factura.
                                        </p>
                                    )}
                                </div>
                            )}
                            {saleInvoices.length > 0 && (
                                <p className="text-sm text-slate-600 dark:text-slate-300">
                                    Monto de la devolución: <strong>{Number(service?.salePrice || 0).toLocaleString('es-AR', { minimumFractionDigits: 2 })} {service?.currency || 'ARS'}</strong>
                                </p>
                            )}
                        </>
                    ) : (
                        // Servicio no confirmado: se puede borrar directo (era un borrador).
                        <p className="text-sm text-slate-600 dark:text-slate-300">
                            <span className="font-bold">¿Borrar este servicio?</span>{' '}
                            El operador todavía no lo confirmó, así que desaparece de la reserva sin ningún trámite adicional.
                        </p>
                    )}
                </div>

                <div className="flex justify-end gap-3 border-t border-slate-100 px-6 py-4 dark:border-slate-800">
                    <button
                        type="button"
                        onClick={onClose}
                        disabled={loading}
                        className="rounded-lg px-4 py-2 text-sm font-bold text-slate-600 hover:bg-slate-100 disabled:opacity-50 transition-colors dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                        Volver
                    </button>
                    <button
                        type="button"
                        onClick={handleConfirmar}
                        // En el camino de anulación (servicio confirmado) exigimos motivo válido.
                        // En el camino de borrado no hay textarea, así que no aplica la restricción.
                        disabled={loading || (estaConfirmado && (!motivoValido || (saleInvoices.length > 1 && !targetInvoicePublicId)))}
                        data-testid={estaConfirmado ? 'btn-confirm-cancel-service' : 'btn-confirm-delete-service'}
                        className={`flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-bold text-white transition-colors disabled:opacity-50 ${
                            estaConfirmado
                                ? 'bg-amber-600 hover:bg-amber-700'
                                : 'bg-rose-600 hover:bg-rose-700'
                        }`}
                    >
                        {loading && <Loader2 className="h-4 w-4 animate-spin" />}
                        {estaConfirmado ? 'Anular servicio' : 'Sí, borrar'}
                    </button>
                </div>
            </div>
        </div>
    );
}

/**
 * Modal que aparece cuando el backend rechaza la anulacion de un servicio con 409.
 * Antes de la obra "anular sin factura" (2026-07-23) esto podia pasar por 3 motivos: voucher
 * emitido vivo, pago al operador sin factura de venta (freno de plata R1), o factura viva sin
 * cliente asignado. El freno R1 DESAPARECIO por decision del dueno — anular un servicio
 * procede directo aunque tenga pagos al operador sin factura — asi que hoy este modal solo
 * puede aparecer por voucher vivo o factura viva sin cliente. El mensaje descriptivo viene
 * del backend TAL CUAL (no se reescribe en el front).
 *
 * Accesibilidad: foco al boton "Entendido" al montar, Escape cierra, role="dialog".
 *
 * Props:
 * - mensaje: string con el detalle del error que mando el backend
 * - rechazo: { codigoConocido: boolean, boton: "ver-vouchers"|null } — salida de
 *   resolverRechazoAnularServicio. Si el codigo NO es ninguno de los catalogados (backend
 *   viejo sin `code`, u otra carrera fuera de lo esperado), el modal muestra SOLO el mensaje
 *   real del motor — que ya viene curado en criollo — sin ningun boton extra.
 * - onIrAVouchers: () => void — navega a la solapa "Vouchers" (motivo voucher vivo)
 * - onClose: () => void
 */
function ModalBloqueoCancelacionServicio({ mensaje, rechazo, onIrAVouchers, onClose }) {
    // Si no llega ningun rechazo estructurado (por si algun caller viejo sigue pasando
    // solo el mensaje), tratamos como "codigo no conocido" — mismo camino de respaldo.
    const codigoConocido = rechazo?.codigoConocido === true;
    const boton = rechazo?.boton ?? null;
    // Al abrir el modal de bloqueo, enfocamos el botón "Entendido" para que el usuario
    // pueda cerrarlo con Enter o con Escape sin tener que mover el mouse.
    const entendidoButtonRef = useRef(null);

    useEffect(() => {
        if (entendidoButtonRef.current) {
            entendidoButtonRef.current.focus();
        }
    // useEffect con [] corre solo al montar (una vez por apertura del modal).
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    const handleCerrarConEscape = (e) => {
        if (e.key === 'Escape') onClose();
    };

    return (
        <div
            data-testid="modal-bloqueo-cancelacion"
            className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200"
            role="dialog"
            aria-modal="true"
            aria-labelledby="bloqueo-cancelacion-titulo"
            onKeyDown={handleCerrarConEscape}
        >
            <div className="w-full max-w-md rounded-2xl border bg-white shadow-2xl dark:bg-slate-900 dark:border-slate-800">
                {/* Header */}
                <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
                    <div className="flex items-center gap-2">
                        <Ban className="h-4 w-4 text-rose-500" />
                        <h3
                            id="bloqueo-cancelacion-titulo"
                            className="font-bold text-slate-900 dark:text-white"
                        >
                            No se puede anular el servicio
                        </h3>
                    </div>
                    <button
                        type="button"
                        onClick={onClose}
                        aria-label="Cerrar"
                        className="text-slate-400 hover:text-slate-600 transition-colors dark:hover:text-slate-200"
                    >
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <div className="p-6 space-y-4">
                    {/* El mensaje viene del backend con el detalle real (voucher vivo, o factura
                        viva sin cliente asignado) — se muestra TAL CUAL, sin reescribirlo. */}
                    <div className="rounded-xl border border-rose-100 bg-rose-50 px-4 py-3 text-sm text-rose-800 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-200">
                        {mensaje}
                    </div>
                </div>

                <div className="flex flex-col sm:flex-row justify-end gap-3 border-t border-slate-100 px-6 py-4 dark:border-slate-800">
                    <button
                        ref={entendidoButtonRef}
                        type="button"
                        onClick={onClose}
                        className="rounded-lg px-4 py-2 text-sm font-bold text-slate-600 hover:bg-slate-100 transition-colors dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                        Entendido
                    </button>
                    {/* Motivo voucher vivo — único botón de camino que le queda a este modal
                        desde la obra "anular sin factura" (2026-07-23): navega a la solapa
                        correcta (Vouchers, no Estado de Cuenta). */}
                    {boton === "ver-vouchers" && onIrAVouchers && (
                        <button
                            type="button"
                            data-testid="btn-ir-a-vouchers"
                            onClick={() => {
                                onIrAVouchers();
                                onClose();
                            }}
                            className="inline-flex items-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-bold text-white transition-colors hover:bg-indigo-700"
                        >
                            <FileText className="h-4 w-4" />
                            Ver vouchers de la reserva
                        </button>
                    )}
                </div>
            </div>
        </div>
    );
}

/**
 * Formatea una fecha ISO en texto corto legible (ej. "08/06/2026").
 * Retorna null si la fecha no es válida o no existe.
 */
function formatFechaCancelacion(valorFecha) {
    if (!valorFecha) return null;
    const fecha = new Date(valorFecha);
    if (Number.isNaN(fecha.getTime())) return null;
    return fecha.toLocaleDateString('es-AR', { day: '2-digit', month: '2-digit', year: 'numeric' });
}

/**
 * Boton para marcar un vuelo como emitido.
 * Decision 3 (guia UX 2026-06-08): boton en la misma fila, texto "Marcar emitido".
 * Endpoint: POST /api/reservas/{reservaId}/flights/{id}/mark-issued
 */
function BotonMarcarEmitido({ reservaId, servicePublicId, onResuelto }) {
    const [loading, setLoading] = useState(false);

    const handleClick = async (e) => {
        e.stopPropagation();
        setLoading(true);
        try {
            await api.post(`/reservas/${reservaId}/flights/${servicePublicId}/mark-issued`);
            showSuccess("Vuelo marcado como emitido");
            if (onResuelto) onResuelto();
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo marcar el vuelo como emitido."));
        } finally {
            setLoading(false);
        }
    };

    return (
        <button
            type="button"
            onClick={handleClick}
            disabled={loading}
            data-testid="btn-mark-issued"
            className="inline-flex items-center gap-1 rounded-lg border border-emerald-200 bg-emerald-50 px-2 py-1 text-[10px] font-bold text-emerald-700 transition-colors hover:bg-emerald-100 disabled:opacity-50 dark:border-emerald-800 dark:bg-emerald-950/30 dark:text-emerald-300"
        >
            <CheckCircle2 className="h-3 w-3" />
            {loading ? "..." : "Marcar emitido"}
        </button>
    );
}

/**
 * Boton para marcar un traslado como "no requiere confirmacion".
 * Decision 3 (guia UX 2026-06-08): boton en la misma fila, texto "No requiere confirmacion".
 * Endpoint: POST /api/reservas/{reservaId}/transfers/{id}/no-confirmation
 */
function BotonNoRequiereConfirmacion({ reservaId, servicePublicId, onResuelto }) {
    const [loading, setLoading] = useState(false);

    const handleClick = async (e) => {
        e.stopPropagation();
        setLoading(true);
        try {
            await api.post(`/reservas/${reservaId}/transfers/${servicePublicId}/no-confirmation`);
            showSuccess("Traslado marcado como que no requiere confirmacion");
            if (onResuelto) onResuelto();
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo registrar el traslado."));
        } finally {
            setLoading(false);
        }
    };

    return (
        <button
            type="button"
            onClick={handleClick}
            disabled={loading}
            data-testid="btn-no-confirmation"
            className="inline-flex items-center gap-1 rounded-lg border border-slate-200 bg-slate-50 px-2 py-1 text-[10px] font-bold text-slate-600 transition-colors hover:bg-slate-100 disabled:opacity-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300"
        >
            <Clock className="h-3 w-3" />
            {loading ? "..." : "No requiere confirmacion"}
        </button>
    );
}

/**
 * Resumen de resolucion de servicios para la vista "En gestion" (decision 4 de UX).
 * Muestra "X de Y servicios resueltos" con pelotita verde (resuelto) / amarilla (pendiente) por fila.
 * Solo aparece cuando la reserva esta en estado InManagement.
 */
function ResumenServiciosResueltos({ services, reservaStatus }) {
    if (reservaStatus !== 'InManagement') return null;

    // No contar servicios cancelados
    const activos = services.filter(s => (s.workflowStatus || '') !== 'Cancelado');
    if (activos.length === 0) return null;

    const resueltos = activos.filter(esServicioResuelto).length;

    return (
        <div
            className="mb-4 flex items-center gap-3 rounded-xl border border-cyan-200 bg-cyan-50 px-4 py-2.5 text-sm dark:border-cyan-900/40 dark:bg-cyan-950/20"
            data-testid="resumen-servicios-resueltos"
            data-resolved={resueltos}
            data-total={activos.length}
        >
            <span className="font-bold text-cyan-800 dark:text-cyan-200">
                {resueltos} de {activos.length} {activos.length === 1 ? 'servicio resuelto' : 'servicios resueltos'}
            </span>
            {resueltos === activos.length ? (
                <span className="text-xs text-cyan-600 dark:text-cyan-400">
                    Todos listos — la reserva se va a confirmar automaticamente.
                </span>
            ) : (
                <span className="text-xs text-cyan-600 dark:text-cyan-400">
                    Cuando se resuelvan todos, la reserva se confirma sola.
                </span>
            )}
        </div>
    );
}

/**
 * Icono representativo del tipo de servicio en la lista.
 * Cada tipo tiene su color canonico para diferenciarlos visualmente de un vistazo.
 */
function ServiceIcon({ service, className = "w-4 h-4 mr-2" }) {
    if (service.recordKind === SERVICE_RECORD_KIND.FLIGHT) {
        return <Plane className={`${className} text-sky-500`} />;
    }
    if (service.recordKind === SERVICE_RECORD_KIND.HOTEL) {
        return <Hotel className={`${className} text-amber-500`} />;
    }
    if (service.recordKind === SERVICE_RECORD_KIND.TRANSFER) {
        return <Car className={`${className} text-emerald-500`} />;
    }
    // Asistencia / seguro de viaje: escudo de proteccion en azul (color "confianza")
    if (service.recordKind === SERVICE_RECORD_KIND.ASSISTANCE) {
        return <ShieldCheck className={`${className} text-blue-500`} />;
    }

    return <Package className={`${className} text-violet-500`} />;
}

/**
 * Chip discreto "Operador: X" debajo del nombre del servicio (decisión 3, spec 2026-07-03).
 * Con permiso de ver proveedores es un link a la ficha del operador; sin permiso, el mismo
 * texto pero SIN link (el nombre del operador no es un dato de costo, no se enmascara — la
 * única diferencia por permiso es si se puede navegar a la ficha o no).
 *
 * No se muestra nada si el servicio no tiene operador asignado (svc.supplierName vacío).
 */
function ServiceSupplierChip({ supplierName, supplierPublicId, puedeVerProveedores, testId }) {
    if (!supplierName) return null;

    // Sin publicId no hay a dónde linkear (dato legacy/incompleto): degrada a texto plano
    // aunque el usuario tenga permiso, en vez de armar un link roto.
    if (puedeVerProveedores && supplierPublicId) {
        return (
            <Link
                to={`/suppliers/${supplierPublicId}/account`}
                className="mt-0.5 inline-flex w-fit items-center gap-1 text-[10px] text-indigo-600 dark:text-indigo-400 hover:underline"
                data-testid={testId}
            >
                Operador: {supplierName}
            </Link>
        );
    }

    return (
        <div className="mt-0.5 text-[10px] text-slate-400 dark:text-slate-500" data-testid={testId}>
            Operador: {supplierName}
        </div>
    );
}

/**
 * Mini-formulario inline que aparece debajo de un servicio cuando faltan datos de pasajeros.
 *
 * Guía UX 2026-06-15 (P4b, P5):
 *   - Pantalla D (Aéreo): aparece cuando faltan nombre o documento de algún pasajero declarado.
 *   - Pantalla E (Hotel/Traslado): aparece cuando el titular no tiene nombre.
 *   - NUNCA ventana flotante: siempre en línea, debajo del servicio.
 *   - Cuando todos los slots quedan completos, el mini-formulario desaparece.
 *
 * Fix B1 (ADR-031 v2.1 review): usa el SET del servicio para calcular los slots faltantes.
 *   Si el servicio tiene asignaciones explícitas ("2 de 3"), solo pide esos 2.
 *   Si el servicio es "Para: Todos", pide todos los que falten.
 *   Antes usaba calcularSlotsFaltantes sobre TODOS los pasajeros de la reserva — incorrecto.
 *
 * Props:
 *   reservaId          — publicId de la reserva
 *   reserva            — objeto reserva (para pasajeros completos: se pasan como pasajerosCompletos)
 *   servicio           — objeto del servicio (para saber el recordKind y el label)
 *   coverage           — ServiceNominalCoverageDto del backend (del render-prop ConCoverageDeServicio)
 *   onPasajeroGuardado — callback() tras guardar un pasajero (el padre recarga)
 */
function MiniFormularioPasajerosFaltantes({ reservaId, reserva, servicio, coverage, onPasajeroGuardado }) {
    const [slotAbierto, setSlotAbierto] = useState(null);

    // Fix B1: usamos calcularSlotsFaltantesDelSet en vez de calcularSlotsFaltantes.
    // La diferencia clave: calcularSlotsFaltantesDelSet trabaja sobre el SET del servicio
    // que ya resolvió el backend (hasExplicitAssignments + serviceSet[]).
    // Un servicio "2 de 3" solo pide los 2 nombres, no los 3.
    // Si coverage aún no llegó → lista vacía → el mini-form no se muestra todavía.
    const slotsFaltantes = calcularSlotsFaltantesDelSet(
        coverage,
        reserva?.passengers || [],
    );

    // Si no hay slots faltantes, no mostramos nada (el botón ya estará habilitado).
    if (slotsFaltantes.length === 0) return null;

    // Determinamos el modo del mini-formulario según el tipo de servicio.
    // El "mode" controla qué campos se piden (nombre, documento, fecha de nacimiento).
    function modeDelServicio(recordKind) {
        switch (recordKind) {
            case "flight": return "flight";
            case "hotel": return "hotel";
            case "transfer": return "transfer";
            case "assistance": return "assistance";
            case "package": return "package";
            default: return "full";
        }
    }

    const mode = modeDelServicio(servicio.recordKind);

    return (
        <tr
            className="bg-amber-50/60 dark:bg-amber-950/10"
            data-testid={`mini-form-pasajeros-${getReservationServicePublicId(servicio)}`}
        >
            <td colSpan={20} className="px-4 pb-4 pt-2">
                {/* Encabezado del mini-formulario */}
                <div className="mb-3 flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-amber-700 dark:text-amber-400">
                    <UserX className="h-4 w-4" aria-hidden="true" />
                    Faltan datos para emitir — cargá los pasajeros que faltan:
                </div>

                {/* Un mini-formulario por cada slot faltante */}
                <div className="space-y-2">
                    {slotsFaltantes.map((slot, i) => {
                        const esteSlotAbierto = slotAbierto === i;

                        return (
                            <div key={`${slot.slot}-${i}`}>
                                {/* Mostrar botón "Completar" si el slot no está expandido */}
                                {!esteSlotAbierto && (
                                    <button
                                        type="button"
                                        onClick={() => setSlotAbierto(i)}
                                        className="inline-flex items-center gap-1.5 rounded-lg border border-amber-300 bg-amber-100 px-3 py-1.5 text-xs font-bold text-amber-700 hover:bg-amber-200 dark:border-amber-700 dark:bg-amber-900/30 dark:text-amber-300"
                                    >
                                        <Plus className="h-3 w-3" aria-hidden="true" />
                                        {slot.slot}
                                    </button>
                                )}

                                {/* Mini-formulario expandido */}
                                {esteSlotAbierto && (
                                    <PasajeroInlineForm
                                        reservaId={reservaId}
                                        passengerToEdit={slot.passenger}
                                        slotLabel={slot.slot}
                                        mode={mode}
                                        onGuardado={() => {
                                            setSlotAbierto(null);
                                            // El padre recarga para que el hint se recalcule
                                            // con los datos frescos del backend.
                                            onPasajeroGuardado?.();
                                        }}
                                        onCancelar={() => setSlotAbierto(null)}
                                    />
                                )}
                            </div>
                        );
                    })}
                </div>
            </td>
        </tr>
    );
}

/**
 * Props:
 *   services                        — lista de servicios normalizados
 *   serviceCollectionErrors         — objeto { tipoKey: mensajeError } para mostrar errores de carga
 *   onAddService                    — callback para agregar un servicio nuevo
 *   onEditService                   — callback(service) para editar un servicio existente
 *   onDeleteService                 — callback(service) para eliminar un servicio (NO confirmado por operador)
 *   onCancelService                 — callback async (service, motivo|null) → { ok, result?, error? }
 *                                     Cancela un servicio confirmado por el operador. Retorna el resultado
 *                                     del backend para actualizar el contador sin hacer fetch completo.
 *   reservaId                       — publicId de la reserva (necesario para los endpoints confirm-cost y resolver)
 *   reservaStatus                   — status actual de la reserva (para mostrar resumen InManagement y pelotitas)
 *   isCatalogFindOrCreateEnabled    — flag catálogo: cuando es false, el render es IDÉNTICO al original
 *   isServiceDeadlineAlertsEnabled  — flag avisos: cuando es true, muestra columna "Avisos" (UpcomingStartPill).
 *                                     Es INDEPENDIENTE del flag de catálogo (decisión del dueño).
 *   windowDays                      — int|null, días de ventana para las pills (upcomingStartsWindowDays del contexto)
 *   onServiceConfirmed              — callback(servicioActualizado) cuando confirm-cost tiene éxito
 *   onServiceResolved               — callback() cuando un servicio se resuelve (marcar emitido / no requiere confirmacion)
 *                                     El padre recarga la reserva para reflejar el nuevo estado.
 *   onIrAFacturas                   — callback () => void para navegar a la solapa "Estado de Cuenta" (facturas).
 *                                     Se usa en el modal de bloqueo 409 del flujo individual y también se
 *                                     reenvía a la sección "Anular varios" (Tanda 4: misma paridad de ayuda
 *                                     en filas fallidas por bloqueo fiscal).
 *                                     Opcional; si no se pasa, el botón no aparece.
 *   onIrAVouchers                   — callback () => void: navega a la solapa "Vouchers" de la reserva
 *                                     (Tanda 7). Se usa en el modal de bloqueo 409 cuando el motivo es
 *                                     "voucher emitido vivo". Opcional; si no se pasa, el botón no aparece.
 *   canCancelServices               — bool: si el usuario tiene permiso reservas.cancel (UI-only gate).
 *                                     El server siempre re-valida. Si no se pasa, el botón no aparece.
 *   serviceCancellationBlockReason  — string|null: motivo de bloqueo fiscal a nivel reserva (ADR-025).
 *                                     Si no es null, toda la reserva está bloqueada para cancelaciones.
 *                                     Se propaga a la sección inline "Anular varios".
 *   onCancelacionVariosTerminada    — callback () => void: el padre recarga la reserva al terminar.
 *   pasajerosConNombre              — Array de pasajeros que ya tienen fullName cargado.
 *                                     Necesario para el control "Para: Todos" (Pieza A ADR-031 v2.1).
 *                                     Si no se pasa (o vacío), el control aparece en modo deshabilitado.
 *   onRequestEdit                   — callback () => void: abre la ventana de destrabar
 *                                     (EditAuthorizationModal). Candado C1 (spec 2026-07-22): se usa
 *                                     cuando la reserva está bloqueada sin autorización viva — los
 *                                     botones de escritura de esta lista (Agregar, Editar, Anular
 *                                     servicio/varios, Confirmar costo) quedan gris + candadito y, al
 *                                     tocarlos, abren esta ventana en vez de ejecutar la acción.
 */
export function ServiceList({
    services,
    serviceCollectionErrors = {},
    onAddService,
    onEditService,
    onDeleteService,
    onCancelService,
    saleInvoices = [],
    reservaId,
    reservaStatus,
    // reserva: objeto completo de la reserva. Necesario para el hint de pasajeros
    // (adultCount/childCount/infantCount y la lista de passengers ya cargados).
    // Si no se pasa, los botones de resolución/emisión no quedan gateados por pasajeros.
    reserva = null,
    // capabilities: CapabilityDto del backend { canEditServices, canCancel, ... }.
    // Cada campo es { allowed: boolean, reason: string | null }.
    // Si no llega (DTO viejo), degradamos mostrando los botones (comportamiento anterior).
    // Guía UX 2026-06-22: los botones de escritura se ocultan en solo lectura, y la fuente
    // de verdad es el backend (no re-derivamos el estado en el front).
    capabilities = null,
    isCatalogFindOrCreateEnabled = false,
    isServiceDeadlineAlertsEnabled = false,
    windowDays = null,
    onServiceConfirmed,
    onServiceResolved,
    onIrAFacturas,
    onIrAVouchers,
    // Multimoneda (2026-06-11): true cuando la reserva mezcla servicios en ARS y USD.
    // Cuando es true se muestra el cartelito $/US$ en cada precio y la fila TOTAL al pie.
    // Regla ③: con false (o sin el prop) la lista se ve igual que siempre.
    esMultimoneda = false,
    // ADR-025: "Anular varios" en línea.
    canCancelServices = false,
    serviceCancellationBlockReason = null,
    onCancelacionVariosTerminada,
    // Callback para cuando se guarda un pasajero desde el mini-formulario inline.
    // El padre recarga la reserva para que el hint se actualice con datos frescos.
    onPasajeroGuardado,
    // ADR-031 v2.1 — Pieza A: pasajeros con nombre para el control "Para: Todos".
    // El padre filtra reserva.passengers por los que tienen fullName.
    pasajerosConNombre = [],
    // Candado C1 (spec 2026-07-22): abre la ventana de destrabar cuando se toca un botón
    // gris + candadito de esta lista.
    onRequestEdit,
}) {
    // ── ADR-036 punto 4c: estado de pago al operador por servicio ─────────────────
    // Cargamos el estado de pago al montar. Si falla, degradamos silenciosamente:
    // statusDto queda null y los badges simplemente no aparecen (no rompemos la solapa).
    //
    // Fix E2E P3 (2026-07-22): le pasamos `reserva` como segundo argumento (refreshSignal).
    // ReservaDetailPage arma un objeto `reserva` NUEVO cada vez que corre `fetchReserva`
    // (edición de servicio, pago, cancelación, etc. — ver useReservaDetail.js), así que esta
    // referencia cambia exactamente en los mismos momentos en que la lista de servicios se
    // recarga. Reusamos esa misma señal en vez de armar un canal de recarga aparte: cuando
    // `reserva` cambia, el hook vuelve a pedir el estado de pagos al operador, y el badge dice
    // lo mismo que la fila (antes de este fix quedaba con el dato viejo hasta hacer F5).
    const { statusDto: pagoOperadorDto, loading: pagoOperadorLoading } = useReservaSupplierPaymentStatus(reservaId, reserva);

    // amountsVisible viene del DTO raíz: el backend ya sabe si el usuario tiene
    // cobranzas.see_cost y enmascara los montos a 0 cuando no lo tiene.
    const pagoOperadorAmountsVisible = puedenVerseMontos(pagoOperadorDto);

    // ── Guía UX 2026-06-22: botones de escritura ──────────────────────────────────
    // "Agregar / Editar" se ocultan cuando canEditServices.allowed === false.
    // "Anular servicio" se oculta cuando canCancelServices.allowed === false (2026-06-24).
    //   canCancelServices = true SOLO en {En gestión, Confirmada}.
    //   En Cotización/Presupuesto es false → la acción "Anular" no aparece; solo "Borrar".
    //   El ModalBorrarVsCancelar también lee esServicioConfirmadoPorOperador: si el servicio
    //   no está resuelto, el modal muestra "Borrar" aunque canCancelServices fuese true.
    //   Ambas condiciones trabajan juntas sin duplicar lógica de negocio.
    // Si no hay capabilities (DTO viejo), degradamos mostrando los botones (fallback seguro).
    const puedeEditarServicios = capabilities
        ? (capabilities.canEditServices?.allowed !== false)
        : true;
    const puedeCancelarServicios = capabilities
        // G3 (2026-06-24): usamos canCancelServices específica para servicios.
        // Fallback a canCancel (campo más viejo) si el backend no mandó canCancelServices todavía.
        ? (capabilities.canCancelServices?.allowed ?? capabilities.canCancel?.allowed ?? true)
        : true;

    // ─── Candado C1 (spec UX 2026-07-22) ─────────────────────────────────────────
    // Reserva Confirmada SIN autorización de edición viva: los botones de escritura de
    // esta lista (Agregar, Editar, Anular servicio/varios, Confirmar costo) se muestran
    // "gris + candadito" en vez de encendidos. Al tocarlos, en vez de ejecutar la acción,
    // abren la misma ventana de destrabar que ya usa la franja ámbar de arriba.
    const candadoDeEdicionActivo = tieneCandadoDeEdicionActivo(reserva);
    // Gate de costo: con flag OFF se usa isAdmin() (comportamiento original).
    // Con flag ON se usa hasPermission("cobranzas.see_cost") — admin pasa igual (bypass en hasPermission).
    const mostrarCosto = isCatalogFindOrCreateEnabled
        ? hasPermission("cobranzas.see_cost")
        : isAdmin();

    // Solo quien ve costos Y tiene flag ON puede interactuar con confirm-cost.
    const puedeConfirmarCosto = isCatalogFindOrCreateEnabled && mostrarCosto;

    // Decisión 3 (spec 2026-07-03): gate del chip "Operador: X" — mismo permiso que muestra/oculta
    // la entrada "Operadores" en el menú principal (proveedores.view). El nombre del operador NO es
    // un dato de costo (no usa cobranzas.see_cost); solo decide si el chip es un link o texto plano.
    const puedeVerProveedores = hasPermission("proveedores.view");

    const collectionErrorMessages = Object.values(serviceCollectionErrors).filter(Boolean);

    // Estado del modal de borrar vs cancelar (decisión #9 guia UX 2026-06-08).
    // El modal se abre al presionar la papelera de cualquier servicio.
    const [modalBorrarCancelar, setModalBorrarCancelar] = useState(null);

    // Estado del modal de bloqueo fiscal 409: se abre cuando el backend rechaza la
    // anulacion de un servicio (voucher vivo, o factura viva sin cliente asignado —
    // Tanda 7, 2026-07-20; el freno por "pago al operador sin factura" se eliminó en la
    // obra "anular sin factura", 2026-07-23).
    // Guarda { mensaje, rechazo } — el texto real del backend y el botón que corresponde
    // según el `code` (resolverRechazoAnularServicio).
    const [modalBloqueo409, setModalBloqueo409] = useState(null);

    // ADR-025: visibilidad de la sección inline "Anular varios" (el nombre interno del
    // estado sigue diciendo "CancelarVarios" — es un identificador de código, no texto
    // visible; ver esServicioAnulado para el porqué del vocabulario "Anular").
    // Solo se muestra cuando el usuario lo solicita con el botón "Anular varios".
    const [showCancelarVarios, setShowCancelarVarios] = useState(false);

    // Servicios "anulables": los que tienen proveedor asignado Y no están ya anulados.
    // Estos son los candidatos para mostrar en la sección "Anular varios".
    // Misma lógica que calculateServiciosCanceladosResumen para determinar "con proveedor".
    const serviciosCancelables = (services || []).filter((svc) => {
        const tieneProveedor = Boolean(svc.supplierPublicId || svc.supplierId || svc.supplierName);
        const esTipoEspecifico = svc.recordKind && svc.recordKind !== 'generic';
        const estaCancelado = (svc.workflowStatus || svc.status) === 'Cancelado';
        return (tieneProveedor || esTipoEspecifico) && !estaCancelado;
    });

    /**
     * Maneja el clic en la papelera de un servicio.
     * Decide solo si mostrar el modal de "¿Borrar?" o "¿Anular?" según si el servicio
     * está confirmado por el operador (esServicioResuelto).
     * Decisión #9 (guia UX 2026-06-08).
     */
    const handleTrashClick = useCallback((svc) => {
        setModalBorrarCancelar(svc);
    }, []);

    const handleModalBorrar = useCallback(async () => {
        if (!modalBorrarCancelar) return;
        if (onDeleteService) {
            await onDeleteService(modalBorrarCancelar);
        }
        setModalBorrarCancelar(null);
    }, [modalBorrarCancelar, onDeleteService]);

    const handleModalCancelar = useCallback(async (motivo, creditSelection) => {
        if (!modalBorrarCancelar) return;
        if (onCancelService) {
            const respuesta = await onCancelService(modalBorrarCancelar, motivo, creditSelection);

            // Si la cancelación fue bloqueada por el backend (409), mostramos el modal
            // explicativo en vez del toast genérico de error.
            // El 409 ocurre por voucher vivo o factura viva sin cliente asignado (Tanda 7;
            // el freno por "pago al operador sin factura" se eliminó, obra 2026-07-23).
            // resolverRechazoAnularServicio lee el `code` del body para elegir el botón
            // correcto — el mensaje sigue viniendo del backend tal cual.
            if (!respuesta?.ok && respuesta?.error?.status === 409) {
                setModalBorrarCancelar(null);
                setModalBloqueo409({
                    mensaje: getApiErrorMessage(respuesta.error, 'No se puede anular el servicio en este momento.'),
                    rechazo: resolverRechazoAnularServicio(respuesta.error),
                });
                return;
            }

            // Cualquier otro error: mostramos toast genérico (el hook ya no lo muestra).
            if (!respuesta?.ok && respuesta?.error) {
                showError(getApiErrorMessage(respuesta.error, 'No se pudo anular el servicio.'));
            }
        }
        setModalBorrarCancelar(null);
    }, [modalBorrarCancelar, onCancelService]);

    /**
     * Fábrica de callbacks confirm-cost por servicio.
     * El DTO que devuelve el backend NO tiene recordKind (lo agrega el frontend al normalizar).
     * Por eso necesitamos pasarle el recordKind original del servicio al padre para que
     * pueda hacer el upsert en la colección correcta del snapshot de la reserva.
     */
    const crearCallbackConfirmado = useCallback((recordKind) => {
        return (servicioActualizado) => {
            if (onServiceConfirmed) {
                // Pasamos el recordKind junto al DTO para que el padre sepa en qué colección insertar
                onServiceConfirmed(servicioActualizado, recordKind);
            }
        };
    }, [onServiceConfirmed]);

    return (
        <div>
            {/* Modal de borrar vs anular: se abre al presionar la papelera de cualquier servicio.
                El sistema decide solo el texto ("¿Borrar?" vs "¿Anular?") según si el operador confirmó. */}
            {modalBorrarCancelar && (
                <ModalBorrarVsCancelar
                    service={modalBorrarCancelar}
                    saleInvoices={saleInvoices}
                    onBorrar={handleModalBorrar}
                    onCancelar={handleModalCancelar}
                    onClose={() => setModalBorrarCancelar(null)}
                />
            )}

            {/* Modal de bloqueo fiscal 409: aparece cuando el backend rechaza la cancelacion
                porque hay factura con CAE viva o voucher emitido. NO un toast genérico:
                mostramos el mensaje del backend + el camino para resolver (ir a facturas). */}
            {modalBloqueo409 && (
                <ModalBloqueoCancelacionServicio
                    mensaje={modalBloqueo409.mensaje}
                    rechazo={modalBloqueo409.rechazo}
                    onIrAVouchers={onIrAVouchers}
                    onClose={() => setModalBloqueo409(null)}
                />
            )}

            <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4 mb-4">
                <h3 className="text-lg font-medium text-gray-900 dark:text-white">Servicios Contratados</h3>
                <div className="flex flex-wrap gap-2 w-full sm:w-auto justify-end">
                    {/* Botón "Anular varios servicios": solo con permiso reservas.cancel, cuando hay
                        servicios anulables Y cuando el backend lo habilita (puedeCancelarServicios).
                        Guía UX 2026-06-22: ocultar en solo lectura según capability del backend.
                        Texto aclarado 2026-07-22 (P2 firmado por Gaston): ya decía así la sección que
                        este botón abre (CancelarVariosServiciosInline) — solo faltaba el disparador. */}
                    {canCancelServices && puedeCancelarServicios && serviciosCancelables.length > 0 && !showCancelarVarios && (
                        candadoDeEdicionActivo ? (
                            <button
                                type="button"
                                onClick={onRequestEdit}
                                data-testid="btn-cancelar-varios"
                                aria-label="Anular varios servicios — bloqueado, pedí autorización"
                                className="flex items-center justify-center gap-2 border border-slate-200 bg-slate-100 text-slate-500 px-4 py-2 rounded-lg hover:bg-slate-200 transition-colors text-sm font-semibold dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700"
                            >
                                <Lock className="w-4 h-4" aria-hidden="true" />
                                Anular varios servicios
                            </button>
                        ) : (
                            <button
                                type="button"
                                onClick={() => setShowCancelarVarios(true)}
                                data-testid="btn-cancelar-varios"
                                className="flex items-center justify-center gap-2 border border-amber-300 bg-amber-50 text-amber-700 px-4 py-2 rounded-lg hover:bg-amber-100 transition-colors text-sm font-semibold dark:border-amber-700 dark:bg-amber-950/30 dark:text-amber-300 dark:hover:bg-amber-900/40"
                            >
                                <XSquare className="w-4 h-4" />
                                Anular varios servicios
                            </button>
                        )
                    )}
                    {/* "Agregar Servicio": oculto en solo lectura (canEditServices.allowed === false).
                        Guía UX 2026-06-22: en Traveling / Finalizada / Perdida / Anulada no se puede agregar.
                        Candado C1 (2026-07-22): con la reserva bloqueada sin autorización viva, el botón
                        queda gris + candadito y abre la ventana de destrabar en vez de agregar directo. */}
                    {puedeEditarServicios && (
                        candadoDeEdicionActivo ? (
                            <button
                                type="button"
                                onClick={onRequestEdit}
                                aria-label="Agregar servicio — bloqueado, pedí autorización"
                                className="flex items-center justify-center gap-2 bg-slate-100 text-slate-500 px-4 py-2 rounded-lg hover:bg-slate-200 transition-colors shadow-sm text-sm dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700"
                            >
                                <Lock className="w-4 h-4" aria-hidden="true" /> Agregar Servicio
                            </button>
                        ) : (
                            <button
                                onClick={onAddService}
                                className="flex items-center justify-center gap-2 bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors shadow-sm text-sm"
                            >
                                <Plus className="w-4 h-4" /> Agregar Servicio
                            </button>
                        )
                    )}
                </div>
            </div>

            {/* Resumen "X de Y servicios resueltos": solo aparece en estado InManagement (decision 4 de UX) */}
            <ResumenServiciosResueltos services={services} reservaStatus={reservaStatus} />

            {collectionErrorMessages.length > 0 ? (
                <div className="mb-4 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/20 dark:text-amber-300">
                    <div className="flex items-start gap-3">
                        <AlertTriangle className="mt-0.5 h-4 w-4 flex-shrink-0" />
                        <div className="space-y-1">
                            <div className="font-semibold">La reserva sigue visible, pero una o mas listas de servicios no se pudieron refrescar.</div>
                            {collectionErrorMessages.map((message) => (
                                <div key={message}>{message}</div>
                            ))}
                        </div>
                    </div>
                </div>
            ) : null}

            {services.length === 0 ? (
                <div className="text-center py-12 bg-gray-50 dark:bg-slate-800 rounded-lg border border-dashed border-gray-300 dark:border-slate-700">
                    <Plane className="w-12 h-12 text-gray-300 dark:text-slate-600 mx-auto mb-3" />
                    <p className="text-gray-500 dark:text-slate-400">No hay servicios cargados en este file.</p>
                </div>
            ) : (
                <>
                    {/* Desktop Table View */}
                    <div className="hidden md:block overflow-hidden">
                        <table className="min-w-full text-left border-collapse">
                            <thead>
                                <tr className="border-b border-slate-100 dark:border-slate-800">
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Tipo</th>
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Descripción</th>
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Fecha / Estancia</th>
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Estado</th>
                                    {mostrarCosto && <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right pr-4">Costo Neto</th>}
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right pr-4">Precio Venta</th>
                                    {/* Columna Avisos: aparece cuando enableServiceDeadlineAlerts está ON.
                                        Es independiente del flag de catálogo (decisión del dueño). */}
                                    {isServiceDeadlineAlertsEnabled && (
                                        <th className="pb-3 text-xs uppercase text-slate-400 font-medium pr-4">Avisos</th>
                                    )}
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right pr-4">Acciones</th>
                                </tr>
                            </thead>
                            <tbody>
                                {services.map((svc) => {
                                    const netCost = svc.netCost || 0;
                                    const isGeneric = svc.recordKind === SERVICE_RECORD_KIND.GENERIC;
                                    const displayType = svc.displayType || svc._type || 'Servicio';
                                    const serviceKey = `${svc.recordKind || displayType}-${getReservationServicePublicId(svc)}`;

                                    // Usamos Fragment con key para poder devolver fila + mini-formulario
                                    // como un bloque sin envolver en un <div> (que rompería el <tbody>).
                                    // ConCoverageDeServicio encapsula el hook de nominal-coverage (no se puede
                                    // llamar hooks dentro de un .map(), necesita un componente propio).
                                    return (
                                        <ConCoverageDeServicio
                                            key={serviceKey}
                                            reservaId={reservaId}
                                            svc={svc}
                                            pasajerosConNombre={pasajerosConNombre}
                                            reservaStatus={reservaStatus}
                                        >
                                        {(coverage, coverageLoading, updateCoverage, serviceType, servicePublicId) => (
                                        <React.Fragment>
                                        <tr className="group border-b border-slate-50 dark:border-slate-800/50 hover:bg-slate-50/50 dark:hover:bg-slate-800/20 transition-colors">
                                            <td className="py-4 align-middle whitespace-nowrap pr-4">
                                                <div className="flex items-center">
                                                    <ServiceIcon service={svc} />
                                                    <span className="text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">{displayType}</span>
                                                    {isGeneric && (
                                                        <span className="ml-2 text-[9px] font-bold uppercase tracking-wider rounded-full bg-slate-100 dark:bg-slate-800 text-slate-500 px-2 py-0.5">
                                                            Generico
                                                        </span>
                                                    )}
                                                </div>
                                            </td>
                                            <td className="py-4 align-middle">
                                                {/* Nombre tachado cuando el servicio está anulado.
                                                    El badge "Anulado" ya aparece en la columna Estado.
                                                    El tachado + quién/cuándo dan contexto adicional sin ser chillones. */}
                                                <div className={`text-sm font-semibold line-clamp-1 ${
                                                    esServicioAnulado(svc)
                                                        ? 'line-through text-slate-400 dark:text-slate-500'
                                                        : 'text-slate-900 dark:text-white'
                                                }`}>
                                                    {svc.name}
                                                </div>
                                                <ServiceSupplierChip
                                                    supplierName={svc.supplierName}
                                                    supplierPublicId={svc.supplierPublicId}
                                                    puedeVerProveedores={puedeVerProveedores}
                                                    testId={`chip-operador-desktop-${getReservationServicePublicId(svc)}`}
                                                />
                                                {/* Línea de auditoría de anulación: quién y cuándo.
                                                    cancelledAt y cancelledByUserName son proyectados por el backend
                                                    en los 6 DTOs de servicio (vuelo, hotel, traslado, paquete,
                                                    asistencia, genérico) y llegan al recargar la colección
                                                    tras la anulación.
                                                    La línea se renderiza solo cuando ambos campos están presentes. */}
                                                {esServicioAnulado(svc) &&
                                                    (svc.cancelledAt || svc.cancelledByUserName) && (
                                                    <div className="mt-0.5 text-[10px] text-slate-400 dark:text-slate-500 flex items-center gap-1">
                                                        <span>Anulado</span>
                                                        {svc.cancelledByUserName && (
                                                            <span>por <span className="font-semibold">{svc.cancelledByUserName}</span></span>
                                                        )}
                                                        {svc.cancelledAt && (
                                                            <span>el {formatFechaCancelacion(svc.cancelledAt)}</span>
                                                        )}
                                                    </div>
                                                )}
                                                <div className="flex flex-wrap gap-2 mt-1">
                                                    {/* FIX 4: pill "creado en venta" eliminada (no aporta al usuario) */}
                                                    {svc.pnr && (
                                                        <span className="text-[10px] bg-slate-100 dark:bg-slate-800 text-slate-600 dark:text-slate-400 px-1.5 py-0.5 rounded font-mono font-bold">PNR: {svc.pnr}</span>
                                                    )}
                                                    {svc.confirmationNumber && (
                                                        <span className="text-[10px] bg-emerald-50 dark:bg-emerald-900/20 text-emerald-700 dark:text-emerald-400 px-1.5 py-0.5 rounded flex items-center gap-1 font-medium">
                                                            <ShieldCheck className="w-3 h-3" /> {svc.confirmationNumber}
                                                        </span>
                                                    )}
                                                    {svc.recordKind === SERVICE_RECORD_KIND.FLIGHT && svc.origin && (
                                                        <span className="text-[10px] text-slate-400">{svc.origin} ➔ {svc.destination}</span>
                                                    )}
                                                    {svc.recordKind === SERVICE_RECORD_KIND.TRANSFER && svc.pickupLocation && (
                                                        <span className="text-[10px] text-slate-400">{svc.pickupLocation} ➔ {svc.dropoffLocation}</span>
                                                    )}
                                                    {/* Datos clave de la asistencia: poliza, plan y zona */}
                                                    {svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE && svc.policyNumber && (
                                                        <span className="text-[10px] bg-blue-50 dark:bg-blue-900/20 text-blue-700 dark:text-blue-400 px-1.5 py-0.5 rounded font-mono font-bold">Poliza: {svc.policyNumber}</span>
                                                    )}
                                                    {svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE && svc.planType && (
                                                        <span className="text-[10px] bg-slate-100 dark:bg-slate-800 text-slate-600 dark:text-slate-400 px-1.5 py-0.5 rounded">{svc.planType}</span>
                                                    )}
                                                    {svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE && svc.coverageZone && (
                                                        <span className="text-[10px] text-slate-400">{svc.coverageZone}</span>
                                                    )}
                                                </div>
                                            </td>
                                            <td className="py-4 align-middle whitespace-nowrap text-xs text-slate-600 dark:text-slate-400">
                                                {svc.recordKind === SERVICE_RECORD_KIND.HOTEL || svc.recordKind === SERVICE_RECORD_KIND.PACKAGE ? (
                                                    <div className="flex flex-col">
                                                        <span>{formatFechaSegura(svc.date || svc.startDate || svc.checkIn)}</span>
                                                        {/* FIX: solo mostrar "al ..." si hay fecha de fin válida.
                                                            Paquete puede no tener endDate si el operador no la informa. */}
                                                        {formatFechaSegura(svc.endDate || svc.checkOut) !== '-' && (
                                                            <span className="text-[10px] opacity-60">al {formatFechaSegura(svc.endDate || svc.checkOut)}</span>
                                                        )}
                                                    </div>
                                                ) : svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE ? (
                                                    // Asistencia: muestra vigencia desde/hasta (fechas date-only)
                                                    <div className="flex flex-col">
                                                        <span>{formatFechaSegura(svc.validFrom)}</span>
                                                        {formatFechaSegura(svc.validTo) !== '-' && (
                                                            <span className="text-[10px] opacity-60">al {formatFechaSegura(svc.validTo)}</span>
                                                        )}
                                                    </div>
                                                ) : (
                                                    formatFechaSegura(svc.date)
                                                )}
                                            </td>
                                            <td className="py-4 align-middle whitespace-nowrap">
                                                {/* items-start: el badge hugea su texto y no se estira al ancho de la celda */}
                                                <div className="flex flex-col items-start gap-1.5">
                                                    {/* px-1.5 (en vez de px-2) para que el área de color no se extienda
                                                        de más en textos cortos como "En espera" o "Emitido". */}
                                                    <span className={`px-1.5 py-0.5 rounded text-[10px] font-semibold uppercase tracking-wider ${claseColorEstadoServicio(svc.workflowStatus, reservaStatus)}`}>
                                                        {etiquetaEstadoServicio(svc.workflowStatus, reservaStatus)}
                                                    </span>
                                                    {/* Pelotita ADR-020 (decision 4): solo en estado InManagement.
                                                        Verde = resuelto; Amarilla con texto = que falta resolver. */}
                                                    {reservaStatus === 'InManagement' && (
                                                        esServicioResuelto(svc) ? (
                                                            <span className="inline-flex items-center gap-1 text-[10px] font-semibold text-emerald-600 dark:text-emerald-400">
                                                                <span className="h-2 w-2 rounded-full bg-emerald-500 flex-shrink-0" aria-hidden="true" />
                                                                Resuelto
                                                            </span>
                                                        ) : (
                                                            <span className="inline-flex items-center gap-1 text-[10px] font-semibold text-amber-600 dark:text-amber-400">
                                                                <span className="h-2 w-2 rounded-full bg-amber-400 flex-shrink-0" aria-hidden="true" />
                                                                {textoFaltante(svc)}
                                                            </span>
                                                        )
                                                    )}
                                                    {/* ADR-036 P4=B: etiqueta de pago al operador por servicio.
                                                        Estado (pagado/parcial/impago) visible para todos.
                                                        Montos solo si amountsVisible (cobranzas.see_cost).
                                                        Si el endpoint falló, pagoOperadorDto es null → badge no aparece.
                                                        Regla 6/7 del modelo de estados (2026-07-17): en un servicio
                                                        ANULADO este badge no tiene sentido (ya no hay "pago al operador"
                                                        vivo que reportar) — se apaga acá y, si dejó multa, en su lugar
                                                        aparece la etiqueta "Con multa" de abajo. */}
                                                    {!esServicioAnulado(svc) && (
                                                        <OperadorPagoStatusBadge
                                                            servicioStatus={buscarEstadoPagoServicio(
                                                                svc.recordKind,
                                                                getReservationServicePublicId(svc),
                                                                pagoOperadorDto
                                                            )}
                                                            amountsVisible={pagoOperadorAmountsVisible}
                                                            loading={pagoOperadorLoading}
                                                        />
                                                    )}
                                                    {/* Etiqueta "Con multa" / "✓ Multa cobrada" (spec P3 FIRMADA):
                                                        solo sobre un servicio anulado que dejó multa confirmada del
                                                        operador. Null (sin multa) o el campo ausente → no se muestra
                                                        nada, la fila queda solo con el badge "Anulado" de arriba. */}
                                                    {esServicioAnulado(svc) && (
                                                        <CancellationPenaltyLabel
                                                            cancellationPenaltyState={svc.cancellationPenaltyState}
                                                        />
                                                    )}
                                                </div>
                                            </td>

                                            {/* Celda de costo: dos ramas completamente separadas para garantizar
                                                que flag OFF = markup IDÉNTICO al de HEAD (sin diferencia de clase ni de elemento).
                                                - Rama flag ON + see_cost + tipo específico: td con align-top y CostConfirmCell
                                                - Rama genérica/flag OFF: td EXACTAMENTE igual al td original de HEAD */}
                                            {mostrarCosto && puedeConfirmarCosto && !isGeneric ? (
                                                // Flag ON + see_cost + tipo específico: celda con pill y botón de confirmación.
                                                // FIX B1 (review frontend 2026-07-17): esta rama se había quedado afuera del
                                                // tachado de la regla 6 — un servicio anulado con este flag+permiso mostraba
                                                // el costo neto SIN tachar. Se tacha la celda ENTERA (mismo criterio que la
                                                // rama simple de abajo): la línea de tachado atraviesa visualmente todo el
                                                // contenido de CostConfirmCell, sin tocar ese componente (que sigue
                                                // permitiendo confirmar costo sobre un anulado — decisión del dueño
                                                // documentada en CostConfirmCell.jsx, no se toca en esta tanda).
                                                <td className={`py-4 align-top text-right pr-4 ${
                                                    esServicioAnulado(svc) ? 'line-through text-slate-400 dark:text-slate-500' : ''
                                                }`}>
                                                    {/* Cartelito de moneda solo cuando la reserva mezcla monedas */}
                                                    {esMultimoneda && svc.currency && (
                                                        <span className="inline-block mb-1">
                                                            <CurrencyBadge currency={svc.currency} />
                                                        </span>
                                                    )}
                                                    <CostConfirmCell
                                                        service={svc}
                                                        reservaId={reservaId}
                                                        onConfirmado={crearCallbackConfirmado(svc.recordKind)}
                                                        candadoActivo={candadoDeEdicionActivo}
                                                        onRequestEdit={onRequestEdit}
                                                    />
                                                </td>
                                            ) : mostrarCosto ? (
                                                // Flag OFF o genérico: td IDÉNTICO al HEAD (align-middle, sin span extra)
                                                // Regla 6 del modelo de estados (2026-07-17): el costo de un servicio
                                                // anulado se tacha igual que su nombre (mismo patrón visual aprobado,
                                                // ServiceList.jsx:1261) — es historia de la reserva, no un dato vivo.
                                                <td className={`py-4 align-middle text-right text-xs font-mono pr-4 ${
                                                    esServicioAnulado(svc)
                                                        ? 'line-through text-slate-400 dark:text-slate-500'
                                                        : 'text-slate-500'
                                                }`}>
                                                    {/* Cartelito de moneda solo en modo multimoneda */}
                                                    {esMultimoneda && svc.currency && (
                                                        <span className="inline-flex items-center gap-1 justify-end">
                                                            <CurrencyBadge currency={svc.currency} />
                                                        </span>
                                                    )}
                                                    {formatCurrency(netCost, svc.currency || "ARS")}
                                                </td>
                                            ) : null}

                                            {/* Precio venta: mismo tachado que el costo cuando el servicio está
                                                anulado. En vivo mantiene el énfasis fuerte (font-bold + texto oscuro)
                                                que ya tenía; el tachado lo pierde a propósito (deja de ser el
                                                importe "actual" de la reserva). */}
                                            <td className={`py-4 align-middle text-right text-xs font-mono pr-4 ${
                                                esServicioAnulado(svc)
                                                    ? 'line-through text-slate-400 dark:text-slate-500'
                                                    : 'font-bold text-slate-900 dark:text-white'
                                            }`}>
                                                {/* Cartelito de moneda: solo en modo multimoneda. La moneda viene del servicio. */}
                                                {esMultimoneda && svc.currency && (
                                                    <span className="inline-flex items-center gap-1 justify-end mb-0.5">
                                                        <CurrencyBadge currency={svc.currency} />
                                                    </span>
                                                )}
                                                {formatCurrency(svc.salePrice || 0, svc.currency || "ARS")}
                                            </td>

                                            {/* Columna Avisos: solo con flag enableServiceDeadlineAlerts ON.
                                                Usa UpcomingStartPill (fecha de inicio del servicio). */}
                                            {isServiceDeadlineAlertsEnabled && (
                                                <td className="py-4 align-middle pr-4 whitespace-nowrap">
                                                    <UpcomingStartPill service={svc} windowDays={windowDays} mostrarGuion={true} />
                                                </td>
                                            )}

                                            <td className="py-4 align-middle pr-4">
                                                <div className="flex flex-col items-end gap-1.5">
                                                    {/* Botones de resolver: decision 3 de UX (ADR-020).
                                                        Solo aparecen en InManagement Y cuando el servicio todavia no esta resuelto.
                                                        ADR-031: el botón también se gate-a si faltan datos de pasajeros
                                                        (el hint calcula la condición; el backend siempre re-valida). */}
                                                    {reservaStatus === 'InManagement' && !esServicioResuelto(svc) && !esServicioAnulado(svc) && (() => {
                                                        // Calculamos el hint para este servicio.
                                                        // Si reserva es null (prop no pasada), no aplicamos gate.
                                                        const hint = reserva
                                                            ? calcularHintPorTipo(svc.recordKind, reserva.passengers || [], reserva)
                                                            : { listo: true };
                                                        const pasajerosListos = hint.listo;

                                                        return (
                                                            <>
                                                                {svc.recordKind === SERVICE_RECORD_KIND.FLIGHT && (
                                                                    pasajerosListos ? (
                                                                        <BotonMarcarEmitido
                                                                            reservaId={reservaId}
                                                                            servicePublicId={getReservationServicePublicId(svc)}
                                                                            onResuelto={onServiceResolved}
                                                                        />
                                                                    ) : (
                                                                        // P5: botón apagado + texto explicativo
                                                                        <span
                                                                            className="text-[10px] font-semibold text-amber-600 dark:text-amber-400"
                                                                            data-testid={`hint-pasajeros-flight-${getReservationServicePublicId(svc)}`}
                                                                        >
                                                                            Cargá los nombres primero
                                                                        </span>
                                                                    )
                                                                )}
                                                                {svc.recordKind === SERVICE_RECORD_KIND.TRANSFER && (
                                                                    pasajerosListos ? (
                                                                        <BotonNoRequiereConfirmacion
                                                                            reservaId={reservaId}
                                                                            servicePublicId={getReservationServicePublicId(svc)}
                                                                            onResuelto={onServiceResolved}
                                                                        />
                                                                    ) : (
                                                                        // P6: titular falta → botón apagado + aviso
                                                                        <span
                                                                            className="text-[10px] font-semibold text-amber-600 dark:text-amber-400"
                                                                            data-testid={`hint-pasajeros-transfer-${getReservationServicePublicId(svc)}`}
                                                                        >
                                                                            Cargá al menos el titular primero
                                                                        </span>
                                                                    )
                                                                )}
                                                            </>
                                                        );
                                                    })()}
                                                    {/* Control "Para: Todos" (ADR-031 v2.1 — Pieza A).
                                                        Aparece en desktop antes de los botones Editar/Borrar.
                                                        Al tocarlo, despliega el panel de tildes en línea (PUT = escritura).
                                                        D2: NO se muestra si el servicio está anulado.
                                                        Solo lectura: oculto cuando canEditServices.allowed === false
                                                        (mismo gate que Editar/Agregar — el PUT de asignaciones es escritura). */}
                                                    {puedeEditarServicios && !esServicioAnulado(svc) && (
                                                        <ControlAsignacionServicio
                                                            reservaId={reservaId}
                                                            serviceType={serviceType}
                                                            servicePublicId={servicePublicId}
                                                            pasajerosConNombre={pasajerosConNombre}
                                                            coverage={coverage}
                                                            coverageLoading={coverageLoading}
                                                            onAsignacionGuardada={updateCoverage}
                                                            className="mb-1"
                                                        />
                                                    )}

                                                    {/* Desktop: icono + palabra siempre visible (spec UX 2026-06-08).
                                                        textoTacho es dinámico: "Anular servicio" si el operador ya confirmó,
                                                        "Borrar" si no (texto aclarado 2026-07-22, P2 firmado por Gaston — el de
                                                        "Borrar" NO se toca, solo distingue el de "Anular" del de la cabecera
                                                        "Anular reserva" y el de la lista "Anular varios servicios").
                                                        aria-label y texto visible dicen lo mismo para coherencia con lectores de pantalla.
                                                        Guía UX 2026-06-22: en solo lectura (Traveling/Finalizada/etc.) se ocultan
                                                        ambos botones; quedan visibles los datos del servicio y el badge de estado.
                                                        Servicio ya ANULADO (2026-07-16): tampoco se muestra ninguno de los dos —
                                                        es historia cerrada de la reserva (ver esServicioAnulado), no hay nada
                                                        para editar ni para volver a anular/borrar sobre algo que ya quedó sin efecto. */}
                                                    {!esServicioAnulado(svc) && (() => {
                                                        const esConfirmado = esServicioConfirmadoPorOperador(svc);
                                                        // Convivencia de candados (§1.7 de la spec 2026-07-22): con
                                                        // la reserva bloqueada (candadoDeEdicionActivo), manda el
                                                        // candado de la RESERVA — todavía no evaluamos el freno
                                                        // fiscal por servicio (voucher vivo / pago sin factura /
                                                        // factura sin cliente). Ese freno recién aparece cuando la
                                                        // reserva ya está destrabada: "un candado a la vez por botón".
                                                        const bloqueoAnular = esConfirmado && !candadoDeEdicionActivo
                                                            ? resolverBloqueoAnularServicio(svc)
                                                            : { bloqueado: false, motivo: null };

                                                        return (
                                                        <>
                                                        <div className="flex justify-end gap-1 transition-opacity">
                                                        {puedeEditarServicios && (
                                                            candadoDeEdicionActivo ? (
                                                                <button
                                                                    type="button"
                                                                    onClick={onRequestEdit}
                                                                    data-testid={`btn-edit-service-${getReservationServicePublicId(svc)}`}
                                                                    aria-label="Editar servicio — bloqueado, pedí autorización"
                                                                    className="inline-flex items-center gap-1 p-1.5 text-slate-400 hover:bg-slate-100 dark:text-slate-500 dark:hover:bg-slate-800 rounded text-xs font-semibold"
                                                                >
                                                                    <Lock className="w-4 h-4" aria-hidden="true" />
                                                                    Editar
                                                                </button>
                                                            ) : (
                                                                <button
                                                                    onClick={() => onEditService(svc)}
                                                                    data-testid={`btn-edit-service-${getReservationServicePublicId(svc)}`}
                                                                    aria-label="Editar servicio"
                                                                    className="inline-flex items-center gap-1 p-1.5 text-blue-600 hover:bg-blue-50 dark:hover:bg-blue-900/20 rounded text-xs font-semibold"
                                                                >
                                                                    <Edit2 className="w-4 h-4" />
                                                                    Editar
                                                                </button>
                                                            )
                                                        )}
                                                        {/* Papelera: abre el modal que decide borrar vs anular según decisión #9.
                                                            Se oculta si no puede anular (solo lectura) o no puede editar (borrar
                                                            = editar en servicios no confirmados). */}
                                                        {(puedeEditarServicios || puedeCancelarServicios) && (() => {
                                                            // Si está confirmado → anular (requiere puedeCancelarServicios)
                                                            // Si no está confirmado → borrar (requiere puedeEditarServicios)
                                                            const mostrarBotonDestructivo = esConfirmado
                                                                ? puedeCancelarServicios
                                                                : puedeEditarServicios;
                                                            if (!mostrarBotonDestructivo) return null;
                                                            // P2 firmado por Gaston (2026-07-22): "Anular" pasa a "Anular
                                                            // servicio" para distinguirlo de "Anular reserva" (cabecera) y
                                                            // "Anular varios servicios" (lista). "Borrar" NO se toca (es
                                                            // otra acción, un servicio que ni llegó a confirmarse).
                                                            const textoTacho = esConfirmado ? 'Anular servicio' : 'Borrar';

                                                            // Candado C1 (2026-07-22): con la reserva bloqueada, el tacho
                                                            // va gris + candadito de RESERVA y abre la ventana de destrabar
                                                            // — manda antes que el freno fiscal (§1.7, "un candado a la vez").
                                                            if (candadoDeEdicionActivo) {
                                                                return (
                                                                    <button
                                                                        type="button"
                                                                        onClick={onRequestEdit}
                                                                        data-testid={`btn-delete-service-${getReservationServicePublicId(svc)}`}
                                                                        aria-label={`${textoTacho} — bloqueado, pedí autorización`}
                                                                        className="inline-flex items-center gap-1 p-1.5 text-slate-400 hover:bg-slate-100 dark:text-slate-500 dark:hover:bg-slate-800 rounded text-xs font-semibold"
                                                                    >
                                                                        <Lock className="w-4 h-4" aria-hidden="true" />
                                                                        {textoTacho}
                                                                    </button>
                                                                );
                                                            }

                                                            // El bloqueado SOLO puede darse con esConfirmado=true (ver
                                                            // bloqueoAnular arriba), así que acá textoTacho siempre es
                                                            // "Anular servicio" — el aria-label ya no necesita agregarle
                                                            // "servicio" de nuevo (evita el "servicio servicio" repetido).
                                                            if (bloqueoAnular.bloqueado) {
                                                                return (
                                                                    <button
                                                                        type="button"
                                                                        disabled
                                                                        data-testid={`btn-delete-service-${getReservationServicePublicId(svc)}`}
                                                                        aria-label={`${textoTacho} bloqueado: ${bloqueoAnular.motivo}`}
                                                                        title={bloqueoAnular.motivo}
                                                                        className="inline-flex items-center gap-1 p-1.5 text-slate-300 dark:text-slate-600 rounded text-xs font-semibold cursor-not-allowed"
                                                                    >
                                                                        <Trash2 className="w-4 h-4" />
                                                                        {textoTacho}
                                                                    </button>
                                                                );
                                                            }

                                                            // Acá sí puede ser cualquiera de los dos casos: "Anular
                                                            // servicio" ya trae la palabra completa (no se le vuelve a
                                                            // agregar "servicio"); "Borrar" sigue sumándosela en el
                                                            // aria-label para que el lector de pantalla diga a qué aplica
                                                            // (igual que siempre — el texto visible de "Borrar" no cambia).
                                                            return (
                                                                <button
                                                                    onClick={() => handleTrashClick(svc)}
                                                                    data-testid={`btn-delete-service-${getReservationServicePublicId(svc)}`}
                                                                    aria-label={esConfirmado ? textoTacho : `${textoTacho} servicio`}
                                                                    className="inline-flex items-center gap-1 p-1.5 text-red-600 hover:bg-red-50 dark:hover:bg-red-900/20 rounded text-xs font-semibold"
                                                                >
                                                                    <Trash2 className="w-4 h-4" />
                                                                    {textoTacho}
                                                                </button>
                                                            );
                                                        })()}
                                                        </div>
                                                        {/* Chip "al lado" de la papelera (precedente 2026-06-13):
                                                            motivo real del backend, sin parafrasearlo. Mismo formato
                                                            chico que ya usa el hint de pasajeros faltantes. Nada de
                                                            tooltip: este texto queda siempre a la vista.
                                                            No se muestra con la reserva bloqueada (bloqueoAnular ya
                                                            queda en false arriba mientras candadoDeEdicionActivo). */}
                                                        {bloqueoAnular.bloqueado && (
                                                            <span
                                                                className="max-w-[220px] text-right text-[10px] font-semibold text-amber-700 dark:text-amber-400"
                                                                data-testid={`aviso-bloqueo-anular-${getReservationServicePublicId(svc)}`}
                                                            >
                                                                🔒 {bloqueoAnular.motivo}
                                                            </span>
                                                        )}
                                                        </>
                                                        );
                                                    })()}
                                                </div>
                                            </td>
                                        </tr>

                                        {/* Mini-formulario inline de pasajeros faltantes (ADR-031, pantallas D y E).
                                            Fix B1: ahora gateado por coverage.isComplete del backend
                                            (no por calcularHintPorTipo sobre todos los pasajeros).
                                            Solo aparece en InManagement + servicio no resuelto + coverage cargada.
                                            Si coverage no llegó aún, no mostramos (evita parpadeo incorrecto).
                                            La fila completa del servicio sigue visible arriba.
                                            Guard de anulado: no tiene sentido pedir nombres para un servicio
                                            tachado — el control "Para:" ya lo ocultaba, el mini-form también. */}
                                        {reservaStatus === 'InManagement' && !esServicioResuelto(svc) &&
                                            !esServicioAnulado(svc) &&
                                            coverage && !coverage.isComplete &&
                                            (svc.recordKind === SERVICE_RECORD_KIND.FLIGHT ||
                                             svc.recordKind === SERVICE_RECORD_KIND.HOTEL ||
                                             svc.recordKind === SERVICE_RECORD_KIND.TRANSFER ||
                                             svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE ||
                                             svc.recordKind === SERVICE_RECORD_KIND.PACKAGE) && (
                                            <MiniFormularioPasajerosFaltantes
                                                reservaId={reservaId}
                                                reserva={reserva}
                                                servicio={svc}
                                                coverage={coverage}
                                                onPasajeroGuardado={onPasajeroGuardado}
                                            />
                                        )}
                                        </React.Fragment>
                                        )}
                                        </ConCoverageDeServicio>
                                    );
                                })}

                                {/* Fila TOTAL al pie: SOLO cuando la reserva tiene dos monedas (decisión A, 2026-06-11).
                                    Muestra el total de Precio Venta por moneda, separados por punto medio.
                                    Regla ③: con una sola moneda esta fila NO aparece (igual que hoy). */}
                                {esMultimoneda && services.length > 0 && (() => {
                                    // Acumular total de salePrice por moneda (no mezclar)
                                    const totalesPorMoneda = services.reduce((acc, svc) => {
                                        const moneda = svc.currency || "ARS";
                                        acc[moneda] = (acc[moneda] || 0) + (svc.salePrice || 0);
                                        return acc;
                                    }, {});

                                    const monedas = Object.keys(totalesPorMoneda);
                                    if (monedas.length <= 1) return null; // Seguridad: no mostrar si todos son una moneda

                                    // Calcular colspan correcto para la fila de total
                                    const colspanDescripcion = mostrarCosto
                                        ? (isServiceDeadlineAlertsEnabled ? 2 : 2)
                                        : (isServiceDeadlineAlertsEnabled ? 3 : 3);

                                    return (
                                        <tr className="border-t-2 border-slate-200 dark:border-slate-700 bg-slate-50/80 dark:bg-slate-800/40">
                                            <td colSpan={3} className="py-3 pl-2 text-xs font-black uppercase tracking-wider text-slate-500 dark:text-slate-400">
                                                Total venta
                                            </td>
                                            {/* Celdas vacías para Estado y posible Costo (para alinear con columna Precio Venta) */}
                                            {mostrarCosto && <td />}
                                            <td className="py-3 text-right pr-4">
                                                {/* Totales por moneda separados por · */}
                                                <span className="flex flex-col items-end gap-0.5">
                                                    {monedas.map((moneda, idx) => (
                                                        <span key={moneda} className="inline-flex items-center gap-1.5 text-xs font-black text-slate-900 dark:text-white font-mono">
                                                            <CurrencyBadge currency={moneda} />
                                                            {formatCurrency(totalesPorMoneda[moneda], moneda)}
                                                            {idx < monedas.length - 1 && (
                                                                <span className="text-slate-400 mx-0.5">·</span>
                                                            )}
                                                        </span>
                                                    ))}
                                                </span>
                                            </td>
                                            {isServiceDeadlineAlertsEnabled && <td />}
                                            <td />
                                        </tr>
                                    );
                                })()}
                            </tbody>
                        </table>
                    </div>

                    {/* Mobile View */}
                    <div className="md:hidden space-y-4">
                        {services.map((svc) => {
                            const netCost = svc.netCost || 0;
                            const isGeneric = svc.recordKind === SERVICE_RECORD_KIND.GENERIC;
                            const displayType = svc.displayType || svc._type || 'Servicio';
                            const serviceKey = `${svc.recordKind || displayType}-${getReservationServicePublicId(svc)}`;

                            // ConCoverageDeServicio también envuelve la card mobile:
                            // necesitamos la coverage por servicio para el control "Para: Todos".
                            return (
                                <ConCoverageDeServicio
                                    key={serviceKey}
                                    reservaId={reservaId}
                                    svc={svc}
                                    pasajerosConNombre={pasajerosConNombre}
                                    reservaStatus={reservaStatus}
                                >
                                {(coverage, coverageLoading, updateCoverage, serviceType, servicePublicId) => {
                            // FIX 4: pill "creado en venta" eliminada. Solo queda la pill de próximo inicio.
                            // Sin pill no hay "—" en mobile (solo omitimos la línea entera).
                            // Pill de próximo inicio: solo con flag avisos ON, sin cancelado,
                            // Y con la fecha DENTRO de la ventana de alerta (no solo que exista).
                            // Si la fecha está fuera de ventana, UpcomingStartPill devuelve null en mobile
                            // → renderizar el div vacío produciría márgenes con nada adentro.
                            const tieneUpcomingPillMobile = isServiceDeadlineAlertsEnabled &&
                                svc.workflowStatus !== "Cancelado" &&
                                estaEnVentana(svc.date, windowDays);
                            const mostrarLineaPills = tieneUpcomingPillMobile;

                            return (
                                <div className="bg-white dark:bg-slate-900 p-4 rounded-xl border border-slate-100 dark:border-slate-800 shadow-sm">
                                    <div className="flex justify-between mb-2">
                                        <div className="flex items-center gap-2">
                                            <ServiceIcon service={svc} className="w-4 h-4" />
                                            <span className="text-xs font-bold text-slate-400 uppercase tracking-tighter">{displayType}</span>
                                            {isGeneric && (
                                                <span className="text-[9px] font-bold uppercase rounded-full bg-slate-100 dark:bg-slate-800 text-slate-500 px-2 py-0.5">
                                                    Generico
                                                </span>
                                            )}
                                        </div>
                                        {/* px-1.5 en vez de px-2: mismo ajuste que desktop para que el área
                                            de color no se extienda de más en textos cortos. */}
                                        <span className={`text-[10px] px-1.5 py-0.5 rounded font-bold uppercase tracking-wider ${claseColorEstadoServicio(svc.workflowStatus, reservaStatus)}`}>
                                            {etiquetaEstadoServicio(svc.workflowStatus, reservaStatus)}
                                        </span>
                                    </div>
                                    {/* Nombre tachado en mobile cuando el servicio está anulado */}
                                    <div className={`font-medium mb-1 line-clamp-1 ${
                                        esServicioAnulado(svc)
                                            ? 'line-through text-slate-400 dark:text-slate-500'
                                            : 'text-slate-900 dark:text-white'
                                    }`}>
                                        {svc.name}
                                    </div>
                                    <ServiceSupplierChip
                                        supplierName={svc.supplierName}
                                        supplierPublicId={svc.supplierPublicId}
                                        puedeVerProveedores={puedeVerProveedores}
                                        testId={`chip-operador-mobile-${getReservationServicePublicId(svc)}`}
                                    />
                                    {/* Auditoria de anulación en mobile: quién y cuándo (campos opcionales del backend) */}
                                    {esServicioAnulado(svc) &&
                                        (svc.cancelledAt || svc.cancelledByUserName) && (
                                        <div className="mb-1 text-[10px] text-slate-400 dark:text-slate-500 flex items-center gap-1 flex-wrap">
                                            <span>Anulado</span>
                                            {svc.cancelledByUserName && (
                                                <span>por <span className="font-semibold">{svc.cancelledByUserName}</span></span>
                                            )}
                                            {svc.cancelledAt && (
                                                <span>el {formatFechaCancelacion(svc.cancelledAt)}</span>
                                            )}
                                        </div>
                                    )}

                                    {/* Línea de pill de próximo inicio: solo si aplica */}
                                    {mostrarLineaPills && (
                                        <div className="flex flex-wrap gap-2 mt-1 mb-1">
                                            {tieneUpcomingPillMobile && (
                                                <UpcomingStartPill service={svc} windowDays={windowDays} mostrarGuion={false} />
                                            )}
                                        </div>
                                    )}

                                    {/* ADR-036 P4=B: etiqueta de pago al operador en mobile.
                                        Mismo dato que desktop; se ubica debajo del nombre y las pills.
                                        Regla 6/7 (2026-07-17): apagada en anulado, reemplazada por
                                        "Con multa"/"✓ Multa cobrada" cuando corresponde — mismo criterio
                                        que la versión desktop. */}
                                    {!esServicioAnulado(svc) && (
                                        <OperadorPagoStatusBadge
                                            servicioStatus={buscarEstadoPagoServicio(
                                                svc.recordKind,
                                                getReservationServicePublicId(svc),
                                                pagoOperadorDto
                                            )}
                                            amountsVisible={pagoOperadorAmountsVisible}
                                            loading={pagoOperadorLoading}
                                        />
                                    )}
                                    {esServicioAnulado(svc) && (
                                        <CancellationPenaltyLabel
                                            cancellationPenaltyState={svc.cancellationPenaltyState}
                                        />
                                    )}

                                    <div className="flex justify-between items-end">
                                        <div className="text-[11px] text-slate-500 flex flex-col gap-0.5">
                                            <span>
                                                {svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE
                                                    ? (svc.validFrom ? `Vigencia: ${formatFechaSegura(svc.validFrom)}` : '-')
                                                    : formatFechaSegura(svc.date)}
                                                {/* FIX: solo agregar "al ..." si la fecha de fin es válida */}
                                                {(svc.recordKind === SERVICE_RECORD_KIND.HOTEL || svc.recordKind === SERVICE_RECORD_KIND.PACKAGE) &&
                                                    formatFechaSegura(svc.endDate || svc.checkOut) !== '-' &&
                                                    ` al ${formatFechaSegura(svc.endDate || svc.checkOut)}`}
                                                {svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE && svc.validTo &&
                                                    ` al ${formatFechaSegura(svc.validTo)}`}
                                            </span>
                                            <div className="flex gap-2 items-center mt-1 flex-wrap">
                                                {/* Precio de venta: con cartelito de moneda en modo multimoneda.
                                                    Regla 6 (2026-07-17): tachado en anulado, mismo criterio que
                                                    el nombre y que la columna equivalente de escritorio. */}
                                                <span className={`inline-flex items-center gap-1 ${
                                                    esServicioAnulado(svc)
                                                        ? 'line-through text-slate-400 dark:text-slate-500'
                                                        : 'font-bold text-slate-900 dark:text-white'
                                                }`}>
                                                    Venta:{" "}
                                                    {esMultimoneda && svc.currency && (
                                                        <CurrencyBadge currency={svc.currency} />
                                                    )}
                                                    {formatCurrency(svc.salePrice || 0, svc.currency || "ARS")}
                                                </span>
                                                {/* Costo en mobile: gateado igual que en desktop */}
                                                {mostrarCosto && (
                                                    puedeConfirmarCosto && !isGeneric ? (
                                                        // Con flag ON + see_cost: celda interactiva.
                                                        // FIX B1 (review frontend 2026-07-17): mismo criterio que la rama
                                                        // desktop — se envuelve en un <span> tachado cuando el servicio
                                                        // está anulado, sin tocar CostConfirmCellMobile (que sigue
                                                        // permitiendo confirmar costo sobre un anulado, decisión del
                                                        // dueño ya documentada en ese componente).
                                                        <span className={esServicioAnulado(svc) ? 'line-through text-slate-400 dark:text-slate-500' : ''}>
                                                            <CostConfirmCellMobile
                                                                service={svc}
                                                                reservaId={reservaId}
                                                                onConfirmado={crearCallbackConfirmado(svc.recordKind)}
                                                                candadoActivo={candadoDeEdicionActivo}
                                                                onRequestEdit={onRequestEdit}
                                                            />
                                                        </span>
                                                    ) : (
                                                        // Sin flag o sin permiso: número simple, tachado en anulado.
                                                        <span className={`text-[9px] inline-flex items-center gap-1 ${
                                                            esServicioAnulado(svc)
                                                                ? 'line-through text-slate-400 dark:text-slate-500'
                                                                : 'opacity-70'
                                                        }`}>
                                                            Costo:{" "}
                                                            {esMultimoneda && svc.currency && (
                                                                <CurrencyBadge currency={svc.currency} />
                                                            )}
                                                            {formatCurrency(netCost, svc.currency || "ARS")}
                                                        </span>
                                                    )
                                                )}
                                            </div>
                                            {/* Pelotita ADR-020 en mobile: solo en InManagement (decision 4) */}
                                            {reservaStatus === 'InManagement' && (
                                                esServicioResuelto(svc) ? (
                                                    <span className="inline-flex items-center gap-1 text-[10px] font-semibold text-emerald-600 dark:text-emerald-400 mt-1">
                                                        <span className="h-2 w-2 rounded-full bg-emerald-500 flex-shrink-0" aria-hidden="true" />
                                                        Resuelto
                                                    </span>
                                                ) : (
                                                    <span className="inline-flex items-center gap-1 text-[10px] font-semibold text-amber-600 dark:text-amber-400 mt-1">
                                                        <span className="h-2 w-2 rounded-full bg-amber-400 flex-shrink-0" aria-hidden="true" />
                                                        {textoFaltante(svc)}
                                                    </span>
                                                )
                                            )}
                                        </div>
                                        <div className="flex flex-col items-end gap-2">
                                            {/* Botones de resolver en mobile: ADR-020 + ADR-031.
                                                Mismo criterio que desktop: si faltan datos de pasajeros
                                                el botón se apaga y aparece el aviso ámbar.
                                                El mini-formulario en línea no entra en la card mobile,
                                                así que el aviso apunta a la solapa Pasajeros. */}
                                            {reservaStatus === 'InManagement' && !esServicioResuelto(svc) && !esServicioAnulado(svc) && (() => {
                                                // Calculamos el hint igual que en desktop.
                                                // Si reserva es null (prop no pasada), dejamos pasar sin gate.
                                                const hint = reserva
                                                    ? calcularHintPorTipo(svc.recordKind, reserva.passengers || [], reserva)
                                                    : { listo: true };
                                                const pasajerosListos = hint.listo;

                                                return (
                                                    <>
                                                        {svc.recordKind === SERVICE_RECORD_KIND.FLIGHT && (
                                                            pasajerosListos ? (
                                                                <BotonMarcarEmitido
                                                                    reservaId={reservaId}
                                                                    servicePublicId={getReservationServicePublicId(svc)}
                                                                    onResuelto={onServiceResolved}
                                                                />
                                                            ) : (
                                                                // Aéreo: faltan nombre o documento.
                                                                // El usuario tiene que ir a la solapa Pasajeros a completar.
                                                                <span
                                                                    className="text-[10px] font-semibold text-amber-600 dark:text-amber-400 text-right"
                                                                    data-testid={`hint-pasajeros-flight-mobile-${getReservationServicePublicId(svc)}`}
                                                                >
                                                                    Cargá los nombres primero
                                                                </span>
                                                            )
                                                        )}
                                                        {svc.recordKind === SERVICE_RECORD_KIND.TRANSFER && (
                                                            pasajerosListos ? (
                                                                <BotonNoRequiereConfirmacion
                                                                    reservaId={reservaId}
                                                                    servicePublicId={getReservationServicePublicId(svc)}
                                                                    onResuelto={onServiceResolved}
                                                                />
                                                            ) : (
                                                                // Traslado: falta al menos el titular.
                                                                <span
                                                                    className="text-[10px] font-semibold text-amber-600 dark:text-amber-400 text-right"
                                                                    data-testid={`hint-pasajeros-transfer-mobile-${getReservationServicePublicId(svc)}`}
                                                                >
                                                                    Cargá al menos el titular primero
                                                                </span>
                                                            )
                                                        )}
                                                    </>
                                                );
                                            })()}
                                            {/* Mobile: mismo patrón icono + palabra (spec UX 2026-06-08).
                                                textoTachoMobile sincronizado con la lógica desktop para no bifurcar.
                                                Guía UX 2026-06-22: ocultar botones de escritura en solo lectura.
                                                Servicio ya ANULADO (2026-07-16): ídem desktop, no se muestra
                                                ninguno de los dos botones (ver esServicioAnulado). */}
                                            {!esServicioAnulado(svc) && (puedeEditarServicios || puedeCancelarServicios) && (() => {
                                                const esConfirmadoMobile = esServicioConfirmadoPorOperador(svc);
                                                const mostrarDestructivoMobile = esConfirmadoMobile
                                                    ? puedeCancelarServicios
                                                    : puedeEditarServicios;
                                                // P2 firmado por Gaston (2026-07-22): mismo cambio de texto que
                                                // desktop — "Anular servicio" distingue esta fila de "Anular
                                                // reserva"/"Anular varios servicios"; "Borrar" no se toca.
                                                const textoTachoMobile = esConfirmadoMobile ? 'Anular servicio' : 'Borrar';
                                                // Tanda 7 (2026-07-20): mismo pre-chequeo que desktop — solo
                                                // aplica al camino "Anular" (servicio confirmado). canCancel
                                                // null → no calculado todavía, no bloqueamos (degradación elegante).
                                                // Candado C1 (§1.7): con la reserva bloqueada, manda el candado de
                                                // reserva — el freno fiscal recién se evalúa una vez destrabada.
                                                const bloqueoAnularMobile = esConfirmadoMobile && !candadoDeEdicionActivo
                                                    ? resolverBloqueoAnularServicio(svc)
                                                    : { bloqueado: false, motivo: null };

                                                return (
                                                    <div className="flex flex-col items-end gap-1">
                                                    <div className="flex items-center gap-2">
                                                        {puedeEditarServicios && (
                                                            candadoDeEdicionActivo ? (
                                                                <button
                                                                    type="button"
                                                                    onClick={onRequestEdit}
                                                                    data-testid={`btn-edit-service-mobile-${getReservationServicePublicId(svc)}`}
                                                                    aria-label="Editar servicio — bloqueado, pedí autorización"
                                                                    className="inline-flex items-center gap-1 p-2 text-slate-400 rounded-lg bg-slate-100 dark:bg-slate-800 dark:text-slate-500 text-xs font-semibold"
                                                                >
                                                                    <Lock className="w-3.5 h-3.5" aria-hidden="true" />
                                                                    Editar
                                                                </button>
                                                            ) : (
                                                                <button
                                                                    onClick={() => onEditService(svc)}
                                                                    data-testid={`btn-edit-service-mobile-${getReservationServicePublicId(svc)}`}
                                                                    aria-label="Editar servicio"
                                                                    className="inline-flex items-center gap-1 p-2 text-slate-500 rounded-lg bg-slate-50 dark:bg-slate-800 text-xs font-semibold"
                                                                >
                                                                    <Edit2 className="w-3.5 h-3.5" />
                                                                    Editar
                                                                </button>
                                                            )
                                                        )}
                                                        {/* Papelera mobile: mismo modal borrar vs anular.
                                                            Tanda 7: gris + sin onClick cuando el pre-chequeo bloquea.
                                                            Candado C1: si la reserva está bloqueada, gris + candadito
                                                            de reserva manda ANTES que el pre-chequeo fiscal. */}
                                                        {mostrarDestructivoMobile && (
                                                            candadoDeEdicionActivo ? (
                                                                <button
                                                                    type="button"
                                                                    onClick={onRequestEdit}
                                                                    data-testid={`btn-delete-service-mobile-${getReservationServicePublicId(svc)}`}
                                                                    aria-label={`${textoTachoMobile} — bloqueado, pedí autorización`}
                                                                    className="inline-flex items-center gap-1 p-2 text-slate-400 rounded-lg bg-slate-100 dark:bg-slate-800 dark:text-slate-500 text-xs font-semibold"
                                                                >
                                                                    <Lock className="w-3.5 h-3.5" aria-hidden="true" />
                                                                    {textoTachoMobile}
                                                                </button>
                                                            ) : bloqueoAnularMobile.bloqueado ? (
                                                                // Igual que desktop: bloqueado solo puede darse con
                                                                // esConfirmadoMobile=true, así que textoTachoMobile ya
                                                                // es "Anular servicio" — no se le vuelve a agregar "servicio".
                                                                <button
                                                                    type="button"
                                                                    disabled
                                                                    data-testid={`btn-delete-service-mobile-${getReservationServicePublicId(svc)}`}
                                                                    aria-label={`${textoTachoMobile} bloqueado: ${bloqueoAnularMobile.motivo}`}
                                                                    title={bloqueoAnularMobile.motivo}
                                                                    className="inline-flex items-center gap-1 p-2 text-slate-300 dark:text-slate-600 rounded-lg bg-slate-50 dark:bg-slate-800 text-xs font-semibold cursor-not-allowed"
                                                                >
                                                                    <Trash2 className="w-3.5 h-3.5" />
                                                                    {textoTachoMobile}
                                                                </button>
                                                            ) : (
                                                                <button
                                                                    onClick={() => handleTrashClick(svc)}
                                                                    data-testid={`btn-delete-service-mobile-${getReservationServicePublicId(svc)}`}
                                                                    aria-label={esConfirmadoMobile ? textoTachoMobile : `${textoTachoMobile} servicio`}
                                                                    className="inline-flex items-center gap-1 p-2 text-red-500 rounded-lg bg-red-50 dark:bg-red-900/20 text-xs font-semibold"
                                                                >
                                                                    <Trash2 className="w-3.5 h-3.5" />
                                                                    {textoTachoMobile}
                                                                </button>
                                                            )
                                                        )}
                                                    </div>
                                                    {/* Chip "al lado" de la papelera, mismo criterio que desktop. */}
                                                    {bloqueoAnularMobile.bloqueado && (
                                                        <span
                                                            className="max-w-[200px] text-right text-[10px] font-semibold text-amber-700 dark:text-amber-400"
                                                            data-testid={`aviso-bloqueo-anular-mobile-${getReservationServicePublicId(svc)}`}
                                                        >
                                                            🔒 {bloqueoAnularMobile.motivo}
                                                        </span>
                                                    )}
                                                    </div>
                                                );
                                            })()}
                                        </div>
                                    </div>

                                    {/* Control "Para: Todos" en mobile (ADR-031 v2.1 — Pieza A).
                                        Va debajo del nombre del servicio, en línea propia, arriba de los botones.
                                        El panel de tildes se abre a ancho completo debajo del control.
                                        D2: NO se muestra si el servicio está anulado.
                                        Solo lectura: mismo gate que el control desktop (canEditServices). */}
                                    {puedeEditarServicios && !esServicioAnulado(svc) && (
                                        <div className="mt-2">
                                            <ControlAsignacionServicio
                                                reservaId={reservaId}
                                                serviceType={serviceType}
                                                servicePublicId={servicePublicId}
                                                pasajerosConNombre={pasajerosConNombre}
                                                coverage={coverage}
                                                coverageLoading={coverageLoading}
                                                onAsignacionGuardada={updateCoverage}
                                            />
                                        </div>
                                    )}
                                </div>
                            );
                            }}
                            </ConCoverageDeServicio>
                        );
                        })}
                    </div>
                </>
            )}

            {/* ─ Sección inline "Anular varios" (ADR-025) ───────────────────────
                Se despliega debajo de la lista cuando el usuario presiona el botón.
                Solo visible cuando showCancelarVarios = true.
                El bloqueo fiscal (serviceCancellationBlockReason) se pasa hacia abajo:
                si hay bloqueo, los checkboxes aparecen deshabilitados y el botón
                Confirmar queda apagado — la sección igual se muestra con el aviso. */}
            {showCancelarVarios && (
                <div className="mt-4" data-testid="seccion-cancelar-varios-wrapper">
                    <CancelarVariosServiciosInline
                        serviciosCancelables={serviciosCancelables}
                        reservaPublicId={reservaId}
                        saleInvoices={saleInvoices}
                        blockReason={serviceCancellationBlockReason}
                        onIrAFacturas={onIrAFacturas}
                        onCerrar={() => setShowCancelarVarios(false)}
                        onCancelacionTerminada={() => {
                            // Solo recargamos los datos del padre — NO cerramos la sección.
                            // El usuario necesita leer el resultado (qué falló / cuántos OK)
                            // antes de cerrar manualmente. El botón "Cerrar" del inline
                            // llama a onCerrar cuando el usuario está listo.
                            if (onCancelacionVariosTerminada) {
                                onCancelacionVariosTerminada();
                            }
                        }}
                    />
                </div>
            )}
        </div>
    );
}
