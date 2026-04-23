import { useState, useEffect, useCallback, useMemo } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import {
    findNormalizedService,
    getServiceMutationEndpoint,
    normalizeReservaServices
} from "../lib/reservationServiceModel";

/**
 * Hook to manage a single Reserva's details, including CRUD for services and passengers.
 */
export function useReservaDetail(reservaId, navigate) {
    const [reserva, setReserva] = useState(null);
    const [loading, setLoading] = useState(true);
    const [suppliers, setSuppliers] = useState([]);

    const fetchServiceCollections = useCallback(async ({ strict = false } = {}) => {
        if (!reservaId) return {};

        const endpoints = [
            ["flightSegments", `/reservas/${reservaId}/flights`],
            ["hotelBookings", `/reservas/${reservaId}/hotels`],
            ["transferBookings", `/reservas/${reservaId}/transfers`],
            ["packageBookings", `/reservas/${reservaId}/packages`],
        ];

        const results = await Promise.allSettled(
            endpoints.map(([, endpoint]) => api.get(endpoint, { cache: "no-store" }))
        );

        if (strict) {
            const failed = results
                .map((result, index) => ({ result, endpoint: endpoints[index][1] }))
                .find(({ result }) => result.status === "rejected" || !Array.isArray(result.value));

            if (failed) {
                const reason = failed.result.reason?.message || "respuesta invalida";
                throw new Error(`No se pudo refrescar ${failed.endpoint}: ${reason}`);
            }
        }

        return endpoints.reduce((collections, [key], index) => {
            const result = results[index];
            if (result.status === "fulfilled" && Array.isArray(result.value)) {
                collections[key] = result.value;
            }

            return collections;
        }, {});
    }, [reservaId]);

    const fetchReserva = useCallback(async ({ showLoading = true, strictServices = false } = {}) => {
        if (!reservaId) return null;
        try {
            if (showLoading) {
                setLoading(true);
            }
            const [res, serviceCollections] = await Promise.all([
                api.get(`/reservas/${reservaId}`, { cache: "no-store" }),
                fetchServiceCollections({ strict: strictServices })
            ]);
            const nextReserva = { ...res, ...serviceCollections };
            setReserva(nextReserva);
            return nextReserva;
        } catch (error) {
            console.error(error);
            showError("Error al cargar la reserva: " + (error.response?.data?.Error || error.message || "Error desconocido"));
            setReserva(null);
            return null;
        } finally {
            if (showLoading) {
                setLoading(false);
            }
        }
    }, [reservaId, fetchServiceCollections]);

    const fetchSuppliers = useCallback(async () => {
        try {
            const res = await api.get("/suppliers?page=1&pageSize=100&includeInactive=true");
            setSuppliers(res?.items || []);
        } catch (error) {
            console.error("Error fetching suppliers:", error);
        }
    }, []);


    useEffect(() => {
        fetchReserva();
        fetchSuppliers();
    }, [fetchReserva, fetchSuppliers]);

    // Actions
    const handleArchiveReserva = async () => {
        try {
            await api.put(`/reservas/${reservaId}/archive`);
            showSuccess("Reserva archivada correctamente");
            if (navigate) navigate("/reservas");
            return true;
        } catch (error) {
            showError(error.response?.data?.message || error.response?.data || "Error al archivar la reserva");
            return false;
        }
    };

    const handleDeleteReserva = async () => {
        try {
            await api.delete(`/reservas/${reservaId}`);
            showSuccess("Reserva eliminada correctamente");
            if (navigate) navigate("/reservas");
            return true;
        } catch (error) {
            showError(error.response?.data?.message || error.response?.data || "Error al eliminar");
            return false;
        }
    };

    const handleStatusChange = async (newStatus) => {
        if (reserva?.status === 'Reservado' && newStatus === 'Presupuesto') {
            if (reserva.payments?.length > 0) {
                showError("No se puede volver a Presupuesto: hay pagos registrados.");
                return;
            }
            if (reserva.invoices?.length > 0) {
                showError("No se puede volver a Presupuesto: hay facturas emitidas.");
                return;
            }
        }

        try {
            await api.put(`/reservas/${reservaId}/status`, { status: newStatus });
            await fetchReserva();
            showSuccess(`Estado actualizado a ${newStatus}`);
            return true;
        } catch (error) {
            const msg = error.response?.data?.message || error.response?.data || "Error al cambiar estado";
            showError(typeof msg === 'string' ? msg : "Error al cambiar estado");
            return false;
        }
    };

    const handleDeleteService = async (service) => {
        try {
            const endpoint = getServiceMutationEndpoint(reservaId, service);

            await api.delete(endpoint);
            const updatedReserva = await fetchReserva({ showLoading: false, strictServices: true });

            if (!updatedReserva) {
                throw new Error("Servicio eliminado, pero no se pudo validar la reserva actualizada.");
            }

            if (updatedReserva && findNormalizedService(updatedReserva, service)) {
                throw new Error("El servicio sigue apareciendo en la reserva despues de eliminarlo.");
            }

            showSuccess("Servicio eliminado");
            return true;
        } catch (error) {
            const msg = error.response?.data?.message || error.response?.data || error.message || "Error al eliminar servicio";
            showError(typeof msg === 'string' ? msg : "Error al eliminar servicio");
            return false;
        }
    };

    const handleDeletePassenger = async (passengerId) => {
        try {
            await api.delete(`/reservas/passengers/${passengerId}`);
            await fetchReserva();
            showSuccess("Pasajero eliminado");
            return true;
        } catch (error) {
            showError("Error al eliminar");
            return false;
        }
    };

    // Memoized Helpers
    const allServices = useMemo(() => {
        return normalizeReservaServices(reserva);
    }, [reserva]);

    const hotelCapacity = useMemo(() => {
        if (!reserva) return 0;
        let capacity = 0;
        reserva.hotelBookings?.forEach(h => {
            const type = h.roomType?.toLowerCase() || '';
            if (type.includes('sing')) capacity += h.rooms;
            else if (type.includes('trip')) capacity += (3 * h.rooms);
            else if (type.includes('quad')) capacity += (4 * h.rooms);
            else capacity += (2 * h.rooms);
        });
        return capacity;
    }, [reserva]);

    return {
        reserva,
        loading,
        suppliers,
        fetchReserva,
        handleArchiveReserva,
        handleDeleteReserva,
        handleStatusChange,
        handleDeleteService,
        handleDeletePassenger,
        allServices,
        hotelCapacity
    };
}
