/**
 * Hook que carga el estado de pago al operador POR SERVICIO para una reserva.
 *
 * Endpoint: GET /reservas/{reservaId}/supplier-payment-status
 * Respuesta: ReservaSupplierPaymentStatusDto
 *   {
 *     services: [{ recordKind, servicePublicId, status, netCost, paidToOperator, outstandingToOperator }],
 *     amountsVisible: bool
 *   }
 *
 * Reglas de uso (ADR-036 punto 4c + guía UX 2026-06-21 P4=B):
 *   - Se carga al montar la solapa Servicios (una sola llamada por reserva).
 *   - Si falla, degradamos silenciosamente: las etiquetas no aparecen, el resto
 *     de la solapa sigue funcionando (NO rompemos la pantalla por este dato).
 *   - amountsVisible = false → el backend ya enmascaró los montos a 0 → no mostramos cifras.
 *
 * Props:
 *   reservaId — publicId de la reserva (string). Si es null/undefined, no se llama al backend.
 */

import { useState, useEffect } from "react";
import { api } from "../../../api";

export function useReservaSupplierPaymentStatus(reservaId) {
    const [statusDto, setStatusDto] = useState(null);
    const [loading, setLoading] = useState(false);
    // error se guarda para reportar en consola, pero el componente lo ignora
    // (degradación silenciosa: si falla, simplemente no se muestran las etiquetas).
    const [error, setError] = useState(null);

    // Carga los estados de pago al montar o cuando cambia la reserva.
    // useEffect vacío-de-reservaId: no cargamos si no hay reservaId.
    useEffect(() => {
        if (!reservaId) return;

        let cancelled = false;
        setLoading(true);
        setError(null);

        api.get(`/reservas/${reservaId}/supplier-payment-status`)
            .then((response) => {
                if (!cancelled) setStatusDto(response);
            })
            .catch((err) => {
                if (!cancelled) {
                    // Degradación silenciosa: logueamos pero no rompemos la UI.
                    // Si el endpoint falla, statusDto queda null → no se muestran badges.
                    console.warn("[useReservaSupplierPaymentStatus] No se pudo cargar el estado de pagos al operador:", err?.message);
                    setError(err);
                }
            })
            .finally(() => {
                if (!cancelled) setLoading(false);
            });

        // Limpieza: si la reserva cambia antes de que llegue la respuesta,
        // ignoramos el resultado para no mostrar datos de otra reserva.
        return () => { cancelled = true; };
    }, [reservaId]);

    return { statusDto, loading, error };
}
