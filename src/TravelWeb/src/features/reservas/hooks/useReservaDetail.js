import { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { camelize } from "../../../lib/utils";
import { cancellationsApi } from "../../cancellations/api/cancellationsApi";
import {
    findNormalizedService,
    getReservationCollectionKeyForRecordKind,
    getReservaCollectionLabel,
    getServiceMutationEndpoint,
    normalizeReservaServices,
    getReservationCollectionKeyForServiceType,
    getReservationServicePublicId
} from "../lib/reservationServiceModel";
// Tanda P4 "circuito proveedor" (2026-07-22): usamos este código SOLO para detectar el
// motivo (nunca adivinar por texto), el mismo que ya usan "anular servicio" (Tanda 7) y
// "bajar el estado" (Tanda P1, SupplierAccountPage). El backend documenta explícitamente
// que este PUT de status reusa el MISMO code (ver
// BookingCancellationService.EnsureServiceStatusDowngradeHasReceivableAnchorAsync).
// Fix B1 (post-E2E, 2026-07-22): acá NO se ofrece ningún botón de acción — en este flujo
// puntual la reserva siempre está en Presupuesto y ahí no hay ningún camino real para
// facturar; el aviso solo sirve para mostrar el mensaje largo del motor en ventana fija
// en vez de un toast (Aviso 1 del inventario, spec 2026-07-22).
// Obra "anular sin factura" (2026-07-23): comparamos el `code` DIRECTO (no vía
// resolverRechazoAnularServicio) porque ese mapeo ya no distingue "botón" — desde esta
// obra ningún code de esta lib ofrece botón, así que dejó de servir para detectar ESTE
// motivo puntual entre cualquier otro 409.
import { CODIGO_RECHAZO_ANULAR_SERVICIO } from "../lib/serviceCancellationGuard";

const SERVICE_COLLECTION_ENDPOINTS = Object.freeze({
    flightSegments: (reservaId) => `/reservas/${reservaId}/flights`,
    hotelBookings: (reservaId) => `/reservas/${reservaId}/hotels`,
    transferBookings: (reservaId) => `/reservas/${reservaId}/transfers`,
    packageBookings: (reservaId) => `/reservas/${reservaId}/packages`,
    // Asistencias: endpoint dedicado igual que los otros 4 tipos
    assistanceBookings: (reservaId) => `/reservas/${reservaId}/assistances`,
});

/**
 * Reemplaza un servicio en el snapshot local de la reserva sin hacer fetch.
 * Usado por confirm-cost: el backend devuelve el DTO actualizado y lo insertamos
 * directamente en la colección correcta (por recordKind), manteniendo el orden.
 *
 * Si el servicio no se encuentra en la colección (caso raro), se hace un upsert al final.
 */
function upsertServiceInReservaSnapshot(reserva, servicioActualizado, recordKind) {
    if (!reserva || !servicioActualizado) return reserva;

    const collectionKey = getReservationCollectionKeyForRecordKind(recordKind);
    if (!collectionKey || !Array.isArray(reserva[collectionKey])) return reserva;

    const servicioId = String(
        servicioActualizado.publicId ||
        servicioActualizado.PublicId ||
        servicioActualizado.id ||
        servicioActualizado.Id ||
        ""
    );

    const coleccionActual = reserva[collectionKey];
    const existeEnColeccion = coleccionActual.some((item) => {
        const itemId = String(item.publicId || item.PublicId || item.id || item.Id || "");
        return itemId === servicioId;
    });

    const coleccionActualizada = existeEnColeccion
        ? coleccionActual.map((item) => {
            const itemId = String(item.publicId || item.PublicId || item.id || item.Id || "");
            return itemId === servicioId ? servicioActualizado : item;
        })
        : [...coleccionActual, servicioActualizado];

    return { ...reserva, [collectionKey]: coleccionActualizada };
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
    // Tanda P4 (2026-07-22): cuando el cambio de estado (ej. "El cliente aceptó",
    // Presupuesto → En gestión) rebota por el candado de plata pagada al operador sin
    // factura, guardamos acá el mensaje real del motor en vez de mostrarlo como toast —
    // la página pinta un aviso fijo (sin botón: fix B1 post-E2E, la reserva sigue en
    // Presupuesto y ahí no hay ningún camino para facturar) con un botón "Entendido"
    // que solo lo cierra.
    const [statusChangeBlockedByMoneyGuard, setStatusChangeBlockedByMoneyGuard] = useState(null);
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
        const payments = await api.get(`/payments/reserva/${reservaId}`, { cache: "no-store" });
        return camelize(payments || []);
    }, [reservaId]);

    // silentErrors: para los refrescos AUTOMÁTICOS de fondo (ej. el polling del paso de multa).
    // Un tick de fondo que falla no debe gritarle "Error al cargar la reserva" al usuario cada
    // 10 segundos: la reserva sigue visible en pantalla y el próximo tick lo reintenta solo.
    // Los refrescos disparados por una ACCIÓN del usuario siguen avisando (default false).
    const fetchReserva = useCallback(async ({ showLoading = true, collectionKeys, strictCollections = false, service, serviceType, passenger, preserveOnError = false, silentErrors = false } = {}) => {
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

            // Inyecta el servicio recién guardado en su colección (fixes RabbitMQ/Cache sync lag):
            // la lista de servicios puede tardar un instante en reflejar el cambio, así que lo
            // insertamos/actualizamos nosotros para que aparezca de inmediato en la pantalla.
            //
            // Fix C3 (Tanda 6, 2026-07-05): ACÁ ANTES había un parche manual que sumaba/restaba
            // totalSale/totalCost/balance "a mano" usando los datos del servicio recién guardado.
            // Ese parche se ELIMINÓ: `res` (unas líneas más arriba) YA es la respuesta autoritativa
            // de GET /reservas/{id}, que YA incluye el servicio nuevo/editado en sus totales. Sumarle
            // un delta encima podía DOBLAR el número (bug real de la auditoría) hasta el próximo
            // fetch. La plata SIEMPRE sale del fetch autoritativo; acá solo tocamos la lista de
            // servicios para que la fila aparezca sin esperar a un segundo round-trip.
            if (service && serviceType) {
                const collectionKey = getReservationCollectionKeyForServiceType(serviceType);
                if (collectionKey) {
                    const currentCollection = nextReserva[collectionKey] || [];
                    const serviceId = getReservationServicePublicId(service);
                    const prevService = currentCollection.find(s => getReservationServicePublicId(s) === serviceId);

                    nextReserva[collectionKey] = prevService
                        ? currentCollection.map(s => getReservationServicePublicId(s) === serviceId ? service : s)
                        : [...currentCollection, service];
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
            if (!silentErrors) {
                showError("Error al cargar la reserva: " + getApiErrorMessage(error, "Error desconocido"));
            }
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
        if (reserva?.status === 'Confirmed' && newStatus === 'Budget') {
            if (reserva.payments?.length > 0) {
                showError("No se puede volver a Presupuesto: hay pagos registrados.");
                return;
            }
            if (reserva.invoices?.length > 0) {
                showError("No se puede volver a Presupuesto: hay facturas emitidas.");
                return;
            }
        }

        // Limpiamos el aviso fijo anterior (si lo había) antes de reintentar: no queremos
        // que quede pegado un aviso viejo si este intento nuevo sale bien o falla por otro motivo.
        setStatusChangeBlockedByMoneyGuard(null);

        try {
            await api.put(`/reservas/${reservaId}/status`, { status: newStatus });
            await fetchReserva();
            showSuccess(`Estado actualizado`);
            return true;
        } catch (error) {
            // Candado de plata pagada al operador sin factura (mismo code que "anular
            // servicio"): un toast que desaparece no alcanza — el mensaje del motor trae
            // la instrucción completa y el usuario necesita leerla con calma en un aviso
            // fijo (sin botón: en Presupuesto no se puede facturar todavía, fix B1 P4).
            // El resto de los errores sigue mostrándose como toast, igual que antes.
            if (error?.payload?.code === CODIGO_RECHAZO_ANULAR_SERVICIO.PAGO_SIN_FACTURA) {
                setStatusChangeBlockedByMoneyGuard({
                    mensaje: getApiErrorMessage(error, "Error al cambiar estado"),
                });
            } else {
                showError(getApiErrorMessage(error, "Error al cambiar estado"));
            }
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

    /**
     * Cancela un servicio ya confirmado por el operador.
     *
     * Decisión #9 (guia UX 2026-06-08): el servicio queda con workflowStatus="Cancelado"
     * (tachado en la lista) en vez de desaparecer. Hubo compromiso real con el operador.
     *
     * ADR-025: migrado de PATCH /{tipo}-bookings/{id}/status a POST /cancellations/cancel-service.
     * El nuevo endpoint pasa por el candado fiscal del backend: si hay factura con CAE viva
     * o voucher emitido, devuelve 409 con un mensaje descriptivo (el llamador debe mostrarlo
     * en un modal explicativo, no en un toast genérico).
     *
     * @param {object} service - El servicio normalizado (con recordKind).
     * @param {string|null} motivo - Motivo de la cancelación (obligatorio en backend: mín 10 chars).
     *   Si viene null o vacío, mostramos error de validación antes de llamar al API.
     * @returns {{ ok: boolean, result?: CancelServiceResultDto, error?: Error }}
     *   Retornamos el objeto para que el llamador pueda actualizar el contador "N de M"
     *   sin hacer un fetch completo (los contadores vienen en el resultado).
     */
    const handleCancelService = async (service, motivo = null, creditSelection = null) => {
        // Mapeo de recordKind (front) → serviceTable (backend).
        // Verificado contra CancelServiceRequest del backend (CancellationDtos.cs).
        const RECORD_KIND_TO_SERVICE_TABLE = {
            hotel: 'Hotel',
            flight: 'Flight',
            transfer: 'Transfer',
            package: 'Package',
            assistance: 'Assistance',
            generic: 'Generic',
        };

        const serviceTable = RECORD_KIND_TO_SERVICE_TABLE[service?.recordKind];
        if (!serviceTable) {
            showError('Este tipo de servicio no se puede cancelar desde aquí.');
            return { ok: false };
        }

        const servicePublicId = service.publicId || service.id;
        if (!servicePublicId) {
            showError('No se pudo identificar el servicio para cancelarlo.');
            return { ok: false };
        }

        // El motivo es obligatorio en el backend (mínimo 10 caracteres, máximo 1000).
        // La validación del largo mínimo la hace el modal antes de llamar a este hook,
        // así que acá solo saneamos espacios y confiamos en que el valor ya cumple la regla.
        const motivoFinal = motivo ? motivo.trim() : '';

        try {
            const result = await cancellationsApi.cancelService({
                reservaPublicId: reservaId,
                serviceTable,
                servicePublicId,
                reason: motivoFinal,
                ...(creditSelection?.targetInvoicePublicId
                    ? { targetInvoicePublicId: creditSelection.targetInvoicePublicId }
                    : {}),
                ...(Number(creditSelection?.confirmedGrossCreditAmount) > 0
                    ? { confirmedGrossCreditAmount: Number(creditSelection.confirmedGrossCreditAmount) }
                    : {}),
            });

            // Recargamos la colección del tipo de servicio para que el workflowStatus
            // "Cancelado" aparezca en la fila y el tachado sea visible de inmediato.
            const collectionKey = getReservationCollectionKeyForRecordKind(service?.recordKind);
            await fetchReserva({
                showLoading: false,
                collectionKeys: collectionKey ? [collectionKey] : undefined,
                preserveOnError: true,
            });

            showSuccess('Servicio anulado. Quedó tachado en la lista de la reserva.');
            return { ok: true, result };
        } catch (error) {
            // Devolvemos el error para que el llamador decida cómo mostrarlo.
            // 409 = bloqueo fiscal (factura con CAE viva / voucher emitido) → modal explicativo.
            // Otros errores → el llamador puede mostrar toast si quiere.
            return { ok: false, error };
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

    const capacity = useMemo(() => {
        if (!reserva) return { hotel: 0, transfer: 0, package: 0, total: 0 };

        let hotel = 0;
        reserva.hotelBookings?.forEach(h => {
            // Si el booking tiene Adults/Children explicitos, usar esos. Sino fallback a roomType * rooms.
            const explicit = (h.adults || 0) + (h.children || 0);
            if (explicit > 0) {
                hotel += explicit;
            } else {
                const type = h.roomType?.toLowerCase() || '';
                if (type.includes('sing')) hotel += h.rooms;
                else if (type.includes('trip')) hotel += (3 * h.rooms);
                else if (type.includes('quad')) hotel += (4 * h.rooms);
                else hotel += (2 * h.rooms);
            }
        });

        const transfer = (reserva.transferBookings || []).reduce((max, t) => Math.max(max, t.passengers || 0), 0);
        const pkg = (reserva.packageBookings || []).reduce((sum, p) => sum + ((p.adults || 0) + (p.children || 0)), 0);
        // La asistencia tiene adultos + menores cubiertos por la poliza
        const assistance = (reserva.assistanceBookings || []).reduce((sum, a) => sum + ((a.adults || 0) + (a.children || 0)), 0);

        return {
            hotel,
            transfer,
            package: pkg,
            assistance,
            total: Math.max(hotel, Math.max(transfer, Math.max(pkg, assistance)))
        };
    }, [reserva]);

    /**
     * Reemplaza un servicio en el estado local de la reserva con el DTO actualizado
     * devuelto por confirm-cost. No dispara un fetch; la UI se actualiza instantáneamente.
     *
     * También ajusta totalCost del snapshot para que la tarjeta "Inversión (Costo)"
     * (ReservaSummaryStrip) muestre el total correcto sin necesidad de hacer un fetch.
     * Solo ajusta el costo; salePrice y balance NO se tocan (confirm-cost no los modifica).
     *
     * Patrón copiado del path de inyección de fetchReserva (~:309-313), que ya hace
     * el mismo cálculo de delta para evitar un totalizador desactualizado.
     *
     * @param {object} servicioActualizado — DTO devuelto por POST .../confirm-cost
     * @param {string} recordKind — tipo del servicio ("hotel", "flight", etc.)
     */
    const handleServiceUpdated = useCallback((servicioActualizado, recordKind) => {
        setReservaSnapshot((currentReserva) => {
            // Buscar el servicio anterior en el snapshot usando los mismos fallbacks de id
            // que usa upsertServiceInReservaSnapshot (publicId / PublicId / id / Id)
            const collectionKey = getReservationCollectionKeyForRecordKind(recordKind);
            const servicioId = String(
                servicioActualizado.publicId ||
                servicioActualizado.PublicId ||
                servicioActualizado.id ||
                servicioActualizado.Id ||
                ""
            );

            const servicioViejo = collectionKey && Array.isArray(currentReserva?.[collectionKey])
                ? currentReserva[collectionKey].find((item) => {
                    const itemId = String(item.publicId || item.PublicId || item.id || item.Id || "");
                    return itemId === servicioId;
                })
                : null;

            // Hacer el upsert del servicio en la colección correspondiente
            const reservaConServicioActualizado = upsertServiceInReservaSnapshot(
                currentReserva,
                servicioActualizado,
                recordKind
            );

            // Si encontramos el servicio viejo, ajustamos totalCost por la diferencia.
            // Si no lo encontramos (caso agregar), no tocamos el total: el comportamiento
            // es el mismo que antes de este fix (sin ajuste).
            if (!servicioViejo || !reservaConServicioActualizado) {
                return reservaConServicioActualizado;
            }

            const deltaCosto = (servicioActualizado.netCost || 0) - (servicioViejo.netCost || 0);

            return {
                ...reservaConServicioActualizado,
                totalCost: (reservaConServicioActualizado.totalCost || 0) + deltaCosto,
            };
        });
    }, [setReservaSnapshot]);

    return {
        reserva,
        loading,
        suppliers,
        serviceCollectionErrors,
        fetchReserva,
        handleArchiveReserva,
        handleDeleteReserva,
        handleStatusChange,
        statusChangeBlockedByMoneyGuard,
        setStatusChangeBlockedByMoneyGuard,
        handleDeleteService,
        handleCancelService,
        handleDeletePassenger,
        handleServiceUpdated,
        allServices,
        capacity,
        // Backwards compat
        hotelCapacity: capacity.hotel
    };
}
