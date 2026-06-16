/**
 * Hook que consulta al backend el "set nominal" de un servicio:
 * quiénes viajan en ese servicio y qué datos le faltan.
 *
 * Se usa en el panel "Para: Todos / X de N" (Pieza A del ADR-031 v2.1)
 * y en el mini-formulario de nombres (Pieza B), donde la autoridad es el
 * backend (mismo PassengerNominalRules) y NO el cálculo local de pasajeroHint.
 *
 * Endpoint: GET /api/reservas/{reservaId}/services/{serviceType}/{servicePublicId}/nominal-coverage
 * Respuesta: ServiceNominalCoverageDto
 *
 * @param {string} reservaId        - publicId de la reserva
 * @param {string} serviceType      - tipo en formato backend: "Hotel", "Flight", "Transfer", etc.
 * @param {string} servicePublicId  - publicId del servicio
 * @param {boolean} enabled         - false para no ejecutar la llamada (ej: panel cerrado)
 */

import { useState, useEffect } from "react";
import { api } from "../../../api";

export function useServiceNominalCoverage({ reservaId, serviceType, servicePublicId, enabled = true }) {
    const [coverage, setCoverage] = useState(null);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState(null);

    // Función para recargar la cobertura manualmente (se usa tras guardar una asignación).
    const [reloadKey, setReloadKey] = useState(0);
    const refetch = () => setReloadKey(k => k + 1);

    // El efecto corre cuando cambia el servicio O cuando se pide refetch.
    // enabled=false: no llamamos al backend (el panel está cerrado, no hace falta).
    useEffect(() => {
        if (!enabled || !reservaId || !serviceType || !servicePublicId) {
            setCoverage(null);
            return;
        }

        let cancelled = false;
        setLoading(true);
        setError(null);

        api.get(`/reservas/${reservaId}/services/${serviceType}/${servicePublicId}/nominal-coverage`)
            .then(response => {
                if (!cancelled) setCoverage(response.data);
            })
            .catch(err => {
                if (!cancelled) setError(err);
            })
            .finally(() => {
                if (!cancelled) setLoading(false);
            });

        // Limpieza: si el componente se desmonta o cambian las deps, cancelamos la respuesta.
        return () => { cancelled = true; };
    }, [reservaId, serviceType, servicePublicId, enabled, reloadKey]);

    // updateCoverage: permite al consumidor pisar el estado con una coverage nueva
    // obtenida de otro endpoint (ej: el PUT atómico de assignments devuelve el DTO
    // actualizado — no hace falta re-pedir con refetch, usamos el dato que ya llegó).
    const updateCoverage = (nuevaCoverage) => setCoverage(nuevaCoverage);

    return { coverage, loading, error, refetch, updateCoverage };
}
