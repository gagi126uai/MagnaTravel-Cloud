/**
 * Panel EN LÍNEA para cerrar el paso de multa del operador SIN emitir ningún comprobante.
 *
 * Se usa cuando el operador NO cobró multa por la anulación: registra el motivo
 * y cierra el paso diferido. No se emite ninguna Nota de Débito en AFIP/ARCA.
 *
 * IMPORTANTE — diferencia visual con ConfirmarMultaOperadorInline:
 *   ConfirmarMultaOperadorInline = naranja → emite Nota de Débito (hay plata involucrada).
 *   Este componente = teal/verde → NO emite nada (solo cierra el paso de auditoría).
 *   El color distinto evita que el agente confunda "cerrar sin multa" con "emitir ND".
 *
 * Especificación UX aprobada: docs/ux/2026-06-28c-cierre-sin-multa-operador.md (mockup 2).
 *
 * Props:
 *   cancellationPublicId - GUID del BookingCancellation (obtenido de GET by-reserva).
 *   reservaNumero        - Número de la reserva (para mostrar en el header).
 *   supplierPublicId     - (ADR-044 T1, 2026-07-10, opcional) GUID del operador al que
 *                           corresponde este cierre sin multa. Solo hace falta cuando la
 *                           cancelación tiene servicios de más de un operador (ADR-025)
 *                           — en el caso mono-operador de siempre no se pasa y el payload
 *                           de waive-penalty sale exactamente igual que antes.
 *   onCerrado            - Callback tras cerrar exitosamente; el padre muestra el toast de éxito.
 *   onCerrar             - Callback para abandonar el panel sin guardar.
 *
 * Errores 409 con detalle claro del backend (getApiErrorMessage ya los muestra tal cual,
 * sin mapeo extra acá): INV-ADR044-OPERATOR-REQUIRED (2+ operadores, falta especificar
 * cuál) e INV-ADR044-OPERATOR-NOT-FOUND (el supplierPublicId mandado no tiene servicios
 * en esta cancelación) — ambos del 2026-07-10.
 */

import { useState } from "react";
import { CheckCircle2, Loader2, AlertTriangle, X } from "lucide-react";
import { cancellationsApi } from "../api/cancellationsApi";
import { getApiErrorMessage } from "../../../lib/errors";

// Límites del campo motivo — espejamos el contrato del backend (5..500 caracteres).
const MOTIVO_MIN = 5;
const MOTIVO_MAX = 500;

/**
 * Valida el campo motivo del cierre sin multa.
 * Se exporta para poder testearse sin DOM (lógica pura).
 *
 * @param {string} motivo - Texto del motivo ingresado por el usuario.
 * @returns {string|null} Mensaje de error, o null si el campo es válido.
 */
export function validarMotivoCierreSinMulta(motivo) {
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
 * Determina si el formulario de cierre sin multa puede enviarse.
 * Se exporta para testearse sin DOM (lógica pura).
 *
 * @param {{ motivo: string, submitting: boolean }} params
 * @returns {boolean}
 */
export function puedeCerrarSinMulta({ motivo, submitting }) {
    if (submitting) return false;
    return validarMotivoCierreSinMulta(motivo) === null;
}

export function CerrarSinMultaInline({
    cancellationPublicId,
    reservaNumero,
    supplierPublicId,
    onCerrado,
    onCerrar,
}) {
    const [motivo, setMotivo] = useState("");
    // Mostramos el error de validación del motivo solo después de que el usuario
    // tocó el campo (blur) o intentó enviar — no al abrir el panel vacío.
    const [motivoTocado, setMotivoTocado] = useState(false);
    const [submitting, setSubmitting] = useState(false);
    // Error de API: se muestra inline; el panel permanece abierto con los datos intactos.
    const [errorMensaje, setErrorMensaje] = useState(null);

    const motivoError = validarMotivoCierreSinMulta(motivo);
    const canSubmit = puedeCerrarSinMulta({ motivo, submitting });

    const handleConfirmar = async () => {
        // Forzamos la validación visual antes de intentar enviar.
        setMotivoTocado(true);
        if (!canSubmit) return;

        setSubmitting(true);
        setErrorMensaje(null);

        try {
            await cancellationsApi.waivePenalty(cancellationPublicId, motivo.trim(), supplierPublicId);
            // El toast de éxito ("Listo. Se cerró sin multa del operador.") lo muestra
            // el padre (ReservaDetailPage) para seguir el patrón de todos los paneles inline.
            onCerrado();
        } catch (error) {
            const statusCode = error?.status ?? error?.response?.status ?? 0;

            if (statusCode === 403) {
                // 403 es anómalo: el botón solo se muestra cuando el usuario tiene permiso.
                // Si llega acá, el token puede haber cambiado entre la carga y el submit.
                setErrorMensaje("No tenés permiso para registrar este cierre. Volvé a iniciar sesión si el problema persiste.");
            } else {
                setErrorMensaje(
                    getApiErrorMessage(error, "No se pudo registrar el cierre. Intentá de nuevo.")
                );
            }
            setSubmitting(false);
        }
    };

    return (
        <div
            className="rounded-xl border-2 border-teal-200 bg-teal-50/40 dark:border-teal-900/40 dark:bg-teal-950/10 p-5 space-y-4"
            data-testid="cerrar-sin-multa-inline"
        >
            {/* ── Cabecera del panel ── */}
            <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                    {/* Ícono verde/teal: señal visual inmediata de que esto es el camino "sin multa". */}
                    <CheckCircle2 className="w-4 h-4 text-teal-600 dark:text-teal-400" aria-hidden="true" />
                    <h4 className="text-sm font-bold text-slate-900 dark:text-white">
                        El operador no cobró multa
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

            {/* ── Explicación del cierre ── */}
            <div
                className="rounded-lg border border-teal-200 bg-teal-50 p-3.5 text-xs text-teal-800 dark:bg-teal-950/30 dark:border-teal-800 dark:text-teal-200"
                data-testid="sin-multa-explicacion"
            >
                Estás registrando que el operador NO te cobró ninguna penalidad por la anulación
                y devolvió todo. No se emite ninguna nota de débito al cliente.
                El paso de la multa queda cerrado.
            </div>

            {/* ── Banner de error de API — datos intactos, el usuario puede reintentar ── */}
            {errorMensaje && (
                <div
                    role="alert"
                    className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex items-start gap-2"
                    data-testid="sin-multa-error"
                >
                    <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
                    <span>{errorMensaje}</span>
                </div>
            )}

            {/* ── Campo: motivo ── */}
            <div>
                <label
                    className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5"
                    htmlFor="sin-multa-motivo"
                >
                    ¿Por qué?{" "}
                    <span className="text-rose-500" aria-hidden="true">*</span>
                </label>
                <textarea
                    id="sin-multa-motivo"
                    rows={3}
                    value={motivo}
                    onChange={(e) => setMotivo(e.target.value)}
                    onBlur={() => setMotivoTocado(true)}
                    maxLength={MOTIVO_MAX}
                    disabled={submitting}
                    placeholder="El operador confirmó por mail que no aplica penalidad..."
                    data-testid="sin-multa-motivo-input"
                    className={`w-full rounded-xl border px-3 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-teal-400 dark:bg-slate-800 dark:text-white disabled:opacity-50 resize-none ${
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
                        data-testid="sin-multa-motivo-error"
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
                    onClick={handleConfirmar}
                    disabled={!canSubmit}
                    data-testid="sin-multa-confirmar-btn"
                    className="rounded-lg bg-teal-600 px-4 py-2 text-sm font-bold text-white hover:bg-teal-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                >
                    {submitting && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
                    {submitting ? "Confirmando..." : "Confirmar: sin multa"}
                </button>
            </div>
        </div>
    );
}
