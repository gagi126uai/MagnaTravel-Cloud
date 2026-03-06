import { useState, useEffect, useCallback, useMemo } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";

/**
 * Hook to manage a single Travel File's details, including CRUD for services and passengers.
 */
export function useTravelFileDetail(fileId, navigate) {
    const [file, setFile] = useState(null);
    const [loading, setLoading] = useState(true);
    const [suppliers, setSuppliers] = useState([]);

    const fetchFile = useCallback(async () => {
        if (!fileId) return;
        try {
            setLoading(true);
            const res = await api.get(`/travelfiles/${fileId}`);
            setFile(res);
        } catch (error) {
            console.error(error);
            showError("Error al cargar el expediente: " + (error.response?.data?.Error || error.message || "Error desconocido"));
            setFile(null);
        } finally {
            setLoading(false);
        }
    }, [fileId]);

    const fetchSuppliers = useCallback(async () => {
        try {
            const res = await api.get("/suppliers");
            setSuppliers(res || []);
        } catch (error) {
            console.error("Error fetching suppliers:", error);
        }
    }, []);

    useEffect(() => {
        fetchFile();
        fetchSuppliers();
    }, [fetchFile, fetchSuppliers]);

    // Actions
    const handleArchiveFile = async () => {
        try {
            await api.put(`/travelfiles/${fileId}/archive`);
            showSuccess("File archivado correctamente");
            if (navigate) navigate("/files");
            return true;
        } catch (error) {
            showError("Error al archivar");
            return false;
        }
    };

    const handleDeleteFile = async () => {
        try {
            await api.delete(`/travelfiles/${fileId}`);
            showSuccess("File eliminado correctamente");
            if (navigate) navigate("/files");
            return true;
        } catch (error) {
            showError(error.response?.data || "Error al eliminar");
            return false;
        }
    };

    const handleStatusChange = async (newStatus) => {
        if (file?.status === 'Reservado' && newStatus === 'Presupuesto') {
            if (file.payments?.length > 0) {
                showError("No se puede volver a Presupuesto: hay pagos registrados.");
                return;
            }
            if (file.invoices?.length > 0) {
                showError("No se puede volver a Presupuesto: hay facturas emitidas.");
                return;
            }
        }

        try {
            await api.put(`/travelfiles/${fileId}/status`, { status: newStatus });
            await fetchFile();
            showSuccess(`Estado actualizado a ${newStatus}`);
            return true;
        } catch (error) {
            showError("Error al cambiar estado");
            return false;
        }
    };

    const handleDeleteService = async (service) => {
        try {
            let endpoint = "";
            if (service._type === 'Flight') endpoint = `/files/${fileId}/flights/${service.id}`;
            else if (service._type === 'Hotel') endpoint = `/files/${fileId}/hotels/${service.id}`;
            else if (service._type === 'Transfer') endpoint = `/files/${fileId}/transfers/${service.id}`;
            else if (service._type === 'Package') endpoint = `/files/${fileId}/packages/${service.id}`;

            if (!endpoint) return;

            await api.delete(endpoint);
            await fetchFile();
            showSuccess("Servicio eliminado");
            return true;
        } catch (error) {
            showError("Error al eliminar servicio");
            return false;
        }
    };

    const handleDeletePassenger = async (passengerId) => {
        try {
            await api.delete(`/travelfiles/passengers/${passengerId}`);
            await fetchFile();
            showSuccess("Pasajero eliminado");
            return true;
        } catch (error) {
            showError("Error al eliminar");
            return false;
        }
    };

    // Memoized Helpers
    const allServices = useMemo(() => {
        if (!file) return [];
        const services = [];
        file.flightSegments?.forEach(f => services.push({ ...f, _type: 'Flight', date: f.departureTime, name: `${f.airlineName} ${f.flightNumber}` }));
        file.hotelBookings?.forEach(h => services.push({ ...h, _type: 'Hotel', date: h.checkIn, name: h.hotelName }));
        file.transferBookings?.forEach(t => services.push({ ...t, _type: 'Transfer', date: t.pickupDateTime, name: `${t.pickupLocation} > ${t.dropoffLocation}` }));
        file.packageBookings?.forEach(p => services.push({ ...p, _type: 'Package', date: p.startDate, name: p.packageName }));
        file.reservations?.forEach(r => services.push({ ...r, _type: r.serviceType || 'Generic', date: r.departureDate, name: r.description }));
        return services.sort((a, b) => new Date(a.date) - new Date(b.date));
    }, [file]);

    const hotelCapacity = useMemo(() => {
        if (!file) return 0;
        let capacity = 0;
        file.hotelBookings?.forEach(h => {
            const type = h.roomType?.toLowerCase() || '';
            if (type.includes('sing')) capacity += h.rooms;
            else if (type.includes('trip')) capacity += (3 * h.rooms);
            else if (type.includes('quad')) capacity += (4 * h.rooms);
            else capacity += (2 * h.rooms);
        });
        return capacity;
    }, [file]);

    return {
        file,
        loading,
        suppliers,
        fetchFile,
        handleArchiveFile,
        handleDeleteFile,
        handleStatusChange,
        handleDeleteService,
        handleDeletePassenger,
        allServices,
        hotelCapacity
    };
}
