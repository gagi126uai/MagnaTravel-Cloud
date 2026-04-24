import { useState, useEffect, useCallback, useMemo } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import {
    findNormalizedService,
    getReservationCollectionKeyForRecordKind,
    getReservaCollectionLabel,
    getServiceMutationEndpoint,
    normalizeReservaServices,
    getReservationCollectionKeyForServiceType,
    getReservationServicePublicId
} from "../lib/reservationServiceModel";

const SERVICE_COLLECTION_ENDPOINTS = Object.freeze({
    flightSegments: (reservaId) => `/reservas/${reservaId}/flights`,
    hotelBookings: (reservaId) => `/reservas/${reservaId}/hotels`,
    transferBookings: (reservaId) => `/reservas/${reservaId}/transfers`,
    packageBookings: (reservaId) => `/reservas/${reservaId}/packages`,
});

function getApiErrorMessage(error, fallbackMessage) {
    if (typeof error?.payload === "string" && error.payload.trim()) {
        return error.payload;
    }

    if (error?.payload?.message) {
        return error.payload.message;
    }

    if (error?.payload?.error) {
        return error.payload.error;
    }

    if (error?.payload?.title) {
        return error.payload.title;
    }

    return error?.message || fallbackMessage;
}

function removeServiceFromReservaSnapshot(reserva, service) {
    if (!reserva || !service) {
        return reserva;
    }

    const serviceId = service.publicId || service.PublicId || service.id || service.Id;
    const collectionKey = getReservationCollectionKeyForRecordKind(service.recordKind);

    if (!collectionKey || !Array.isArray(reserva[collectionKey])) {
        return reserva;
    }

    return {
        ...reserva,
        [collectionKey]: reserva[collectionKey].filter((item) => {
            const itemId = item.publicId || item.PublicId || item.id || item.Id;
            return String(itemId || "") !== String(serviceId || "");
        }),
    };
}

/**
 * Hook to manage a single Reserva's details, including CRUD for services and passengers.
 */
export function useReservaDetail(reservaId, navigate) {
    const [reserva, setReserva] = useState(null);
    const [loading, setLoading] = useState(true);
    const [suppliers, setSuppliers] = useState([]);
    const [serviceCollectionErrors, setServiceCollectionErrors] = useState({});

    const fetchServiceCollections = useCallback(async ({ keys, strict = false } = {}) => {
        if (!reservaId) return {};

        const collectionKeys = (keys?.length ? keys : Object.keys(SERVICE_COLLECTION_ENDPOINTS))
            .filter((key) => typeof SERVICE_COLLECTION_ENDPOINTS[key] === "function");

        const results = await Promise.allSettled(
            collectionKeys.map((key) => api.get(SERVICE_COLLECTION_ENDPOINTS[key](reservaId), { cache: "no-store" }))
        );

        const collections = {};
        const errors = {};
        let strictError = null;

        collectionKeys.forEach((key, index) => {
            const result = results[index];
            if (result.status === "fulfilled" && Array.isArray(result.value)) {
                collections[key] = result.value;
                return;
            }

            const message = result.status === "rejected"
                ? getApiErrorMessage(result.reason, `No se pudo cargar ${getReservaCollectionLabel(key)}.`)
                : `No se pudo cargar ${getReservaCollectionLabel(key)}.`;

            errors[key] = message;
            if (strict && !strictError) {
                strictError = {
                    key,
                    label: getReservaCollectionLabel(key),
                    message,
                };
            }
        });

        return { collections, errors, strictError, collectionKeys };
    }, [reservaId]);

    const fetchReserva = useCallback(async ({ showLoading = true, collectionKeys, strictCollections = false, service, serviceType } = {}) => {
        if (!reservaId) return null;
        try {
            if (showLoading) {
                setLoading(true);
            }
            const res = await api.get(`/reservas/${reservaId}`, { cache: "no-store" });
            const {
                collections,
                errors,
                strictError,
                collectionKeys: refreshedCollectionKeys,
            } = await fetchServiceCollections({
                keys: collectionKeys,
                strict: strictCollections,
            });

            setServiceCollectionErrors((currentErrors) => {
                const nextErrors = collectionKeys?.length ? { ...currentErrors } : {};
                (refreshedCollectionKeys || []).forEach((key) => {
                    if (errors[key]) {
                        nextErrors[key] = errors[key];
                    } else {
                        delete nextErrors[key];
                    }
                });
                return nextErrors;
            });

            const nextReserva = { ...res, ...collections };

            // Inject authoritative service data if provided (fixes RabbitMQ/Cache sync lag)
            if (service && serviceType) {
                const collectionKey = getReservationCollectionKeyForServiceType(serviceType);
                if (collectionKey) {
                    const currentCollection = nextReserva[collectionKey] || [];
                    const serviceId = getReservationServicePublicId(service);
                    const exists = currentCollection.some(s => getReservationServicePublicId(s) === serviceId);

                    if (!exists) {
                        nextReserva[collectionKey] = [...currentCollection, service];
                    } else {
                        nextReserva[collectionKey] = currentCollection.map(s =>
                            getReservationServicePublicId(s) === serviceId ? service : s
                        );
                    }
                }
            }

            setReserva(nextReserva);
            return {
                reserva: nextReserva,
                serviceCollectionError: strictError,
                collectionErrors: errors,
            };
        } catch (error) {
            console.error(error);
            showError("Error al cargar la reserva: " + getApiErrorMessage(error, "Error desconocido"));
            setReserva(null);
            return {
                reserva: null,
                serviceCollectionError: null,
                collectionErrors: {},
            };
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
            showError(getApiErrorMessage(error, "Error al archivar la reserva"));
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
            showError(getApiErrorMessage(error, "Error al eliminar"));
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
            showError(getApiErrorMessage(error, "Error al cambiar estado"));
            return false;
        }
    };

    const handleDeleteService = async (service) => {
        try {
            const endpoint = getServiceMutationEndpoint(reservaId, service);
            const collectionKey = getReservationCollectionKeyForRecordKind(service?.recordKind);
            const hasDedicatedCollection = Boolean(collectionKey && SERVICE_COLLECTION_ENDPOINTS[collectionKey]);

            await api.delete(endpoint);
            const refreshResult = await fetchReserva({
                showLoading: false,
                collectionKeys: hasDedicatedCollection ? [collectionKey] : undefined,
                strictCollections: hasDedicatedCollection,
            });
            const updatedReserva = refreshResult?.reserva || null;

            if (refreshResult?.serviceCollectionError) {
                setReserva((currentReserva) => removeServiceFromReservaSnapshot(currentReserva, service));
                showSuccess("Servicio eliminado. Esa lista no se pudo refrescar en este momento.");
                return true;
            }

            if (!updatedReserva) {
                throw new Error("Servicio eliminado, pero no se pudo validar la reserva actualizada.");
            }

            if (updatedReserva && findNormalizedService(updatedReserva, service)) {
                throw new Error("El servicio sigue apareciendo en la reserva despues de eliminarlo.");
            }

            showSuccess("Servicio eliminado");
            return true;
        } catch (error) {
            showError(getApiErrorMessage(error, "Error al eliminar servicio"));
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
            showError(getApiErrorMessage(error, "Error al eliminar"));
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
        serviceCollectionErrors,
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
