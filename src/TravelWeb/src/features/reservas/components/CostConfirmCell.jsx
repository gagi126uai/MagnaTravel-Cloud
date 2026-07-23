/**
 * Celda de costo con pill "A confirmar" y modo edición inline.
 *
 * Se usa en ServiceList cuando el flag EnableCatalogFindOrCreate está ON
 * y el usuario tiene el permiso cobranzas.see_cost.
 *
 * Flujo:
 *   1. Vista lectura: muestra costo + pill "A confirmar" + botón "Confirmar costo"
 *      (solo si costToConfirm === true).
 *   2. Modo edición: al apretar el botón, la celda se transforma en un mini-form
 *      inline (sin modal) con inputs de Costo e Impuesto, y botones Confirmar/Cancelar.
 *   3. Ventana $0: si el costo editado es 0 o vacío, se abre un alertdialog antes
 *      de enviar al backend, pidiendo confirmación explícita.
 *   4. Éxito: el response del endpoint reemplaza el servicio en el estado del padre
 *      (sin recargar la página). La pill y el botón desaparecen.
 *   5. Error: se queda en modo edición con los valores del usuario + texto rojo.
 *
 * El component no valida costo negativo en el front: el backend lo rechaza y
 * devuelve su mensaje, que se muestra tal cual al usuario.
 *
 * Decisión del dueño: confirm-cost se permite aunque el servicio esté cancelado
 * o la reserva facturada (nunca toca la factura).
 */

import { useState, useRef, useEffect, useCallback } from "react";
import { Lock } from "lucide-react";
import { api } from "../../../api";
import { getApiErrorMessage } from "../../../lib/errors";
import { SERVICE_RECORD_KIND, getReservationServicePublicId } from "../lib/reservationServiceModel";

// Mapa recordKind → segmento del endpoint de confirm-cost
const ENDPOINT_POR_TIPO = {
    [SERVICE_RECORD_KIND.HOTEL]: "hotels",
    [SERVICE_RECORD_KIND.FLIGHT]: "flights",
    [SERVICE_RECORD_KIND.TRANSFER]: "transfers",
    [SERVICE_RECORD_KIND.PACKAGE]: "packages",
    [SERVICE_RECORD_KIND.ASSISTANCE]: "assistances",
};

/**
 * Construye la URL del endpoint confirm-cost según el tipo de servicio.
 * Ejemplo: /reservas/abc123/hotels/def456/confirm-cost
 */
function buildConfirmCostUrl(reservaId, service) {
    const segmento = ENDPOINT_POR_TIPO[service.recordKind];
    const serviceId = getReservationServicePublicId(service);
    return `/reservas/${reservaId}/${segmento}/${serviceId}/confirm-cost`;
}

// ─── Clases reutilizables ─────────────────────────────────────────────────────

const CLASES_PILL_AMBAR = "inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-semibold bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400";
const CLASES_BTN_CONFIRMAR_COSTO = "text-[11px] font-semibold px-2 py-1 rounded-lg border border-amber-300 text-amber-700 bg-amber-50 hover:bg-amber-100 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-400 transition-colors";
const CLASES_INPUT = "w-20 rounded border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-900 px-2 py-1 text-xs text-slate-900 dark:text-white focus:outline-none focus:ring-1 focus:ring-blue-500";

// ─── Diálogo de confirmación de costo $0 ─────────────────────────────────────

/**
 * AlertDialog que aparece cuando el usuario intenta confirmar costo $0 o vacío.
 * Trapa el foco (solo Tab/Shift+Tab entre "Volver" y "Sí, confirmar").
 * Esc = Volver (cancela sin cerrar el modo edición).
 *
 * Props:
 *   onVolver       — cierra el diálogo, vuelve a modo edición
 *   onConfirmar    — cierra el diálogo y dispara el envío al backend
 */
function DialogoCostoCero({ onVolver, onConfirmar }) {
    const dialogRef = useRef(null);
    const volverBtnRef = useRef(null);
    const confirmarBtnRef = useRef(null);

    // Al montar el diálogo, el foco va a "Volver" (botón secundario, acción segura)
    useEffect(() => {
        volverBtnRef.current?.focus();
    }, []);

    // Trampa de foco: Tab/Shift+Tab cicla solo entre los dos botones
    const handleKeyDown = useCallback((e) => {
        if (e.key === "Escape") {
            e.preventDefault();
            onVolver();
            return;
        }

        if (e.key === "Tab") {
            const focusableElements = [volverBtnRef.current, confirmarBtnRef.current].filter(Boolean);
            const first = focusableElements[0];
            const last = focusableElements[focusableElements.length - 1];

            if (e.shiftKey && document.activeElement === first) {
                e.preventDefault();
                last?.focus();
            } else if (!e.shiftKey && document.activeElement === last) {
                e.preventDefault();
                first?.focus();
            }
        }
    }, [onVolver]);

    const dialogId = "dialog-confirm-zero-cost";
    const titleId = "dialog-zero-cost-title";
    const descId = "dialog-zero-cost-desc";

    return (
        // Fondo oscuro que bloquea el resto de la UI
        <div
            className="fixed inset-0 z-50 flex items-center justify-center bg-black/40"
            onKeyDown={handleKeyDown}
        >
            <div
                ref={dialogRef}
                role="alertdialog"
                aria-modal="true"
                aria-labelledby={titleId}
                aria-describedby={descId}
                data-testid={dialogId}
                className="bg-white dark:bg-slate-900 rounded-xl shadow-xl border border-slate-200 dark:border-slate-700 p-6 max-w-sm w-full mx-4"
            >
                <h2
                    id={titleId}
                    className="text-base font-semibold text-slate-900 dark:text-white mb-2"
                >
                    ¿Seguro?
                </h2>
                <p
                    id={descId}
                    className="text-sm text-slate-600 dark:text-slate-400 mb-6"
                >
                    Va a quedar costo $0 como sugerencia para todos.
                </p>
                <div className="flex gap-3 justify-end">
                    {/* Foco inicial en Volver (acción segura) */}
                    <button
                        ref={volverBtnRef}
                        type="button"
                        onClick={onVolver}
                        className="px-4 py-2 text-sm font-medium text-slate-600 border border-slate-200 rounded-lg hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800 transition-colors"
                    >
                        Volver
                    </button>
                    <button
                        ref={confirmarBtnRef}
                        type="button"
                        onClick={onConfirmar}
                        className="px-4 py-2 text-sm font-semibold text-white bg-amber-600 hover:bg-amber-700 rounded-lg transition-colors"
                    >
                        Sí, confirmar
                    </button>
                </div>
            </div>
        </div>
    );
}

// ─── Componente principal CostConfirmCell ─────────────────────────────────────

/**
 * Props:
 *   service        — servicio normalizado (necesita recordKind, netCost, costToConfirm, currency, etc.)
 *   reservaId      — publicId de la reserva para construir la URL del endpoint
 *   onConfirmado   — callback(servicioActualizado) llamado con el DTO devuelto por el backend;
 *                    el padre lo usa para reemplazar el servicio en el estado sin recargar
 *   candadoActivo  — bool (candado C1, spec 2026-07-22): true cuando la reserva está bloqueada
 *                    sin autorización de edición viva. El botón "Confirmar costo" queda
 *                    gris + candadito y, al tocarlo, abre la ventana de destrabar en vez de
 *                    entrar al modo edición. Default false: comportamiento sin cambios.
 *   onRequestEdit  — callback () => void que abre la ventana de destrabar (EditAuthorizationModal).
 *                    Solo se usa cuando candadoActivo=true.
 */
export function CostConfirmCell({ service, reservaId, onConfirmado, candadoActivo = false, onRequestEdit }) {
    const [modoEdicion, setModoEdicion] = useState(false);
    const [valorCosto, setValorCosto] = useState("");
    const [valorImpuesto, setValorImpuesto] = useState("");
    const [confirmando, setConfirmando] = useState(false);
    const [error, setError] = useState(null);
    const [mostrarDialogoCero, setMostrarDialogoCero] = useState(false);

    // Ref del botón "Confirmar costo" para devolver el foco al cancelar
    const botonConfirmarCostoRef = useRef(null);
    // Ref del input de costo para hacer focus+select al entrar en modo edición
    const inputCostoRef = useRef(null);

    // Al entrar en modo edición, precargamos con el valor actual y hacemos focus+select
    const entrarModoEdicion = useCallback(() => {
        const costoActual = service.netCost ?? 0;
        const impuestoActual = service.tax ?? 0;
        setValorCosto(String(costoActual));
        setValorImpuesto(String(impuestoActual));
        setError(null);
        setModoEdicion(true);
    }, [service.netCost, service.tax]);

    // useEffect: cuando modoEdicion pasa a true, hacemos focus y seleccionamos el valor del input
    useEffect(() => {
        if (modoEdicion) {
            inputCostoRef.current?.focus();
            inputCostoRef.current?.select();
        }
    }, [modoEdicion]);

    const salirModoEdicion = useCallback(() => {
        setModoEdicion(false);
        setError(null);
        // Devolvemos el foco al botón "Confirmar costo" al cancelar
        // (pequeño setTimeout porque el botón se re-renderiza luego del setState)
        setTimeout(() => botonConfirmarCostoRef.current?.focus(), 0);
    }, []);

    /**
     * Envía el POST confirm-cost al backend.
     * En éxito: llama onConfirmado con el DTO actualizado (el padre actualiza la lista).
     * En error: queda en modo edición con el mensaje de error del server.
     * NOTA: declarado ANTES de intentarConfirmar porque este lo referencia en su
     * array de deps (evaluación eager en cada render; un const en TDZ lanzaría
     * ReferenceError). Mismo orden que en CostConfirmCellMobile.
     */
    const enviarAlBackend = useCallback(async (netCost, tax) => {
        setConfirmando(true);
        setError(null);

        try {
            const url = buildConfirmCostUrl(reservaId, service);
            const body = {};
            // El backend acepta body opcional; si vienen valores los enviamos
            if (netCost !== undefined) body.netCost = netCost;
            if (tax !== undefined) body.tax = tax;

            const servicioActualizado = await api.post(url, body);

            // Éxito: el padre reemplaza el servicio con el DTO devuelto
            // La celda vuelve a vista lectura (modoEdicion = false)
            setModoEdicion(false);
            onConfirmado(servicioActualizado);
        } catch (err) {
            // El backend rechaza costos negativos con su propio mensaje.
            // No validamos negativo en el front (el server es la fuente de verdad).
            setError(
                getApiErrorMessage(err, "No se pudo confirmar. Probá de nuevo.")
            );
        } finally {
            setConfirmando(false);
        }
    }, [reservaId, service, onConfirmado]);

    /**
     * Intenta confirmar el costo.
     * Si el costo resultante es 0 o vacío, abre el diálogo de confirmación antes de enviar.
     */
    const intentarConfirmar = useCallback(() => {
        const costoFinal = Number(valorCosto) || 0;

        // Ventana $0: si el costo es 0 o vacío (vacío = 0), mostrar diálogo
        if (costoFinal === 0) {
            setMostrarDialogoCero(true);
            return;
        }

        // Costo no-cero: enviar directamente
        enviarAlBackend(costoFinal, Number(valorImpuesto) || 0);
    // enviarAlBackend va en deps porque es un useCallback que depende de reservaId/service/onConfirmado.
    // Sin él, intentarConfirmar captura una versión desactualizada de enviarAlBackend.
    }, [valorCosto, valorImpuesto, enviarAlBackend]);

    // ─── Handlers de teclado en el modo edición ───────────────────────────────

    /**
     * Handler de teclado para el CONTENEDOR del modo edición.
     * - Esc: cancela siempre (funciona aunque el foco esté en los botones Confirmar/Cancelar)
     * - Enter: solo confirma si el foco está en un input (para no disparar al presionar Enter en un botón)
     *
     * Usamos e.target para distinguir el origen: `tagName === "INPUT"` garantiza
     * que Enter solo actúa en los campos de texto, no en los botones.
     */
    const handleKeyDownContenedor = useCallback((e) => {
        if (e.key === "Escape") {
            e.preventDefault();
            salirModoEdicion();
            return;
        }
        if (e.key === "Enter" && e.target.tagName === "INPUT") {
            e.preventDefault();
            intentarConfirmar();
        }
    }, [intentarConfirmar, salirModoEdicion]);

    // ─── Formato del costo en vista lectura ───────────────────────────────────

    const netCostDisplay = (service.netCost || 0).toLocaleString(undefined, { minimumFractionDigits: 2 });
    const moneda = service.currency || "ARS";

    // ─── Vista en modo edición ────────────────────────────────────────────────

    if (modoEdicion) {
        return (
            <>
                {/* Diálogo de confirmación de costo $0 (sobre todo lo demás) */}
                {mostrarDialogoCero && (
                    <DialogoCostoCero
                        onVolver={() => {
                            setMostrarDialogoCero(false);
                            // Al cerrar el diálogo con "Volver", devolvemos el foco al input de costo.
                            // setTimeout porque el diálogo se desmonta antes de que el input sea focuseable.
                            setTimeout(() => inputCostoRef.current?.focus(), 0);
                        }}
                        onConfirmar={() => {
                            setMostrarDialogoCero(false);
                            enviarAlBackend(0, Number(valorImpuesto) || 0);
                        }}
                    />
                )}

                {/* Fila 1: etiquetas */}
                <div className="flex gap-2 text-[10px] text-slate-500 mb-0.5">
                    <span className="w-20">Costo</span>
                    <span className="w-16">Impuesto</span>
                </div>

                {/* Fila 2: inputs + moneda + botones.
                    onKeyDown en el contenedor para que Esc funcione también con foco en botones. */}
                <div className="flex items-center gap-1.5 flex-wrap" onKeyDown={handleKeyDownContenedor}>
                    <input
                        ref={inputCostoRef}
                        type="number"
                        value={valorCosto}
                        onChange={(e) => {
                            setValorCosto(e.target.value);
                            setError(null);
                        }}
                        disabled={confirmando}
                        className={CLASES_INPUT}
                        aria-label="Costo a confirmar"
                        data-testid="input-confirm-cost"
                    />
                    <span className="text-[10px] text-slate-500 font-mono">{moneda}</span>
                    <input
                        type="number"
                        value={valorImpuesto}
                        onChange={(e) => {
                            setValorImpuesto(e.target.value);
                            setError(null);
                        }}
                        disabled={confirmando}
                        className={CLASES_INPUT}
                        aria-label="Impuesto del costo"
                        data-testid="input-confirm-tax"
                    />
                    <button
                        type="button"
                        onClick={intentarConfirmar}
                        disabled={confirmando}
                        aria-busy={confirmando}
                        className="text-[11px] font-semibold px-2 py-1 rounded-lg bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-60 transition-colors"
                        data-testid="btn-confirm-cost-submit"
                    >
                        {confirmando ? "Confirmando…" : "Confirmar"}
                    </button>
                    <button
                        type="button"
                        onClick={salirModoEdicion}
                        disabled={confirmando}
                        className="text-[11px] font-medium px-2 py-1 rounded-lg border border-slate-200 dark:border-slate-700 text-slate-600 dark:text-slate-400 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-60 transition-colors"
                        data-testid="btn-confirm-cost-cancel"
                    >
                        Cancelar
                    </button>
                </div>

                {/* Fila 3: mensaje de error (solo cuando hubo un fallo) */}
                {error && (
                    <div
                        className="text-[10px] text-rose-600 dark:text-rose-400 mt-1"
                        role="alert"
                    >
                        {error}
                    </div>
                )}
            </>
        );
    }

    // ─── Vista en modo lectura ────────────────────────────────────────────────

    return (
        <>
            {/* Monto del costo */}
            <div className="text-xs text-slate-500 font-mono">${netCostDisplay}</div>

            {/* Pill "A confirmar" + botón "Confirmar costo" (solo si costToConfirm) */}
            {service.costToConfirm && (
                <div className="flex flex-col items-end gap-1 mt-1">
                    <span
                        className={CLASES_PILL_AMBAR}
                        data-testid="pill-cost-to-confirm"
                    >
                        A confirmar
                    </span>
                    {/* Candado C1 (2026-07-22): con la reserva bloqueada y sin autorización viva,
                        el botón queda gris + candadito y abre la ventana de destrabar en vez de
                        entrar al modo edición. */}
                    {candadoActivo ? (
                        <button
                            ref={botonConfirmarCostoRef}
                            type="button"
                            onClick={onRequestEdit}
                            aria-label="Confirmar costo — bloqueado, pedí autorización"
                            className="inline-flex items-center gap-1 text-[11px] font-semibold px-2 py-1 rounded-lg border border-slate-200 bg-slate-100 text-slate-500 hover:bg-slate-200 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700 transition-colors"
                            data-testid="btn-confirm-cost"
                        >
                            <Lock className="h-3 w-3" aria-hidden="true" />
                            Confirmar costo
                        </button>
                    ) : (
                        <button
                            ref={botonConfirmarCostoRef}
                            type="button"
                            onClick={entrarModoEdicion}
                            className={CLASES_BTN_CONFIRMAR_COSTO}
                            data-testid="btn-confirm-cost"
                        >
                            Confirmar costo
                        </button>
                    )}
                </div>
            )}
        </>
    );
}

/**
 * Versión mobile del CostConfirmCell.
 * Mismo flujo, layout vertical apilado (sin la fila de etiquetas separada).
 */
export function CostConfirmCellMobile({ service, reservaId, onConfirmado, candadoActivo = false, onRequestEdit }) {
    const [modoEdicion, setModoEdicion] = useState(false);
    const [valorCosto, setValorCosto] = useState("");
    const [valorImpuesto, setValorImpuesto] = useState("");
    const [confirmando, setConfirmando] = useState(false);
    const [error, setError] = useState(null);
    const [mostrarDialogoCero, setMostrarDialogoCero] = useState(false);

    const botonConfirmarCostoRef = useRef(null);
    const inputCostoRef = useRef(null);

    const entrarModoEdicion = useCallback(() => {
        setValorCosto(String(service.netCost ?? 0));
        setValorImpuesto(String(service.tax ?? 0));
        setError(null);
        setModoEdicion(true);
    }, [service.netCost, service.tax]);

    useEffect(() => {
        if (modoEdicion) {
            inputCostoRef.current?.focus();
            inputCostoRef.current?.select();
        }
    }, [modoEdicion]);

    const salirModoEdicion = useCallback(() => {
        setModoEdicion(false);
        setError(null);
        setTimeout(() => botonConfirmarCostoRef.current?.focus(), 0);
    }, []);

    const enviarAlBackend = useCallback(async (netCost, tax) => {
        setConfirmando(true);
        setError(null);

        try {
            const url = buildConfirmCostUrl(reservaId, service);
            const body = {};
            if (netCost !== undefined) body.netCost = netCost;
            if (tax !== undefined) body.tax = tax;

            const servicioActualizado = await api.post(url, body);
            setModoEdicion(false);
            onConfirmado(servicioActualizado);
        } catch (err) {
            setError(getApiErrorMessage(err, "No se pudo confirmar. Probá de nuevo."));
        } finally {
            setConfirmando(false);
        }
    }, [reservaId, service, onConfirmado]);

    const intentarConfirmar = useCallback(() => {
        const costoFinal = Number(valorCosto) || 0;
        if (costoFinal === 0) {
            setMostrarDialogoCero(true);
            return;
        }
        enviarAlBackend(costoFinal, Number(valorImpuesto) || 0);
    }, [valorCosto, valorImpuesto, enviarAlBackend]);

    /**
     * Handler de teclado para el CONTENEDOR del modo edición mobile.
     * Mismo patrón que el desktop: Esc cancela siempre; Enter solo confirma desde un input.
     */
    const handleKeyDownContenedor = useCallback((e) => {
        if (e.key === "Escape") {
            e.preventDefault();
            salirModoEdicion();
            return;
        }
        if (e.key === "Enter" && e.target.tagName === "INPUT") {
            e.preventDefault();
            intentarConfirmar();
        }
    }, [intentarConfirmar, salirModoEdicion]);

    const moneda = service.currency || "ARS";
    const netCostDisplay = (service.netCost || 0).toLocaleString();

    if (modoEdicion) {
        return (
            // onKeyDown en el contenedor para que Esc funcione también con foco en botones
            <div className="flex flex-col gap-1 mt-1" onKeyDown={handleKeyDownContenedor}>
                {mostrarDialogoCero && (
                    <DialogoCostoCero
                        onVolver={() => {
                            setMostrarDialogoCero(false);
                            // Al cerrar con "Volver", devolvemos el foco al input de costo.
                            setTimeout(() => inputCostoRef.current?.focus(), 0);
                        }}
                        onConfirmar={() => {
                            setMostrarDialogoCero(false);
                            enviarAlBackend(0, Number(valorImpuesto) || 0);
                        }}
                    />
                )}
                {/* Costo */}
                <div className="flex items-center gap-1">
                    <span className="text-[10px] text-slate-500 w-16">Costo</span>
                    <input
                        ref={inputCostoRef}
                        type="number"
                        value={valorCosto}
                        onChange={(e) => { setValorCosto(e.target.value); setError(null); }}
                        disabled={confirmando}
                        className={CLASES_INPUT}
                        aria-label="Costo a confirmar"
                        data-testid="input-confirm-cost"
                    />
                    <span className="text-[10px] text-slate-500 font-mono">{moneda}</span>
                </div>
                {/* Impuesto */}
                <div className="flex items-center gap-1">
                    <span className="text-[10px] text-slate-500 w-16">Impuesto</span>
                    <input
                        type="number"
                        value={valorImpuesto}
                        onChange={(e) => { setValorImpuesto(e.target.value); setError(null); }}
                        disabled={confirmando}
                        className={CLASES_INPUT}
                        aria-label="Impuesto del costo"
                        data-testid="input-confirm-tax"
                    />
                </div>
                {/* Botones */}
                <div className="flex gap-1 mt-0.5">
                    <button
                        type="button"
                        onClick={intentarConfirmar}
                        disabled={confirmando}
                        aria-busy={confirmando}
                        className="text-[11px] font-semibold px-2 py-1 rounded-lg bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-60 transition-colors"
                        data-testid="btn-confirm-cost-submit"
                    >
                        {confirmando ? "Confirmando…" : "Confirmar"}
                    </button>
                    <button
                        type="button"
                        onClick={salirModoEdicion}
                        disabled={confirmando}
                        className="text-[11px] font-medium px-2 py-1 rounded-lg border border-slate-200 text-slate-600 hover:bg-slate-50 disabled:opacity-60 transition-colors"
                        data-testid="btn-confirm-cost-cancel"
                    >
                        Cancelar
                    </button>
                </div>
                {error && (
                    <div className="text-[10px] text-rose-600 dark:text-rose-400" role="alert">
                        {error}
                    </div>
                )}
            </div>
        );
    }

    return (
        <>
            <span className="text-[9px] opacity-70">Costo: ${netCostDisplay}</span>
            {service.costToConfirm && (
                <div className="flex flex-col gap-1 mt-0.5">
                    <span
                        className={CLASES_PILL_AMBAR + " self-start"}
                        data-testid="pill-cost-to-confirm"
                    >
                        A confirmar
                    </span>
                    {/* Candado C1 (2026-07-22): mismo tratamiento que la versión desktop. */}
                    {candadoActivo ? (
                        <button
                            ref={botonConfirmarCostoRef}
                            type="button"
                            onClick={onRequestEdit}
                            aria-label="Confirmar costo — bloqueado, pedí autorización"
                            className="inline-flex items-center gap-1 text-[11px] font-semibold px-2 py-1 rounded-lg border border-slate-200 bg-slate-100 text-slate-500 hover:bg-slate-200 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700 transition-colors"
                            data-testid="btn-confirm-cost"
                        >
                            <Lock className="h-3 w-3" aria-hidden="true" />
                            Confirmar costo
                        </button>
                    ) : (
                        <button
                            ref={botonConfirmarCostoRef}
                            type="button"
                            onClick={entrarModoEdicion}
                            className={CLASES_BTN_CONFIRMAR_COSTO}
                            data-testid="btn-confirm-cost"
                        >
                            Confirmar costo
                        </button>
                    )}
                </div>
            )}
        </>
    );
}
