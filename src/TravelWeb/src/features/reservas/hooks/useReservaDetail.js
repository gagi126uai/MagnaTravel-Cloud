import { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { camelize } from "../../../lib/utils";
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

function upsertPassengerInReservaSnapshot(reserva, passenger) {
    if (!reserva || !passenger) {
        return reserva;
    }

    const normalizedPassenger = camelize(passenger);
    const passengerId = getPublicId(normalizedPassenger);
    const passengers = Array.isArray(reserva.passengers) ? reserva.passengers : [];
    const exists = passengers.some((item) => {
        const itemId = getPublicId(item);
        return String(itemId || "") === String(passengerId || "");
    });

    return {
        ...reserva,
        passengers: exists
            ? passengers.map((item) => {
                const itemId = getPublicId(item);
                return String(itemId || "") === String(passengerId || "") ? normalizedPassenger : item;
            })
            : [...passengers, normalizedPassenger],
    };
}

function removePassengerFromReservaSnapshot(reserva, passengerId) {
    if (!reserva) {
        return reserva;
    }

    return {
        ...reserva,
        passengers: (reserva.passengers || []).filter((item) => {
            const itemId = getPublicId(item);
            return String(itemId || "") !== String(passengerId || "");
        }),
    };
}

function mergePassengerCollections(...collections) {
    const passengersById = new Map();

    collections.flat().forEach((passenger) => {
        if (!passenger) return;
        const normalizedPassenger = camelize(passenger);
        const passengerId = getPublicId(normalizedPassenger);
        if (!passengerId) return;
        passengersById.set(String(passengerId), normalizedPassenger);
    });

    return Array.from(passengersById.values());
}

/**
 * Hook to manage a single Reserva's details, including CRUD for services and passengers.
 */
export function useReservaDetail(reservaId, navigate) {
    const [reserva, setReserva] = useState(null);
    const [loading, setLoading] = useState(true);
    const [suppliers, setSuppliers] = useState([]);
    const [serviceCollectionErrors, setServiceCollectionErrors] = useState({});
    const reservaRef = useRef(null);
    const fetchSequenceRef = useRef(0);

    useEffect(() => {
        reservaRef.current = reserva;
    }, [reserva]);

    const setReservaSnapshot = useCallback((valueOrUpdater) => {
        setReserva((currentReserva) => {
            const nextReserva = typeof valueOrUpdater === "function"
                ? valueOrUpdater(currentReserva)
                : valueOrUpdater;
            reservaRef.current = nextReserva;
            return nextReserva;
        });
    }, []);

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
                collections[key] = camelize(result.value);
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

    const fetchPassengers = useCallback(async () => {
        if (!reservaId) return [];
        const passengers = await api.get(`/reservas/${reservaId}/passengers`, { cache: "no-store" });
        return camelize(passengers || []);
    }, [reservaId]);

    const fetchPayments = useCallback(async () => {
        if (!reservaId) return [];
        const payments = await api.get(`/reservas/${reservaId}/payments`, { cache: "no-store" });
        return camelize(payments || []);
    }, [reservaId]);

    const fetchReserva = useCallback(async ({ showLoading = true, collectionKeys, strictCollections = false, service, serviceType, passenger, preserveOnError = false } = {}) => {
        if (!reservaId) return null;
        const fetchSequence = fetchSequenceRef.current + 1;
        fetchSequenceRef.current = fetchSequence;
        try {
            if (showLoading) {
                setLoading(true);
            }

            const [rawRes, passengerResult, paymentResult] = await Promise.all([
                api.get(`/reservas/${reservaId}`, { cache: "no-store" }),
                fetchPassengers()
                    .then((value) => ({ status: "fulfilled", value }))
                    .catch((reason) => ({ status: "rejected", reason })),
                fetchPayments()
                    .then((value) => ({ status: "fulfilled", value }))
                    .catch((reason) => ({ status: "rejected", reason })),
            ]);

            if (fetchSequence !== fetchSequenceRef.current) {
                return {
                    reserva: reservaRef.current,
                    serviceCollectionError: null,
                    collectionErrors: {},
                    ignored: true,
                };
            }

            const res = camelize(rawRes);
            const {
                collections,
                errors,
                strictError,
                collectionKeys: refreshedCollectionKeys,
            } = await fetchServiceCollections({
                keys: collectionKeys,
                strict: strictCollections,
            });

            if (fetchSequence !== fetchSequenceRef.current) {
                return {
                    reserva: reservaRef.current,
                    serviceCollectionError: null,
                    collectionErrors: {},
                    ignored: true,
                };
            }

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

            const currentPassengers = Array.isArray(reservaRef.current?.passengers)
                ? reservaRef.current.passengers
                : [];
            const responsePassengers = Array.isArray(res.passengers) ? res.passengers : [];
            const fetchedPassengers = passengerResult.status === "fulfilled"
                ? passengerResult.value
                : null;
            const shouldMergeCurrentPassengers = currentPassengers.length > 0
                && (Boolean(passenger) || Boolean(service) || Boolean(serviceType) || preserveOnError);
            const nextPassengers = fetchedPassengers
                ? (shouldMergeCurrentPassengers
                    ? mergePassengerCollections(currentPassengers, fetchedPassengers)
                    : fetchedPassengers)
                : (currentPassengers.length > 0 ? currentPassengers : responsePassengers);
            const responsePayments = Array.isArray(res.payments) ? res.payments : [];
            const fetchedPayments = paymentResult.status === "fulfilled"
                ? paymentResult.value
                : null;
            const nextPayments = fetchedPayments ?? responsePayments;

            const nextReserva = { ...res, ...collections, passengers: nextPassengers, payments: nextPayments };

            // Inject authoritative service data if provided (fixes RabbitMQ/Cache sync lag)
            if (service && serviceType) {
                const collectionKey = getReservationCollectionKeyForServiceType(serviceType);
                if (collectionKey) {
                    const currentCollection = nextReserva[collectionKey] || [];
                    const serviceId = getReservationServicePublicId(service);
                    const prevService = currentCollection.find(s => getReservationServicePublicId(s) === serviceId);

                    if (!prevService) {
                        nextReserva[collectionKey] = [...currentCollection, service];
                        // Add to totals
                        nextReserva.totalSale = (nextReserva.totalSale || 0) + (service.salePrice || 0);
                        nextReserva.totalCost = (nextReserva.totalCost || 0) + (service.netCost || 0);
                        nextReserva.balance = (nextReserva.balance || 0) + (service.salePrice || 0);
                    } else {
                        // Apply delta to totals
                        const deltaSale = (service.salePrice || 0) - (prevService.salePrice || 0);
                        const deltaCost = (service.netCost || 0) - (prevService.netCost || 0);
                        nextReserva.totalSale = (nextReserva.totalSale || 0) + deltaSale;
                        nextReserva.totalCost = (nextReserva.totalCost || 0) + deltaCost;
                        nextReserva.balance = (nextReserva.balance || 0) + deltaSale;

                        nextReserva[collectionKey] = currentCollection.map(s =>
                            getReservationServicePublicId(s) === serviceId ? service : s
                        );
                    }
                }
            }

            const hydratedReserva = passenger
                ? upsertPassengerInReservaSnapshot(nextReserva, passenger)
                : nextReserva;

            setReservaSnapshot(hydratedReserva);
            return {
                reserva: hydratedReserva,
                serviceCollectionError: strictError,
                collectionErrors: errors,
            };
        } catch (error) {
            console.error(error);
            showError("Error al cargar la reserva: " + getApiErrorMessage(error, "Error desconocido"));
            if (!preserveOnError) {
                setReservaSnapshot(null);
            }
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
    }, [reservaId, fetchServiceCollections, fetchPassengers, fetchPayments, setReservaSnapshot]);

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
            setReservaSnapshot((currentReserva) => removeServiceFromReservaSnapshot(currentReserva, service));
            const refreshResult = await fetchReserva({
                showLoading: false,
                collectionKeys: hasDedicatedCollection ? [collectionKey] : undefined,
                strictCollections: hasDedicatedCollection,
                preserveOnError: true,
            });
            const updatedReserva = refreshResult?.reserva || null;

            if (refreshResult?.serviceCollectionError) {
                setReservaSnapshot((currentReserva) => removeServiceFromReservaSnapshot(currentReserva, service));
                showSuccess("Servicio eliminado. Esa lista no se pudo refrescar en este momento.");
                return true;
            }

            if (updatedReserva && findNormalizedService(updatedReserva, service)) {
                setReservaSnapshot((currentReserva) => removeServiceFromReservaSnapshot(currentReserva, service));
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
            setReservaSnapshot((currentReserva) => removePassengerFromReservaSnapshot(currentReserva, passengerId));
            const refreshResult = await fetchReserva({ showLoading: false, preserveOnError: true });
            if (refreshResult?.reserva?.passengers?.some((passenger) => {
                const currentId = getPublicId(passenger);
                return String(currentId || "") === String(passengerId || "");
            })) {
                setReservaSnapshot((currentReserva) => removePassengerFromReservaSnapshot(currentReserva, passengerId));
            }
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
