/**
 * Trigger(s) de la celda ESTADO en "Servicios comprados" (cuenta del operador),
 * Tanda T5 (2026-08-18, spec docs/ux/2026-08-18-spec-t5-expansion-pasajero.md sección 2,
 * respuesta firmada P3=A). Antes el casillero completo (etiqueta + input + botones) vivía
 * apretado adentro de esta misma columna de ~140px — acá solo queda EL BOTÓN, con molde
 * `Button` (P4=A del estándar visual 2026-08-11: nada de clases de color a mano). El
 * casillero en sí ahora vive en `ResolverServicioCasillero`, en una fila de expansión
 * aparte que ocupa todo el ancho de la tabla (ver `SupplierAccountPage.jsx`).
 *
 * Un traslado pendiente puede tener DOS botones a la vez ("Marcar confirmado" + "No
 * requiere confirmación") — mismo comportamiento que `ResolverServicioInline` de siempre,
 * ver `resolverAccionesParaServicioPendiente` en serviceResolutionActions.js.
 */

import { CheckCircle2, ChevronDown, Loader2 } from "lucide-react";
import { Button } from "../../../components/ui/button";
import { CartelEmergente, CARTEL_EMERGENTE_VARIANTES } from "../../../components/CartelEmergente";

export function ResolverServicioBotones({
    acciones,
    accionAbierta,
    guardando,
    onAbrirCasillero,
    onCerrarCasillero,
    onEjecutarSinCasillero,
    errorSinCasillero,
    onCerrarErrorSinCasillero,
    servicePublicId,
}) {
    return (
        <div className="flex flex-col items-start gap-1">
            {acciones.map((accion) => {
                if (!accion.necesitaCasillero) {
                    // "No requiere confirmación" (traslado mudo): un solo click, sin fila
                    // de expansión — no hay número de operador que cargar acá.
                    return (
                        <Button
                            key={accion.tipo}
                            type="button"
                            variant="outline"
                            size="sm"
                            onClick={() => onEjecutarSinCasillero(accion.tipo)}
                            disabled={guardando}
                            data-testid={`btn-resolver-${accion.tipo}-${servicePublicId}`}
                        >
                            {guardando ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : accion.etiqueta}
                        </Button>
                    );
                }

                // El resto ("Marcar confirmado"/"Marcar emitido") abre la fila de expansión
                // con el casillero del N° de confirmación. Tocar el botón de nuevo mientras
                // está abierto la cierra (mismo gesto que "Usar esta" en Copias de seguridad).
                const estaAbierto = accionAbierta === accion.tipo;
                return (
                    <Button
                        key={accion.tipo}
                        type="button"
                        variant="outline"
                        size="sm"
                        onClick={() => (estaAbierto ? onCerrarCasillero() : onAbrirCasillero(accion.tipo))}
                        disabled={guardando}
                        aria-expanded={estaAbierto}
                        data-testid={`btn-resolver-${accion.tipo}-${servicePublicId}`}
                        className="gap-1"
                    >
                        <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />
                        {accion.etiqueta}
                        <ChevronDown className={`h-3.5 w-3.5 transition-transform ${estaAbierto ? "rotate-180" : ""}`} aria-hidden="true" />
                    </Button>
                );
            })}

            {/* H8: rechazo largo de "No requiere confirmación" — mismo Cartel único de siempre,
                no un toast que se cierra solo. */}
            <CartelEmergente
                isOpen={Boolean(errorSinCasillero)}
                variant={CARTEL_EMERGENTE_VARIANTES.BLOQUEO}
                message={errorSinCasillero}
                onClose={onCerrarErrorSinCasillero}
                dataTestId={`cartel-emergente-resolver-sin-casillero-${servicePublicId}`}
            />
        </div>
    );
}
