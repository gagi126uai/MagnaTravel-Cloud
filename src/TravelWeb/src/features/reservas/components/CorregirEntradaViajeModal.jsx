import React, { useState } from 'react';
import { X, AlertTriangle, Loader2, CornerUpLeft } from 'lucide-react';
import { api } from '../../../api';
import { getApiErrorMessage } from '../../../lib/errors';

/**
 * Modal de confirmación para la acción "Sacar de viaje".
 *
 * Acción de EXCEPCIÓN: devuelve una reserva "En viaje" (Traveling) a "Confirmada" cuando
 * entró por error (fecha mal cargada, viaje que no salió, etc.). No es "Anular" ni "Volver atrás":
 * es una corrección operativa con permiso elevado (solo Admin) que queda auditada.
 *
 * Después de ejecutarse, la reserva queda con isUnderCorrection=true (marca "En corrección")
 * que la congela para el proceso automático de pase a viaje hasta que se corrija la fecha del servicio.
 *
 * Spec UX: docs/ux/guia-ux-gaston.md — sección "Tanda 2 — 'Sacar de viaje'" (2026-06-22).
 *
 * Props:
 * - reservaPublicId: string — publicId de la reserva a corregir.
 * - onClose: () => void — cierra el modal sin hacer nada.
 * - onCorregida: () => void — callback al completar con éxito; el padre recarga la reserva.
 */
export function CorregirEntradaViajeModal({ reservaPublicId, onClose, onCorregida }) {
    const [motivo, setMotivo] = useState('');
    const [enviando, setEnviando] = useState(false);
    // El error se muestra DENTRO del modal (no un toast) para que el usuario pueda ver el motivo
    // que ya escribió y reintentarlo sin perder lo que cargó.
    const [errorMensaje, setErrorMensaje] = useState(null);

    // El motivo es obligatorio siempre (incluso para Admin). Mínimo 10 caracteres con trim.
    // Regla UX 2026-06-22: queda registrado quién, cuándo y por qué — auditoría obligatoria.
    const motivoValido = motivo.trim().length >= 10;
    const caracteresRestantes = Math.max(0, 10 - motivo.trim().length);

    const handleConfirmar = async () => {
        if (!motivoValido || enviando) return;

        setEnviando(true);
        setErrorMensaje(null);

        try {
            await api.post(`/reservas/${reservaPublicId}/correct-traveling-entry`, {
                reason: motivo.trim(),
            });
            // Éxito: el padre recarga la reserva; cerramos el modal desde acá.
            onCorregida();
        } catch (error) {
            // Nunca cerramos el modal en error: el usuario debe poder ver qué pasó y reintentar.
            // El motivo que escribió se preserva para no tener que volver a escribirlo.
            const status = error?.response?.status ?? error?.status ?? 0;

            if (status === 403) {
                setErrorMensaje('No tenés permiso para hacer esta corrección.');
            } else if (status === 409) {
                // El backend distingue varios casos de 409. Intentamos leer el mensaje.
                const mensajeBackend = getApiErrorMessage(error, '');

                // Detectamos el caso específico por texto del mensaje del backend.
                // Si el backend agrega un campo "code" en el futuro, usar eso primero.
                if (mensajeBackend?.toLowerCase().includes('factura') || mensajeBackend?.toLowerCase().includes('invoice')) {
                    setErrorMensaje('No se puede sacar de viaje: la reserva tiene una factura emitida. La corrección se hace por Nota de Crédito o ajuste.');
                } else if (mensajeBackend?.toLowerCase().includes('voucher')) {
                    setErrorMensaje('Anulá el voucher antes de sacar de viaje.');
                } else if (mensajeBackend?.toLowerCase().includes('no está en viaje') || mensajeBackend?.toLowerCase().includes('not traveling')) {
                    setErrorMensaje('La reserva ya no está en viaje. Recargá la pantalla.');
                } else {
                    // 409 genérico: mostramos lo que mandó el backend o un mensaje de fallback.
                    setErrorMensaje(mensajeBackend || 'No se puede realizar la corrección en el estado actual.');
                }
            } else if (status === 400) {
                setErrorMensaje('El motivo ingresado no es válido. Tiene que tener al menos 10 caracteres.');
            } else {
                setErrorMensaje('No se pudo completar la corrección. Probá de nuevo.');
            }
        } finally {
            setEnviando(false);
        }
    };

    return (
        <div
            className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200"
            role="dialog"
            aria-modal="true"
            aria-label="Sacar de viaje"
            // Cerrar con Escape: solo si no estamos enviando.
            onKeyDown={(e) => { if (e.key === 'Escape' && !enviando) onClose(); }}
        >
            <div className="w-full max-w-lg rounded-2xl border bg-white shadow-2xl dark:bg-slate-900 dark:border-slate-800">

                {/* ── Header ─────────────────────────────────────────────────────────── */}
                <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
                    <div className="flex items-center gap-2">
                        <CornerUpLeft className="h-4 w-4 text-slate-500" aria-hidden="true" />
                        <h3 className="font-bold text-slate-900 dark:text-white">Sacar de viaje</h3>
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

                <div className="p-6 space-y-4">

                    {/* ── Cartel ámbar de advertencia ────────────────────────────────── */}
                    {/* Texto exacto de la spec de UX. Explica la consecuencia y el recordatorio clave. */}
                    <div
                        className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-200"
                        role="note"
                    >
                        <div className="flex items-start gap-2">
                            <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5 text-amber-600 dark:text-amber-400" aria-hidden="true" />
                            <div className="space-y-2">
                                <p>
                                    <strong>La reserva va a volver a "Confirmada".</strong>{' '}
                                    Usá esta opción solo si la reserva entró en viaje por error.
                                    Queda registrado quién, cuándo y por qué.
                                </p>
                                <p>
                                    <strong>Importante:</strong> si la fecha de salida estaba mal cargada,
                                    después de sacarla de viaje corregí la fecha del servicio.
                                    Si no, el sistema puede volver a ponerla en viaje esta misma noche.
                                </p>
                            </div>
                        </div>
                    </div>

                    {/* ── Campo de motivo ────────────────────────────────────────────── */}
                    {/* Siempre obligatorio — incluso para Admin. Auditoría de acciones sensibles. */}
                    <div>
                        <label
                            htmlFor="motivo-correccion-viaje"
                            className="mb-1 block text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400"
                        >
                            Motivo de la corrección <span className="text-rose-500">*</span>
                        </label>
                        <textarea
                            id="motivo-correccion-viaje"
                            value={motivo}
                            onChange={(e) => setMotivo(e.target.value)}
                            placeholder="Describí brevemente por qué se saca de viaje..."
                            rows={3}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-amber-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                            data-testid="motivo-correccion-input"
                            aria-describedby="motivo-correccion-hint"
                        />
                        {/* Contador de caracteres: visible solo mientras el motivo no es válido. */}
                        {!motivoValido && (
                            <p
                                id="motivo-correccion-hint"
                                className="mt-1 text-xs text-slate-400"
                                aria-live="polite"
                            >
                                {motivo.trim().length === 0
                                    ? 'Requerido — mínimo 10 caracteres'
                                    : `Faltan ${caracteresRestantes} caracteres`}
                            </p>
                        )}
                    </div>

                    {/* ── Mensaje de error del servidor ──────────────────────────────── */}
                    {/* No cerramos el modal cuando hay error: el motivo se preserva. */}
                    {errorMensaje && (
                        <div
                            className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-800 dark:border-rose-800 dark:bg-rose-950/30 dark:text-rose-200"
                            role="alert"
                            data-testid="error-correccion-viaje"
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
                        Descartar
                    </button>
                    <button
                        type="button"
                        onClick={handleConfirmar}
                        disabled={!motivoValido || enviando}
                        data-testid="confirmar-sacar-de-viaje-btn"
                        className="flex items-center gap-2 rounded-lg bg-rose-700 px-4 py-2 text-sm font-bold text-white transition-colors hover:bg-rose-800 disabled:opacity-50"
                    >
                        {enviando && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
                        {enviando ? 'Sacando…' : 'Sacar de viaje'}
                    </button>
                </div>
            </div>
        </div>
    );
}
