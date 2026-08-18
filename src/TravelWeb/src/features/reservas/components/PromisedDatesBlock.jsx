import { useState } from 'react';
import { Plus, Pencil, Lock, AlertTriangle } from 'lucide-react';
import { Button } from '../../../components/ui/button';
import { api } from '../../../api';
import { showSuccess } from '../../../alerts';
import { getApiErrorMessage } from '../../../lib/errors';
import { formatTripDate, toDateInputValue } from '../lib/tripDateFormat';
import { hayDiscrepanciaFechaPrometida, tieneFechaPrometidaCargada } from '../lib/promisedDatesLogic';

/**
 * Bloque de "Fecha prometida al cliente" (ADR-053, spec UX 2026-08-13, P3/P4):
 * par de fechas OPCIONAL que carga el vendedor a mano y que el cálculo automático
 * de Salida/Regreso nunca pisa. Vive debajo del renglón de fechas calculadas
 * (ver TripDatesRow.jsx), escondido detrás de un enlace chiquito hasta que se usa
 * (P3, decisión firmada: "la gran mayoría de las reservas no la va a usar").
 *
 * Tres formas visuales, según el estado:
 *   1. Nada cargado todavía → enlace discreto "Fecha prometida al cliente +".
 *   2. El vendedor tocó el enlace / "Editar" → casilleros EN LA MISMA pantalla
 *      (nunca ventana flotante, guía 2026-08-03 P6=A), con Guardar/Descartar.
 *   3. Ya hay algo cargado → renglón gris chico "Fecha prometida al cliente: sale
 *      el .../vuelve el ..." + enlace "Editar". Si no coincide con lo calculado,
 *      se suma un renglón ámbar aparte (P8, decisión firmada = opción C).
 */
export function PromisedDatesBlock({ reserva, publicId, canEdit, candadoActivo, onRequestEdit, onSaved }) {
    const [mostrarFormulario, setMostrarFormulario] = useState(false);
    const [salidaInput, setSalidaInput] = useState('');
    const [regresoInput, setRegresoInput] = useState('');
    const [guardando, setGuardando] = useState(false);
    const [errorGuardado, setErrorGuardado] = useState(null);

    const promisedStartLabel = formatTripDate(reserva.promisedStartDate);
    const promisedEndLabel = formatTripDate(reserva.promisedEndDate);
    const hayAlgoCargado = tieneFechaPrometidaCargada(reserva);

    // Reservas en solo lectura dura (Anulada/Perdida/Finalizada/Archivada, guía
    // ADR-036): no se ofrece ningún enlace de edición, pero lo cargado se sigue
    // viendo (spec §7, "lo cargado nunca se esconde").
    const puedeEditar = canEdit === true;

    const abrirFormulario = () => {
        // Pre-carga con lo que la reserva tiene guardado AHORA — si el vendedor
        // había tocado algo y cerró sin guardar (Descartar), la próxima vez que
        // abre vuelve a ver el valor real del servidor, no un resto viejo.
        setSalidaInput(toDateInputValue(reserva.promisedStartDate));
        setRegresoInput(toDateInputValue(reserva.promisedEndDate));
        setErrorGuardado(null);
        setMostrarFormulario(true);
    };

    const handleGuardar = async () => {
        // Validación de USABILIDAD, no de negocio (P-11): evita el viaje al
        // backend cuando a simple vista el regreso queda antes que la salida. El
        // backend sigue siendo quien decide si el resto del pedido es válido.
        if (salidaInput && regresoInput && regresoInput < salidaInput) {
            setErrorGuardado('La fecha de "vuelve el" no puede ser anterior a "sale el".');
            return;
        }
        setErrorGuardado(null);
        setGuardando(true);
        try {
            const teniaSalidaAntes = Boolean(reserva.promisedStartDate);
            const teniaRegresoAntes = Boolean(reserva.promisedEndDate);
            await api.patch(`/reservas/${publicId}/promised-dates`, {
                promisedStartDate: salidaInput || null,
                promisedEndDate: regresoInput || null,
                // Casillero vacío + antes había algo cargado = pedirle al backend que lo borre.
                clearPromisedStartDate: !salidaInput && teniaSalidaAntes,
                clearPromisedEndDate: !regresoInput && teniaRegresoAntes,
            });
            showSuccess('Fecha prometida guardada.');
            setMostrarFormulario(false);
            // El padre refetchea la ficha completa — misma fuente de verdad que el
            // resto de la pantalla, no guardamos un estado local aparte que podría
            // desalinearse del servidor.
            onSaved?.();
        } catch (error) {
            // Error PEGADO al bloque, sin perder lo tipeado (spec §7) — no es un
            // toast, así el vendedor ve el motivo justo al lado de lo que escribió.
            setErrorGuardado(getApiErrorMessage(error, 'No se pudo guardar la fecha prometida.'));
        } finally {
            setGuardando(false);
        }
    };

    // ─── Estado 2: formulario en línea ─────────────────────────────────────────
    // `w-fit` (fix layout 2026-08-18, vuelve a como estaba antes del band-aid de
    // Tanda A UX 2026-08-16): el bloque de fechas y el botón "Reprogramar viaje"
    // ahora viven en renglones separados (ver ReservaHeader.jsx), así que este
    // form ya no tiene que competir por ancho con nada — puede ocupar solo el
    // ancho que necesita, como cualquier casillero suelto de la pantalla.
    if (mostrarFormulario) {
        return (
            <div
                className="w-fit rounded-[10px] border border-slate-200 bg-white p-3 text-sm dark:border-slate-800 dark:bg-slate-900"
                data-testid="promised-dates-form"
            >
                <p className="mb-2 text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                    Fecha prometida al cliente
                </p>
                <div className="flex flex-wrap items-end gap-3">
                    <label className="flex flex-col gap-1 text-xs text-slate-500 dark:text-slate-400">
                        Sale el
                        <input
                            type="date"
                            value={salidaInput}
                            onChange={(e) => setSalidaInput(e.target.value)}
                            disabled={guardando}
                            data-testid="promised-dates-input-salida"
                            className="rounded-[10px] border border-slate-200 bg-white px-2 py-1 text-sm text-slate-900 focus:border-primary focus:outline-none focus:ring-2 focus:ring-primary/20 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                        />
                    </label>
                    <label className="flex flex-col gap-1 text-xs text-slate-500 dark:text-slate-400">
                        Vuelve el
                        <input
                            type="date"
                            value={regresoInput}
                            onChange={(e) => setRegresoInput(e.target.value)}
                            disabled={guardando}
                            data-testid="promised-dates-input-regreso"
                            className="rounded-[10px] border border-slate-200 bg-white px-2 py-1 text-sm text-slate-900 focus:border-primary focus:outline-none focus:ring-2 focus:ring-primary/20 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                        />
                    </label>
                </div>

                {errorGuardado && (
                    <p className="mt-2 text-xs font-medium text-rose-600 dark:text-rose-400" data-testid="promised-dates-error">
                        {errorGuardado}
                    </p>
                )}

                <div className="mt-3 flex justify-end gap-2">
                    {/* "Descartar", no "Cancelar" (decisión del dueño 11/08 — este botón
                        SOLO cierra el bloque sin guardar, "cancelar" es otra palabra del
                        negocio en este producto, ver B.7 del estándar visual). Mismo molde
                        Guardar/Descartar que ServiceInlineCard.jsx (Button del sistema,
                        size="sm" = 32px, la altura que pide el estándar dentro de un bloque
                        chico como este). */}
                    <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={() => setMostrarFormulario(false)}
                        disabled={guardando}
                        data-testid="promised-dates-descartar"
                    >
                        Descartar
                    </Button>
                    <Button
                        type="button"
                        size="sm"
                        onClick={handleGuardar}
                        disabled={guardando}
                        data-testid="promised-dates-guardar"
                    >
                        {guardando ? 'Guardando…' : 'Guardar'}
                    </Button>
                </div>
            </div>
        );
    }

    // Sin candado y sin nada cargado y sin permiso: no hay nada que ofrecer ni
    // que mostrar — el bloque entero desaparece (no deja un enlace muerto).
    if (!puedeEditar && !hayAlgoCargado) return null;

    // ─── Estado 3: ya hay algo cargado (candado o no, siempre se VE lo cargado) ─
    if (hayAlgoCargado) {
        const partes = [];
        if (promisedStartLabel) partes.push(`sale el ${promisedStartLabel}`);
        if (promisedEndLabel) partes.push(`vuelve el ${promisedEndLabel}`);

        const discrepa = hayDiscrepanciaFechaPrometida(reserva);

        return (
            <div className="flex flex-col gap-1 pl-1">
                <p className="text-xs text-slate-400 dark:text-slate-500" data-testid="promised-dates-resumen">
                    <span className="font-semibold text-slate-500 dark:text-slate-400">Fecha prometida al cliente:</span>{' '}
                    {partes.join(', ')}
                    {puedeEditar && (
                        candadoActivo ? (
                            <button
                                type="button"
                                onClick={onRequestEdit}
                                data-testid="promised-dates-editar"
                                aria-label="Editar fecha prometida — bloqueado, pedí autorización"
                                className="ml-1.5 inline-flex items-center gap-1 font-bold text-slate-400 hover:text-slate-600 dark:text-slate-500 dark:hover:text-slate-300"
                            >
                                <Lock className="h-3 w-3" aria-hidden="true" />
                                Editar
                            </button>
                        ) : (
                            <button
                                type="button"
                                onClick={abrirFormulario}
                                data-testid="promised-dates-editar"
                                className="ml-1.5 inline-flex items-center gap-1 font-bold text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200"
                            >
                                <Pencil className="h-3 w-3" aria-hidden="true" />
                                Editar
                            </button>
                        )
                    )}
                </p>
                {/* P8 (decisión firmada, opción C): renglón ÁMBAR aparte cuando lo
                    prometido no coincide con lo calculado. No repite el dato de arriba
                    (P-16) — solo agrega la alerta de que hay una diferencia. */}
                {discrepa && (
                    <p
                        className="inline-flex items-center gap-1 text-xs font-semibold text-amber-700 dark:text-amber-400"
                        data-testid="promised-dates-discrepancia"
                    >
                        <AlertTriangle className="h-3.5 w-3.5 flex-shrink-0" aria-hidden="true" />
                        Lo prometido al cliente no coincide con los servicios cargados
                    </p>
                )}
            </div>
        );
    }

    // ─── Estado 1: nunca se cargó nada (lo que ve el 95% de las reservas) ──────
    return candadoActivo ? (
        <button
            type="button"
            onClick={onRequestEdit}
            data-testid="promised-dates-abrir"
            aria-label="Fecha prometida al cliente — bloqueado, pedí autorización"
            className="inline-flex w-fit items-center gap-1 pl-1 text-xs font-medium text-slate-400 hover:text-slate-500 dark:text-slate-500 dark:hover:text-slate-400"
        >
            <Lock className="h-3 w-3" aria-hidden="true" />
            Fecha prometida al cliente
        </button>
    ) : (
        <button
            type="button"
            onClick={abrirFormulario}
            data-testid="promised-dates-abrir"
            className="inline-flex w-fit items-center gap-1 pl-1 text-xs font-medium text-slate-400 hover:text-slate-600 dark:text-slate-500 dark:hover:text-slate-300"
        >
            Fecha prometida al cliente
            <Plus className="h-3 w-3" aria-hidden="true" />
        </button>
    );
}
