import React, { useState } from 'react';
import { X, Loader2, AlertTriangle } from 'lucide-react';
import { api } from '../../../api';
import { showError, showSuccess } from '../../../alerts';
import { getApiErrorMessage } from '../../../lib/errors';

/**
 * Modal para marcar una reserva como "Perdida".
 *
 * Decision 7 (guia UX 2026-06-08):
 * - Boton discreto en la cabecera (solo visible en Cotizacion y Presupuesto).
 * - Confirmacion simple + campo de motivo OPCIONAL.
 * - Queda en el historial con quién y cuándo.
 *
 * Endpoint: PUT /api/reservas/{publicId}/status con body { status: "Lost", reason? }
 *
 * Props:
 * - reservaPublicId: string
 * - onClose: () => void
 * - onMarked: () => void — callback al marcar con exito
 */
export function MarkLostModal({ reservaPublicId, onClose, onMarked }) {
    const [motivo, setMotivo] = useState('');
    const [loading, setLoading] = useState(false);

    const handleConfirmar = async () => {
        setLoading(true);
        try {
            await api.put(`/reservas/${reservaPublicId}/status`, {
                status: 'Lost',
                // El motivo es opcional — lo mandamos solo si el usuario escribio algo.
                ...(motivo.trim() ? { reason: motivo.trim() } : {}),
            });
            showSuccess('Reserva marcada como Perdida');
            onMarked();
        } catch (error) {
            showError(getApiErrorMessage(error, 'No se pudo marcar la reserva como perdida.'));
        } finally {
            setLoading(false);
        }
    };

    return (
        <div
            className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200"
            role="dialog"
            aria-modal="true"
            aria-label="Marcar como Perdida"
            onKeyDown={(e) => { if (e.key === 'Escape') onClose(); }}
        >
            <div className="w-full max-w-sm rounded-2xl border bg-white shadow-2xl dark:bg-slate-900 dark:border-slate-800">
                {/* Header */}
                <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
                    <div className="flex items-center gap-2">
                        <AlertTriangle className="h-4 w-4 text-slate-500" />
                        <h3 className="font-bold text-slate-900 dark:text-white">Marcar como Perdida</h3>
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
                    <p className="text-sm text-slate-600 dark:text-slate-300">
                        <span className="font-bold">¿Seguro?</span> La reserva va a quedar en el historial
                        como Perdida. Podes revertirla despues si el cliente vuelve.
                    </p>
                    <div>
                        <label
                            htmlFor="motivo-perdida"
                            className="mb-1 block text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400"
                        >
                            Motivo (opcional)
                        </label>
                        <textarea
                            id="motivo-perdida"
                            value={motivo}
                            onChange={(e) => setMotivo(e.target.value)}
                            placeholder="¿Por que no compro? (puede dejarse en blanco)"
                            rows={2}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                        />
                    </div>
                </div>

                <div className="flex justify-end gap-3 border-t border-slate-100 px-6 py-4 dark:border-slate-800">
                    <button
                        type="button"
                        onClick={onClose}
                        disabled={loading}
                        className="rounded-lg px-4 py-2 text-sm font-bold text-slate-600 hover:bg-slate-100 disabled:opacity-50 transition-colors dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                        Cancelar
                    </button>
                    <button
                        type="button"
                        onClick={handleConfirmar}
                        disabled={loading}
                        data-testid="mark-lost-confirm-btn"
                        className="flex items-center gap-2 rounded-lg bg-slate-700 px-4 py-2 text-sm font-bold text-white transition-colors hover:bg-slate-800 disabled:opacity-50"
                    >
                        {loading && <Loader2 className="h-4 w-4 animate-spin" />}
                        Si, marcar como perdida
                    </button>
                </div>
            </div>
        </div>
    );
}
