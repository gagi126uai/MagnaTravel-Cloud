import React from 'react';
import { AlertTriangle } from "lucide-react";
import { getAdvertenciaCapacidad } from "../avisosFicha";

export function CapacityWarning({ paxCount, capacity }) {
    // La decisión de "hay que avisar" vive en avisosFicha.js: la usa también el
    // plegado "N avisos más" de la ficha para contar este aviso sin duplicar la regla.
    const advertencia = getAdvertenciaCapacidad(paxCount, capacity);
    if (!advertencia) return null;

    const cap = advertencia;
    const detalle = advertencia.detalle;

    return (
        <div className="bg-yellow-50 border-l-4 border-yellow-400 p-4 mb-4 dark:bg-yellow-950/20 dark:border-yellow-700">
            <div className="flex">
                <div className="flex-shrink-0">
                    <AlertTriangle className="h-5 w-5 text-yellow-400" aria-hidden="true" />
                </div>
                <div className="ml-3">
                    <p className="text-sm text-yellow-700 dark:text-yellow-300">
                        Atencion: hay <strong>{paxCount}</strong> pasajeros cargados pero los servicios contratados solo soportan <strong>{cap.total}</strong>{detalle.length > 0 ? ` (${detalle.join(", ")})` : ""}.
                        <br /><span className="text-xs opacity-75">Ajusta la capacidad de los servicios o agrega uno nuevo antes de continuar.</span>
                    </p>
                </div>
            </div>
        </div>
    );
}
