import React, { useEffect, useState } from 'react';
import { X, Calendar, Loader2, AlertTriangle, MoveRight } from 'lucide-react';
import { api } from '../../../api';
import { getApiErrorMessage } from '../../../lib/errors';

/**
 * Convierte un valor de fecha ISO (string) a "YYYY-MM-DD" para usarlo como
 * value de un <input type="date">.
 * Devuelve "" si el valor es nulo, inválido o no parseable.
 *
 * Bug "fechas corridas un día" (2026-07-16): esta función PRE-RELLENA la
 * referencia de "salida actual" del modal. Antes usaba
 * new Date(value).getFullYear()/getMonth()/getDate(), que lee los componentes
 * en hora LOCAL del navegador. Como el backend guarda startDate/endDate como
 * medianoche UTC, en Argentina (UTC-3) eso corría el día hacia atrás y el
 * modal mostraba la salida actual un día antes de la real. Ahora leemos el
 * día calendario directo del texto (string-split), sin pasar por new Date()
 * — mismo patrón que formatearFechaLegible/calcularDeltaDias más abajo.
 */
function toDateInputValue(value) {
    if (!value) return '';
    const soloFecha = String(value).split('T')[0];
    const partes = soloFecha.split('-');
    if (partes.length !== 3) return '';
    const [anio, mes, dia] = partes;
    if (!/^\d{4}$/.test(anio) || !/^\d{2}$/.test(mes) || !/^\d{2}$/.test(dia)) return '';
    return `${anio}-${mes}-${dia}`;
}

/**
 * Formatea una fecha "YYYY-MM-DD" o ISO a "DD/MM/YYYY" para mostrarla al usuario.
 * Usa Date.UTC para evitar desfases de zona horaria.
 */
function formatearFechaLegible(fechaIso) {
    // Tomamos solo la parte de fecha (ignora la parte de hora si viene ISO completo)
    const soloFecha = (fechaIso || '').split('T')[0];
    if (!soloFecha) return '—';

    const partes = soloFecha.split('-');
    if (partes.length !== 3) return '—';

    const [anio, mes, dia] = partes;
    return `${dia}/${mes}/${anio}`;
}

/**
 * Calcula la diferencia en días entre dos fechas en formato "YYYY-MM-DD".
 * Usa Date.UTC para evitar desfases de zona horaria.
 *
 * Devuelve un número entero (puede ser negativo si la nueva fecha es anterior).
 * Devuelve null si alguna de las dos fechas es inválida.
 */
export function calcularDeltaDias(fechaDesde, fechaHasta) {
    if (!fechaDesde || !fechaHasta) return null;

    const parsePartes = (fecha) => {
        const soloFecha = fecha.split('T')[0];
        const partes = soloFecha.split('-');
        if (partes.length !== 3) return null;
        const [anio, mes, dia] = partes;
        const anioNum = Number(anio);
        const mesNum = Number(mes);
        const diaNum = Number(dia);
        if (isNaN(anioNum) || isNaN(mesNum) || isNaN(diaNum)) return null;
        return Date.UTC(anioNum, mesNum - 1, diaNum);
    };

    const desdeMs = parsePartes(fechaDesde);
    const hastaMs = parsePartes(fechaHasta);

    if (desdeMs === null || hastaMs === null) return null;

    return Math.round((hastaMs - desdeMs) / (1000 * 60 * 60 * 24));
}

/**
 * Calcula la nueva fecha de fin sumándole un delta en días a una fecha "YYYY-MM-DD".
 * Usa Date.UTC para evitar desfases.
 * Devuelve "YYYY-MM-DD" o null si la entrada es inválida.
 */
export function calcularNuevaFechaFin(endDateIso, deltaDias) {
    if (!endDateIso || deltaDias === null || deltaDias === undefined) return null;

    const soloFecha = endDateIso.split('T')[0];
    const partes = soloFecha.split('-');
    if (partes.length !== 3) return null;

    const [anio, mes, dia] = partes;
    const anioNum = Number(anio);
    const mesNum = Number(mes);
    const diaNum = Number(dia);
    if (isNaN(anioNum) || isNaN(mesNum) || isNaN(diaNum)) return null;

    // Sumamos el delta en milisegundos
    const nuevaMs = Date.UTC(anioNum, mesNum - 1, diaNum) + deltaDias * 24 * 60 * 60 * 1000;
    const nuevaFecha = new Date(nuevaMs);

    const yyyy = nuevaFecha.getUTCFullYear();
    const mm = String(nuevaFecha.getUTCMonth() + 1).padStart(2, '0');
    const dd = String(nuevaFecha.getUTCDate()).padStart(2, '0');
    return `${yyyy}-${mm}-${dd}`;
}

/**
 * Modal "Reprogramar viaje".
 *
 * Permite mover TODAS las fechas de los servicios de una reserva a partir de una
 * nueva fecha de salida. El sistema calcula el delta (nueva salida − salida actual)
 * y desplaza todos los servicios ese mismo número de días.
 *
 * Cuándo se usa:
 *   - El botón "Reprogramar viaje" en ReservaHeader está visible cuando
 *     capabilities.canEditServices.allowed === true (backend lo controla).
 *
 * Flujo:
 *   1. El usuario ve la fecha de salida actual como referencia.
 *   2. Elige la nueva fecha de salida.
 *   3. Ve una previsualización del corrimiento en días y la nueva fecha de regreso.
 *   4. Confirma → POST /reservas/{id}/reschedule { newStartDate }.
 *   5. Éxito → cierra + recarga la reserva + feedback de éxito.
 *   6. Error → mensaje claro sin cerrar el modal (preserva la fecha elegida).
 *
 * Si la reserva NO tiene startDate (sin fecha cargada aún), el campo se habilita
 * pero se muestra un aviso: el backend retornará 400 y el modal lo informará.
 *
 * Props:
 *   - isOpen: boolean — controla visibilidad.
 *   - reserva: objeto de la reserva (debe tener startDate, endDate, publicId).
 *   - onClose: () => void — cierra sin cambios.
 *   - onReprogramada: () => void — callback al éxito; el padre recarga y muestra toast.
 */
export function ReprogramarViajeModal({ isOpen, reserva, onClose, onReprogramada }) {
    const [nuevaSalida, setNuevaSalida] = useState('');
    const [enviando, setEnviando] = useState(false);
    const [errorMensaje, setErrorMensaje] = useState(null);

    // Al abrir el modal, limpiamos el estado. Preservamos la fecha si ya había una.
    // useEffect con [isOpen]: se ejecuta cada vez que el modal se abre o cierra.
    useEffect(() => {
        if (isOpen) {
            setNuevaSalida('');
            setErrorMensaje(null);
            setEnviando(false);
        }
    }, [isOpen]);

    if (!isOpen) return null;

    // Fecha de salida actual en formato "YYYY-MM-DD" para cálculos y comparaciones.
    const salidaActualInput = toDateInputValue(reserva?.startDate);
    const regresoActualInput = toDateInputValue(reserva?.endDate);

    // ── Previsualización ──────────────────────────────────────────────────────
    // Calculamos el delta solo cuando el usuario eligió una nueva fecha de salida.
    // Si no hay startDate en la reserva, el delta queda en null (no podemos calcular).
    const deltaDias = (salidaActualInput && nuevaSalida)
        ? calcularDeltaDias(salidaActualInput, nuevaSalida)
        : null;

    // Nuevo regreso = regreso actual + delta. Solo se muestra si hay regreso y delta.
    const nuevoRegresoInput = (regresoActualInput && deltaDias !== null)
        ? calcularNuevaFechaFin(regresoActualInput, deltaDias)
        : null;

    // Texto del corrimiento: "adelanta N días", "atrasa N días", "misma fecha".
    const textoCorrimiento = (() => {
        if (deltaDias === null) return null;
        if (deltaDias === 0) return 'Sin corrimiento (es la misma fecha de salida).';
        if (deltaDias > 0) return `El viaje se adelanta ${deltaDias} ${deltaDias === 1 ? 'día' : 'días'}.`;
        return `El viaje se atrasa ${Math.abs(deltaDias)} ${Math.abs(deltaDias) === 1 ? 'día' : 'días'}.`;
    })();

    // ── Validaciones ──────────────────────────────────────────────────────────
    // No permitir enviar si no eligió fecha.
    const puedeEnviar = Boolean(nuevaSalida) && !enviando;

    // Aviso informativo: si la reserva no tiene startDate, el backend va a dar 400.
    const sinFechaSalida = !salidaActualInput;

    const handleReprogramar = async () => {
        if (!puedeEnviar) return;

        setEnviando(true);
        setErrorMensaje(null);

        try {
            const publicId = reserva?.publicId || reserva?.PublicId;
            await api.post(`/reservas/${publicId}/reschedule`, {
                newStartDate: nuevaSalida,
            });

            // Éxito: el padre recarga la reserva y muestra el toast.
            onReprogramada(nuevaSalida);
        } catch (error) {
            // Nunca cerramos el modal en error: el usuario puede ver qué pasó y reintentar.
            // La fecha elegida se preserva en el estado.
            const status = error?.response?.status ?? error?.status ?? 0;

            if (status === 409) {
                const mensajeBackend = getApiErrorMessage(error, '');
                const msLower = (mensajeBackend || '').toLowerCase();

                // El backend distingue dos tipos de 409: documento vivo vs estado/candado.
                if (msLower.includes('factura') || msLower.includes('invoice') ||
                    msLower.includes('voucher')) {
                    setErrorMensaje(
                        'No se puede reprogramar: la reserva tiene factura o voucher emitido. ' +
                        'Corregí por anulación/reemisión.'
                    );
                } else {
                    // 409 por estado no editable o candado
                    setErrorMensaje(
                        mensajeBackend ||
                        'La reserva no puede reprogramarse en el estado actual. ' +
                        'Verificá que no esté bloqueada.'
                    );
                }
            } else if (status === 400) {
                // 400: body inválido o reserva sin fecha de salida cargada
                const mensajeBackend = getApiErrorMessage(error, '');
                setErrorMensaje(
                    mensajeBackend || 'Revisá la fecha elegida. La reserva puede no tener fecha de salida cargada.'
                );
            } else if (status === 404) {
                setErrorMensaje('No se encontró la reserva. Recargá la pantalla.');
            } else {
                setErrorMensaje('No se pudo reprogramar. Intentá de nuevo.');
            }
        } finally {
            setEnviando(false);
        }
    };

    return (
        <div
            className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm p-4 animate-in fade-in duration-200"
            role="dialog"
            aria-modal="true"
            aria-label="Reprogramar viaje"
            // Cerrar con Escape si no está enviando
            onKeyDown={(e) => { if (e.key === 'Escape' && !enviando) onClose(); }}
        >
            <div
                className="w-full max-w-md rounded-2xl border bg-white shadow-2xl dark:bg-slate-900 dark:border-slate-800"
                onClick={(e) => e.stopPropagation()}
            >
                {/* ── Header ───────────────────────────────────────────────────────── */}
                <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
                    <div className="flex items-center gap-2">
                        <Calendar className="h-4 w-4 text-indigo-500" aria-hidden="true" />
                        <h3 className="font-bold text-slate-900 dark:text-white">Reprogramar viaje</h3>
                    </div>
                    <button
                        type="button"
                        onClick={onClose}
                        disabled={enviando}
                        aria-label="Cerrar"
                        className="text-slate-400 hover:text-slate-600 transition-colors disabled:opacity-50 dark:hover:text-slate-200"
                    >
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <div className="p-6 space-y-5">

                    {/* ── Aviso: sin fecha de salida cargada ──────────────────────────── */}
                    {/* Si la reserva no tiene startDate, el backend va a rechazar el pedido.
                        Avisamos ANTES de que el usuario intente y frustre. */}
                    {sinFechaSalida && (
                        <div
                            className="rounded-xl border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-200"
                            role="note"
                        >
                            <div className="flex items-start gap-2">
                                <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5 text-amber-600" aria-hidden="true" />
                                <span>
                                    Esta reserva no tiene fecha de salida cargada. Cargá las fechas
                                    primero (botón "Editar fechas") antes de reprogramar.
                                </span>
                            </div>
                        </div>
                    )}

                    {/* ── Fecha de salida actual ──────────────────────────────────────── */}
                    <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3 dark:border-slate-800 dark:bg-slate-800/50">
                        <div className="text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400 mb-1">
                            Salida actual
                        </div>
                        <div className="font-bold text-slate-900 dark:text-white">
                            {salidaActualInput ? formatearFechaLegible(salidaActualInput) : (
                                <span className="italic font-normal text-slate-400">Sin cargar</span>
                            )}
                        </div>
                        {regresoActualInput && (
                            <div className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
                                Regreso actual: {formatearFechaLegible(regresoActualInput)}
                            </div>
                        )}
                    </div>

                    {/* ── Campo: nueva fecha de salida ────────────────────────────────── */}
                    <div>
                        <label
                            htmlFor="nueva-salida-reprogramar"
                            className="mb-1 block text-xs font-bold uppercase tracking-wider text-slate-600 dark:text-slate-400"
                        >
                            Nueva fecha de salida <span className="text-rose-500">*</span>
                        </label>
                        <input
                            id="nueva-salida-reprogramar"
                            type="date"
                            value={nuevaSalida}
                            onChange={(e) => {
                                setNuevaSalida(e.target.value);
                                // Limpiamos el error anterior al cambiar la fecha: el usuario está corrigiendo.
                                setErrorMensaje(null);
                            }}
                            disabled={enviando}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 disabled:opacity-50 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                            data-testid="input-nueva-salida-reprogramar"
                        />
                    </div>

                    {/* ── Previsualización del corrimiento ────────────────────────────── */}
                    {/* Solo aparece cuando el usuario ya eligió una fecha nueva.
                        Muestra el delta en días y el nuevo regreso para que pueda verificar. */}
                    {nuevaSalida && deltaDias !== null && (
                        <div
                            className="rounded-xl border border-indigo-200 bg-indigo-50 px-4 py-3 text-sm dark:border-indigo-800 dark:bg-indigo-950/30"
                            data-testid="previa-corrimiento"
                        >
                            {/* Flecha visual: salida actual → nueva salida */}
                            <div className="flex items-center gap-2 font-semibold text-indigo-800 dark:text-indigo-200 mb-2">
                                <span>{salidaActualInput ? formatearFechaLegible(salidaActualInput) : '—'}</span>
                                <MoveRight className="h-4 w-4 flex-shrink-0" aria-hidden="true" />
                                <span>{formatearFechaLegible(nuevaSalida)}</span>
                                <span className="ml-auto text-xs font-bold text-indigo-600 dark:text-indigo-300">
                                    {deltaDias === 0 ? '±0 días' : deltaDias > 0 ? `+${deltaDias} días` : `${deltaDias} días`}
                                </span>
                            </div>

                            {/* Nuevo regreso calculado (solo si la reserva tiene endDate) */}
                            {nuevoRegresoInput && (
                                <div className="text-indigo-700 dark:text-indigo-300">
                                    Regreso pasa a: <strong>{formatearFechaLegible(nuevoRegresoInput)}</strong>
                                </div>
                            )}

                            <p className="mt-1 text-xs text-indigo-600 dark:text-indigo-400">
                                {textoCorrimiento}
                            </p>
                        </div>
                    )}

                    {/* ── Aviso operativo ─────────────────────────────────────────────── */}
                    {/* Decisión de UX: avisamos siempre (no solo en hover) que esto mueve TODAS
                        las fechas. Es una acción con impacto en todos los servicios — el usuario
                        tiene que entender el alcance antes de confirmar. */}
                    <div
                        className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2.5 text-xs text-slate-600 dark:border-slate-700 dark:bg-slate-800/50 dark:text-slate-400"
                        role="note"
                    >
                        Se mueven <strong>TODAS las fechas de los servicios</strong> el mismo
                        corrimiento. No cambia precios ni costos.
                    </div>

                    {/* ── Mensaje de error del servidor ──────────────────────────────── */}
                    {/* El modal permanece abierto con la fecha elegida para que el usuario pueda reintentar. */}
                    {errorMensaje && (
                        <div
                            className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-800 dark:border-rose-800 dark:bg-rose-950/30 dark:text-rose-200"
                            role="alert"
                            data-testid="error-reprogramar-viaje"
                        >
                            <div className="flex items-start gap-2">
                                <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
                                <span>{errorMensaje}</span>
                            </div>
                        </div>
                    )}
                </div>

                {/* ── Botones ────────────────────────────────────────────────────────── */}
                <div className="flex justify-end gap-3 border-t border-slate-100 px-6 py-4 dark:border-slate-800">
                    <button
                        type="button"
                        onClick={onClose}
                        disabled={enviando}
                        className="rounded-lg px-4 py-2 text-sm font-bold text-slate-600 hover:bg-slate-100 disabled:opacity-50 transition-colors dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                        Cancelar
                    </button>
                    <button
                        type="button"
                        onClick={handleReprogramar}
                        disabled={!puedeEnviar}
                        data-testid="btn-confirmar-reprogramar"
                        className="flex items-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-bold text-white transition-colors hover:bg-indigo-700 disabled:opacity-50"
                    >
                        {enviando && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
                        {enviando ? 'Reprogramando…' : 'Reprogramar'}
                    </button>
                </div>
            </div>
        </div>
    );
}
