/**
 * Control "Para: Todos" / "Para: X de N" por servicio (Pieza A — ADR-031 v2.1).
 *
 * Aparece en cada fila de servicio de la lista.
 * Al tocarlo, despliega el PanelAsignarPasajeros — nunca un modal. En mobile lo hace
 * EN LÍNEA (empuja la tarjeta); en la tabla de escritorio (prop flotante) es un popover
 * anclado al botón para no romper la altura de la fila (ver prop `flotante` más abajo).
 *
 * Reglas (guía UX 2026-06-15 tarde):
 *   - Si no hay nombres cargados: muestra "Para: Todos — cargá los nombres para elegir"
 *     y no abre el panel (no se puede acotar sin pasajeros con nombre).
 *   - Si hay nombres: muestra "Para: Todos" o "Para: X de N" según asignaciones.
 *   - Al tocar → despliega PanelAsignarPasajeros.
 *   - Tras guardar o cancelar → cierra el panel.
 *
 * H19 (barrido E2E 2026-07-25, decisión firmada 9): el aviso de "sin nombres" solo se
 * muestra en aéreo y traslado (ver debeMostrarAvisoSinNombresParaElegir). En el resto de
 * los tipos, sin nombres cargados el control no muestra nada todavía — evita el ruido de
 * un aviso en filas donde elegir un pasajero puntual no es una necesidad real hoy.
 *
 * Props:
 *   reservaId            — publicId de la reserva
 *   serviceType          — tipo en formato backend ("Hotel", "Flight", "Transfer", etc.)
 *   servicePublicId      — publicId del servicio
 *   recordKind           — tipo en formato front ("flight"|"hotel"|"transfer"|"assistance"|"package")
 *   pasajerosConNombre   — array de pasajeros que ya tienen fullName cargado
 *   coverage             — ServiceNominalCoverageDto | null (del hook useServiceNominalCoverage)
 *   coverageLoading      — bool: si el hook está cargando la coverage
 *   onAsignacionGuardada — callback(nuevaCoverage) que el padre llama con la coverage fresca.
 *                          Recibe el ServiceNominalCoverageDto que devuelve el PUT atómico,
 *                          para actualizar el estado SIN hacer otra llamada al backend.
 *   className            — clases adicionales de Tailwind (para adaptar a desktop/mobile)
 *   flotante             — bool (default false). En true, el panel deja de empujar el flujo
 *                          y se despliega como POPOVER ANCLADO al botón (mismo patrón que el
 *                          menú "⋯" de ReservaHeader.jsx y "otras monedas" de DolarBnaTira.jsx:
 *                          position:absolute + click afuera + Escape cierran y devuelven el
 *                          foco al botón). Lo usa la tabla de escritorio de ServiceList, donde
 *                          el panel inline rompía la altura de la fila (bug visual 2026-08-06).
 *                          La tarjeta mobile sigue con el panel EN el flujo (flotante=false):
 *                          ahí cada tarjeta ya cambia de alto libremente, no hay fila que romper.
 */

import React, { useState, useRef, useEffect } from "react";
import { ChevronDown, Users } from "lucide-react";
import { PanelAsignarPasajeros } from "./PanelAsignarPasajeros";
import { debeMostrarAvisoSinNombresParaElegir } from "../lib/serviceResolutionActions";

// Mismo lenguaje visual que los demás popovers del repo (menú "⋯" de ReservaHeader,
// desplegable "otras monedas" de DolarBnaTira): fondo blanco/slate, borde sutil, sombra
// media, esquinas redondeadas. Ancho fijo moderado (no un cuadrado gigante) para que la
// lista de pasajeros entre cómoda sin ocupar media pantalla.
const CLASE_POPOVER_FLOTANTE =
    "w-72 rounded-lg border border-slate-200 bg-white px-4 py-3 shadow-md dark:border-slate-700 dark:bg-slate-900";

export function ControlAsignacionServicio({
    reservaId,
    serviceType,
    servicePublicId,
    recordKind,
    pasajerosConNombre,
    coverage,
    coverageLoading,
    onAsignacionGuardada,
    className = "",
    flotante = false,
}) {
    const [panelAbierto, setPanelAbierto] = useState(false);
    const contenedorRef = useRef(null);
    const triggerRef = useRef(null);

    // Cierra el popover al clickear afuera o con Escape, y devuelve el foco al botón —
    // solo aplica en modo flotante (mobile deja el panel en el flujo, no necesita esto).
    // El listener se registra siempre que el componente vive en modo flotante (no solo
    // mientras está abierto): mismo patrón que MenuAccionesExcepcion en ReservaHeader.jsx.
    useEffect(() => {
        if (!flotante) return undefined;

        function alClickearAfuera(evento) {
            if (contenedorRef.current && !contenedorRef.current.contains(evento.target)) {
                setPanelAbierto(false);
            }
        }
        function alApretarTecla(evento) {
            if (evento.key === "Escape") {
                setPanelAbierto(false);
                triggerRef.current?.focus();
            }
        }
        document.addEventListener("mousedown", alClickearAfuera);
        document.addEventListener("keydown", alApretarTecla);
        return () => {
            document.removeEventListener("mousedown", alClickearAfuera);
            document.removeEventListener("keydown", alApretarTecla);
        };
    }, [flotante]);

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
        // H19: fuera de aéreo/traslado, sin nombres el control no muestra nada todavía
        // (nada que "elegir" para el usuario en esta fila por ahora).
        if (!debeMostrarAvisoSinNombresParaElegir(recordKind)) {
            return null;
        }
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
        <div
            ref={contenedorRef}
            className={flotante ? `relative ${className}` : className}
        >
            {/* Botón del control: muestra el estado actual y abre el panel al tocarlo */}
            <button
                type="button"
                ref={triggerRef}
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

            {/* En flotante (desktop): popover anclado al botón, no empuja la fila.
                En inline (mobile): panel EN el flujo, debajo del control, como siempre. */}
            {panelAbierto && (
                <div
                    id={`panel-asignacion-${servicePublicId}`}
                    className={flotante ? `absolute right-0 top-full z-20 mt-1 ${CLASE_POPOVER_FLOTANTE}` : undefined}
                >
                    <PanelAsignarPasajeros
                        reservaId={reservaId}
                        serviceType={serviceType}
                        servicePublicId={servicePublicId}
                        pasajeros={pasajerosConNombre}
                        coverage={coverage}
                        {...(flotante ? { claseContenedor: "" } : {})}
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
