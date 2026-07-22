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
 *    Retoque P4-3 (2026-07-22, spec docs/ux/2026-07-22-p4-retoques-circuito-proveedor.md,
 *    P3=A): el texto de ESTA franja ahora distingue por el permiso
 *    reservas.authorize_locked_edit (prop `puedeAutorizar`) — el MISMO permiso que ya usa
 *    EditAuthorizationModal para decidir si mostrar el formulario de destrabe directo. Antes
 *    la franja le decia "pedi autorizacion" al admin tambien, aunque el modal que abre ese
 *    mismo boton ya lo dejara destrabar solo — el texto quedaba incoherente con lo que
 *    ofrecia. El modal en si NO cambia.
 *
 * Formato UNA LÍNEA (spec UX 2026-07-05, respuesta 4B — "arriba la foto, abajo solo lo
 * que hay que hacer"): las tres variantes usan la misma franja fina de un solo renglón
 * (icono + texto corto + botón a la derecha si corresponde), en vez del bloque tipo
 * párrafo que tenían antes. El contenido esencial de cada variante no cambia, solo el
 * formato visual — así no compite en altura con el banner "con cambios" (ADR-027) que
 * queda arriba, grande y accionable.
 *
 * Props:
 * - isLocked: boolean — true cuando el status esta en {Confirmed, Traveling, Closed} (ADR-036: ToSettle eliminado)
 * - onRequestEdit: callback — el vendedor/admin hizo clic en el boton de autorizacion
 * - hasRegressionWarning: boolean — true si volvio sola a En gestion
 * - regressionReason: string|null — motivo de la regresion (del campo lastRegressionReason del DTO)
 * - hasLiveEditAuthorization: boolean — true si hay una autorizacion de edicion vigente
 * - editAuthorizationExpiresAt: string|null — ISO datetime del vencimiento de la autorizacion
 * - puedeAutorizar: boolean — true si el usuario tiene el permiso reservas.authorize_locked_edit
 *   (P4-3). Cambia solo el TEXTO de la franja ambar; el onRequestEdit sigue abriendo el mismo
 *   EditAuthorizationModal de siempre, que ya sabe mostrar el formulario de destrabe para este
 *   mismo permiso.
 */
export function ReservaLockBanner({
    isLocked,
    onRequestEdit,
    hasRegressionWarning,
    regressionReason,
    hasLiveEditAuthorization,
    editAuthorizationExpiresAt,
    puedeAutorizar = false,
}) {
    // Franja de regresion automatica (naranja): prioridad maxima, va ANTES que el candado.
    // Es el mensaje mas urgente: el operador cancelo/reprogramo algo y la reserva retrocedio.
    if (hasRegressionWarning) {
        return (
            <div
                data-testid="reserva-regression-banner"
                className="flex items-center gap-2 rounded-xl border border-orange-300 bg-orange-50 px-3 py-2 text-sm text-orange-900 dark:border-orange-800/50 dark:bg-orange-950/30 dark:text-orange-200"
            >
                <AlertTriangle className="h-4 w-4 flex-shrink-0 text-orange-500" aria-hidden="true" />
                <span>
                    <span className="font-bold">Volvió a En gestión.</span>{' '}
                    {regressionReason || 'Un servicio cambió de estado y la reserva ya no tiene todos los servicios resueltos.'}{' '}
                    Revisá los servicios.
                </span>
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
                className="flex items-center gap-2 rounded-xl border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-900 dark:border-emerald-800/50 dark:bg-emerald-950/30 dark:text-emerald-200"
            >
                <LockOpen className="h-4 w-4 flex-shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />
                <span>
                    <span className="font-bold">Destrabada{textoVencimiento}.</span>
                    {' '}Podés hacer cambios ahora; después vuelve a bloquearse sola.
                </span>
            </div>
        );
    }

    // Franja ambar: reserva bloqueada sin autorizacion activa. Achicada a una línea
    // fina (spec 2026-07-05, respuesta 4B): texto corto + botón a la derecha.
    //
    // P4-3: el Admin (puedeAutorizar=true) ve un texto distinto porque, a diferencia del
    // vendedor, el mismo boton lo va a dejar destrabar la reserva el mismo (no pedirle
    // permiso a otro) — el onRequestEdit y el modal que abre son EXACTAMENTE los mismos
    // para los dos roles, solo cambia como se lo anunciamos.
    const tituloFranja = puedeAutorizar ? 'Reserva confirmada (con candado).' : 'Reserva confirmada.';
    const textoFranja = puedeAutorizar
        ? 'Podés destrabarla para editar.'
        : 'Para cambiar algo, pedí autorización.';
    const textoBoton = puedeAutorizar ? 'Destrabar reserva' : 'Pedí autorización';

    return (
        <div
            data-testid="reserva-lock-banner"
            className="flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900 dark:border-amber-800/50 dark:bg-amber-950/30 dark:text-amber-200"
        >
            <Lock className="h-4 w-4 flex-shrink-0 text-amber-600 dark:text-amber-400" aria-hidden="true" />
            <span>
                <span className="font-bold">{tituloFranja}</span>
                {' '}{textoFranja}
            </span>
            {onRequestEdit && (
                <button
                    type="button"
                    onClick={onRequestEdit}
                    data-testid="reserva-request-edit-btn"
                    className="ml-auto flex-shrink-0 rounded-lg border border-amber-300 bg-white px-3 py-1 text-xs font-bold text-amber-800 transition-colors hover:bg-amber-100 dark:border-amber-700 dark:bg-slate-800 dark:text-amber-200 dark:hover:bg-amber-900/30"
                >
                    {textoBoton}
                </button>
            )}
        </div>
    );
}
