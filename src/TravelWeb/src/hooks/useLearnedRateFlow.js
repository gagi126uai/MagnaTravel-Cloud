/**
 * Hook que encapsula el flujo "tarifa aprendida":
 * cuando el admin carga un servicio a mano y tilda "Guardar esta tarifa para reusar",
 * este hook se encarga de:
 *   1. Llamar a /rates/duplicate-check con los datos del servicio.
 *   2. Si hay coincidencias (exactas o aproximadas), abrir el modal de duplicados.
 *   3. Si NO hay coincidencias, crear la tarifa directo via POST /rates.
 *
 * IMPORTANTE — este flujo es BEST-EFFORT:
 *   El servicio ya fue guardado antes de que esto corra.
 *   Si cualquier llamada a /rates falla, el error se muestra como notificacion no bloqueante
 *   pero el servicio creado NO se revierte.
 *
 * Uso tipico:
 *   const { duplicateModalProps, runLearnedRateFlow } = useLearnedRateFlow();
 *   // despues del api.post exitoso:
 *   await runLearnedRateFlow({ serviceType, form });
 *   // en el JSX:
 *   <RateDuplicateModal {...duplicateModalProps} />
 */
import { useState, useCallback } from "react";
import { api } from "../api";
import { showSuccess, showError } from "../alerts";

// ──────────────────────────────────────────────────────────────
// Helpers para armar name + fingerprint por tipo de servicio
// ──────────────────────────────────────────────────────────────

// El form del servicio usa etiquetas en español para el regimen de comidas
// ("Desayuno", "Media Pension"...), pero el tarifario (Rate) guarda codigos
// canonicos ("BB", "HB"...). Si guardaramos la etiqueta cruda, ensuciariamos el
// tarifario (mezcla etiquetas y codigos) y la deteccion de duplicados de hotel
// fallaria (compara mealPlan exacto). Por eso traducimos antes de comparar y de crear.
const MEAL_PLAN_CODE = {
    "solo alojamiento": "RO",
    "desayuno": "BB",
    "media pension": "HB",
    "pension completa": "FB",
    "all inclusive": "AI",
};

function toMealPlanCode(value) {
    if (!value) return null;
    // Normalizamos (minusculas + sin tildes) para tolerar variaciones de tipeo.
    const key = value
        .toString()
        .trim()
        .toLowerCase()
        .normalize("NFD")
        .replace(/[̀-ͯ]/g, "");
    // Si ya viene como codigo o es un valor desconocido, lo dejamos tal cual.
    return MEAL_PLAN_CODE[key] || value;
}

/**
 * Calcula el "nombre de tarifa" segun el tipo de servicio.
 * Este nombre se usa para el campo productName del Rate y para la busqueda de duplicados.
 */
function buildRateName(serviceType, form) {
    switch (serviceType) {
        case "Hotel":
            // Para hotel el nombre canónico es el nombre del hotel
            return form.hotelName || "Hotel sin nombre";

        case "Aereo":
            // Para vuelo: "Aerolineas Argentinas BUE→MIA" o simplemente el origen/destino
            if (form.airlineName && form.origin && form.destination) {
                return `${form.airlineName} ${form.origin}→${form.destination}`;
            }
            if (form.origin && form.destination) {
                return `${form.origin}→${form.destination}`;
            }
            return form.airlineName || "Vuelo sin nombre";

        case "Traslado":
            // Para traslado: "EZE → Sheraton Retiro"
            if (form.pickupLocation && form.dropoffLocation) {
                return `${form.pickupLocation} → ${form.dropoffLocation}`;
            }
            return form.pickupLocation || form.dropoffLocation || "Traslado sin nombre";

        case "Paquete":
            return form.packageName || form.destination || "Paquete sin nombre";

        case "Asistencia":
            // La asistencia no tiene productName propio en Rate, usamos el tipo de plan
            return form.planType || "Asistencia al viajero";

        default:
            return form.description || "Servicio sin nombre";
    }
}

/**
 * Arma el objeto fingerprint para el duplicate-check segun el tipo de servicio.
 * Solo se incluyen los campos relevantes al tipo — el resto va null o undefined.
 */
function buildFingerprint(serviceType, form) {
    switch (serviceType) {
        case "Hotel":
            return {
                roomType: form.roomType || null,
                mealPlan: toMealPlanCode(form.mealPlan),
                roomCategory: null, // ServiceFormModal no tiene roomCategory
            };

        case "Aereo":
            return {
                origin: form.origin || null,
                destination: form.destination || null,
                airline: form.airlineName || null,
            };

        case "Traslado":
            return {
                pickupLocation: form.pickupLocation || null,
                dropoffLocation: form.dropoffLocation || null,
                vehicleType: form.vehicleType || null,
                isRoundTrip: !!form.isRoundTrip,
            };

        case "Paquete":
            return {
                destination: form.destination || null,
            };

        case "Asistencia":
            // Rate no tiene campos propios para asistencia — la fingerprintea por zona de cobertura
            return {
                destination: form.coverageZone || null,
            };

        default:
            return {};
    }
}

/**
 * Construye el body del POST /rates segun el tipo de servicio.
 * Mapeamos los campos del form de ServiceFormModal al shape que espera la API de tarifario.
 * NO incluimos "commission" — el backend la recalcula solo.
 */
function buildRatePayload(serviceType, form) {
    const rateName = buildRateName(serviceType, form);

    const basePayload = {
        serviceType,
        productName: rateName,
        supplierId: form.supplierId || null,
        netCost: Number(form.netCost) || 0,
        salePrice: Number(form.salePrice) || 0,
        // tax: si el servicio no tiene campo tax, mandamos 0 (el backend lo acepta)
        tax: Number(form.tax) || 0,
        // currency: solo si el form tiene moneda definida (multimoneda futuro)
        ...(form.currency ? { currency: form.currency } : {}),
        isActive: true,
    };

    switch (serviceType) {
        case "Hotel":
            return {
                ...basePayload,
                hotelName: form.hotelName || null,
                city: form.city || null,
                starRating: form.starRating ? Number(form.starRating) : null,
                roomType: form.roomType || null,
                mealPlan: toMealPlanCode(form.mealPlan),
                roomCategory: null, // ServiceFormModal no tiene roomCategory
            };

        case "Aereo":
            return {
                ...basePayload,
                // El tarifario usa el campo "airline" (no "airlineName" como en el form del servicio)
                airline: form.airlineName || null,
                airlineCode: form.airlineCode || null,
                origin: form.origin || null,
                destination: form.destination || null,
                cabinClass: form.cabinClass || null,
                baggageIncluded: form.baggage || null,
            };

        case "Traslado":
            return {
                ...basePayload,
                pickupLocation: form.pickupLocation || null,
                dropoffLocation: form.dropoffLocation || null,
                vehicleType: form.vehicleType || null,
                isRoundTrip: !!form.isRoundTrip,
                maxPassengers: form.passengers ? Number(form.passengers) : null,
            };

        case "Paquete":
            return {
                ...basePayload,
                destination: form.destination || null,
                // includes* son booleanos — se mapean directo
                includesFlight: !!form.includesFlight,
                includesHotel: !!form.includesHotel,
                includesTransfer: !!form.includesTransfer,
                includesExcursions: !!form.includesExcursions,
                durationDays: form.durationDays ? Number(form.durationDays) : null,
                itinerary: form.itinerary || null,
            };

        case "Asistencia":
            // Rate no tiene campos dedicados para asistencia; usamos description para planType/zona
            return {
                ...basePayload,
                description: [form.planType, form.coverageZone].filter(Boolean).join(" - ") || null,
            };

        default:
            return basePayload;
    }
}

// ──────────────────────────────────────────────────────────────
// El hook en si
// ──────────────────────────────────────────────────────────────

export function useLearnedRateFlow() {
    // Estado del modal de duplicados
    const [duplicateModalIsOpen, setDuplicateModalIsOpen] = useState(false);
    const [duplicateData, setDuplicateData] = useState({
        exactMatch: null,
        fuzzyMatches: [],
        pendingPayload: null,   // el body del POST /rates que quedaria pendiente si el admin elige "Crear nueva"
        newNetCost: 0,
        newSalePrice: 0,
    });
    const [isActionLoading, setIsActionLoading] = useState(false);

    /**
     * Cierra el modal de duplicados y limpia el estado pendiente.
     * Se llama en "Usar esa" o despues de cualquier accion completada.
     */
    const closeDuplicateModal = useCallback(() => {
        setDuplicateModalIsOpen(false);
        setDuplicateData({
            exactMatch: null,
            fuzzyMatches: [],
            pendingPayload: null,
            newNetCost: 0,
            newSalePrice: 0,
        });
    }, []);

    /**
     * Accion "Actualizar precio" del modal:
     * llama PUT /rates/{publicId} con netCost/salePrice/tax del servicio que se acaba de cargar.
     * Es best-effort: si falla, muestra error no bloqueante.
     */
    const handleUpdateRate = useCallback(async (selectedMatch) => {
        if (!selectedMatch?.publicId || !duplicateData.pendingPayload) return;

        setIsActionLoading(true);
        try {
            await api.put(`/rates/${selectedMatch.publicId}`, {
                netCost: duplicateData.pendingPayload.netCost,
                salePrice: duplicateData.pendingPayload.salePrice,
                tax: duplicateData.pendingPayload.tax,
            });
            showSuccess("Precio de tarifa actualizado en el tarifario");
            closeDuplicateModal();
        } catch (error) {
            // No bloqueamos al usuario — el servicio ya fue creado exitosamente
            showError(error.message || "No se pudo actualizar la tarifa, pero el servicio fue guardado");
            closeDuplicateModal();
        } finally {
            setIsActionLoading(false);
        }
    }, [duplicateData.pendingPayload, closeDuplicateModal]);

    /**
     * Accion "Crear nueva igual" del modal:
     * llama POST /rates con el payload armado a partir del form del servicio.
     * Es best-effort: si falla, muestra error no bloqueante.
     */
    const handleCreateRate = useCallback(async () => {
        if (!duplicateData.pendingPayload) return;

        setIsActionLoading(true);
        try {
            await api.post("/rates", duplicateData.pendingPayload);
            showSuccess("Tarifa guardada para reusar");
            closeDuplicateModal();
        } catch (error) {
            showError(error.message || "No se pudo guardar la tarifa, pero el servicio fue creado");
            closeDuplicateModal();
        } finally {
            setIsActionLoading(false);
        }
    }, [duplicateData.pendingPayload, closeDuplicateModal]);

    /**
     * Punto de entrada del flujo "tarifa aprendida".
     * Se llama DESPUES de que el servicio fue guardado exitosamente.
     *
     * Pasos:
     *   1. Armar name + fingerprint + payload a partir del form.
     *   2. Llamar POST /rates/duplicate-check.
     *   3a. Si hay coincidencias → abrir modal.
     *   3b. Si no hay coincidencias → crear tarifa directamente con POST /rates.
     *
     * Cualquier error en este flujo es no bloqueante.
     */
    const runLearnedRateFlow = useCallback(async ({ serviceType, form }) => {
        const rateName = buildRateName(serviceType, form);
        const fingerprint = buildFingerprint(serviceType, form);
        const ratePayload = buildRatePayload(serviceType, form);

        let duplicateCheckResult = null;

        // Paso 1: chequear duplicados
        try {
            duplicateCheckResult = await api.post("/rates/duplicate-check", {
                serviceType,
                supplierId: form.supplierId || null,
                name: rateName,
                fingerprint,
            });
        } catch (error) {
            // Si el duplicate-check falla, intentamos crear la tarifa igual (degrada a best-effort)
            console.warn("[useLearnedRateFlow] duplicate-check fallo, intentando crear tarifa directo:", error.message);
        }

        const exactMatch = duplicateCheckResult?.exactMatch || null;
        const fuzzyMatches = duplicateCheckResult?.fuzzyMatches || [];
        const hasAnyMatch = exactMatch !== null || fuzzyMatches.length > 0;

        if (hasAnyMatch) {
            // Paso 2: hay coincidencias → abrir modal para que el admin decida
            setDuplicateData({
                exactMatch,
                fuzzyMatches,
                pendingPayload: ratePayload,
                newNetCost: Number(form.netCost) || 0,
                newSalePrice: Number(form.salePrice) || 0,
            });
            setDuplicateModalIsOpen(true);
            return;
        }

        // Paso 3: sin coincidencias → crear tarifa directamente, sin modal
        try {
            await api.post("/rates", ratePayload);
            showSuccess("Tarifa guardada para reusar");
        } catch (error) {
            // Best-effort: el servicio ya fue creado, este error es secundario
            showError(error.message || "No se pudo guardar la tarifa, pero el servicio fue creado");
        }
    }, []);

    /**
     * Props listos para pasarle al componente RateDuplicateModal.
     * Uso: <RateDuplicateModal {...duplicateModalProps} />
     */
    const duplicateModalProps = {
        isOpen: duplicateModalIsOpen,
        onClose: closeDuplicateModal,
        onUpdate: handleUpdateRate,
        onCreate: handleCreateRate,
        exactMatch: duplicateData.exactMatch,
        fuzzyMatches: duplicateData.fuzzyMatches,
        isLoading: isActionLoading,
        newNetCost: duplicateData.newNetCost,
        newSalePrice: duplicateData.newSalePrice,
    };

    return {
        duplicateModalProps,
        runLearnedRateFlow,
    };
}
