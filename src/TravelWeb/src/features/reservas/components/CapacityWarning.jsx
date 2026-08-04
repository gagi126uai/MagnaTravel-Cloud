import React from 'react';
import { getAdvertenciaCapacidad } from "../avisosFicha";
import { AvisoFila } from "./AvisoFila";

/**
 * Aviso: hay más pasajeros cargados que lugares que alcanzan los servicios
 * contratados (ej. 3 pasajeros pero el hotel solo tiene lugar para 2).
 *
 * P11 (Tanda 2 del rediseño, 2026-08-03): fila GRIS de una sola línea (con
 * AvisoFila), reemplaza el bloque amarillo de antes. `onVer` (opcional) lleva
 * a la pestaña Servicios, donde se ajusta la capacidad de cada uno.
 */
export function CapacityWarning({ paxCount, capacity, onVer }) {
    // La decisión de "hay que avisar" vive en avisosFicha.js: la usa también el
    // plegado "N avisos más" de la ficha para contar este aviso sin duplicar la regla.
    const advertencia = getAdvertenciaCapacidad(paxCount, capacity);
    if (!advertencia) return null;

    const detalle = advertencia.detalle;

    return (
        <AvisoFila
            variante="info"
            dataTestId="aviso-capacidad-excedida"
            textoBoton={onVer ? "Ver" : undefined}
            onClickBoton={onVer}
        >
            {/* Sin `title`: la guía prohíbe info solo-por-hover (review Tanda 2). La
                indicación de qué hacer va visible, corta; el detalle vive en Servicios. */}
            <span>
                Hay <strong>{paxCount}</strong> {paxCount === 1 ? "pasajero cargado" : "pasajeros cargados"} y los servicios
                contratados alcanzan para <strong>{advertencia.total}</strong>
                {detalle.length > 0 ? ` (${detalle.join(", ")})` : ""} — ajustá la capacidad o sumá un servicio.
            </span>
        </AvisoFila>
    );
}
