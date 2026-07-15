/**
 * Panel EN LÍNEA para que un ADMINISTRADOR deshaga un cierre "sin multa" del operador.
 *
 * Se usa cuando, después de cerrar el paso como "sin multa", el operador termina
 * cobrando algo. Reabre el paso pendiente: el agente puede volver a elegir entre
 * cargar la multa (con Nota de Débito) o cerrar sin multa otra vez.
 *
 * VISIBILIDAD: solo para administradores. El padre (ReservaDetailPage) solo monta
 * este componente cuando isAdmin() es true — la lógica de visibilidad NO está acá.
 *
 * Especificación UX aprobada: docs/ux/2026-06-28c-cierre-sin-multa-operador.md (mockup 3).
 *
 * Copy actualizada (2026-07-14, spec docs/ux/2026-07-14-config-multas-proveedor.md,
 * Pieza 3): el título y las explicaciones pasaron de "Deshacer el cierre sin multa" a
 * "Reabrir el paso de la multa" — dice QUÉ hace en vez de adivinar el motivo, y aclara
 * que este cierre en particular nunca tuvo un comprobante (no hay nada que anular ante
 * ARCA). Los textos viven en `lib/reabrirPasoMultaTextos.js` para compartirlos con el
 * enlace del cartel rosa de ReservaDetailPage.jsx. La visibilidad admin-only y el resto
 * del flujo (motivo obligatorio, confirmación en dos pasos, bloqueo por SALDO_YA_USADO)
 * NO cambiaron en esta tanda.
 *
 * E2 (spec "el paso de multa vive en la ficha", 2026-07-08): antes de mandar el motivo al
 * backend, el panel pide una confirmación explícita ("Volver" / "Sí, reabrir") — reabrir
 * el paso de la multa no es gratis: puede terminar en otra Nota de Débito o en un cambio
 * de lo que el cliente ya considera "cerrado". El motivo ya cargado no se pierde si el
 * admin toca "Volver": sigue en el campo.
 *
 * E1 (misma spec): si el backend contesta 409 SALDO_YA_USADO (el cliente ya usó el saldo
 * a favor que generó el cierre sin multa), el panel muestra ese mensaje y NO deja
 * reintentar — hay que resolverlo por otro lado (ver mensaje del backend) antes de reabrir.
 *
 * Props:
 *   cancellationPublicId - GUID del BookingCancellation (obtenido de GET by-reserva).
 *   reservaNumero        - Número de la reserva (para mostrar en el header y la confirmación).
 *   onDeshecho           - Callback tras deshacer exitosamente; el padre muestra el toast de éxito.
 *   onCerrar             - Callback para cerrar el panel sin guardar.
 */

import { useState } from "react";
import { RotateCcw, Loader2, AlertTriangle, X } from "lucide-react";
import { cancellationsApi } from "../api/cancellationsApi";
import { getApiErrorMessage } from "../../../lib/errors";
// Configuracion de multas de cancelacion (2026-07-14, spec Pieza 3): textos compartidos
// con el enlace del cartel rosa en ReservaDetailPage.jsx — ver reabrirPasoMultaTextos.js
// para el porqué de tenerlos en un módulo aparte.
import {
  TITULO_PANEL_REABRIR_PASO_MULTA,
  EXPLICACION_REABRIR_PASO_MULTA,
  textoConfirmacionReabrirPasoMulta,
} from "../lib/reabrirPasoMultaTextos.js";

// Límites del campo motivo — espejamos el contrato del backend (5..500 caracteres).
const MOTIVO_MIN = 5;
const MOTIVO_MAX = 500;

/**
 * Valida el campo motivo del "deshacer cierre sin multa".
 * Se exporta para poder testearse sin DOM (lógica pura).
 *
 * @param {string} motivo - Texto del motivo ingresado por el usuario.
 * @returns {string|null} Mensaje de error, o null si el campo es válido.
 */
export function validarMotivoDeshacer(motivo) {
    const trimmed = (motivo ?? "").trim();
    if (trimmed.length < MOTIVO_MIN) {
        return `El motivo debe tener al menos ${MOTIVO_MIN} caracteres.`;
    }
    if (trimmed.length > MOTIVO_MAX) {
        return `El motivo no puede superar los ${MOTIVO_MAX} caracteres.`;
    }
    return null;
}

/**
 * E1 (2026-07-08): detecta si el error de la API es el 409 SALDO_YA_USADO — el cliente
 * ya usó el saldo a favor que había generado este cierre sin multa, así que reabrir el
 * paso no tiene sentido hasta que alguien resuelva la plata por otro lado.
 * Se exporta para poder testearse sin DOM (lógica pura).
 *
 * @param {{ status?: number, payload?: { code?: string } }} error
 * @returns {boolean}
 */
export function esErrorSaldoYaUsado(error) {
    return error?.status === 409 && error?.payload?.code === "SALDO_YA_USADO";
}

/**
 * Determina si el formulario de "deshacer" puede enviarse.
 * Se exporta para poder testearse sin DOM (lógica pura).
 *
 * @param {{ motivo: string, submitting: boolean }} params
 * @returns {boolean}
 */
export function puedeDeshacer({ motivo, submitting }) {
    if (submitting) return false;
    return validarMotivoDeshacer(motivo) === null;
}

export function DeshacerCierreSinMultaInline({
    cancellationPublicId,
    reservaNumero,
    onDeshecho,
    onCerrar,
}) {
    const [motivo, setMotivo] = useState("");
    // Mostramos el error de validación solo después de que el usuario tocó el campo
    // o intentó enviar — no al abrir el panel.
    const [motivoTocado, setMotivoTocado] = useState(false);
    const [submitting, setSubmitting] = useState(false);
    // Error de API: se muestra inline; el panel permanece abierto con los datos intactos.
    const [errorMensaje, setErrorMensaje] = useState(null);
    // E2: paso intermedio de confirmación explícita antes de llamar al backend.
    const [mostrarConfirmacion, setMostrarConfirmacion] = useState(false);
    // E1: si el backend dice SALDO_YA_USADO, el reintento no tiene sentido hasta que
    // alguien resuelva la plata por otro lado — bloqueamos el submit para no insistir.
    const [bloqueadoPorSaldoUsado, setBloqueadoPorSaldoUsado] = useState(false);

    const motivoError = validarMotivoDeshacer(motivo);
    const canSubmit = puedeDeshacer({ motivo, submitting }) && !bloqueadoPorSaldoUsado;

    // Primer click en "Deshacer": valida el motivo y, si está OK, pide la confirmación
    // explícita en vez de llamar al backend directamente (E2).
    const handlePedirConfirmacion = () => {
        setMotivoTocado(true);
        if (!canSubmit) return;
        setMostrarConfirmacion(true);
    };

    // Segundo click, ya en la pantalla de confirmación ("Sí, reabrir"): ahí sí se llama al backend.
    const handleDeshacer = async () => {
        if (!canSubmit) return;

        setSubmitting(true);
        setErrorMensaje(null);

        try {
            await cancellationsApi.revertWaive(cancellationPublicId, motivo.trim());
            // El toast de éxito ("Listo. Se reabrió el paso de la multa.") lo muestra
            // el padre (ReservaDetailPage) para seguir el patrón de todos los paneles inline.
            onDeshecho();
        } catch (error) {
            const statusCode = error?.status ?? error?.response?.status ?? 0;

            if (esErrorSaldoYaUsado(error)) {
                // E1: el cliente ya usó el saldo a favor que generó el cierre sin multa.
                // El mensaje ya viene listo del backend en criollo — lo mostramos tal cual
                // y bloqueamos el submit (reintentar no cambia nada hasta resolver la plata).
                setErrorMensaje(
                    error?.payload?.message ||
                    "El cliente ya usó ese saldo a favor, por eso no se puede deshacer este cierre. Si el operador te cobró una multa ahora, cobrásela al cliente como un cargo de la agencia desde la ficha."
                );
                setBloqueadoPorSaldoUsado(true);
                setMostrarConfirmacion(false);
            } else if (statusCode === 403) {
                // 403 es anómalo: el enlace "Deshacer" solo se muestra para Admin.
                // Si el backend lo rechaza con 403, el token puede haber cambiado.
                setErrorMensaje("No tenés permiso para deshacer este cierre. Solo administradores pueden realizar esta acción.");
                setMostrarConfirmacion(false);
            } else {
                setErrorMensaje(
                    getApiErrorMessage(error, "No se pudo deshacer el cierre. Intentá de nuevo.")
                );
                setMostrarConfirmacion(false);
            }
            setSubmitting(false);
        }
    };

    return (
        <div
            className="rounded-xl border-2 border-slate-200 bg-slate-50/60 dark:border-slate-700/60 dark:bg-slate-900/20 p-5 space-y-4"
            data-testid="deshacer-cierre-sin-multa-inline"
        >
            {/* ── Cabecera del panel ── */}
            <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                    <RotateCcw className="w-4 h-4 text-slate-600 dark:text-slate-400" aria-hidden="true" />
                    <h4 className="text-sm font-bold text-slate-900 dark:text-white">
                        {TITULO_PANEL_REABRIR_PASO_MULTA}
                    </h4>
                    <span className="text-xs text-slate-500 dark:text-slate-400">
                        Reserva #{reservaNumero}
                    </span>
                </div>
                <button
                    type="button"
                    onClick={onCerrar}
                    disabled={submitting}
                    className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 rounded p-1 disabled:opacity-40"
                    aria-label="Cerrar sin guardar"
                >
                    <X className="w-4 h-4" />
                </button>
            </div>

            {mostrarConfirmacion ? (
                <>
                    {/* ── E2: confirmación explícita antes de tocar el backend ──
                        Reabrir el paso de la multa no es gratis (puede terminar en otra ND
                        o cambiar algo que el cliente ya daba por cerrado) — un solo click
                        en "Deshacer" no alcanza. */}
                    <div
                        className="rounded-lg border border-amber-200 bg-amber-50 p-3.5 text-sm text-amber-900 dark:bg-amber-950/30 dark:border-amber-800 dark:text-amber-200"
                        data-testid="deshacer-confirmacion-explicita"
                        role="alert"
                    >
                        {textoConfirmacionReabrirPasoMulta(reservaNumero)}
                    </div>

                    <div className="flex justify-end gap-3 pt-1">
                        <button
                            type="button"
                            onClick={() => setMostrarConfirmacion(false)}
                            disabled={submitting}
                            data-testid="deshacer-confirmacion-volver-btn"
                            className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
                        >
                            Volver
                        </button>
                        <button
                            type="button"
                            onClick={handleDeshacer}
                            disabled={submitting}
                            data-testid="deshacer-confirmar-btn"
                            className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-bold text-white hover:bg-slate-800 transition-colors disabled:opacity-50 flex items-center gap-2 dark:bg-slate-600 dark:hover:bg-slate-500"
                        >
                            {submitting && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
                            {submitting ? "Reabriendo..." : "Sí, reabrir"}
                        </button>
                    </div>
                </>
            ) : (
                <>
                    {/* ── Explicación de la consecuencia — el usuario entiende qué va a pasar ── */}
                    <div
                        className="rounded-lg border border-slate-200 bg-white p-3.5 text-xs text-slate-700 dark:bg-slate-800 dark:border-slate-700 dark:text-slate-300"
                        data-testid="deshacer-explicacion"
                    >
                        {EXPLICACION_REABRIR_PASO_MULTA}
                    </div>

                    {/* ── Banner de error de API — datos intactos, el usuario puede reintentar ──
                        E1: si vino de un 409 SALDO_YA_USADO, bloqueadoPorSaldoUsado queda en true
                        y el botón de abajo se deshabilita — no tiene sentido reintentar sin
                        resolver la plata primero. */}
                    {errorMensaje && (
                        <div
                            role="alert"
                            className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex items-start gap-2"
                            data-testid="deshacer-error"
                        >
                            <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
                            <span>{errorMensaje}</span>
                        </div>
                    )}

                    {/* ── Campo: motivo ── */}
                    <div>
                        <label
                            className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5"
                            htmlFor="deshacer-motivo"
                        >
                            ¿Por qué lo deshacés?{" "}
                            <span className="text-rose-500" aria-hidden="true">*</span>
                        </label>
                        <textarea
                            id="deshacer-motivo"
                            rows={3}
                            value={motivo}
                            onChange={(e) => setMotivo(e.target.value)}
                            onBlur={() => setMotivoTocado(true)}
                            maxLength={MOTIVO_MAX}
                            disabled={submitting || bloqueadoPorSaldoUsado}
                            placeholder="El operador finalmente informó una penalidad de US$ 80..."
                            data-testid="deshacer-motivo-input"
                            className={`w-full rounded-xl border px-3 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-slate-400 dark:bg-slate-800 dark:text-white disabled:opacity-50 resize-none ${
                                motivoTocado && motivoError
                                    ? "border-rose-400"
                                    : "border-slate-300 dark:border-slate-600"
                            }`}
                        />
                        {/* Error de validación — solo visible después de tocar el campo */}
                        {motivoTocado && motivoError && (
                            <div
                                className="mt-1 text-xs text-rose-600"
                                role="alert"
                                data-testid="deshacer-motivo-error"
                            >
                                {motivoError}
                            </div>
                        )}
                        <div className="mt-1 text-xs text-slate-400">
                            {motivo.length}/{MOTIVO_MAX} caracteres
                        </div>
                    </div>

                    {/* ── Acciones ── */}
                    <div className="flex justify-end gap-3 pt-1">
                        <button
                            type="button"
                            onClick={onCerrar}
                            disabled={submitting}
                            className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
                        >
                            Volver
                        </button>
                        <button
                            type="button"
                            onClick={handlePedirConfirmacion}
                            disabled={!canSubmit}
                            data-testid="deshacer-siguiente-btn"
                            className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-bold text-white hover:bg-slate-800 transition-colors disabled:opacity-50 flex items-center gap-2 dark:bg-slate-600 dark:hover:bg-slate-500"
                        >
                            Deshacer
                        </button>
                    </div>
                </>
            )}
        </div>
    );
}
