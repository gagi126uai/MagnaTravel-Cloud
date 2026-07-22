/**
 * Cartel emergente ÚNICO para avisos largos de bloqueo/confirmación (spec
 * docs/ux/2026-07-22-tratamiento-unico-avisos-bloqueo.md, respuestas de Gastón P1/P2/P3=A).
 *
 * Antes, cada pantalla dibujaba el rechazo del motor INCRUSTADO (una fila, una ficha) y,
 * al ser texto largo, deformaba la tabla/ficha donde caía. Gastón pidió que TODOS estos
 * avisos salgan como una única ventanita, siempre igual, encima de la pantalla.
 *
 * Se usa SOLO cuando el usuario hizo un click y el motor RECHAZÓ esa acción (traje "bloqueo",
 * rojo) o le pide CONFIRMAR una consecuencia antes de seguir (traje "confirmación", ámbar).
 * No sirve para explicar algo que ya está a la vista de entrada (esos siguen en línea, ver
 * la spec sección 2 — "la raya").
 *
 * El mensaje se muestra TAL CUAL lo manda el motor: este componente nunca lo reescribe.
 *
 * Props:
 *   - isOpen: bool — si es false, el componente no renderiza nada.
 *   - variant: "bloqueo" | "confirmacion" (usar CARTEL_EMERGENTE_VARIANTES).
 *   - title: string opcional — casi nunca hace falta, cada gravedad ya tiene su título genérico.
 *   - message: string — el texto del motor, tal cual (respeta saltos de línea).
 *   - onClose: () => void — se llama al cerrar con el botón secundario, la "✕" o Escape.
 *     En "bloqueo" es "ya lo vi, listo". En "confirmación" es "Volver" (NO confirma nada).
 *   - closeLabel: string opcional — por defecto "Entendido"/"Volver" según la gravedad.
 *   - action: { label, onClick } | { label, to, state } opcional — el botón para resolver
 *     el aviso (ej. "Emitir factura"). Si trae `to`, navega con react-router; si trae
 *     `onClick`, ejecuta el callback. Solo tiene sentido en "bloqueo" (en "confirmación" el
 *     botón principal siempre es "Sí, confirmar", ver `onConfirm`).
 *   - onConfirm: () => void — obligatorio en "confirmacion": el botón principal "Sí, confirmar".
 *   - confirmLabel: string opcional — por defecto "Sí, confirmar" (o "Guardando…" mientras
 *     `isConfirming` es true).
 *   - isConfirming: bool — deshabilita TODA vía de cierre (botón secundario, "✕" y Escape)
 *     mientras hay un guardado en vuelo, no solo el botón primario — evitar la carrera de
 *     cerrar el cartel a mitad de una llamada. Además muestra el spinner en "Sí, confirmar".
 *   - dataTestId / titleTestId / messageTestId / actionTestId / closeTestId: overrides
 *     puntuales para no romper selectores de E2E que ya apuntaban al aviso migrado (por
 *     defecto usan los ids genéricos "cartel-emergente*" de la spec).
 */

import { useEffect, useRef } from "react";
import { Link } from "react-router-dom";
import { OctagonAlert, AlertTriangle, X, Loader2 } from "lucide-react";
import {
    CARTEL_EMERGENTE_VARIANTES,
    resolverTituloCartelEmergente,
    resolverTextoBotonSecundario,
} from "../lib/cartelEmergenteLogic";

export { CARTEL_EMERGENTE_VARIANTES };

export function CartelEmergente({
    isOpen,
    variant = CARTEL_EMERGENTE_VARIANTES.BLOQUEO,
    title,
    message,
    onClose,
    closeLabel,
    action,
    onConfirm,
    confirmLabel,
    isConfirming = false,
    dataTestId = "cartel-emergente",
    titleTestId = "cartel-emergente-titulo",
    messageTestId = "cartel-emergente-mensaje",
    actionTestId = "cartel-emergente-accion",
    closeTestId = "cartel-emergente-cerrar",
}) {
    const esBloqueo = variant === CARTEL_EMERGENTE_VARIANTES.BLOQUEO;
    const botonSecundarioRef = useRef(null);

    // Spec 3.4: "el foco arranca en el botón secundario" (el más seguro — un Enter accidental
    // no dispara la acción ni confirma nada). Solo corre cuando el cartel pasa a estar abierto.
    useEffect(() => {
        if (isOpen) botonSecundarioRef.current?.focus();
    }, [isOpen]);

    // Spec 3.4: Escape cierra igual que el botón secundario. NUNCA se cierra al tocar el
    // fondo (un click al costado no debe descartar sin querer un aviso importante).
    // Fix review (carrera menor): mientras isConfirming hay un guardado en vuelo — igual
    // que la "✕" y el botón secundario quedan disabled, Escape tampoco debe poder cerrar
    // el cartel a mitad de esa llamada (cerrarlo ahí dejaría al usuario sin ver el
    // resultado real, o peor, lo dejaría reintentar como si nada estuviera en curso).
    useEffect(() => {
        if (!isOpen || isConfirming) return;
        const handleKeyDown = (event) => {
            if (event.key === "Escape") onClose?.();
        };
        document.addEventListener("keydown", handleKeyDown);
        return () => document.removeEventListener("keydown", handleKeyDown);
    }, [isOpen, isConfirming, onClose]);

    if (!isOpen) return null;

    const tituloResuelto = resolverTituloCartelEmergente(variant, title);
    const textoBotonSecundario = resolverTextoBotonSecundario(variant, closeLabel);
    const colorIcono = esBloqueo ? "text-rose-600 dark:text-rose-400" : "text-amber-600 dark:text-amber-400";
    const fondoIcono = esBloqueo
        ? "bg-rose-50 dark:bg-rose-950/30"
        : "bg-amber-50 dark:bg-amber-950/30";
    const colorBotonPrimario = esBloqueo
        ? "bg-rose-600 hover:bg-rose-700"
        : "bg-amber-600 hover:bg-amber-700";

    return (
        // Fondo oscurecido SIN onClick: tocar afuera nunca cierra el cartel (spec 3.4).
        <div
            className="fixed inset-0 z-[100] flex items-center justify-center bg-slate-900/50 backdrop-blur-sm p-4 animate-in fade-in duration-200"
            data-testid={dataTestId}
        >
            <div
                role="dialog"
                aria-modal="true"
                aria-labelledby={titleTestId}
                aria-describedby={messageTestId}
                className="relative w-full max-w-md rounded-2xl border bg-white shadow-2xl dark:bg-slate-900 dark:border-slate-800 animate-in zoom-in-95 duration-200"
            >
                <button
                    type="button"
                    onClick={onClose}
                    disabled={isConfirming}
                    aria-label="Cerrar aviso"
                    className="absolute top-4 right-4 rounded-full p-1.5 text-slate-400 hover:bg-slate-100 hover:text-slate-600 transition-colors disabled:opacity-40 disabled:cursor-not-allowed dark:hover:bg-slate-800 dark:hover:text-slate-200"
                >
                    <X className="h-5 w-5" aria-hidden="true" />
                </button>

                <div className="p-6 space-y-4">
                    <div className="flex items-center gap-3">
                        <div className={`flex h-11 w-11 shrink-0 items-center justify-center rounded-xl ${fondoIcono}`}>
                            {esBloqueo
                                ? <OctagonAlert className={`h-6 w-6 ${colorIcono}`} aria-hidden="true" />
                                : <AlertTriangle className={`h-6 w-6 ${colorIcono}`} aria-hidden="true" />}
                        </div>
                        <h3 id={titleTestId} data-testid={titleTestId} className="text-base font-bold text-slate-900 dark:text-white">
                            {tituloResuelto}
                        </h3>
                    </div>

                    {/* El mensaje del motor tal cual — whitespace-pre-line respeta los saltos
                        de línea que venga trayendo, sin que el front le agregue ni le saque nada. */}
                    <p
                        id={messageTestId}
                        data-testid={messageTestId}
                        className="text-sm text-slate-700 dark:text-slate-300 leading-relaxed whitespace-pre-line"
                    >
                        {message}
                    </p>
                </div>

                <div className="flex flex-wrap items-center justify-end gap-3 border-t border-slate-100 px-6 py-4 dark:border-slate-800">
                    <button
                        type="button"
                        ref={botonSecundarioRef}
                        onClick={onClose}
                        disabled={isConfirming}
                        data-testid={closeTestId}
                        className="rounded-lg px-4 py-2 text-sm font-bold text-slate-600 hover:bg-slate-100 disabled:opacity-50 transition-colors dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                        {textoBotonSecundario}
                    </button>

                    {/* Traje BLOQUEO: botón de resolución, solo si el aviso trae una salida real. */}
                    {esBloqueo && action && (
                        action.to ? (
                            <Link
                                to={action.to}
                                state={action.state}
                                data-testid={actionTestId}
                                onClick={action.onClick}
                                className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-bold text-white transition-colors ${colorBotonPrimario}`}
                            >
                                {action.label}
                            </Link>
                        ) : (
                            <button
                                type="button"
                                onClick={action.onClick}
                                data-testid={actionTestId}
                                className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-bold text-white transition-colors ${colorBotonPrimario}`}
                            >
                                {action.label}
                            </button>
                        )
                    )}

                    {/* Traje CONFIRMACIÓN: el botón principal siempre es "Sí, confirmar". */}
                    {!esBloqueo && (
                        <button
                            type="button"
                            onClick={onConfirm}
                            disabled={isConfirming}
                            data-testid={actionTestId}
                            className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-bold text-white disabled:opacity-50 transition-colors ${colorBotonPrimario}`}
                        >
                            {isConfirming && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
                            {confirmLabel ?? (isConfirming ? "Guardando…" : "Sí, confirmar")}
                        </button>
                    )}
                </div>
            </div>
        </div>
    );
}
