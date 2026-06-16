/**
 * Control "Para: Todos" / "Para: X de N" por servicio (Pieza A — ADR-031 v2.1).
 *
 * Aparece en cada fila de servicio de la lista.
 * Al tocarlo, despliega EN LÍNEA el PanelAsignarPasajeros (nunca un modal).
 *
 * Reglas (guía UX 2026-06-15 tarde):
 *   - Si no hay nombres cargados: muestra "Para: Todos — cargá los nombres para elegir"
 *     y no abre el panel (no se puede acotar sin pasajeros con nombre).
 *   - Si hay nombres: muestra "Para: Todos" o "Para: X de N" según asignaciones.
 *   - Al tocar → despliega PanelAsignarPasajeros.
 *   - Tras guardar o cancelar → cierra el panel.
 *
 * Props:
 *   reservaId            — publicId de la reserva
 *   serviceType          — tipo en formato backend ("Hotel", "Flight", "Transfer", etc.)
 *   servicePublicId      — publicId del servicio
 *   pasajerosConNombre   — array de pasajeros que ya tienen fullName cargado
 *   coverage             — ServiceNominalCoverageDto | null (del hook useServiceNominalCoverage)
 *   coverageLoading      — bool: si el hook está cargando la coverage
 *   onAsignacionGuardada — callback(nuevaCoverage) que el padre llama con la coverage fresca.
 *                          Recibe el ServiceNominalCoverageDto que devuelve el PUT atómico,
 *                          para actualizar el estado SIN hacer otra llamada al backend.
 *   className            — clases adicionales de Tailwind (para adaptar a desktop/mobile)
 */

import React, { useState } from "react";
import { ChevronDown, Users } from "lucide-react";
import { PanelAsignarPasajeros } from "./PanelAsignarPasajeros";

export function ControlAsignacionServicio({
    reservaId,
    serviceType,
    servicePublicId,
    pasajerosConNombre,
    coverage,
    coverageLoading,
    onAsignacionGuardada,
    className = "",
}) {
    const [panelAbierto, setPanelAbierto] = useState(false);

    const hayNombresCargados = Array.isArray(pasajerosConNombre) && pasajerosConNombre.length > 0;

    // Calculamos el texto del control según el estado de asignaciones.
    // coverage viene del backend y es la fuente de verdad:
    //   hasExplicitAssignments = false → "Para: Todos"
    //   hasExplicitAssignments = true  → "Para: X de N"
    function calcularTextoControl() {
        if (!coverage) return "Para: Todos";

        if (!coverage.hasExplicitAssignments) {
            return "Para: Todos";
        }

        // Tiene asignaciones explícitas: mostrar "Para: X de N"
        const x = coverage.serviceSetCount;
        const n = coverage.reservaPassengerCount;
        return `Para: ${x} de ${n}`;
    }

    const textoControl = calcularTextoControl();

    // Si no hay nombres, el control queda disabled: no se puede acotar sin conocer
    // a los pasajeros concretos (la UX lo dice explícitamente).
    if (!hayNombresCargados) {
        return (
            <span
                className={`inline-flex items-center gap-1 text-[10px] text-slate-400 dark:text-slate-500 italic ${className}`}
                title="Cargá los nombres para poder elegir quiénes van"
                data-testid="control-asignacion-sin-nombres"
            >
                <Users className="h-3 w-3 flex-shrink-0" aria-hidden="true" />
                Para: Todos — cargá los nombres para elegir
            </span>
        );
    }

    return (
        <div className={className}>
            {/* Botón del control: muestra el estado actual y abre el panel al tocarlo */}
            <button
                type="button"
                onClick={() => setPanelAbierto(!panelAbierto)}
                aria-expanded={panelAbierto}
                aria-controls={`panel-asignacion-${servicePublicId}`}
                data-testid={`control-asignacion-${servicePublicId}`}
                className={`inline-flex items-center gap-1 rounded-md px-2 py-1 text-[10px] font-semibold transition-colors ${
                    coverage?.hasExplicitAssignments
                        ? "border border-indigo-200 bg-indigo-50 text-indigo-700 hover:bg-indigo-100 dark:border-indigo-800/60 dark:bg-indigo-950/20 dark:text-indigo-300 dark:hover:bg-indigo-900/30"
                        : "border border-slate-200 bg-slate-50 text-slate-600 hover:bg-slate-100 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700"
                }`}
            >
                <Users className="h-3 w-3 flex-shrink-0" aria-hidden="true" />
                {coverageLoading ? "Para: ..." : textoControl}
                <ChevronDown
                    className={`h-3 w-3 flex-shrink-0 transition-transform ${panelAbierto ? "rotate-180" : ""}`}
                    aria-hidden="true"
                />
            </button>

            {/* Panel inline de tildes: se despliega debajo del botón (no en ventana flotante) */}
            {panelAbierto && (
                <div id={`panel-asignacion-${servicePublicId}`}>
                    <PanelAsignarPasajeros
                        reservaId={reservaId}
                        serviceType={serviceType}
                        servicePublicId={servicePublicId}
                        pasajeros={pasajerosConNombre}
                        coverage={coverage}
                        onListo={(nuevaCoverage) => {
                            setPanelAbierto(false);
                            // Propagamos la coverage que devolvió el PUT atómico al padre.
                            // El padre actualiza su estado directamente sin hacer otra llamada.
                            onAsignacionGuardada?.(nuevaCoverage);
                        }}
                        onCancelar={() => setPanelAbierto(false)}
                    />
                </div>
            )}
        </div>
    );
}
