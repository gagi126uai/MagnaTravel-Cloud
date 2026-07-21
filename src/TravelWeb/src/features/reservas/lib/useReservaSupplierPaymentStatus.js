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
 * Fix E2E P3 (2026-07-22): antes este hook SOLO recargaba cuando cambiaba `reservaId`
 * (o sea, una vez por reserva). Si el vendedor editaba un servicio (o pagaba, cancelaba,
 * etc.) SIN salir de la página, el badge "Pago parcial al operador" quedaba con el dato
 * viejo mientras el resto de la fila (Costo Neto) ya mostraba el valor nuevo — dos
 * afirmaciones contradictorias en la misma fila hasta que el vendedor apretaba F5.
 *
 * Props:
 *   reservaId     — publicId de la reserva (string). Si es null/undefined, no se llama al backend.
 *   refreshSignal — cualquier valor que cambie de referencia cuando la pantalla ya recargó
 *     los servicios (ej. el objeto `reserva` completo, que ReservaDetailPage reemplaza por
 *     uno nuevo cada vez que corre `fetchReserva`). Es OPCIONAL para no romper otras pantallas
 *     que llamen a este hook con un solo argumento (siguen recargando solo por reservaId,
 *     comportamiento idéntico al de antes de este fix).
 */

import { useState, useEffect } from "react";
import { api } from "../../../api";

export function useReservaSupplierPaymentStatus(reservaId, refreshSignal) {
    const [statusDto, setStatusDto] = useState(null);
    const [loading, setLoading] = useState(false);
    // error se guarda para reportar en consola, pero el componente lo ignora
    // (degradación silenciosa: si falla, simplemente no se muestran las etiquetas).
    const [error, setError] = useState(null);

    // Carga los estados de pago al montar, cuando cambia la reserva, y también cuando
    // `refreshSignal` cambia de referencia (por ejemplo, tras editar un servicio y que la
    // pantalla vuelva a pedir la reserva completa). Sin `refreshSignal` en las dependencias,
    // este efecto no se enteraba de esos cambios porque `reservaId` no varía entre ediciones.
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
    }, [reservaId, refreshSignal]);

    return { statusDto, loading, error };
}
