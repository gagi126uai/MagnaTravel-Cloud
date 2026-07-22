import React, { useState } from 'react';
import { Lock, Loader2, X } from 'lucide-react';
import { api } from '../../../api';
import { showError, showSuccess } from '../../../alerts';
import { getApiErrorMessage } from '../../../lib/errors';
import { hasPermission } from '../../../auth';

/**
 * Modal para destrabar la edicion de una reserva con candado.
 *
 * Decision 2 (guia UX 2026-06-08):
 * - El vendedor comun ve el mensaje "Pedile a un administrador que la destrabe".
 * - El admin (permiso reservas.authorize_locked_edit) escribe el motivo y destraba
 *   la reserva entera por 30 minutos.
 *
 * Endpoint: POST /api/reservas/{publicId}/edit-authorizations
 * Body: { reason: string (min 10 chars) }
 *
 * Props:
 * - reservaPublicId: string — publicId de la reserva a destrabar
 * - onClose: () => void
 * - onAuthorized: () => void — callback al destrabar con exito (para refrescar la pagina)
 */
export function EditAuthorizationModal({ reservaPublicId, onClose, onAuthorized }) {
    // Solo los admins con reservas.authorize_locked_edit pueden destrabar.
    // Los demas ven el mensaje pasivo.
    const puedeAutorizar = hasPermission('reservas.authorize_locked_edit');

    const [motivo, setMotivo] = useState('');
    const [loading, setLoading] = useState(false);

    const motivoValido = motivo.trim().length >= 10;

    const handleAutorizar = async () => {
        if (!motivoValido) return;

        setLoading(true);
        try {
            // POST /api/reservas/{id}/edit-authorizations
            // Body: CreateEditAuthorizationRequest { Reason, AuthorizedByUserId }
            // El admin se auto-autoriza (AuthorizedByUserId = null: el backend lo toma del token).
            await api.post(`/reservas/${reservaPublicId}/edit-authorizations`, {
                reason: motivo.trim(),
                authorizedByUserId: null,
            });
            showSuccess('Reserva desbloqueada por 30 minutos. Hace los cambios ahora.');
            onAuthorized();
        } catch (error) {
            showError(getApiErrorMessage(error, 'No se pudo desbloquear la reserva.'));
        } finally {
            setLoading(false);
        }
    };

    return (
        <div
            className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200"
            role="dialog"
            aria-modal="true"
            aria-label="Reserva bloqueada"
            onKeyDown={(e) => { if (e.key === 'Escape') onClose(); }}
        >
            <div className="w-full max-w-md rounded-2xl border bg-white shadow-2xl dark:bg-slate-900 dark:border-slate-800">
                {/* Header */}
                <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
                    <div className="flex items-center gap-2">
                        <Lock className="h-4 w-4 text-amber-600" />
                        <h3 className="font-bold text-slate-900 dark:text-white">Reserva bloqueada</h3>
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
                    {puedeAutorizar ? (
                        // --- Vista del admin: puede destrabar escribiendo el motivo ---
                        <>
                            <p className="text-sm text-slate-600 dark:text-slate-300">
                                Esta reserva está confirmada y tiene el candado activo.
                                Como administrador, podés desbloquearla por 30 minutos.
                                El motivo queda registrado en el historial.
                            </p>
                            <div>
                                <label
                                    htmlFor="motivo-autorizacion"
                                    className="mb-1 block text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400"
                                >
                                    Motivo del desbloqueo <span className="text-rose-500">*</span>
                                </label>
                                <textarea
                                    id="motivo-autorizacion"
                                    value={motivo}
                                    onChange={(e) => setMotivo(e.target.value)}
                                    placeholder="Explicá por qué necesitás hacer este cambio (mínimo 10 caracteres)"
                                    rows={3}
                                    className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                                />
                                {motivo.length > 0 && !motivoValido && (
                                    <p className="mt-1 text-xs text-rose-500">
                                        El motivo debe tener al menos 10 caracteres ({motivo.trim().length}/10)
                                    </p>
                                )}
                            </div>
                        </>
                    ) : (
                        // --- Vista del vendedor comun: no puede destrabar, informa quien puede ---
                        <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900 dark:border-amber-800/50 dark:bg-amber-950/30 dark:text-amber-200">
                            <p className="font-bold mb-1">Esta reserva está confirmada y no se puede editar.</p>
                            <p>Pedile a un administrador que la destrabe. El desbloqueo dura 30 minutos y queda registrado.</p>
                        </div>
                    )}
                </div>

                {/* Footer */}
                <div className="flex justify-end gap-3 border-t border-slate-100 px-6 py-4 dark:border-slate-800">
                    <button
                        type="button"
                        onClick={onClose}
                        disabled={loading}
                        className="rounded-lg px-4 py-2 text-sm font-bold text-slate-600 hover:bg-slate-100 disabled:opacity-50 transition-colors dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                        {puedeAutorizar ? 'Cancelar' : 'Cerrar'}
                    </button>
                    {puedeAutorizar && (
                        <button
                            type="button"
                            onClick={handleAutorizar}
                            disabled={!motivoValido || loading}
                            data-testid="edit-authorization-confirm-btn"
                            className="flex items-center gap-2 rounded-lg bg-amber-600 px-4 py-2 text-sm font-bold text-white transition-colors hover:bg-amber-700 disabled:opacity-50"
                        >
                            {loading && <Loader2 className="h-4 w-4 animate-spin" />}
                            Desbloquear reserva
                        </button>
                    )}
                </div>
            </div>
        </div>
    );
}
