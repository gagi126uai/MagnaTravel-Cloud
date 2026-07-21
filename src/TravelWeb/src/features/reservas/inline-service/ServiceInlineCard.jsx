/**
 * Ficha de carga en línea de servicios de una reserva.
 * Reemplaza al modal ServiceFormModal cuando el flag EnableCatalogFindOrCreate está ON.
 *
 * Se abre DEBAJO de la lista de servicios (inline, sin ventana emergente).
 * Al guardar, la ficha se cierra y el servicio aparece como una fila más.
 *
 * Pestañas: Hotel | Aéreo | Traslado | Paquete | Asistencia
 * F2 parte 2: todos los tipos implementados (el genérico/ServicioReserva queda en modal viejo).
 *
 * Flujo de guardado por tipo:
 *   - Producto existente (rateId): POST /reservas/{id}/{tipo} con rateId
 *   - Producto nuevo (newCatalogProduct): POST con newCatalogProduct (mutuamente excluyente con rateId)
 *   - Editar (serviceToEdit): PUT /reservas/{id}/{tipo}/{serviceId}
 *
 * Si el guardado falla, la ficha queda abierta con los datos intactos y muestra
 * un cartel rojo arriba de los botones (nunca se pierde lo cargado — guía UX ronda 2).
 *
 * P3 "circuito proveedor" (spec 2026-07-22): al editar, si el costo nuevo queda por
 * debajo de lo ya pagado al operador, el motor no bloquea pero pide confirmar (409 +
 * code COST_BELOW_PAID_CONFIRMATION_REQUIRED) — se muestra un cartel ÁMBAR de aviso
 * (distinto del rojo de error) con "Volver a corregir" / "Sí, confirmar".
 */

import { useState, useCallback } from "react";
import { Hotel, Plane, Car, Package, ShieldCheck, AlertCircle, AlertTriangle, FileText } from "lucide-react";
import { hasPermission } from "../../../auth";
import { api } from "../../../api";
import { getApiErrorMessage } from "../../../lib/errors";
import { getReservationServicePublicId } from "../lib/reservationServiceModel";
import { resolverRechazoAnularServicio } from "../lib/serviceCancellationGuard";
import { esRechazoCostoMenorAPagado, agregarConfirmacionCostoMenorAPagado } from "../lib/costConfirmationGuard";
import { HotelInlineForm, calcularNoches, redondearDinero, formatearPrecio } from "./HotelInlineForm";
import { FlightInlineForm, calcularTotalesVuelo } from "./FlightInlineForm";
import { TransferInlineForm, calcularTotalesTraslado } from "./TransferInlineForm";
import { PackageInlineForm, calcularTotalesPaquete } from "./PackageInlineForm";
import { AssistanceInlineForm, calcularTotalesAsistencia } from "./AssistanceInlineForm";

// ─── Configuración de pestañas ────────────────────────────────────────────────

const TABS = [
    { id: "Hotel", label: "Hotel", icon: Hotel },
    { id: "Aereo", label: "Aéreo", icon: Plane },
    { id: "Traslado", label: "Traslado", icon: Car },
    { id: "Paquete", label: "Paquete", icon: Package },
    { id: "Asistencia", label: "Asistencia", icon: ShieldCheck },
];

// ─── Mapa de tipo de pestaña → segmento de endpoint ──────────────────────────

// Necesario para construir las URLs de POST/PUT de cada tipo.
// El genérico (ServicioReserva) queda en el modal viejo y NO aparece aquí.
const TAB_ENDPOINTS = {
    Hotel: "hotels",
    Aereo: "flights",
    Traslado: "transfers",
    Paquete: "packages",
    Asistencia: "assistances",
};

// ─── Id del campo "Costo" por pestaña (P3, spec 2026-07-22) ──────────────────

// Cuando el vendedor elige "Volver a corregir" en el aviso de costo por debajo de lo
// pagado, el foco tiene que volver al campo de costo — pero cada tipo de servicio lo
// llama distinto (ver los 5 Inline*Form). Este mapa evita hardcodear el id en el handler.
const CAMPO_COSTO_POR_TAB = {
    Hotel: "hotel-costo-noche",
    Aereo: "flight-costo",
    Traslado: "transfer-costo",
    Paquete: "package-costo-persona",
    Asistencia: "assistance-costo",
};

// â”€â”€â”€ Estado inicial por tipo â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

// Traductor de valores guardados con el vocabulario de la version anterior de esta ficha
// (commits previos al fix de Ronda 7): el select nuevo usa los valores canonicos del modal
// viejo; un valor fuera de la lista dejaria el select controlado EN BLANCO al editar.
// Valores desconocidos caen al default canonico (Desayuno / Doble).
const MEAL_PLAN_CANONICOS = ["Solo Alojamiento", "Desayuno", "Media Pension", "Pension Completa", "All Inclusive"];
const MEAL_PLAN_LEGACY = {
    SinDesayuno: "Solo Alojamiento",
    MediaPension: "Media Pension",
    PensionCompleta: "Pension Completa",
    TodoIncluido: "All Inclusive",
};
const ROOM_TYPE_CANONICOS = ["Single", "Doble", "Triple", "Cuadruple", "Familiar"];
// Solo equivalencias INEQUIVOCAS. "Suite" y "FamiliarCuadruple" no tienen equivalente claro
// en la lista canonica -> caen al default (son datos de prueba pre-lanzamiento; si algun dia
// importara, la decision de equivalencia es del dueño, no nuestra).
const ROOM_TYPE_LEGACY = {
    Simple: "Single",
};

function normalizarMealPlan(valor) {
    if (!valor) return "Desayuno";
    if (MEAL_PLAN_CANONICOS.includes(valor)) return valor;
    return MEAL_PLAN_LEGACY[valor] || "Desayuno";
}

function normalizarRoomType(valor) {
    if (!valor) return "Doble";
    if (ROOM_TYPE_CANONICOS.includes(valor)) return valor;
    return ROOM_TYPE_LEGACY[valor] || "Doble";
}

function buildHotelFormInitial(serviceToEdit) {
    if (!serviceToEdit) {
        return {
            hotelName: "", city: "", checkIn: "", checkOut: "",
            passengers: "", rooms: 1, supplierId: "",
            unitNetCost: "", unitSalePrice: "", currency: "ARS",
            // Defaults que coinciden con el modal viejo y con el backend (no-nullables).
            // Los selects siempre muestran un valor, así que estos nunca quedan vacíos.
            mealPlan: "Desayuno",
            roomType: "Doble",
            confirmationNumber: "",
            // operatorPaymentDeadline eliminado en F2: el aviso de campanita viene del backend (firstStartDate).
            address: "",
            rateId: null, newCatalogProduct: null,
        };
    }
    const noches = calcularNoches(serviceToEdit.checkIn, serviceToEdit.checkOut);
    const habitaciones = Math.max(serviceToEdit.rooms || 1, 1);
    const divisor = Math.max(noches, 1) * habitaciones;
    return {
        hotelName: serviceToEdit.hotelName || serviceToEdit.name || "",
        city: serviceToEdit.city || "",
        checkIn: (serviceToEdit.checkIn || "").split("T")[0] || "",
        checkOut: (serviceToEdit.checkOut || "").split("T")[0] || "",
        passengers: serviceToEdit.paxCount || serviceToEdit.adults || serviceToEdit.passengers || "",
        rooms: habitaciones,
        supplierId: serviceToEdit.supplierId || serviceToEdit.supplierPublicId || "",
        unitNetCost: noches > 0 ? String(redondearDinero((serviceToEdit.netCost || 0) / divisor)) : "",
        unitSalePrice: noches > 0 ? String(redondearDinero((serviceToEdit.salePrice || 0) / divisor)) : "",
        currency: serviceToEdit.currency || "ARS",
        // Al editar: cargar el valor persistido NORMALIZADO al vocabulario canonico del select
        // (servicios guardados con la version anterior de la ficha pueden traer valores legacy
        // que dejarian el select controlado en blanco). Fallback al default del modal viejo.
        mealPlan: normalizarMealPlan(serviceToEdit.mealPlan),
        roomType: normalizarRoomType(serviceToEdit.roomType),
        confirmationNumber: serviceToEdit.confirmationNumber || "",
        // operatorPaymentDeadline no se carga en la UI (campo eliminado en F2)
        address: serviceToEdit.address || "",
        rateId: serviceToEdit.rateId || null,
        newCatalogProduct: null,
    };
}

function buildFlightFormInitial(serviceToEdit) {
    if (!serviceToEdit) {
        return {
            routeName: "", supplierId: "", departureDate: "", returnDate: "",
            passengers: "", netCost: "", salePrice: "", currency: "ARS",
            // emissionDeadline eliminado en F2: el aviso de campanita viene del backend (firstStartDate).
            pnr: "", ticketNumber: "", scheduleNotes: "",
            baggage: "",
            // cabinClass: "" = "Sin especificar" (igual que el modal viejo). El select
            // siempre tiene opciones, así que "" nunca queda colgado.
            cabinClass: "",
            rateId: null, newCatalogProduct: null,
        };
    }
    return {
        // ADR-018: la identidad del vuelo se guarda en productName (no en description).
        // Fallback a description/name para servicios cargados antes de ADR-018.
        routeName: serviceToEdit.productName || serviceToEdit.description || serviceToEdit.routeName || serviceToEdit.name || "",
        supplierId: serviceToEdit.supplierId || serviceToEdit.supplierPublicId || "",
        // Las fechas del vuelo vienen como datetime (con hora); tomamos solo la parte de fecha
        departureDate: (serviceToEdit.departureTime || serviceToEdit.departureDate || "").split("T")[0] || "",
        returnDate: (serviceToEdit.arrivalTime || serviceToEdit.returnDate || "").split("T")[0] || "",
        passengers: serviceToEdit.passengerCount || serviceToEdit.passengers || "",
        netCost: String(serviceToEdit.netCost || ""),
        salePrice: String(serviceToEdit.salePrice || ""),
        currency: serviceToEdit.currency || "ARS",
        // emissionDeadline no se carga en la UI (campo eliminado en F2)
        pnr: serviceToEdit.pnr || "",
        ticketNumber: serviceToEdit.ticketNumber || "",
        scheduleNotes: serviceToEdit.scheduleNotes || serviceToEdit.notes || "",
        baggage: serviceToEdit.baggage || "",
        // Round-trip: el backend devuelve cabinClass en FlightSegmentDto; fallback "" (Sin especificar).
        cabinClass: serviceToEdit.cabinClass || "",
        rateId: serviceToEdit.rateId || null,
        newCatalogProduct: null,
    };
}

function buildTransferFormInitial(serviceToEdit) {
    if (!serviceToEdit) {
        return {
            routeName: "", supplierId: "",
            pickupDate: "",
            // movementType almacena el valor "in"/"out" del campo direction del backend.
            // El select de "Llegada o salida" usa estos mismos valores en su atributo value.
            movementType: "",
            passengers: "",
            // transferType almacena el valor "private"/"shared" del campo serviceMode del backend.
            // El select de "Modalidad" usa estos mismos valores en su atributo value.
            transferType: "",
            netCost: "", salePrice: "",
            currency: "ARS", associatedFlightNumber: "", pickupTime: "",
            confirmationNumber: "",
            // vehicleType: texto libre, igual que el modal viejo. "" = no especificado.
            vehicleType: "",
            rateId: null, newCatalogProduct: null,
        };
    }
    return {
        // ADR-018: la identidad del traslado se guarda en productName (no en description).
        // Fallback a description/name para servicios cargados antes de ADR-018.
        routeName: serviceToEdit.productName || serviceToEdit.description || serviceToEdit.routeName || serviceToEdit.name || "",
        supplierId: serviceToEdit.supplierId || serviceToEdit.supplierPublicId || "",
        pickupDate: (serviceToEdit.pickupDateTime || "").split("T")[0] || "",
        // Round-trip: el backend devuelve direction ("in"/"out") en TransferBookingDto
        movementType: serviceToEdit.direction || "",
        passengers: serviceToEdit.passengers || "",
        // Round-trip: el backend devuelve serviceMode ("private"/"shared") en TransferBookingDto
        transferType: serviceToEdit.serviceMode || "",
        netCost: String(serviceToEdit.netCost || ""),
        salePrice: String(serviceToEdit.salePrice || ""),
        currency: serviceToEdit.currency || "ARS",
        associatedFlightNumber: serviceToEdit.flightNumber || serviceToEdit.associatedFlightNumber || "",
        // Extraemos hora del datetime sin convertir a UTC (hora de pared)
        pickupTime: (() => {
            const dt = serviceToEdit.pickupDateTime || "";
            const tIdx = dt.indexOf("T");
            return tIdx >= 0 ? dt.slice(tIdx + 1, tIdx + 6) : "";
        })(),
        confirmationNumber: serviceToEdit.confirmationNumber || "",
        // Round-trip: el backend devuelve vehicleType en TransferBookingDto; fallback "" (no especificado).
        vehicleType: serviceToEdit.vehicleType || "",
        rateId: serviceToEdit.rateId || null,
        newCatalogProduct: null,
    };
}

function buildPackageFormInitial(serviceToEdit) {
    if (!serviceToEdit) {
        return {
            packageName: "", supplierId: "", startDate: "",
            // endDate es opcional; se inicializa vacío (paquetes sin fecha de fin cargada).
            endDate: "",
            passengers: "",
            // roomBase almacena el valor "double"/"triple"/etc del campo occupancyBase del backend.
            // El select de "Base" usa estos mismos valores en su atributo value.
            roomBase: "",
            unitNetCost: "", unitSalePrice: "", currency: "ARS",
            // operatorPaymentDeadline eliminado en F2: el aviso de campanita viene del backend (firstStartDate).
            // El campo sigue en el backend pero ya no lo enviamos desde la ficha inline.
            itinerary: "", fileNumber: "",
            rateId: null, newCatalogProduct: null,
        };
    }
    // El paquete guarda netCost/salePrice como total; dividimos por pasajeros para el precio por persona
    const pasajeros = Math.max(Number(serviceToEdit.adults) || Number(serviceToEdit.passengers) || 1, 1);
    return {
        // ADR-018: la identidad del paquete se guarda en packageName (que ya existía).
        // Fallback a description/name para servicios cargados antes de ADR-018.
        packageName: serviceToEdit.packageName || serviceToEdit.description || serviceToEdit.name || "",
        supplierId: serviceToEdit.supplierId || serviceToEdit.supplierPublicId || "",
        startDate: (serviceToEdit.startDate || "").split("T")[0] || "",
        // Round-trip: poblar endDate desde el backend si viene cargado.
        // Paquetes viejos (endDate null) quedan con string vacío → campo opcional en la UI.
        endDate: (serviceToEdit.endDate || "").split("T")[0] || "",
        passengers: String(pasajeros),
        // Round-trip: el backend devuelve occupancyBase en PackageBookingDto
        roomBase: serviceToEdit.occupancyBase || "",
        unitNetCost: pasajeros > 0 ? String(redondearDinero((serviceToEdit.netCost || 0) / pasajeros)) : "",
        unitSalePrice: pasajeros > 0 ? String(redondearDinero((serviceToEdit.salePrice || 0) / pasajeros)) : "",
        currency: serviceToEdit.currency || "ARS",
        // operatorPaymentDeadline no se carga en la UI (campo eliminado en F2)
        itinerary: serviceToEdit.itinerary || "",
        fileNumber: serviceToEdit.fileNumber || serviceToEdit.confirmationNumber || "",
        rateId: serviceToEdit.rateId || null,
        newCatalogProduct: null,
    };
}

function buildAssistanceFormInitial(serviceToEdit) {
    if (!serviceToEdit) {
        return {
            planName: "", supplierId: "", validFrom: "", validTo: "",
            passengers: "", unitNetCost: "", unitSalePrice: "", currency: "ARS",
            voucherNumbers: "", upgrades: "", rateId: null, newCatalogProduct: null,
        };
    }
    // validFrom/validTo son date-only en el backend
    const pasajeros = Math.max(
        Number(serviceToEdit.adults) || Number(serviceToEdit.passengers) || 1, 1
    );
    return {
        // ADR-018: la identidad de la asistencia se guarda en planType (ya nullable en el backend).
        // Fallback a description/planName/name para servicios cargados antes de ADR-018.
        planName: serviceToEdit.planType || serviceToEdit.description || serviceToEdit.planName || serviceToEdit.name || "",
        supplierId: serviceToEdit.supplierId || serviceToEdit.supplierPublicId || "",
        validFrom: (serviceToEdit.validFrom || "").split("T")[0] || "",
        validTo: (serviceToEdit.validTo || "").split("T")[0] || "",
        passengers: String(pasajeros),
        // Asistencia: precio por persona por día. No podemos dividir por días aquí porque
        // no tenemos el campo calcularDiasVigencia disponible en el constructor del estado;
        // si el backend guarda el unitPrice en el futuro se puede ajustar.
        // Por ahora dejamos el total como referencia y el vendedor lo ajusta.
        unitNetCost: String(serviceToEdit.netCost || ""),
        unitSalePrice: String(serviceToEdit.salePrice || ""),
        currency: serviceToEdit.currency || "ARS",
        voucherNumbers: serviceToEdit.policyNumber || serviceToEdit.voucherNumbers || "",
        upgrades: serviceToEdit.notes || serviceToEdit.upgrades || "",
        rateId: serviceToEdit.rateId || null,
        newCatalogProduct: null,
    };
}

// ─── Detección de pestaña inicial cuando se edita un servicio ─────────────────

/**
 * Dado un servicio a editar, devuelve el id de la pestaña que debe activarse.
 * Usa el recordKind que el modelo normalizado pone en cada servicio.
 */
function detectarTabParaEdicion(serviceToEdit) {
    if (!serviceToEdit) return "Hotel";
    const kind = serviceToEdit.recordKind;
    if (kind === "flight") return "Aereo";
    if (kind === "transfer") return "Traslado";
    if (kind === "package") return "Paquete";
    if (kind === "assistance") return "Asistencia";
    return "Hotel"; // hotel es el default y también el tipo más común
}

// â”€â”€â”€ Componente principal ServiceInlineCard â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/**
 * Props:
 *   reservaId          — publicId de la reserva (para los endpoints)
 *   serviceToEdit       — si viene, la ficha se abre en modo edición con los datos precargados
 *   suppliers           — lista de proveedores del contexto de la reserva
 *   onGuardado          — callback que se llama con opciones después de guardar exitosamente
 *   onCancelar          — callback para cerrar la ficha sin guardar
 *   onIrAEmitirFactura   — callback () => void: abre la ficha de emisión de factura en la
 *     solapa de Facturación de la MISMA reserva (patrón Tanda 7 de ServiceList/ReservaDetailPage).
 *     Se usa cuando el PUT de edición rechaza con 409 porque el servicio ya tiene pagos al
 *     operador y la reserva todavía no tiene factura para anclar el reembolso (P1 "circuito
 *     proveedor", 2026-07-21). Opcional: si no llega, el botón simplemente no se muestra.
 */
export function ServiceInlineCard({ reservaId, serviceToEdit, suppliers, onGuardado, onCancelar, onIrAEmitirFactura }) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    // La pestaña activa: si estamos editando, detectamos el tipo automáticamente.
    // Al editar, la pestaña queda bloqueada (no se puede cambiar de tipo).
    const [tabActiva, setTabActiva] = useState(() => detectarTabParaEdicion(serviceToEdit));

    // â”€â”€â”€ Estados de formulario por tipo â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Inicializamos todos con sus builders; solo el activo se usa para guardar.
    const [formHotel, setFormHotel] = useState(() => buildHotelFormInitial(serviceToEdit?.recordKind === "hotel" ? serviceToEdit : null));
    const [formVuelo, setFormVuelo] = useState(() => buildFlightFormInitial(serviceToEdit?.recordKind === "flight" ? serviceToEdit : null));
    const [formTraslado, setFormTraslado] = useState(() => buildTransferFormInitial(serviceToEdit?.recordKind === "transfer" ? serviceToEdit : null));
    const [formPaquete, setFormPaquete] = useState(() => buildPackageFormInitial(serviceToEdit?.recordKind === "package" ? serviceToEdit : null));
    const [formAsistencia, setFormAsistencia] = useState(() => buildAssistanceFormInitial(serviceToEdit?.recordKind === "assistance" ? serviceToEdit : null));

    // Estado de guardado
    const [guardando, setGuardando] = useState(false);
    const [errorGuardado, setErrorGuardado] = useState(null);
    // Motivo estructurado del último rechazo 409 (P1 "circuito proveedor", 2026-07-21):
    // { codigoConocido, boton } salido de resolverRechazoAnularServicio. Se usa SOLO para
    // decidir si mostramos el botón "Emitir factura" junto al cartel de error — el texto
    // que se ve siempre es errorGuardado (el mensaje real del backend), nunca se inventa acá.
    const [rechazoGuardado, setRechazoGuardado] = useState(null);
    // Aviso ÁMBAR de la P3 "circuito proveedor" (2026-07-22): el motor pidió confirmar que
    // bajar el costo del operador genera saldo a favor. Guarda el `message` tal cual del
    // backend (con el monto exacto) — nunca convive con errorGuardado (cartel rojo), son
    // dos estados mutuamente excluyentes que se limpian entre sí en cada intento de guardado.
    const [avisoCostoMenorAPagado, setAvisoCostoMenorAPagado] = useState(null);

    const esEdicion = Boolean(serviceToEdit);

    // â”€â”€â”€ Acceso al form activo (lectura) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    const formActivo = {
        Hotel: formHotel,
        Aereo: formVuelo,
        Traslado: formTraslado,
        Paquete: formPaquete,
        Asistencia: formAsistencia,
    }[tabActiva];

    // ─── Validación por tipo ──────────────────────────────────────────────────

    const validarForm = useCallback(() => {
        if (tabActiva === "Hotel") {
            if (!formHotel.hotelName?.trim()) return "Escribí el nombre del hotel.";
            if (!formHotel.checkIn) return "Elegí la fecha de entrada.";
            if (!formHotel.checkOut) return "Elegí la fecha de salida.";
            const noches = calcularNoches(formHotel.checkIn, formHotel.checkOut);
            if (noches <= 0) return "La fecha de salida debe ser posterior a la de entrada.";
            if (!formHotel.unitSalePrice || Number(formHotel.unitSalePrice) <= 0) return "Ingresá el precio de venta por noche.";
            // RoomType y MealPlan son obligatorios en el backend (non-nullable). Los selects
            // tienen defaults así que esto solo puede pasar si el estado se cargó mal externamente.
            if (!formHotel.mealPlan) return "Seleccioná el régimen del hotel.";
            if (!formHotel.roomType) return "Seleccioná el tipo de habitación.";
            if (!formHotel.newCatalogProduct && !formHotel.supplierId) return "Elegí el operador.";
            if (formHotel.newCatalogProduct) {
                if (!formHotel.newCatalogProduct.name?.trim()) return "Ingresá el nombre del hotel nuevo.";
                if (!formHotel.newCatalogProduct.city?.trim()) return "La ciudad es obligatoria para crear un hotel nuevo.";
                if (!formHotel.newCatalogProduct.supplierPublicId) return "Elegí el operador del hotel nuevo.";
            }
        }

        if (tabActiva === "Aereo") {
            if (!formVuelo.routeName?.trim()) return "Escribí la ruta o aerolínea.";
            if (!formVuelo.departureDate) return "Elegí la fecha de ida.";
            if (!formVuelo.salePrice || Number(formVuelo.salePrice) <= 0) return "Ingresá el precio de venta.";
            if (!formVuelo.newCatalogProduct && !formVuelo.supplierId) return "Elegí el operador o consolidador.";
            if (formVuelo.newCatalogProduct) {
                if (!formVuelo.newCatalogProduct.name?.trim()) return "Ingresá el nombre de la ruta nueva.";
                if (!formVuelo.newCatalogProduct.supplierPublicId) return "Elegí el operador del vuelo nuevo.";
            }
        }

        if (tabActiva === "Traslado") {
            if (!formTraslado.routeName?.trim()) return "Escribí el trayecto del traslado.";
            if (!formTraslado.pickupDate) return "Elegí la fecha del traslado.";
            if (!formTraslado.salePrice || Number(formTraslado.salePrice) <= 0) return "Ingresá el precio de venta.";
            if (!formTraslado.newCatalogProduct && !formTraslado.supplierId) return "Elegí el operador.";
            if (formTraslado.newCatalogProduct) {
                if (!formTraslado.newCatalogProduct.name?.trim()) return "Ingresá el nombre del trayecto nuevo.";
                if (!formTraslado.newCatalogProduct.supplierPublicId) return "Elegí el operador del traslado nuevo.";
            }
        }

        if (tabActiva === "Paquete") {
            if (!formPaquete.packageName?.trim()) return "Escribí el nombre del paquete.";
            if (!formPaquete.startDate) return "Elegí la fecha de salida.";
            // Validación de coherencia de fechas: fin no puede ser anterior a salida.
            // endDate es opcional; solo se valida cuando el usuario la cargó.
            if (formPaquete.endDate && formPaquete.startDate && formPaquete.endDate < formPaquete.startDate) {
                return "La fecha de fin no puede ser anterior a la salida.";
            }
            if (!formPaquete.unitSalePrice || Number(formPaquete.unitSalePrice) <= 0) return "Ingresá el precio de venta por persona.";
            if (!formPaquete.newCatalogProduct && !formPaquete.supplierId) return "Elegí el operador.";
            if (formPaquete.newCatalogProduct) {
                if (!formPaquete.newCatalogProduct.name?.trim()) return "Ingresá el nombre del paquete nuevo.";
                if (!formPaquete.newCatalogProduct.supplierPublicId) return "Elegí el operador del paquete nuevo.";
            }
        }

        if (tabActiva === "Asistencia") {
            if (!formAsistencia.planName?.trim()) return "Escribí el plan o cobertura.";
            if (!formAsistencia.validFrom) return "Elegí la fecha de inicio de vigencia.";
            if (!formAsistencia.validTo) return "Elegí la fecha de fin de vigencia.";
            if (!formAsistencia.unitSalePrice || Number(formAsistencia.unitSalePrice) <= 0) return "Ingresá el precio de venta por persona/día.";
            if (!formAsistencia.newCatalogProduct && !formAsistencia.supplierId) return "Elegí el proveedor.";
            if (formAsistencia.newCatalogProduct) {
                if (!formAsistencia.newCatalogProduct.name?.trim()) return "Ingresá el nombre del plan nuevo.";
                if (!formAsistencia.newCatalogProduct.supplierPublicId) return "Elegí el proveedor del plan nuevo.";
            }
        }

        return null;
    }, [tabActiva, formHotel, formVuelo, formTraslado, formPaquete, formAsistencia]);

    // â”€â”€â”€ Construir payload por tipo â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    const buildPayload = useCallback(() => {
        if (tabActiva === "Hotel") {
            const noches = calcularNoches(formHotel.checkIn, formHotel.checkOut);
            const habitaciones = Math.max(Number(formHotel.rooms) || 1, 1);
            const factorTotal = Math.max(noches, 1) * habitaciones;
            const netCostTotal = redondearDinero((Number(formHotel.unitNetCost) || 0) * factorTotal);
            const salePriceTotal = redondearDinero((Number(formHotel.unitSalePrice) || 0) * factorTotal);

            const payload = {
                hotelName: formHotel.hotelName?.trim() || "",
                city: formHotel.city?.trim() || "",
                checkIn: formHotel.checkIn,
                checkOut: formHotel.checkOut,
                nights: noches,
                rooms: habitaciones,
                adults: Number(formHotel.passengers) || 1,
                children: 0,
                supplierId: formHotel.supplierId || null,
                netCost: canSeeCost ? netCostTotal : 0,
                salePrice: salePriceTotal,
                tax: 0,
                currency: formHotel.currency || "ARS",
                // RoomType y MealPlan son string NO-nullables en el backend (CreateHotelRequest /
                // UpdateHotelRequest). Con null el backend responde 400. Usamos el mismo default
                // que el modal viejo: Doble / Desayuno. Los selects siempre tienen un valor
                // seleccionado, así que || "X" es solo un fallback de seguridad extra.
                mealPlan: formHotel.mealPlan || "Desayuno",
                roomType: formHotel.roomType || "Doble",
                confirmationNumber: formHotel.confirmationNumber || null,
                address: formHotel.address || null,
                // operatorPaymentDeadline eliminado en F2: el aviso viene del backend (firstStartDate).
            };
            if (formHotel.rateId) {
                payload.rateId = formHotel.rateId;
            } else if (formHotel.newCatalogProduct) {
                payload.newCatalogProduct = { ...formHotel.newCatalogProduct };
                payload.supplierId = formHotel.newCatalogProduct.supplierPublicId || null;
            }
            return payload;
        }

        if (tabActiva === "Aereo") {
            const payload = {
                // ADR-018: la identidad del vuelo va en productName, no en description.
                // El backend (FlightSegment) tiene columna ProductName (varchar200, nullable).
                productName: formVuelo.routeName?.trim() || "",
                // Hora de pared sin conversión UTC (véase ServiceFormModal línea ~2286)
                departureTime: formVuelo.departureDate ? `${formVuelo.departureDate}T00:00:00` : null,
                arrivalTime: formVuelo.returnDate ? `${formVuelo.returnDate}T00:00:00` : null,
                passengerCount: formVuelo.passengers ? Number(formVuelo.passengers) : null,
                supplierId: formVuelo.supplierId || null,
                netCost: canSeeCost ? redondearDinero(Number(formVuelo.netCost) || 0) : 0,
                salePrice: redondearDinero(Number(formVuelo.salePrice) || 0),
                tax: 0,
                currency: formVuelo.currency || "ARS",
                // ticketingDeadline eliminado en F2: el aviso viene del backend (firstStartDate).
                pnr: formVuelo.pnr || null,
                ticketNumber: formVuelo.ticketNumber || null,
                notes: formVuelo.scheduleNotes || null,
                baggage: formVuelo.baggage || null,
                // cabinClass: null cuando no se eligió (backend lo relaja a opcional).
                // Con || null: "" â†’ null, "Economy" â†’ "Economy", etc.
                cabinClass: formVuelo.cabinClass || null,
            };
            if (formVuelo.rateId) {
                payload.rateId = formVuelo.rateId;
            } else if (formVuelo.newCatalogProduct) {
                payload.newCatalogProduct = { ...formVuelo.newCatalogProduct };
                payload.supplierId = formVuelo.newCatalogProduct.supplierPublicId || null;
            }
            return payload;
        }

        if (tabActiva === "Traslado") {
            const payload = {
                // ADR-018: la identidad del traslado va en productName, no en description.
                // El backend (TransferBooking) tiene columna ProductName (varchar200, nullable).
                productName: formTraslado.routeName?.trim() || "",
                pickupDateTime: formTraslado.pickupDate
                    ? `${formTraslado.pickupDate}T${formTraslado.pickupTime || "00:00"}:00`
                    : null,
                passengers: formTraslado.passengers ? Number(formTraslado.passengers) : null,
                supplierId: formTraslado.supplierId || null,
                netCost: canSeeCost ? redondearDinero(Number(formTraslado.netCost) || 0) : 0,
                salePrice: redondearDinero(Number(formTraslado.salePrice) || 0),
                tax: 0,
                currency: formTraslado.currency || "ARS",
                flightNumber: formTraslado.associatedFlightNumber || null,
                confirmationNumber: formTraslado.confirmationNumber || null,
                // direction: "in" (llegada) o "out" (salida); el select ya almacena el valor backend.
                direction: formTraslado.movementType || null,
                // serviceMode: "private" o "shared"; el select ya almacena el valor backend.
                serviceMode: formTraslado.transferType || null,
                // vehicleType: texto libre opcional; null cuando no se especificó.
                vehicleType: formTraslado.vehicleType || null,
                isRoundTrip: false,
            };
            if (formTraslado.rateId) {
                payload.rateId = formTraslado.rateId;
            } else if (formTraslado.newCatalogProduct) {
                payload.newCatalogProduct = { ...formTraslado.newCatalogProduct };
                payload.supplierId = formTraslado.newCatalogProduct.supplierPublicId || null;
            }
            return payload;
        }

        if (tabActiva === "Paquete") {
            const pasajeros = Math.max(Number(formPaquete.passengers) || 1, 1);
            const salePriceTotal = redondearDinero((Number(formPaquete.unitSalePrice) || 0) * pasajeros);
            const netCostTotal = redondearDinero((Number(formPaquete.unitNetCost) || 0) * pasajeros);

            const payload = {
                // ADR-018: la identidad del paquete va en packageName (campo pre-existente en PackageBooking).
                // El ADR relajó Destination y EndDate a nullable; no se mandan desde la ficha inline.
                packageName: formPaquete.packageName?.trim() || "",
                // Fecha de pared sin conversión UTC, igual que Hotel/Vuelo más arriba (bug fechas
                // corridas 2026-07-16). El backend normaliza esto con NormalizeCalendarDate
                // (BookingService), que acepta tanto con Z como sin Z — pero unificamos el contrato.
                startDate: formPaquete.startDate ? `${formPaquete.startDate}T00:00:00` : null,
                // endDate es OPCIONAL en ADR-018: si el form no lo tiene, se omite. El backend coalesce a startDate.
                endDate: formPaquete.endDate ? `${formPaquete.endDate}T00:00:00` : null,
                adults: pasajeros,
                children: 0,
                supplierId: formPaquete.supplierId || null,
                netCost: canSeeCost ? netCostTotal : 0,
                salePrice: salePriceTotal,
                tax: 0,
                currency: formPaquete.currency || "ARS",
                itinerary: formPaquete.itinerary || null,
                // El número de file va en confirmationNumber (el backend tiene ese campo)
                confirmationNumber: formPaquete.fileNumber || null,
                // occupancyBase: "double", "triple", etc. El select ya almacena el valor backend.
                occupancyBase: formPaquete.roomBase || null,
                // operatorPaymentDeadline eliminado en F2: el aviso viene del backend (firstStartDate).
            };
            if (formPaquete.rateId) {
                payload.rateId = formPaquete.rateId;
            } else if (formPaquete.newCatalogProduct) {
                payload.newCatalogProduct = { ...formPaquete.newCatalogProduct };
                payload.supplierId = formPaquete.newCatalogProduct.supplierPublicId || null;
            }
            return payload;
        }

        if (tabActiva === "Asistencia") {
            const payload = {
                // ADR-018: la identidad de la asistencia va en planType, no en description.
                // El backend (AssistanceBooking) ya tenía PlanType nullable.
                planType: formAsistencia.planName?.trim() || "",
                // Fecha de pared sin conversión UTC, igual que Hotel/Vuelo más arriba (bug fechas
                // corridas 2026-07-16). El backend normaliza esto con NormalizeCalendarDate
                // (BookingService), que acepta tanto con Z como sin Z — pero unificamos el contrato.
                validFrom: formAsistencia.validFrom ? `${formAsistencia.validFrom}T00:00:00` : null,
                validTo: formAsistencia.validTo ? `${formAsistencia.validTo}T00:00:00` : null,
                adults: formAsistencia.passengers ? Number(formAsistencia.passengers) : 1,
                children: 0,
                supplierId: formAsistencia.supplierId || null,
                netCost: canSeeCost ? redondearDinero(Number(formAsistencia.unitNetCost) || 0) : 0,
                salePrice: redondearDinero(Number(formAsistencia.unitSalePrice) || 0),
                tax: 0,
                currency: formAsistencia.currency || "ARS",
                // policyNumber se usa para los vouchers (campo existente en el backend)
                policyNumber: formAsistencia.voucherNumbers || null,
                notes: formAsistencia.upgrades || null,
            };
            if (formAsistencia.rateId) {
                payload.rateId = formAsistencia.rateId;
            } else if (formAsistencia.newCatalogProduct) {
                payload.newCatalogProduct = { ...formAsistencia.newCatalogProduct };
                payload.supplierId = formAsistencia.newCatalogProduct.supplierPublicId || null;
            }
            return payload;
        }

        return {};
    }, [tabActiva, formHotel, formVuelo, formTraslado, formPaquete, formAsistencia, canSeeCost]);

    // â”€â”€â”€ Guardar â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // Arma y envía el PUT/POST del tipo activo. `confirmarCostoMenor` es el flag de la P3:
    // cuando es true, reusa el MISMO `buildPayload()` de siempre y le suma
    // `confirmCostBelowPaid: true` (no hay un builder aparte para el reenvío — es
    // exactamente el mismo guardado, solo que con la marca de confirmación puesta).
    const enviarGuardado = async (confirmarCostoMenor) => {
        setGuardando(true);
        try {
            const payloadBase = buildPayload();
            const payload = confirmarCostoMenor
                ? agregarConfirmacionCostoMenorAPagado(payloadBase)
                : payloadBase;
            const endpointSegmento = TAB_ENDPOINTS[tabActiva];

            if (esEdicion) {
                const serviceId = getReservationServicePublicId(serviceToEdit);
                await api.put(`/reservas/${reservaId}/${endpointSegmento}/${serviceId}`, payload);
            } else {
                await api.post(`/reservas/${reservaId}/${endpointSegmento}`, payload);
            }

            // Guardado normal, igual que cualquier edición exitosa: la ficha se cierra en
            // silencio (spec P3, decisión de Gastón 2026-07-21: "guarda calladito", sin
            // cartelito verde extra tras confirmar).
            onGuardado({ showLoading: false, preserveOnError: true });
        } catch (error) {
            // P3 "circuito proveedor" (2026-07-22): si el motor pide confirmar la baja de
            // costo por debajo de lo pagado y todavía no reenviamos con la marca puesta,
            // este 409 puntual se muestra como AVISO ámbar (no error) — nunca junto al
            // cartel rojo. Si YA reenviamos con la marca y el motor vuelve a rechazar (otra
            // causa), cae al cartel rojo de siempre, como cualquier otro fallo de guardado.
            if (!confirmarCostoMenor && esRechazoCostoMenorAPagado(error)) {
                setAvisoCostoMenorAPagado(getApiErrorMessage(error, "Confirmá para continuar."));
                return;
            }

            // Si falla, la ficha queda abierta con todo intacto + cartel rojo (guía UX ronda 2)
            setErrorGuardado(getApiErrorMessage(error, "No se pudo guardar. Revisá la conexión y probá de nuevo."));
            // P1 "circuito proveedor" (2026-07-21): el PUT de edición también puede rechazar con
            // el MISMO código que "anular servicio" (el servicio ya tiene pagos al operador y la
            // reserva no tiene factura para anclar el reembolso). Reusamos la lib de la Tanda 7
            // para decidir si corresponde ofrecer el botón "Emitir factura" — nunca se adivina
            // el motivo comparando el texto del mensaje.
            setRechazoGuardado(resolverRechazoAnularServicio(error));
        } finally {
            setGuardando(false);
        }
    };

    const handleGuardar = async () => {
        setErrorGuardado(null);
        setRechazoGuardado(null);
        setAvisoCostoMenorAPagado(null);

        const errorValidacion = validarForm();
        if (errorValidacion) {
            setErrorGuardado(errorValidacion);
            return;
        }

        await enviarGuardado(false);
    };

    // "Sí, confirmar" del aviso ámbar: reenvía el MISMO guardado (buildPayload() reconstruye
    // el payload desde el estado actual del formulario, que no cambió desde el intento
    // anterior) con la marca de confirmación. No hace falta re-validar: nada se editó.
    const handleConfirmarCostoMenor = async () => {
        setAvisoCostoMenorAPagado(null);
        await enviarGuardado(true);
    };

    // "Volver a corregir" del aviso ámbar: solo saca el cartel, la ficha queda intacta y el
    // foco vuelve al campo de Costo del tipo activo para que el vendedor corrija el número
    // (spec P3 §2 — a diferencia de "Cancelar", esto NO cierra la ficha ni pierde datos).
    const handleVolverACorregirCosto = () => {
        setAvisoCostoMenorAPagado(null);
        document.getElementById(CAMPO_COSTO_POR_TAB[tabActiva])?.focus();
    };

    // â”€â”€â”€ Calcular totales para el footer (por tipo activo) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    const totalesFooter = (() => {
        if (tabActiva === "Hotel") {
            const noches = calcularNoches(formHotel.checkIn, formHotel.checkOut);
            const habitaciones = Math.max(Number(formHotel.rooms) || 1, 1);
            const factorTotal = Math.max(noches, 0) * habitaciones;
            const ventaTotal = redondearDinero((Number(formHotel.unitSalePrice) || 0) * factorTotal);
            const costoTotal = canSeeCost ? redondearDinero((Number(formHotel.unitNetCost) || 0) * factorTotal) : null;
            const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
            // Solo mostramos el total si ya tiene datos suficientes para calcular
            const mostrar = noches > 0 && Number(formHotel.unitSalePrice) > 0;
            return { ventaTotal, ganancia, mostrar };
        }
        if (tabActiva === "Aereo") {
            const { ventaTotal, ganancia } = calcularTotalesVuelo({
                salePrice: formVuelo.salePrice,
                netCost: formVuelo.netCost,
                canSeeCost,
            });
            return { ventaTotal, ganancia, mostrar: ventaTotal > 0 };
        }
        if (tabActiva === "Traslado") {
            const { ventaTotal, ganancia } = calcularTotalesTraslado({
                salePrice: formTraslado.salePrice,
                netCost: formTraslado.netCost,
                canSeeCost,
            });
            return { ventaTotal, ganancia, mostrar: ventaTotal > 0 };
        }
        if (tabActiva === "Paquete") {
            const { ventaTotal, ganancia } = calcularTotalesPaquete({
                unitSalePrice: formPaquete.unitSalePrice,
                unitNetCost: formPaquete.unitNetCost,
                passengers: formPaquete.passengers,
                canSeeCost,
            });
            return { ventaTotal, ganancia, mostrar: ventaTotal > 0 };
        }
        if (tabActiva === "Asistencia") {
            const { ventaTotal, ganancia } = calcularTotalesAsistencia({
                unitSalePrice: formAsistencia.unitSalePrice,
                unitNetCost: formAsistencia.unitNetCost,
                passengers: formAsistencia.passengers,
                validFrom: formAsistencia.validFrom,
                validTo: formAsistencia.validTo,
                canSeeCost,
            });
            return { ventaTotal, ganancia, mostrar: ventaTotal > 0 };
        }
        return { ventaTotal: 0, ganancia: null, mostrar: false };
    })();

    // ─── Label del botón de guardar ───────────────────────────────────────────

    const tieneProductoNuevo = formActivo?.newCatalogProduct != null;
    const tiposLabel = { Hotel: "hotel", Aereo: "vuelo", Traslado: "traslado", Paquete: "paquete", Asistencia: "asistencia" };
    const labelBotonGuardar = esEdicion
        ? "Guardar cambios"
        : tieneProductoNuevo
        ? `Guardar servicio y ${tiposLabel[tabActiva]}`
        : "Guardar servicio";

    // â”€â”€â”€ Render â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    return (
        // Borde azul = zona activa (mockup estilo .inlinecard)
        <div
            className="border-2 border-blue-500 rounded-xl bg-white p-5 mt-4 shadow-sm"
            data-testid="service-inline-card"
        >
            {/* PESTAÑAS */}
            <div className="flex gap-2 mb-5 flex-wrap" role="tablist" aria-label="Tipo de servicio">
                {TABS.map(({ id, label, icon: Icon }) => {
                    const estaActiva = tabActiva === id;
                    return (
                        <button
                            key={id}
                            type="button"
                            role="tab"
                            aria-selected={estaActiva}
                            // Al editar no se puede cambiar de tipo (la ficha es para ese servicio)
                            disabled={esEdicion && !estaActiva}
                            onClick={() => { if (!esEdicion) setTabActiva(id); }}
                            className={`flex items-center gap-1.5 px-4 py-1.5 rounded-full text-sm font-semibold transition-colors ${
                                estaActiva
                                    ? "bg-blue-600 text-white"
                                    : esEdicion
                                    ? "bg-slate-50 text-slate-300 cursor-not-allowed"
                                    : "bg-slate-100 text-slate-600 hover:bg-slate-200"
                            }`}
                            data-testid={`tab-${id.toLowerCase()}`}
                        >
                            <Icon className="w-3.5 h-3.5" />
                            {label}
                        </button>
                    );
                })}
            </div>

            {/* CONTENIDO DE LA PESTAÑA ACTIVA */}
            <div role="tabpanel">
                {tabActiva === "Hotel" && (
                    <HotelInlineForm
                        form={formHotel}
                        setForm={setFormHotel}
                        suppliers={suppliers}
                        isEditing={esEdicion}
                    />
                )}
                {tabActiva === "Aereo" && (
                    <FlightInlineForm
                        form={formVuelo}
                        setForm={setFormVuelo}
                        suppliers={suppliers}
                        isEditing={esEdicion}
                    />
                )}
                {tabActiva === "Traslado" && (
                    <TransferInlineForm
                        form={formTraslado}
                        setForm={setFormTraslado}
                        suppliers={suppliers}
                        isEditing={esEdicion}
                    />
                )}
                {tabActiva === "Paquete" && (
                    <PackageInlineForm
                        form={formPaquete}
                        setForm={setFormPaquete}
                        suppliers={suppliers}
                        isEditing={esEdicion}
                    />
                )}
                {tabActiva === "Asistencia" && (
                    <AssistanceInlineForm
                        form={formAsistencia}
                        setForm={setFormAsistencia}
                        suppliers={suppliers}
                        isEditing={esEdicion}
                    />
                )}
            </div>

            {/* FOOTER FIJO: totales + botones */}
            <div className="mt-5 pt-4 border-t border-slate-100 flex flex-col sm:flex-row justify-between items-start sm:items-center gap-3">
                {/* Izquierda: totales */}
                <div className="text-sm text-slate-700 flex flex-wrap items-center gap-3">
                    {totalesFooter.mostrar && (
                        <>
                            <span>
                                Venta <strong>{formatearPrecio(totalesFooter.ventaTotal)}</strong>
                            </span>
                            {/* Ganancia: solo para quien tiene permiso de ver costos */}
                            {canSeeCost && totalesFooter.ganancia !== null && (
                                <span className={totalesFooter.ganancia >= 0 ? "font-semibold text-emerald-600" : "font-semibold text-red-600"}>
                                    Ganás {formatearPrecio(totalesFooter.ganancia)}
                                </span>
                            )}
                        </>
                    )}
                </div>

                {/* Derecha: cartel de aviso/error + botones */}
                <div className="flex flex-col items-end gap-2 w-full sm:w-auto">
                    {/* Aviso ÁMBAR de la P3 (spec 2026-07-22): bajar el costo del operador por
                        debajo de lo ya pagado no bloquea, pero el motor pide confirmar antes de
                        guardar (genera saldo a favor con ese operador). Nunca se muestra junto
                        al cartel rojo de error — son dos estados mutuamente excluyentes. */}
                    {avisoCostoMenorAPagado && (
                        <div
                            className="flex flex-col gap-2 text-xs text-amber-800 bg-amber-50 border border-amber-200 rounded-lg px-3 py-2 w-full sm:w-auto max-w-sm"
                            role="alert"
                            data-testid="inline-card-confirmar-costo"
                        >
                            <div className="flex items-start gap-2">
                                <AlertTriangle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                                {/* El texto es el message tal cual del motor (es-AR, con el monto
                                    exacto de la diferencia) — el front nunca lo reescribe ni calcula. */}
                                <span>{avisoCostoMenorAPagado}</span>
                            </div>
                            <div className="flex gap-2 self-end">
                                <button
                                    type="button"
                                    onClick={handleVolverACorregirCosto}
                                    disabled={guardando}
                                    className="px-3 py-1.5 text-xs font-semibold text-amber-800 border border-amber-300 rounded-lg hover:bg-amber-100 disabled:opacity-50 transition-colors"
                                    data-testid="confirmar-costo-corregir"
                                >
                                    Volver a corregir
                                </button>
                                <button
                                    type="button"
                                    onClick={handleConfirmarCostoMenor}
                                    disabled={guardando}
                                    className="px-3 py-1.5 text-xs font-semibold bg-amber-600 text-white rounded-lg hover:bg-amber-700 disabled:opacity-50 transition-colors"
                                    data-testid="confirmar-costo-si"
                                >
                                    {guardando ? "Guardando…" : "Sí, confirmar"}
                                </button>
                            </div>
                        </div>
                    )}
                    {/* Error arriba de los botones (guía UX ronda 2: nunca se pierde lo cargado) */}
                    {errorGuardado && (
                        <div
                            className="flex flex-col gap-2 text-xs text-red-700 bg-red-50 border border-red-200 rounded-lg px-3 py-2 w-full sm:w-auto max-w-sm"
                            role="alert"
                            data-testid="inline-card-error"
                        >
                            <div className="flex items-start gap-2">
                                <AlertCircle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                                <span>{errorGuardado}</span>
                            </div>
                            {/* Motivo R1 (pago al operador sin factura viva) — mismo botón que la
                                Tanda 7 del modal de "anular servicio": lleva a la solapa de
                                Facturación de esta misma reserva para emitir la factura pendiente. */}
                            {rechazoGuardado?.boton === "emitir-factura" && onIrAEmitirFactura && (
                                <button
                                    type="button"
                                    data-testid="inline-card-emitir-factura"
                                    onClick={onIrAEmitirFactura}
                                    className="inline-flex items-center gap-1.5 self-start rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-bold text-white transition-colors hover:bg-indigo-700"
                                >
                                    <FileText className="w-3.5 h-3.5" />
                                    Emitir factura
                                </button>
                            )}
                        </div>
                    )}
                    <div className="flex gap-2">
                        <button
                            type="button"
                            onClick={onCancelar}
                            disabled={guardando}
                            className="px-4 py-2 text-sm font-medium text-slate-600 border border-slate-200 rounded-lg hover:bg-slate-50 disabled:opacity-50 transition-colors"
                            data-testid="inline-card-cancelar"
                        >
                            Cancelar
                        </button>
                        <button
                            type="button"
                            onClick={handleGuardar}
                            disabled={guardando}
                            className="px-5 py-2 text-sm font-semibold bg-blue-600 text-white rounded-lg hover:bg-blue-700 disabled:opacity-50 transition-colors"
                            data-testid="inline-card-guardar"
                        >
                            {guardando ? "Guardando…" : labelBotonGuardar}
                        </button>
                    </div>
                </div>
            </div>
        </div>
    );
}
