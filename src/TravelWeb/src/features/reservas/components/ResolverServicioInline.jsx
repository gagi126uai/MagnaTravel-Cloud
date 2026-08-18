/**
 * Botón(es) + casillero inline para RESOLVER un servicio pendiente hacia adelante
 * (fix #34, Tanda 3, 2026-07-24). Spec completa: docs/ux/guia-ux-gaston.md, sección
 * "Confirmar un servicio DESDE LA FICHA de la reserva (2026-07-24, respuestas de
 * Gastón P1..P4)".
 *
 * Se usa en DOS lugares con el MISMO componente (P4=A, unificación — "un solo lenguaje
 * para avanzar en toda la app"):
 *   - ServiceList.jsx: fila de un servicio pendiente en la ficha de la reserva.
 *   - SupplierAccountPage.jsx: vista mobile de "Servicios comprados" (la vista
 *     desktop pasó a usar `ResolverServicioBotones` + `ResolverServicioCasillero`,
 *     Tanda T5 2026-08-18, para tener más aire en una fila de expansión propia — ver
 *     el docstring de esos componentes).
 *
 * Comportamiento (P1/P2/P3 de la spec):
 *   - Un traslado pendiente puede tener DOS botones a la vez ("Marcar confirmado" +
 *     "No requiere confirmación"); el resto de los tipos tiene uno solo.
 *   - Los botones que necesitan casillero (todos menos "No requiere confirmación") NO
 *     confirman al primer click: abren un casillero EN LA MISMA FILA para el N° de
 *     confirmación del operador (opcional) + [Confirmar] / [Cancelar].
 *   - "No requiere confirmación" es de un solo click, sin casillero (no hay número que
 *     cargar — es la excepción que marca la propia spec).
 *   - Guardando: spinner SOLO en esta fila (el resto de la pantalla sigue usable).
 *   - Éxito: toast + onResuelto() (el padre recarga/actualiza sin perder scroll).
 *   - Error: el casillero queda ABIERTO con el número que el usuario ya había escrito
 *     (nunca se pierde), y el mensaje del motor se muestra tal cual — chico al lado del
 *     casillero si es corto, o en el Cartel emergente único si es un rechazo largo
 *     (candado de la reserva, gate de nombres, freno de plata, etc. — "Frenos del
 *     motor" de la spec).
 *
 * Este botón SOLO avanza (Solicitado -> Confirmado/Emitido). Bajar un estado ya
 * confirmado sigue viviendo únicamente en la cuenta del operador, como acción
 * secundaria y separada (P4=A) — este componente no la ofrece.
 *
 * Nota Tanda T5 (2026-08-18): la lógica de estado (acciones, casillero, guardando,
 * error) vive ahora en el hook `useResolverServicioAcciones` — este componente solo
 * arma el JSX con lo que el hook le da. Se movió el código TAL CUAL (sin reescribir
 * nada) para poder compartirlo con la fila de expansión de la cuenta del operador sin
 * duplicar la lógica de guardado/errores.
 */

import { CheckCircle2, Loader2 } from "lucide-react";
import { CartelEmergente, CARTEL_EMERGENTE_VARIANTES } from "../../../components/CartelEmergente";
import { useResolverServicioAcciones } from "../lib/useResolverServicioAcciones";

const CLASES_BOTON_PRIMARIO = "inline-flex items-center gap-1 rounded-[10px] border border-emerald-200 bg-emerald-50 px-2 py-1 text-[11px] font-bold text-emerald-700 transition-colors hover:bg-emerald-100 disabled:opacity-50 dark:border-emerald-800 dark:bg-emerald-950/30 dark:text-emerald-300";
const CLASES_BOTON_SECUNDARIO = "inline-flex items-center gap-1 rounded-[10px] border border-slate-200 bg-slate-50 px-2 py-1 text-[11px] font-bold text-slate-600 transition-colors hover:bg-slate-100 disabled:opacity-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300";

/**
 * Props:
 *   reservaId       — publicId de la reserva (los endpoints "mark-issued" y
 *                      "no-confirmation" son reserva-scoped).
 *   servicePublicId — publicId del servicio (hotel/vuelo/traslado/paquete/asistencia).
 *   recordKind      — "flight"|"hotel"|"transfer"|"assistance"|"package"|"generic".
 *   onResuelto      — callback() cuando el servicio se resolvió con éxito. El padre
 *                      recarga/actualiza el estado (contador "N de M", badge, etc.).
 *   align           — "end" (default, ficha) | "start" (cuenta del operador, tabla
 *                      alineada a la izquierda) — solo cambia la alineación visual.
 */
export function ResolverServicioInline({ reservaId, servicePublicId, recordKind, onResuelto, align = "end" }) {
    const {
        acciones,
        accionAbierta,
        numero,
        setNumero,
        guardando,
        errorMensaje,
        mostrarCartel,
        setMostrarCartel,
        errorSinCasillero,
        setErrorSinCasillero,
        inputRef,
        abrirCasillero,
        cerrarCasillero,
        ejecutarAccionConCasillero,
        ejecutarAccionSinCasillero,
    } = useResolverServicioAcciones({ reservaId, servicePublicId, recordKind, onResuelto });

    if (acciones.length === 0) return null; // "generic": sin flujo de confirmación con operador

    const alineacion = align === "start" ? "items-start" : "items-end";

    if (!accionAbierta) {
        return (
            <div className={`flex flex-col ${alineacion} gap-1`}>
                {acciones.map((accion) =>
                    accion.necesitaCasillero ? (
                        <button
                            key={accion.tipo}
                            type="button"
                            onClick={() => abrirCasillero(accion.tipo)}
                            disabled={guardando}
                            data-testid={`btn-resolver-${accion.tipo}-${servicePublicId}`}
                            className={CLASES_BOTON_PRIMARIO}
                        >
                            <CheckCircle2 className="h-3 w-3" aria-hidden="true" />
                            {accion.etiqueta}
                        </button>
                    ) : (
                        <button
                            key={accion.tipo}
                            type="button"
                            onClick={() => ejecutarAccionSinCasillero(accion.tipo)}
                            disabled={guardando}
                            data-testid={`btn-resolver-${accion.tipo}-${servicePublicId}`}
                            className={CLASES_BOTON_SECUNDARIO}
                        >
                            {guardando ? <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" /> : accion.etiqueta}
                        </button>
                    )
                )}

                {/* H8: rechazo largo de "No requiere confirmación" — mismo Cartel que el resto
                    de la app, no un toast que se cierra solo. */}
                <CartelEmergente
                    isOpen={Boolean(errorSinCasillero)}
                    variant={CARTEL_EMERGENTE_VARIANTES.BLOQUEO}
                    message={errorSinCasillero}
                    onClose={() => setErrorSinCasillero(null)}
                    dataTestId={`cartel-emergente-resolver-sin-casillero-${servicePublicId}`}
                />
            </div>
        );
    }

    const accion = acciones.find((a) => a.tipo === accionAbierta);
    if (!accion) return null;

    return (
        <div
            className={`flex flex-col ${alineacion} gap-1.5 rounded-[10px] border border-emerald-200 bg-emerald-50/70 p-2 dark:border-emerald-800 dark:bg-emerald-950/20`}
            data-testid={`casillero-resolver-${servicePublicId}`}
        >
            <label
                htmlFor={`numero-confirmacion-${servicePublicId}`}
                className="text-[11px] font-semibold text-emerald-700 dark:text-emerald-300"
            >
                N° de confirmación del operador
            </label>
            <input
                id={`numero-confirmacion-${servicePublicId}`}
                ref={inputRef}
                type="text"
                value={numero}
                onChange={(e) => setNumero(e.target.value)}
                disabled={guardando}
                className="w-32 rounded border border-emerald-200 bg-white px-2 py-1 text-xs text-slate-900 focus:outline-none focus:ring-1 focus:ring-emerald-400 dark:border-emerald-800 dark:bg-slate-900 dark:text-white"
                data-testid={`input-numero-confirmacion-${servicePublicId}`}
            />

            {/* Rechazo corto: chico, pegado al casillero. Los rechazos largos van al Cartel
                emergente único (ver abajo) — nunca los dos a la vez. */}
            {errorMensaje && !mostrarCartel && (
                <p className="text-[11px] text-rose-600 dark:text-rose-400" role="alert">
                    {errorMensaje}
                </p>
            )}

            <div className="flex gap-1">
                <button
                    type="button"
                    onClick={() => ejecutarAccionConCasillero(accion.tipo)}
                    disabled={guardando}
                    data-testid={`btn-confirmar-resolver-${servicePublicId}`}
                    className={CLASES_BOTON_PRIMARIO}
                >
                    {guardando && <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />}
                    {guardando ? "Guardando…" : "Confirmar"}
                </button>
                <button
                    type="button"
                    onClick={cerrarCasillero}
                    disabled={guardando}
                    data-testid={`btn-cancelar-resolver-${servicePublicId}`}
                    className={CLASES_BOTON_SECUNDARIO}
                >
                    Cancelar
                </button>
            </div>

            <CartelEmergente
                isOpen={mostrarCartel}
                variant={CARTEL_EMERGENTE_VARIANTES.BLOQUEO}
                message={errorMensaje}
                onClose={() => setMostrarCartel(false)}
                dataTestId={`cartel-emergente-resolver-${servicePublicId}`}
            />
        </div>
    );
}
