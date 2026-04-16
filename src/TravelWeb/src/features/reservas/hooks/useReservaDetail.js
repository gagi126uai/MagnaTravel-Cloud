import { useState, useEffect, useCallback, useMemo } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getPublicId } from "../../../lib/publicIds";

/**
 * Hook to manage a single Reserva's details, including CRUD for services and passengers.
 */
export function useReservaDetail(reservaId, navigate) {
    const [reserva, setReserva] = useState(null);
    const [loading, setLoading] = useState(true);
    const [suppliers, setSuppliers] = useState([]);

    const fetchReserva = useCallback(async () => {
        if (!reservaId) return;
        try {
            setLoading(true);
            const res = await api.get(`/reservas/${reservaId}`);
            setReserva(res);
        } catch (error) {
            console.error(error);
            showError("Error al cargar la reserva: " + (error.response?.data?.Error || error.message || "Error desconocido"));
            setReserva(null);
        } finally {
            setLoading(false);
        }
    }, [reservaId]);

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
            let endpoint = "";
            const servicePublicId = getPublicId(service);
            if (service._type === 'Flight') endpoint = `/reservas/${reservaId}/flights/${servicePublicId}`;
            else if (service._type === 'Hotel') endpoint = `/reservas/${reservaId}/hotels/${servicePublicId}`;
            else if (service._type === 'Transfer') endpoint = `/reservas/${reservaId}/transfers/${servicePublicId}`;
            else if (service._type === 'Package') endpoint = `/reservas/${reservaId}/packages/${servicePublicId}`;

            if (!endpoint) return;

            await api.delete(endpoint);
            await fetchReserva();
            showSuccess("Servicio eliminado");
            return true;
        } catch (error) {
            const msg = error.response?.data?.message || error.response?.data || "Error al eliminar servicio";
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
        if (!reserva) return [];
        const services = [];
        reserva.flightSegments?.forEach(f => services.push({ ...f, _type: f.sourceKind || 'Flight', date: f.departureTime, name: `${f.airlineName || ''} ${f.flightNumber || ''}`.trim() }));
        reserva.hotelBookings?.forEach(h => services.push({ ...h, _type: h.sourceKind || 'Hotel', date: h.checkIn, name: h.hotelName }));
        reserva.transferBookings?.forEach(t => services.push({ ...t, _type: t.sourceKind || 'Transfer', date: t.pickupDateTime, name: `${t.pickupLocation} > ${t.dropoffLocation}` }));
        reserva.packageBookings?.forEach(p => services.push({ ...p, _type: p.sourceKind || 'Package', date: p.startDate, name: p.packageName }));
        reserva.servicios?.forEach(r => services.push({ ...r, _type: r.sourceKind || r.serviceType || 'Generic', date: r.departureDate, name: r.description }));
        return services.sort((a, b) => new Date(a.date) - new Date(b.date));
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
