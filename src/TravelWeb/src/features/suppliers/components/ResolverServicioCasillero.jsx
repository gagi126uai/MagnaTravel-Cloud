/**
 * Contenido de la fila de expansión que se abre debajo de un servicio pendiente en
 * "Servicios comprados" (cuenta del operador), Tanda T5 (2026-08-18, spec
 * docs/ux/2026-08-18-spec-t5-expansion-pasajero.md sección 2, respuesta firmada P3=A).
 *
 * Antes este casillero (etiqueta + input + Confirmar/Cancelar + "Corregir a mano") vivía
 * apretado adentro de la columna ESTADO (~140px de una grilla `density="compact"`) — se
 * veía amontonado. Ahora vive acá, en una fila propia que ocupa todo el ancho de la
 * tabla (colSpan), con lugar de sobra. El comportamiento (guardar, error, "Corregir a
 * mano") es EXACTAMENTE el mismo de siempre — viene del hook `useResolverServicioAcciones`,
 * este componente solo lo pinta con más aire.
 *
 * "Corregir a mano" (link chico y discreto, P-9/P-10: nunca compite con el botón
 * primario) abre acá adentro el desplegable viejo de estado (`ServiceStatusEditor`) —
 * mismo comportamiento de siempre, ahora con lugar propio en vez de apretado en la
 * columna.
 */

import { Loader2 } from "lucide-react";
import { Button } from "../../../components/ui/button";
import { CartelEmergente, CARTEL_EMERGENTE_VARIANTES } from "../../../components/CartelEmergente";
import { ServiceStatusEditor } from "./ServiceStatusEditor";

export function ResolverServicioCasillero({
    numero,
    onNumeroChange,
    guardando,
    errorMensaje,
    mostrarCartel,
    onCerrarCartel,
    onConfirmar,
    onCancelar,
    inputRef,
    servicePublicId,
    mostrarCorreccion,
    onMostrarCorreccion,
    service,
    onUpdated,
    canEdit,
}) {
    return (
        <div
            className="border-t border-emerald-100 bg-emerald-50/40 p-4 dark:border-emerald-900/40 dark:bg-emerald-950/10"
            data-testid={`casillero-resolver-${servicePublicId}`}
        >
            <div className="flex flex-wrap items-end gap-3">
                <div className="flex flex-col gap-1">
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
                        onChange={(e) => onNumeroChange(e.target.value)}
                        disabled={guardando}
                        className="h-10 w-48 rounded-[10px] border border-emerald-200 bg-white px-3 text-sm text-slate-900 focus:outline-none focus:ring-1 focus:ring-emerald-400 dark:border-emerald-800 dark:bg-slate-900 dark:text-white"
                        data-testid={`input-numero-confirmacion-${servicePublicId}`}
                    />
                </div>

                <div className="flex gap-2">
                    <Button
                        type="button"
                        variant="default"
                        size="sm"
                        onClick={onConfirmar}
                        disabled={guardando}
                        data-testid={`btn-confirmar-resolver-${servicePublicId}`}
                        className="gap-1"
                    >
                        {guardando && <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />}
                        {guardando ? "Guardando…" : "Confirmar"}
                    </Button>
                    <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={onCancelar}
                        disabled={guardando}
                        data-testid={`btn-cancelar-resolver-${servicePublicId}`}
                    >
                        Cancelar
                    </Button>
                </div>
            </div>

            {/* Rechazo corto: texto rojo con su propio espacio, ya no apretado contra el
                casillero como en la columna angosta de antes. Los rechazos largos van al
                Cartel emergente único (ver abajo) — nunca los dos a la vez. */}
            {errorMensaje && !mostrarCartel && (
                <p className="mt-2 text-[12px] text-rose-600 dark:text-rose-400" role="alert">
                    {errorMensaje}
                </p>
            )}

            <div className="mt-3">
                {mostrarCorreccion ? (
                    <ServiceStatusEditor service={service} onUpdated={onUpdated} canEdit={canEdit} />
                ) : (
                    <button
                        type="button"
                        onClick={onMostrarCorreccion}
                        className="text-[11px] text-slate-400 hover:text-slate-600 hover:underline dark:text-slate-500 dark:hover:text-slate-300"
                        data-testid={`btn-corregir-a-mano-${servicePublicId}`}
                    >
                        Corregir a mano
                    </button>
                )}
            </div>

            <CartelEmergente
                isOpen={mostrarCartel}
                variant={CARTEL_EMERGENTE_VARIANTES.BLOQUEO}
                message={errorMensaje}
                onClose={onCerrarCartel}
                dataTestId={`cartel-emergente-resolver-${servicePublicId}`}
            />
        </div>
    );
}
