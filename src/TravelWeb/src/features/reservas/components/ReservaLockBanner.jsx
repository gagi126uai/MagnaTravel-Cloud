import React from 'react';
import { Lock, LockOpen, AlertTriangle } from 'lucide-react';

/**
 * Franja informativa que aparece al inicio de la pagina de detalle según el estado del candado.
 *
 * Tres modos posibles (en orden de prioridad):
 *
 * 1. Franja NARANJA de regresion: cuando lastRegressionReason viene del DTO y la reserva
 *    volvio sola a En gestion — es el aviso mas urgente para el vendedor (decisión #6).
 *
 * 2. Franja VERDE "destrabada": cuando hasLiveEditAuthorization=true — la reserva tiene
 *    una autorizacion de edicion vigente. Se muestra "Destrabada hasta las HH:MM"
 *    y las acciones de edicion pasan a habilitarse (decisión N3).
 *
 * 3. Franja AMBAR de candado: cuando isLocked=true y no hay autorizacion activa.
 *    El vendedor ve "Pedir autorizacion"; el admin puede abrir el modal para destrabar.
 *    (Decision #1 y #2 guia UX 2026-06-08).
 *
 * Props:
 * - isLocked: boolean — true cuando el status esta en {Confirmed, Traveling, ToSettle, Closed}
 * - onRequestEdit: callback — el vendedor/admin hizo clic en el boton de autorizacion
 * - hasRegressionWarning: boolean — true si volvio sola a En gestion
 * - regressionReason: string|null — motivo de la regresion (del campo lastRegressionReason del DTO)
 * - hasLiveEditAuthorization: boolean — true si hay una autorizacion de edicion vigente
 * - editAuthorizationExpiresAt: string|null — ISO datetime del vencimiento de la autorizacion
 */
export function ReservaLockBanner({
    isLocked,
    onRequestEdit,
    hasRegressionWarning,
    regressionReason,
    hasLiveEditAuthorization,
    editAuthorizationExpiresAt,
}) {
    // Franja de regresion automatica (naranja): prioridad maxima, va ANTES que el candado.
    // Es el mensaje mas urgente: el operador cancelo/reprogramo algo y la reserva retrocedio.
    if (hasRegressionWarning) {
        return (
            <div
                data-testid="reserva-regression-banner"
                className="flex items-start gap-3 rounded-xl border border-orange-300 bg-orange-50 px-4 py-3 text-sm text-orange-900 dark:border-orange-800/50 dark:bg-orange-950/30 dark:text-orange-200"
            >
                <AlertTriangle className="mt-0.5 h-4 w-4 flex-shrink-0 text-orange-500" aria-hidden="true" />
                <div>
                    <span className="font-bold">Esta reserva volvió a En gestión.</span>
                    {regressionReason ? (
                        <span className="ml-1">{regressionReason}</span>
                    ) : (
                        <span className="ml-1">Un servicio cambió de estado y la reserva ya no tiene todos los servicios resueltos.</span>
                    )}
                    <span className="ml-1">Revisá los servicios.</span>
                </div>
            </div>
        );
    }

    if (!isLocked) return null;

    // Franja verde "destrabada": hay una autorizacion de edicion vigente.
    // El admin ya aprobó — el vendedor puede editar hasta que venza la autorización.
    if (hasLiveEditAuthorization) {
        // Formateamos la hora de vencimiento en hora local (HH:MM) para mostrarla al vendedor.
        let textoVencimiento = '';
        if (editAuthorizationExpiresAt) {
            const fecha = new Date(editAuthorizationExpiresAt);
            if (!isNaN(fecha)) {
                textoVencimiento = ` hasta las ${fecha.toLocaleTimeString('es-AR', { hour: '2-digit', minute: '2-digit' })}`;
            }
        }

        return (
            <div
                data-testid="reserva-unlocked-banner"
                className="flex items-center gap-3 rounded-xl border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-900 dark:border-emerald-800/50 dark:bg-emerald-950/30 dark:text-emerald-200"
            >
                <LockOpen className="h-4 w-4 flex-shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />
                <span>
                    <span className="font-bold">Destrabada{textoVencimiento}.</span>
                    {' '}Podés hacer cambios ahora; después vuelve a bloquearse sola.
                </span>
            </div>
        );
    }

    // Franja ambar: reserva bloqueada sin autorizacion activa.
    return (
        <div
            data-testid="reserva-lock-banner"
            className="flex items-center gap-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-800/50 dark:bg-amber-950/30 dark:text-amber-200"
        >
            <Lock className="h-4 w-4 flex-shrink-0 text-amber-600 dark:text-amber-400" aria-hidden="true" />
            <span>
                <span className="font-bold">Reserva confirmada.</span>
                {' '}Para cambiar algo, pedí autorización.
            </span>
            {onRequestEdit && (
                <button
                    type="button"
                    onClick={onRequestEdit}
                    data-testid="reserva-request-edit-btn"
                    className="ml-auto flex-shrink-0 rounded-lg border border-amber-300 bg-white px-3 py-1 text-xs font-bold text-amber-800 transition-colors hover:bg-amber-100 dark:border-amber-700 dark:bg-slate-800 dark:text-amber-200 dark:hover:bg-amber-900/30"
                >
                    Pedí autorización
                </button>
            )}
        </div>
    );
}
