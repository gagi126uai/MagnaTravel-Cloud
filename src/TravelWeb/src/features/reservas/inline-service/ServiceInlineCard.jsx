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
 * Si el guardado falla, la ficha queda intacta detrás (nunca se pierde lo cargado — guía
 * UX ronda 2). Hay DOS tipos de error, tratados distinto (spec del cartel emergente
 * 2026-07-22, sección 2 — "la raya"):
 *   - Corto, de un CAMPO (validarForm, nunca llegó a la API): sigue incrustado, pegado a
 *     los botones (cartel rojo chico, testid "inline-card-error").
 *   - Largo, del MOTOR (409/400 real del backend): se muestra en el Cartel Emergente
 *     (ventana), nunca incrustado — puede traer el botón "Emitir factura" si el motivo es
 *     "pago al operador sin factura viva".
 *
 * P3 "circuito proveedor" (spec 2026-07-22, P1=A del cartel emergente): al editar, si el
 * costo nuevo queda por debajo de lo ya pagado al operador, el motor no bloquea pero pide
 * confirmar (409 + code COST_BELOW_PAID_CONFIRMATION_REQUIRED) — también se muestra en el
 * Cartel Emergente (traje ámbar de confirmación) con "Volver a corregir" / "Sí, confirmar".
 */

import { useState, useCallback, useEffect, useMemo } from "react";
import { Hotel, Plane, Car, Package, ShieldCheck, AlertCircle } from "lucide-react";
import { hasPermission } from "../../../auth";
import { api } from "../../../api";
import { getApiErrorMessage } from "../../../lib/errors";
import { formatDate } from "../../../lib/utils";
import { Button } from "../../../components/ui/button";
import { getReservationServicePublicId, getServiceMutationEndpoint } from "../lib/reservationServiceModel";
import { CartelEmergente, CARTEL_EMERGENTE_VARIANTES } from "../../../components/CartelEmergente";
import { esRechazoCostoMenorAPagado, agregarConfirmacionCostoMenorAPagado } from "../lib/costConfirmationGuard";
import { resolverRateIdDeEdicion, resolverCamposALimpiarAlCrearNuevo, resolverTocadosAManoTrasLimpiarOrigen } from "./inlineServiceFormHelpers";
import {
    construirClaveServicio,
    esServicioVivoParaOpciones,
    calcularAsignacionDeOpcion,
} from "../lib/optionGroupLogic";
import { HotelInlineForm, calcularNoches, redondearDinero, formatearPrecio } from "./HotelInlineForm";
import { FlightInlineForm, calcularTotalesVuelo } from "./FlightInlineForm";
import { TransferInlineForm, calcularTotalesTraslado } from "./TransferInlineForm";
import { PackageInlineForm, calcularTotalesPaquete } from "./PackageInlineForm";
import { AssistanceInlineForm, calcularTotalesAsistencia, calcularDiasVigencia } from "./AssistanceInlineForm";

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
// Fix ronda 4 (BLOQUEANTE): "Twin" y "Suite" SÍ son valores canónicos del catálogo real
// (ver RoomTypeValue en CatalogVariant.cs, backend) — faltaban acá. Sin ellos, abrir a
// editar un hotel guardado como Twin/Suite mostraba "Doble" en el select (por el default
// de normalizarRoomType) y el PUT siguiente reescribía roomType:"Doble" aunque el
// vendedor solo hubiera tocado un campo ajeno (ej. el número de confirmación) — la
// habitación del pasajero cambiaba sola, y la venta quedaba archivada bajo otra variante.
const ROOM_TYPE_CANONICOS = ["Single", "Doble", "Twin", "Triple", "Cuadruple", "Familiar", "Suite"];
// Solo equivalencias INEQUIVOCAS. "FamiliarCuadruple" no tiene equivalente claro en la
// lista canónica -> cae al default (dato de prueba pre-lanzamiento; si algún día
// importara, la decisión de equivalencia es del dueño, no nuestra).
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
            // Nombre fino de la habitación ("Superior", "Vista al mar"), texto libre CON
            // MEMORIA (spec 2026-08-07, §5.2). Junto con roomType/mealPlan arma la variante
            // que el tarifario recuerda por separado (V1=A: vender una triple ya no pisa
            // el precio de la doble).
            roomCategory: "",
            confirmationNumber: "",
            // operatorPaymentDeadline eliminado en F2: el aviso de campanita viene del backend (firstStartDate).
            address: "",
            // starRating (spec 2026-08-12, §2): dato del PDF de presupuesto. "" = Sin especificar.
            starRating: "",
            // Plan de cuotas (spec 2026-08-13, §8.3): dato informativo para el PDF, NO participa
            // del cálculo de Venta total. installmentsCount es texto con sanitizarCantidadPositiva
            // (mismo molde que "Habitaciones"); installmentAmount usa MoneyInput sin moneda propia
            // (P-16: usa la moneda que ya eligió el servicio, `form.currency`).
            installmentsCount: "",
            installmentAmount: "",
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
        // Round-trip: el backend devuelve roomCategory en HotelBookingDto; fallback "" (sin nombre fino cargado).
        roomCategory: serviceToEdit.roomCategory || "",
        confirmationNumber: serviceToEdit.confirmationNumber || "",
        // operatorPaymentDeadline no se carga en la UI (campo eliminado en F2)
        address: serviceToEdit.address || "",
        // Round-trip: el backend devuelve starRating (int|null) en HotelBookingDto. String vacío
        // cuando no está cargado — el <select> necesita strings, no null/undefined.
        starRating: serviceToEdit.starRating != null ? String(serviceToEdit.starRating) : "",
        // Round-trip: el backend devuelve installmentsCount (int|null) / installmentAmount
        // (decimal|null) en HotelBookingDto. Fallback "" cuando no hay plan de cuotas cargado.
        installmentsCount: serviceToEdit.installmentsCount != null ? String(serviceToEdit.installmentsCount) : "",
        installmentAmount: serviceToEdit.installmentAmount != null ? String(serviceToEdit.installmentAmount) : "",
        // Fix #3 (auditoría de coherencia 2026-08-10, GRAVE) — ver resolverRateIdDeEdicion
        // en inlineServiceFormHelpers.js para el detalle completo del bug.
        rateId: resolverRateIdDeEdicion(serviceToEdit),
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
            // Semáforo de DNI vencido para cabotaje (2026-08-03): "" = "Sin definir" (default,
            // nunca dispara el aviso). El backend ya tiene este campo en CreateFlightRequest/
            // UpdateFlightRequest; buildPayload manda el token "SinDefinir" cuando el select
            // queda en "" (ver ServiceGeographicScopeText.Cleared en el backend).
            geographicScope: "",
            // isDirect/includes* (spec 2026-08-12, §1): datos del PDF de presupuesto. isDirect es
            // string ("" / "true" / "false") porque es el value de un <select> tri-estado; los 3
            // casilleros son boolean simples, arrancan destildados.
            isDirect: "",
            includesBackpack: false,
            includesCarryOn: false,
            includesCheckedBag: false,
            // Obra "PDF completo" (2026-08-13, spec §2): aeropuerto/ciudad, texto libre. El
            // backend YA aceptaba estos 4 campos desde ADR-018 (Origin/Destination opcionales);
            // esta ficha nunca les había dado casillero en pantalla.
            origin: "",
            originCity: "",
            destination: "",
            destinationCity: "",
            // Obra "PDF completo" (2026-08-13, spec §1/§8.2): horarios de ida/vuelta, string
            // "HH:mm" (el mismo formato que devuelve un <input type="time">). Vacío = no
            // informado — nunca se manda dentro de departureTime/arrivalTime (ver buildFlightPayload).
            outboundDepartureTime: "",
            outboundArrivalTime: "",
            returnDepartureTime: "",
            returnArrivalTime: "",
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
        // Semáforo de DNI vencido para cabotaje: FlightSegmentDto SI expone este campo en la
        // lectura, como texto legible ("Nacional"/"Internacional") o null si nunca se definió.
        // Fallback "" (Sin definir) cuando el backend devuelve null.
        geographicScope: serviceToEdit.geographicScope || "",
        // Round-trip: FlightSegmentDto devuelve isDirect/includes* (bool|null). isDirect se guarda
        // como string para el <select> tri-estado: "true"/"false" cuando tiene valor, "" cuando es
        // null (nunca se cargó). Los 3 casilleros son boolean directos, con `false` de fallback.
        isDirect: serviceToEdit.isDirect === true ? "true" : serviceToEdit.isDirect === false ? "false" : "",
        includesBackpack: Boolean(serviceToEdit.includesBackpack),
        includesCarryOn: Boolean(serviceToEdit.includesCarryOn),
        includesCheckedBag: Boolean(serviceToEdit.includesCheckedBag),
        // Round-trip: FlightSegmentDto expone origin/originCity/destination/destinationCity
        // desde siempre (ADR-018); fallback "" cuando nunca se cargaron.
        origin: serviceToEdit.origin || "",
        originCity: serviceToEdit.originCity || "",
        destination: serviceToEdit.destination || "",
        destinationCity: serviceToEdit.destinationCity || "",
        // Round-trip: el backend devuelve TimeOnly como "HH:mm:ss" (ej. "08:30:00"). El
        // casillero <input type="time"> necesita "HH:mm" — cortamos a los primeros 5
        // caracteres, mismo gesto que .split("T")[0] usa para las fechas de arriba.
        outboundDepartureTime: (serviceToEdit.outboundDepartureTime || "").slice(0, 5),
        outboundArrivalTime: (serviceToEdit.outboundArrivalTime || "").slice(0, 5),
        returnDepartureTime: (serviceToEdit.returnDepartureTime || "").slice(0, 5),
        returnArrivalTime: (serviceToEdit.returnArrivalTime || "").slice(0, 5),
        // Fix #3 (auditoría de coherencia 2026-08-10, GRAVE) — ver resolverRateIdDeEdicion
        // en inlineServiceFormHelpers.js para el detalle completo del bug.
        rateId: resolverRateIdDeEdicion(serviceToEdit),
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
        // Fix #3 (auditoría de coherencia 2026-08-10, GRAVE) — ver resolverRateIdDeEdicion
        // en inlineServiceFormHelpers.js para el detalle completo del bug.
        rateId: resolverRateIdDeEdicion(serviceToEdit),
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
        // Fix #3 (auditoría de coherencia 2026-08-10, GRAVE) — ver resolverRateIdDeEdicion
        // en inlineServiceFormHelpers.js para el detalle completo del bug.
        rateId: resolverRateIdDeEdicion(serviceToEdit),
        newCatalogProduct: null,
    };
}

function buildAssistanceFormInitial(serviceToEdit) {
    if (!serviceToEdit) {
        return {
            planName: "", supplierId: "", validFrom: "", validTo: "",
            passengers: "", unitNetCost: "", unitSalePrice: "", currency: "ARS",
            voucherNumbers: "", upgrades: "", confirmationNumber: "",
            rateId: null, newCatalogProduct: null,
        };
    }
    // validFrom/validTo son date-only en el backend
    const pasajeros = Math.max(
        Number(serviceToEdit.adults) || Number(serviceToEdit.passengers) || 1, 1
    );
    const validFrom = (serviceToEdit.validFrom || "").split("T")[0] || "";
    const validTo = (serviceToEdit.validTo || "").split("T")[0] || "";
    // El backend guarda netCost/salePrice como TOTAL de la asistencia (precio por persona/día
    // × días × pasajeros), igual que Hotel (noches × habitaciones) y Paquete (pasajeros) más
    // arriba. Para precargar el campo "por persona/día" del form hay que deshacer esa cuenta.
    // Si no hay vigencia cargada el factor da 0 y no hay forma de deducir el unitario: se
    // muestra el total tal cual y el vendedor lo corrige a mano (evita dividir por cero).
    const diasVigencia = calcularDiasVigencia(validFrom, validTo);
    const factorTotal = Math.max(diasVigencia, 0) * pasajeros;
    return {
        // ADR-018: la identidad de la asistencia se guarda en planType (ya nullable en el backend).
        // Fallback a description/planName/name para servicios cargados antes de ADR-018.
        planName: serviceToEdit.planType || serviceToEdit.description || serviceToEdit.planName || serviceToEdit.name || "",
        supplierId: serviceToEdit.supplierId || serviceToEdit.supplierPublicId || "",
        validFrom,
        validTo,
        passengers: String(pasajeros),
        unitNetCost: factorTotal > 0
            ? String(redondearDinero((serviceToEdit.netCost || 0) / factorTotal))
            : String(serviceToEdit.netCost || ""),
        unitSalePrice: factorTotal > 0
            ? String(redondearDinero((serviceToEdit.salePrice || 0) / factorTotal))
            : String(serviceToEdit.salePrice || ""),
        currency: serviceToEdit.currency || "ARS",
        voucherNumbers: serviceToEdit.policyNumber || serviceToEdit.voucherNumbers || "",
        upgrades: serviceToEdit.notes || serviceToEdit.upgrades || "",
        confirmationNumber: serviceToEdit.confirmationNumber || "",
        // Fix #3 (auditoría de coherencia 2026-08-10, GRAVE) — ver resolverRateIdDeEdicion
        // en inlineServiceFormHelpers.js para el detalle completo del bug.
        rateId: resolverRateIdDeEdicion(serviceToEdit),
        newCatalogProduct: null,
    };
}

// ─── Payload de guardado, por tipo ─────────────────────────────────────────────
//
// Opciones A/B/C (spec 2026-08-12, §3.1, decisión #6 firmada): cuando el vendedor marca un
// servicio como "alternativa de" otro servicio YA cargado que todavía no tiene grupo, hay que
// backfillear ese OTRO servicio (el "socio") con un PUT de round-trip completo — no solo con
// optionGroup/optionLabel, porque los demás campos del Update*Request NO son anti-clobber (si se
// omiten, el backend los pisa con su default y se perdería el precio/nombre/fechas reales del
// socio). Por eso `buildPayload` de acá abajo se separó en 5 funciones PURAS reusables: la misma
// función que arma el payload del formulario ACTIVO también arma el payload del socio, a partir
// de reconstruir su form con build*FormInitial (mismos builders de arriba). Ver
// actualizarOptionGroupDelSocio, más abajo, donde se conectan.

function buildHotelPayload(formHotel, canSeeCost) {
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
        // Bug 2 (QA 11/08/2026): Math.max(...,1) en vez de "|| 1" — con "|| 1" un
        // valor negativo ("-1") es TRUTHY en JS, así que se colaba tal cual al
        // backend. validarForm() ya frena esto en pantalla; este clamp es la red
        // final, mismo criterio que ya usa `habitaciones` (línea de arriba).
        adults: Math.max(Number(formHotel.passengers) || 1, 1),
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
        // roomCategory es opcional (a diferencia de mealPlan/roomType): "" -> null,
        // el backend la acepta como el nombre fino de la variante (spec 2026-08-07).
        roomCategory: formHotel.roomCategory?.trim() || null,
        confirmationNumber: formHotel.confirmationNumber || null,
        address: formHotel.address || null,
        // operatorPaymentDeadline eliminado en F2: el aviso viene del backend (firstStartDate).
        // starRating (spec 2026-08-12, §2): "" -> null (Sin especificar), string numérico -> Number.
        starRating: formHotel.starRating ? Number(formHotel.starRating) : null,
        // Plan de cuotas (spec 2026-08-13, §8.3): dato informativo del PDF, no participa del
        // cálculo de Venta total (que sigue siendo noches × habitaciones × precio, arriba). "" o
        // "0" -> null: el PDF simplemente no imprime la línea si no hay plan cargado.
        installmentsCount: formHotel.installmentsCount && Number(formHotel.installmentsCount) > 0
            ? Number(formHotel.installmentsCount)
            : null,
        // Fix reviewer (14/08): mismo criterio que installmentsCount de arriba — "0" es un string
        // truthy en JS (`"0" ? ... : ...` entra por la rama del "sí"), así que sin el > 0 explícito
        // un valor "0" tipeado a mano viajaba como 0 en vez de null.
        installmentAmount: formHotel.installmentAmount && Number(formHotel.installmentAmount) > 0
            ? Number(formHotel.installmentAmount)
            : null,
    };
    if (formHotel.rateId) {
        payload.rateId = formHotel.rateId;
    } else if (formHotel.newCatalogProduct) {
        payload.newCatalogProduct = { ...formHotel.newCatalogProduct };
        payload.supplierId = formHotel.newCatalogProduct.supplierPublicId || null;
    }
    return payload;
}

function buildFlightPayload(formVuelo, canSeeCost) {
    const payload = {
        // ADR-018: la identidad del vuelo va en productName, no en description.
        // El backend (FlightSegment) tiene columna ProductName (varchar200, nullable).
        productName: formVuelo.routeName?.trim() || "",
        // ══════════════════════════════════════════════════════════════════════════
        // SEMÁNTICA INTOCABLE (obra "PDF completo", 2026-08-13, decisión firmada del
        // dueño) — ver el comentario largo en FlightSegment.cs (backend), citado acá:
        //   departureTime/arrivalTime → VENTANA del viaje (fecha de ida/vuelta a
        //   medianoche, alimenta ReservaScheduleCalculator/ADR-053). NO es un horario.
        //   outboundDepartureTime/outboundArrivalTime/returnDepartureTime/
        //   returnArrivalTime → HORARIOS del papel (PDF de presupuesto), viajan
        //   SIEMPRE por sus 4 campos propios, jamás pisando departureTime/arrivalTime.
        // Hora de pared sin conversión UTC (véase ServiceFormModal línea ~2286)
        departureTime: formVuelo.departureDate ? `${formVuelo.departureDate}T00:00:00` : null,
        arrivalTime: formVuelo.returnDate ? `${formVuelo.returnDate}T00:00:00` : null,
        // Aeropuertos (spec 2026-08-13, §2): texto libre, nunca la palabra "IATA" en pantalla.
        // Estos 4 campos SÍ se mapean por convención en el UPDATE del backend (no son
        // anti-clobber como los 4 de abajo) — round-trip normal, mandamos lo que hay en el form.
        origin: formVuelo.origin?.trim() || null,
        originCity: formVuelo.originCity?.trim() || null,
        destination: formVuelo.destination?.trim() || null,
        destinationCity: formVuelo.destinationCity?.trim() || null,
        // Horarios del papel (spec 2026-08-13, §1/§8.2): "" -> null (no informado). El
        // backend acepta directamente el string "HH:mm" que entrega un <input type="time">
        // (verificado por test permanente: FlightRequestJsonBindingTests, backend).
        outboundDepartureTime: formVuelo.outboundDepartureTime || null,
        outboundArrivalTime: formVuelo.outboundArrivalTime || null,
        returnDepartureTime: formVuelo.returnDepartureTime || null,
        returnArrivalTime: formVuelo.returnArrivalTime || null,
        // Bug 2 (QA 11/08/2026): Math.max(...,1) — sin esto, un "-1" tipeado a mano
        // (o pegado con el mouse) viajaba tal cual al backend. validarForm() ya lo
        // frena en pantalla; esto es la red final, antes de armar el payload.
        passengerCount: formVuelo.passengers ? Math.max(Number(formVuelo.passengers), 1) : null,
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
        // Semáforo de DNI vencido para cabotaje (2026-08-03): a diferencia de cabinClass,
        // acá "" (Sin definir elegido en el select) NO se manda como null. El backend
        // interpreta null/ausente como "no tocar el ámbito ya guardado" (ParseOrNull),
        // así que un null nunca podría borrar un "Nacional" cargado por error. Por eso
        // mandamos el token literal "SinDefinir" (ServiceGeographicScopeText.Cleared en
        // el backend), que el backend SI reconoce como "volver a Sin definir a propósito".
        // Como el form siempre rehidrata el valor guardado (buildFlightFormInitial),
        // mandar el valor del select siempre refleja la intención visible del vendedor.
        geographicScope: formVuelo.geographicScope || "SinDefinir",
        // isDirect/includes* (spec 2026-08-12, §1): a diferencia de cabinClass, ACÁ SÍ mandamos
        // el estado real siempre (round-trip, nunca null) — son campos anti-clobber en el
        // backend (null = no tocar) y esta ficha YA los conoce, así que el valor visible en
        // pantalla es siempre la intención real del vendedor (ver el comentario largo en
        // BookingService.cs junto a estos 4 campos, que anticipa exactamente esta tanda).
        isDirect: formVuelo.isDirect === "" || formVuelo.isDirect == null ? null : formVuelo.isDirect === "true",
        includesBackpack: Boolean(formVuelo.includesBackpack),
        includesCarryOn: Boolean(formVuelo.includesCarryOn),
        includesCheckedBag: Boolean(formVuelo.includesCheckedBag),
    };
    if (formVuelo.rateId) {
        payload.rateId = formVuelo.rateId;
    } else if (formVuelo.newCatalogProduct) {
        payload.newCatalogProduct = { ...formVuelo.newCatalogProduct };
        payload.supplierId = formVuelo.newCatalogProduct.supplierPublicId || null;
    }
    return payload;
}

function buildTransferPayload(formTraslado, canSeeCost) {
    const payload = {
        // ADR-018: la identidad del traslado va en productName, no en description.
        // El backend (TransferBooking) tiene columna ProductName (varchar200, nullable).
        productName: formTraslado.routeName?.trim() || "",
        pickupDateTime: formTraslado.pickupDate
            ? `${formTraslado.pickupDate}T${formTraslado.pickupTime || "00:00"}:00`
            : null,
        // Bug 2 (QA 11/08/2026): mismo clamp que Aéreo — Math.max(...,1), nunca un
        // negativo tipeado/pegado a mano.
        passengers: formTraslado.passengers ? Math.max(Number(formTraslado.passengers), 1) : null,
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

function buildPackagePayload(formPaquete, canSeeCost) {
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

function buildAssistancePayload(formAsistencia, canSeeCost) {
    // El backend espera netCost/salePrice como TOTAL de la asistencia (precio por
    // persona/día × días de vigencia × pasajeros), igual que Hotel (noches ×
    // habitaciones) y Paquete (pasajeros) más arriba. Reusamos calcularTotalesAsistencia
    // (mismo cálculo que ya se muestra en pantalla en AssistanceInlineForm) para no
    // duplicar la cuenta a mano y no desincronizarla del total que el vendedor ve.
    const totales = calcularTotalesAsistencia({
        unitSalePrice: formAsistencia.unitSalePrice,
        unitNetCost: formAsistencia.unitNetCost,
        passengers: formAsistencia.passengers,
        validFrom: formAsistencia.validFrom,
        validTo: formAsistencia.validTo,
        canSeeCost,
    });

    const payload = {
        // ADR-018: la identidad de la asistencia va en planType, no en description.
        // El backend (AssistanceBooking) ya tenía PlanType nullable.
        planType: formAsistencia.planName?.trim() || "",
        // Fecha de pared sin conversión UTC, igual que Hotel/Vuelo más arriba (bug fechas
        // corridas 2026-07-16). El backend normaliza esto con NormalizeCalendarDate
        // (BookingService), que acepta tanto con Z como sin Z — pero unificamos el contrato.
        validFrom: formAsistencia.validFrom ? `${formAsistencia.validFrom}T00:00:00` : null,
        validTo: formAsistencia.validTo ? `${formAsistencia.validTo}T00:00:00` : null,
        // Bug 2 (QA 11/08/2026): Math.max(...,1) — mismo clamp que ya usa
        // calcularTotalesAsistencia() para el total de venta/costo (arriba), así
        // `adults` nunca queda desincronizado con la cuenta que el vendedor ve.
        adults: Math.max(Number(formAsistencia.passengers) || 1, 1),
        children: 0,
        supplierId: formAsistencia.supplierId || null,
        netCost: canSeeCost ? (totales.costoTotal ?? 0) : 0,
        salePrice: totales.ventaTotal,
        tax: 0,
        currency: formAsistencia.currency || "ARS",
        // policyNumber se usa para los vouchers (campo existente en el backend)
        policyNumber: formAsistencia.voucherNumbers || null,
        notes: formAsistencia.upgrades || null,
        confirmationNumber: formAsistencia.confirmationNumber || null,
    };
    if (formAsistencia.rateId) {
        payload.rateId = formAsistencia.rateId;
    } else if (formAsistencia.newCatalogProduct) {
        payload.newCatalogProduct = { ...formAsistencia.newCatalogProduct };
        payload.supplierId = formAsistencia.newCatalogProduct.supplierPublicId || null;
    }
    return payload;
}

// Mapas recordKind -> builder, para reconstruir el form/payload de un servicio AJENO al tab
// activo (el "socio" de las Opciones A/B/C — ver el comentario largo más arriba y
// actualizarOptionGroupDelSocio, más abajo). "generic" no participa: no tiene optionGroup.
const BUILD_FORM_INITIAL_BY_RECORD_KIND = {
    hotel: buildHotelFormInitial,
    flight: buildFlightFormInitial,
    transfer: buildTransferFormInitial,
    package: buildPackageFormInitial,
    assistance: buildAssistanceFormInitial,
};
const BUILD_PAYLOAD_BY_RECORD_KIND = {
    hotel: buildHotelPayload,
    flight: buildFlightPayload,
    transfer: buildTransferPayload,
    package: buildPackagePayload,
    assistance: buildAssistancePayload,
};

/**
 * Opciones A/B/C (spec 2026-08-12, §3.1, decisión #6): backfillea optionGroup/optionLabel en un
 * servicio AJENO (el "socio" elegido en el select "¿Alternativa de cuál?") que todavía era un
 * servicio "normal", sin grupo. Reconstruye su payload de round-trip COMPLETO a partir de sus
 * propios datos guardados (mismos builders que usa esta ficha para abrir "Editar") — así el PUT no
 * pisa ningún campo del socio con un default vacío (varios campos del Update*Request NO son
 * anti-clobber: NetCost/SalePrice/HotelName/etc. se aplican SIEMPRE, tal cual vengan).
 */
async function actualizarOptionGroupDelSocio(reservaId, socio, optionGroup, optionLabel, canSeeCost) {
    const buildFormDelTipo = BUILD_FORM_INITIAL_BY_RECORD_KIND[socio.recordKind];
    const buildPayloadDelTipo = BUILD_PAYLOAD_BY_RECORD_KIND[socio.recordKind];
    if (!buildFormDelTipo || !buildPayloadDelTipo) return; // tipo genérico: no tiene optionGroup.

    const formDelSocio = buildFormDelTipo(socio);
    const payloadDelSocio = { ...buildPayloadDelTipo(formDelSocio, canSeeCost), optionGroup, optionLabel };
    const endpoint = getServiceMutationEndpoint(reservaId, socio);
    await api.put(endpoint, payloadDelSocio);
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
 *   serviciosCargados   — Opciones A/B/C (spec 2026-08-12, §3.1): lista de servicios YA cargados
 *                         en esta reserva (normalizeReservaServices), para el select "¿Alternativa
 *                         de cuál?". Si no llega, el checkbox igual se muestra pero el select
 *                         queda con la lista vacía (degradación elegante).
 *
 * Obra "anular sin factura" (2026-07-23): el PUT de edición puede seguir rechazando con 409
 * cuando se reasigna el operador de un servicio pagado o se baja su estado (el servicio ya
 * tiene pagos al operador que quedarían sin resolver) — pero el motor ya NO ofrece "emitir
 * factura" como salida, así que este componente ya no necesita un botón de camino para ese
 * 409: el Cartel Emergente de más abajo solo muestra el mensaje real (tal cual) + "Entendido".
 */
export function ServiceInlineCard({ reservaId, serviceToEdit, suppliers, onGuardado, onCancelar, serviciosCargados = [] }) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    // La pestaña activa: si estamos editando, detectamos el tipo automáticamente.
    // Al editar, la pestaña queda bloqueada (no se puede cambiar de tipo).
    const [tabActiva, setTabActiva] = useState(() => detectarTabParaEdicion(serviceToEdit));

    // Subida acá arriba (antes vivía más abajo) porque los estados de "sugerido"/"tocado"
    // de acá abajo necesitan su valor inicial (fix #4, auditoría 2026-08-10 — ver más abajo).
    const esEdicion = Boolean(serviceToEdit);

    // ─── Opciones A/B/C (spec 2026-08-12, §3.1) ───────────────────────────────────
    // `marcarComoAlternativa`: estado del checkbox "Es una alternativa de otro servicio ya
    // cargado". Arranca tildado SOLO si el servicio que se está editando YA pertenece a un
    // grupo — en cualquier otro caso (alta nueva, o editar un servicio "normal") arranca
    // destildado. `yaPerteneceAGrupo` es la foto de "cómo llegó" (no cambia con el checkbox):
    // la usamos para decidir si mostrar el select "¿Alternativa de cuál?" (solo tiene sentido
    // para armar un grupo NUEVO) o el texto de solo lectura de un grupo ya armado.
    const yaPerteneceAGrupo = Boolean((serviceToEdit?.optionGroup || "").trim());
    const [marcarComoAlternativa, setMarcarComoAlternativa] = useState(() => yaPerteneceAGrupo);
    const [alternativaDeSeleccionada, setAlternativaDeSeleccionada] = useState("");

    // Servicios elegibles para "¿Alternativa de cuál?": vivos (no anulados), del tipo con
    // optionGroup (no genérico) y sin contar al propio servicio que se está editando.
    const opcionesDeAlternativa = useMemo(() => {
        const publicIdPropio = esEdicion ? getReservationServicePublicId(serviceToEdit) : null;
        return (serviciosCargados || []).filter((servicio) => {
            if (servicio.recordKind === "generic") return false; // sin optionGroup en el backend.
            if (publicIdPropio && getReservationServicePublicId(servicio) === publicIdPropio) return false;
            return esServicioVivoParaOpciones(servicio);
        });
    }, [serviciosCargados, esEdicion, serviceToEdit]);

    // Qué cambio de optionGroup/optionLabel hay que mandar en el payload de ESTE servicio (no
    // del socio — eso lo resuelve `enviarGuardado` más abajo). `null` = el vendedor no tocó el
    // checkbox, no se manda nada (anti-clobber conserva lo que ya había).
    const cambioDeOpcionPendiente = useMemo(() => {
        if (yaPerteneceAGrupo) {
            // Único movimiento posible sobre un servicio que YA es parte de un grupo, en esta
            // tanda: desmarcar para sacarlo (verificado contra el backend — mandar string vacío
            // SÍ limpia optionGroup/optionLabel, ver OptionGroupRules.Normalize + el anti-clobber
            // de BookingService.cs: "" no es null, así que igual entra a la rama que reemplaza).
            return marcarComoAlternativa ? null : { optionGroup: "", optionLabel: "" };
        }
        if (!marcarComoAlternativa || !alternativaDeSeleccionada) return null;
        const socio = opcionesDeAlternativa.find(
            (servicio) => construirClaveServicio(servicio) === alternativaDeSeleccionada
        );
        if (!socio) return null;
        const asignacion = calcularAsignacionDeOpcion({
            servicioSocio: socio,
            todosLosServicios: serviciosCargados,
            publicIdAExcluir: esEdicion ? getReservationServicePublicId(serviceToEdit) : null,
        });
        return { optionGroup: asignacion.optionGroup, optionLabel: asignacion.optionLabel, socio, asignacion };
    }, [yaPerteneceAGrupo, marcarComoAlternativa, alternativaDeSeleccionada, opcionesDeAlternativa, serviciosCargados, esEdicion, serviceToEdit]);

    // â”€â”€â”€ Estados de formulario por tipo â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Inicializamos todos con sus builders; solo el activo se usa para guardar.
    const [formHotel, setFormHotel] = useState(() => buildHotelFormInitial(serviceToEdit?.recordKind === "hotel" ? serviceToEdit : null));
    const [formVuelo, setFormVuelo] = useState(() => buildFlightFormInitial(serviceToEdit?.recordKind === "flight" ? serviceToEdit : null));
    const [formTraslado, setFormTraslado] = useState(() => buildTransferFormInitial(serviceToEdit?.recordKind === "transfer" ? serviceToEdit : null));
    const [formPaquete, setFormPaquete] = useState(() => buildPackageFormInitial(serviceToEdit?.recordKind === "package" ? serviceToEdit : null));
    const [formAsistencia, setFormAsistencia] = useState(() => buildAssistanceFormInitial(serviceToEdit?.recordKind === "assistance" ? serviceToEdit : null));

    // ─── Fix #4 (auditoría de coherencia 2026-08-10) ──────────────────────────────
    // `camposSugeridos` (qué campos siguen "en amarillo", sugerencia sin confirmar) y
    // los flags de precio/moneda "tocados a mano" vivían como useState LOCAL de cada
    // Inline*Form — pero cada form se REMONTA (instancia de React nueva) al cambiar de
    // solapa, aunque su `form` (los VALORES) siga vivo acá arriba, levantado. Un
    // remount resetea el useState local a su default (`false`) — así que un campo que
    // seguía siendo sugerencia (amarillo, reemplazable) se volvía "protegido" por error
    // al volver a esa solapa, y viceversa. Subir estos 3 estados junto con `form`
    // (misma altura, mismo patrón) los deja sobrevivir al remount igual que los valores.
    //
    // Consecuencias reales que este fix arregla: "crear nuevo" veía plata vieja de OTRA
    // solapa como si el vendedor la hubiera tipeado a mano (y no la limpiaba); el precio
    // recién tipeado perdía su protección "no tocar" al volver a la solapa.
    const [camposSugeridosHotel, setCamposSugeridosHotel] = useState({
        supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false, checkIn: false, checkOut: false,
    });
    const [camposSugeridosVuelo, setCamposSugeridosVuelo] = useState({
        supplierId: false, netCost: false, salePrice: false, currency: false, departureDate: false, returnDate: false,
    });
    const [camposSugeridosTraslado, setCamposSugeridosTraslado] = useState({
        supplierId: false, netCost: false, salePrice: false, currency: false, pickupDate: false,
    });
    const [camposSugeridosPaquete, setCamposSugeridosPaquete] = useState({
        supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false, startDate: false, endDate: false,
    });
    const [camposSugeridosAsistencia, setCamposSugeridosAsistencia] = useState({
        supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false, validFrom: false, validTo: false,
    });

    // Los flags de "tocado a mano" (para la sugerencia POR VARIANTE) solo existen en
    // Hotel/Aéreo/Traslado (los 3 forms que usan useVariantPriceSuggestion, spec
    // 2026-08-07 §3.3) — Paquete/Asistencia no tienen sugerencia por variante, así que
    // no necesitan este flag. Arrancan en `esEdicion` (editar un servicio YA GUARDADO:
    // ese precio/moneda es del vendedor, nunca una sugerencia — ver el comentario largo
    // en HotelInlineForm.jsx).
    const [precioTocadoHotel, setPrecioTocadoHotel] = useState(esEdicion);
    const [monedaTocadaHotel, setMonedaTocadaHotel] = useState(esEdicion);
    const [precioTocadoVuelo, setPrecioTocadoVuelo] = useState(esEdicion);
    const [monedaTocadaVuelo, setMonedaTocadaVuelo] = useState(esEdicion);
    const [precioTocadoTraslado, setPrecioTocadoTraslado] = useState(esEdicion);
    const [monedaTocadaTraslado, setMonedaTocadaTraslado] = useState(esEdicion);

    // ─── Fix REGRESIÓN #1+#6 (re-review 2026-08-10) ───────────────────────────────
    // `camposSugeridos` (arriba) cumple DOS trabajos incompatibles hasta este fix:
    // pintar el amarillo (que #6 apaga con cualquier tecleo del buscador) Y decidir qué
    // campo es reemplazable por una selección nueva (que #1 necesita CONSERVAR entre
    // tecleos). Repro real: elegir Hotel A (queda todo sugerido/amarillo) → escribir el
    // nombre de Hotel B en el buscador (fix #6 apaga TODOS los amarillos, sin que el
    // vendedor haya tocado ningún campo puntual) → elegir Hotel B → el sistema veía la
    // plata de A como "tocada a mano" (porque camposSugeridos ya estaba todo en false) y
    // NO la reemplazaba — Hotel B se guardaba con la plata de A, sin amarillo.
    //
    // `camposTocadosAMano` es un estado APARTE, con el mismo shape que camposSugeridos,
    // que representa SOLO "el vendedor tocó este campo puntual a mano" — se prende
    // ÚNICAMENTE en el onChange de CADA campo (operador/costo/venta/moneda/fechas), NUNCA
    // por tipear en el buscador de producto. Solo se resetea ENTERO cuando cambia el
    // CONTEXTO (salto de solapa que limpia el origen — fix #11, ver
    // limpiarBusquedaDelFormOrigen más abajo): elegir o crear un producto NO lo resetea
    // — al contrario, ahí es donde más importa que lo tocado a mano siga protegido.
    const [camposTocadosAManoHotel, setCamposTocadosAManoHotel] = useState({
        supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false, checkIn: false, checkOut: false,
    });
    const [camposTocadosAManoVuelo, setCamposTocadosAManoVuelo] = useState({
        supplierId: false, netCost: false, salePrice: false, currency: false, departureDate: false, returnDate: false,
    });
    const [camposTocadosAManoTraslado, setCamposTocadosAManoTraslado] = useState({
        supplierId: false, netCost: false, salePrice: false, currency: false, pickupDate: false,
    });
    const [camposTocadosAManoPaquete, setCamposTocadosAManoPaquete] = useState({
        supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false, startDate: false, endDate: false,
    });
    const [camposTocadosAManoAsistencia, setCamposTocadosAManoAsistencia] = useState({
        supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false, validFrom: false, validTo: false,
    });

    // ─── Buscador versátil: salto de solapa (spec FIRMADA 2026-08-10, D1..D13) ───────
    // Cuando el vendedor elige, en el buscador de CUALQUIER solapa, una fila de OTRO
    // tipo de servicio, la ficha salta de solapa sola (D3, silencioso e inmediato) y deja
    // la selección "pendiente" acá hasta que el formulario del tipo destino la consuma
    // (con `useSeleccionPendienteDelTipo`, ver los 5 Inline*Form.jsx).
    const [seleccionPendiente, setSeleccionPendiente] = useState(null);

    // Limpia el campo de búsqueda de producto del form de ORIGEN (nombre/rateId/
    // newCatalogProduct) — D10: nada de lo que el vendedor haya tipeado A MANO en esa
    // solapa se toca; si vuelve, lo encuentra intacto.
    //
    // Fix #11 (auditoría de coherencia 2026-08-10): ADEMÁS, los campos que NUNCA fueron
    // tocados a mano en el origen (vinieron de la selección que se está deshaciendo, D3)
    // se limpian con ella — reusa `resolverCamposALimpiarAlCrearNuevo` (mismo criterio
    // del Bug #28).
    //
    // Fix REGRESIÓN #1+#6 (re-review 2026-08-10): la decisión de "qué preservar" pasó de
    // `camposSugeridos` (el amarillo, que #6 apaga con cualquier tecleo y ya no sirve
    // como señal) a `camposTocadosAMano` — ver el comentario largo donde se declara ese
    // estado, más arriba. Además, "salto de solapa que limpia el origen" es uno de los
    // puntos donde `camposTocadosAMano` SÍ se resetea entero (cambia el contexto: el
    // vendedor se va de esta solapa) — a diferencia de elegir/crear un producto, que
    // nunca lo resetea.
    const limpiarBusquedaDelFormOrigen = useCallback((tabOrigen) => {
        // Apaga a `false` los flags de "sugerido" (amarillo) de los campos que se acaban
        // de limpiar — visual nomás, no decide nada (eso ya lo hizo camposTocadosAMano).
        const apagarSugeridos = (setCamposSugeridos, camposLimpios) => {
            setCamposSugeridos((prev) => {
                const next = { ...prev };
                for (const clave of Object.keys(camposLimpios)) next[clave] = false;
                return next;
            });
        };
        // Fix residual (re-review 2026-08-10, ítem B): OJO — NO resetear el mapa entero.
        // `camposLimpios` (arriba) preserva el VALOR de los campos tocados a mano (el
        // vendedor los tipeó antes de saltar de solapa); si acá apagábamos su bandera de
        // "tocado a mano" igual, al volver a esta solapa y elegir un producto ese valor
        // tipeado quedaba "libre" (camposTocadosAMano en false) y una selección nueva lo
        // pisaba en silencio — el mismo bug de plata equivocada, pero por el camino del
        // salto-de-solapa en vez del camino del tecleo. `resolverTocadosAManoTrasLimpiarOrigen`
        // (inlineServiceFormHelpers.js, testeada por separado) solo apaga la bandera de los
        // campos que EFECTIVAMENTE volvieron al default; los tocados a mano quedan protegidos.
        const resetearTocadosAMano = (setCamposTocadosAMano, camposLimpios, camposTocadosAManoDeEsteTipo) => {
            setCamposTocadosAMano(resolverTocadosAManoTrasLimpiarOrigen(camposTocadosAManoDeEsteTipo, camposLimpios));
        };

        if (tabOrigen === "Hotel") {
            const valoresPorDefecto = { supplierId: "", unitNetCost: "", unitSalePrice: "", currency: "ARS", checkIn: "", checkOut: "" };
            const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
                { supplierId: formHotel.supplierId, unitNetCost: formHotel.unitNetCost, unitSalePrice: formHotel.unitSalePrice, currency: formHotel.currency, checkIn: formHotel.checkIn, checkOut: formHotel.checkOut },
                camposTocadosAManoHotel,
                valoresPorDefecto
            );
            // city (fix #5, mismo espíritu): es un dato de la IDENTIDAD del hotel elegido
            // (lo llenaba handleSelectExisting desde el subtitle del resultado) — se va
            // junto con hotelName/rateId, no es algo que el vendedor tipee a mano.
            setFormHotel((prev) => ({ ...prev, hotelName: "", city: "", rateId: null, newCatalogProduct: null, ...camposLimpios }));
            apagarSugeridos(setCamposSugeridosHotel, camposLimpios);
            resetearTocadosAMano(setCamposTocadosAManoHotel, camposLimpios, camposTocadosAManoHotel);
        } else if (tabOrigen === "Aereo") {
            const valoresPorDefecto = { supplierId: "", netCost: "", salePrice: "", currency: "ARS", departureDate: "", returnDate: "" };
            const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
                { supplierId: formVuelo.supplierId, netCost: formVuelo.netCost, salePrice: formVuelo.salePrice, currency: formVuelo.currency, departureDate: formVuelo.departureDate, returnDate: formVuelo.returnDate },
                camposTocadosAManoVuelo,
                valoresPorDefecto
            );
            setFormVuelo((prev) => ({ ...prev, routeName: "", rateId: null, newCatalogProduct: null, ...camposLimpios }));
            apagarSugeridos(setCamposSugeridosVuelo, camposLimpios);
            resetearTocadosAMano(setCamposTocadosAManoVuelo, camposLimpios, camposTocadosAManoVuelo);
        } else if (tabOrigen === "Traslado") {
            const valoresPorDefecto = { supplierId: "", netCost: "", salePrice: "", currency: "ARS", pickupDate: "" };
            const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
                { supplierId: formTraslado.supplierId, netCost: formTraslado.netCost, salePrice: formTraslado.salePrice, currency: formTraslado.currency, pickupDate: formTraslado.pickupDate },
                camposTocadosAManoTraslado,
                valoresPorDefecto
            );
            setFormTraslado((prev) => ({ ...prev, routeName: "", rateId: null, newCatalogProduct: null, ...camposLimpios }));
            apagarSugeridos(setCamposSugeridosTraslado, camposLimpios);
            resetearTocadosAMano(setCamposTocadosAManoTraslado, camposLimpios, camposTocadosAManoTraslado);
        } else if (tabOrigen === "Paquete") {
            const valoresPorDefecto = { supplierId: "", unitNetCost: "", unitSalePrice: "", currency: "ARS", startDate: "", endDate: "" };
            const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
                { supplierId: formPaquete.supplierId, unitNetCost: formPaquete.unitNetCost, unitSalePrice: formPaquete.unitSalePrice, currency: formPaquete.currency, startDate: formPaquete.startDate, endDate: formPaquete.endDate },
                camposTocadosAManoPaquete,
                valoresPorDefecto
            );
            setFormPaquete((prev) => ({ ...prev, packageName: "", rateId: null, newCatalogProduct: null, ...camposLimpios }));
            apagarSugeridos(setCamposSugeridosPaquete, camposLimpios);
            resetearTocadosAMano(setCamposTocadosAManoPaquete, camposLimpios, camposTocadosAManoPaquete);
        } else if (tabOrigen === "Asistencia") {
            const valoresPorDefecto = { supplierId: "", unitNetCost: "", unitSalePrice: "", currency: "ARS", validFrom: "", validTo: "" };
            const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
                { supplierId: formAsistencia.supplierId, unitNetCost: formAsistencia.unitNetCost, unitSalePrice: formAsistencia.unitSalePrice, currency: formAsistencia.currency, validFrom: formAsistencia.validFrom, validTo: formAsistencia.validTo },
                camposTocadosAManoAsistencia,
                valoresPorDefecto
            );
            setFormAsistencia((prev) => ({ ...prev, planName: "", rateId: null, newCatalogProduct: null, ...camposLimpios }));
            apagarSugeridos(setCamposSugeridosAsistencia, camposLimpios);
            resetearTocadosAMano(setCamposTocadosAManoAsistencia, camposLimpios, camposTocadosAManoAsistencia);
        }
    }, [
        formHotel, formVuelo, formTraslado, formPaquete, formAsistencia,
        camposTocadosAManoHotel, camposTocadosAManoVuelo, camposTocadosAManoTraslado, camposTocadosAManoPaquete, camposTocadosAManoAsistencia,
    ]);

    // Handler que reciben los 5 buscadores (`onSelectOtherType` de ProductSearchField):
    // guarda la selección como pendiente, salta de solapa y limpia el buscador de origen.
    // Guard: si el resultado trajera un tipo que no está entre las 5 solapas de esta
    // ficha (no debería pasar con el contrato actual del backend), no hace nada — más
    // vale una selección ignorada que un salto a una solapa que no existe.
    const handleSelectOtherType = useCallback((result, interpretacion) => {
        const tipoDestino = result?.serviceType;
        if (!tipoDestino || !TAB_ENDPOINTS[tipoDestino]) return;
        limpiarBusquedaDelFormOrigen(tabActiva);
        setSeleccionPendiente({ serviceType: tipoDestino, result, interpretacion });
        setTabActiva(tipoDestino);
    }, [tabActiva, limpiarBusquedaDelFormOrigen]);

    // Cada formulario avisa acá cuando ya aplicó su pendiente — así no queda colgada
    // para el próximo salto de solapa.
    const handleConsumirSeleccionPendiente = useCallback(() => {
        setSeleccionPendiente(null);
    }, []);

    // Estado de guardado
    const [guardando, setGuardando] = useState(false);
    // Error CORTO de validación de un campo (lo calcula validarForm() antes de llamar a la
    // API — nunca pasó por el motor). Spec del cartel emergente (2026-07-22, sección 2):
    // este tipo de error sigue INCRUSTADO junto a los botones, nunca en ventana.
    const [errorValidacion, setErrorValidacion] = useState(null);
    // Rechazo LARGO del motor (409/400 real que devolvió el backend al intentar guardar).
    // Spec del cartel emergente (2026-07-22): esto sí es "lo intentaste y el motor te frenó"
    // → va al Cartel Emergente (ventana), nunca incrustado en la ficha. Guarda directo el
    // mensaje tal cual (string) — desde la obra "anular sin factura" (2026-07-23) ningún
    // código de este PUT ofrece botón de camino, así que no hace falta guardar nada más.
    const [rechazoMotor, setRechazoMotor] = useState(null);
    // Aviso ÁMBAR de la P3 "circuito proveedor" (2026-07-22, P1=A del cartel emergente): el
    // motor pidió confirmar que bajar el costo del operador genera saldo a favor. Guarda el
    // `message` tal cual del backend (con el monto exacto) — nunca convive con rechazoMotor
    // (cartel rojo), son dos estados mutuamente excluyentes que se limpian entre sí en cada
    // intento de guardado. También va al Cartel Emergente (traje confirmación, ámbar).
    const [avisoCostoMenorAPagado, setAvisoCostoMenorAPagado] = useState(null);

    // Hallazgo #31 del barrido (2026-07-24): el aviso corto de validación ("Escribí la ruta
    // o aerolínea.", etc.) solo se recalculaba en el próximo click de "Guardar" — validarForm()
    // corre SOLO adentro de handleGuardar. Si el vendedor completaba el campo que faltaba pero
    // no volvía a tocar "Guardar", el cartelito quedaba pegado en pantalla como si el campo
    // siguiera vacío, aunque ya no lo estuviera. Este efecto limpia ese aviso apenas el
    // vendedor vuelve a tocar CUALQUIER campo del tipo activo (o cambia de pestaña) — no hace
    // falta re-validar entero, alcanza con sacar el aviso viejo; el próximo "Guardar" ya lo
    // recalcula de cero si todavía falta algo.
    useEffect(() => {
        setErrorValidacion(null);
    }, [tabActiva, formHotel, formVuelo, formTraslado, formPaquete, formAsistencia]);

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
            // Bug 2 (QA 11/08/2026): el campo dejaba tipear "-1" y el guardado pasaba
            // igual (silenciosamente se guardaba como 1 — ver buildPayload más abajo).
            // Acá lo frenamos con un mensaje claro ANTES de llegar a guardar nada.
            if (formHotel.rooms !== "" && Number(formHotel.rooms) < 1) return "Las habitaciones tienen que ser al menos 1.";
            if (formHotel.passengers !== "" && Number(formHotel.passengers) < 1) return "Los pasajeros tienen que ser al menos 1.";
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
            // Bug 2 (QA 11/08/2026): mismo agujero que Hotel, acá con pasajeros.
            if (formVuelo.passengers !== "" && Number(formVuelo.passengers) < 1) return "Los pasajeros tienen que ser al menos 1.";
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
            // Bug 2 (QA 11/08/2026): mismo agujero que Hotel, acá con pasajeros.
            if (formTraslado.passengers !== "" && Number(formTraslado.passengers) < 1) return "Los pasajeros tienen que ser al menos 1.";
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
            // Bug 2 (QA 11/08/2026): mismo agujero que Hotel, acá con pasajeros.
            if (formPaquete.passengers !== "" && Number(formPaquete.passengers) < 1) return "Los pasajeros tienen que ser al menos 1.";
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
            // Bug 2 (QA 11/08/2026): mismo agujero que Hotel, acá con pasajeros.
            if (formAsistencia.passengers !== "" && Number(formAsistencia.passengers) < 1) return "Los pasajeros tienen que ser al menos 1.";
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
        let payload;
        if (tabActiva === "Hotel") payload = buildHotelPayload(formHotel, canSeeCost);
        else if (tabActiva === "Aereo") payload = buildFlightPayload(formVuelo, canSeeCost);
        else if (tabActiva === "Traslado") payload = buildTransferPayload(formTraslado, canSeeCost);
        else if (tabActiva === "Paquete") payload = buildPackagePayload(formPaquete, canSeeCost);
        else if (tabActiva === "Asistencia") payload = buildAssistancePayload(formAsistencia, canSeeCost);
        else payload = {};

        // Opciones A/B/C (spec 2026-08-12, §3.1): solo sumamos optionGroup/optionLabel al payload
        // si el vendedor TOCÓ el checkbox "Es una alternativa" (marcó o desmarcó) — ver
        // cambioDeOpcionPendiente más abajo. Si no lo tocó, no agregamos nada: el anti-clobber del
        // backend (null = no tocar) conserva lo que ya estaba guardado.
        if (cambioDeOpcionPendiente) {
            payload.optionGroup = cambioDeOpcionPendiente.optionGroup;
            payload.optionLabel = cambioDeOpcionPendiente.optionLabel;
        }
        return payload;
    }, [tabActiva, formHotel, formVuelo, formTraslado, formPaquete, formAsistencia, canSeeCost, cambioDeOpcionPendiente]);

    // â”€â”€â”€ Guardar â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // Arma y envía el PUT/POST del tipo activo. `confirmarCostoMenor` es el flag de la P3:
    // cuando es true, reusa el MISMO `buildPayload()` de siempre y le suma
    // `confirmCostBelowPaid: true` (no hay un builder aparte para el reenvío — es
    // exactamente el mismo guardado, solo que con la marca de confirmación puesta).
    const enviarGuardado = async (confirmarCostoMenor) => {
        setGuardando(true);
        try {
            // Opciones A/B/C (spec 2026-08-12, §3.1, decisión #6): si el socio elegido en "¿Alternativa
            // de cuál?" todavía era un servicio "normal" (sin grupo), lo backfilleamos ANTES de guardar
            // este servicio — así, si algo falla después, el peor caso es un grupo con un solo miembro
            // (el socio con su propio nombre como grupo), que no es ambiguo y no rompe nada; el
            // vendedor puede reintentar y la letra se sigue calculando bien la próxima vez.
            if (cambioDeOpcionPendiente?.asignacion?.socioNecesitaBackfill) {
                await actualizarOptionGroupDelSocio(
                    reservaId,
                    cambioDeOpcionPendiente.socio,
                    cambioDeOpcionPendiente.optionGroup,
                    cambioDeOpcionPendiente.asignacion.socioOptionLabel,
                    canSeeCost
                );
            }

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

            // Si falla, la ficha queda abierta con todo intacto (guía UX ronda 2) y el rechazo
            // real del motor se muestra en el Cartel Emergente (spec 2026-07-22).
            // P1 "circuito proveedor" (2026-07-21): el PUT de edición también puede rechazar
            // cuando se reasigna el operador de un servicio pagado o se baja su estado (la
            // reserva tiene pagos al operador que quedarían sin resolver). Obra "anular sin
            // factura" (2026-07-23): ese rechazo ya NO ofrece ningún botón de camino — el
            // mensaje real del motor (getApiErrorMessage) ya orienta a "gestioná el reembolso
            // con el operador", así que alcanza con mostrarlo tal cual.
            setRechazoMotor(getApiErrorMessage(error, "No se pudo guardar. Revisá la conexión y probá de nuevo."));
        } finally {
            setGuardando(false);
        }
    };

    const handleGuardar = async () => {
        setErrorValidacion(null);
        setRechazoMotor(null);
        setAvisoCostoMenorAPagado(null);

        const mensajeValidacion = validarForm();
        if (mensajeValidacion) {
            // Error corto de campo: nunca pasó por el motor, queda incrustado (no es un
            // rechazo del backend, así que no corresponde al Cartel Emergente).
            setErrorValidacion(mensajeValidacion);
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
        // Marco calmo (estándar visual firmado 2026-08-11, maqueta pantalla 3, clase
        // `.carga`): antes era un borde AZUL de 2px que gritaba más que los datos de
        // adentro — ahora es un borde gris parejo, igual que cualquier otra tarjeta de
        // la app. `dark:` recién se agrega en esta tanda: la carpeta inline-service/
        // no tenía NINGUNA regla de modo oscuro (hallazgo B de la auditoría).
        <div
            className="rounded-[10px] border border-slate-300 bg-white p-4 mt-4 shadow-sm dark:border-slate-700 dark:bg-slate-900"
            data-testid="service-inline-card"
        >
            {/* PESTAÑAS: pastillas .ctab de la maqueta — la activa se pinta con el ÚNICO
                azul de acción del sistema (token `primary`, el mismo de "Guardar
                servicio" más abajo), 32px de alto. Antes eran círculos más grandes en
                azul suelto (`bg-blue-600`, sin relación con el resto de los botones). */}
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
                            className={`inline-flex h-8 items-center gap-1.5 rounded-full border px-3.5 text-xs font-semibold transition-colors ${
                                estaActiva
                                    ? "border-primary bg-primary text-primary-foreground dark:border-primary dark:bg-primary dark:text-primary-foreground"
                                    : esEdicion
                                    ? "border-slate-100 bg-slate-50 text-slate-300 cursor-not-allowed dark:border-slate-800 dark:bg-slate-900 dark:text-slate-700"
                                    : "border-slate-300 bg-white text-slate-600 hover:bg-slate-50 hover:border-slate-400 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700"
                            }`}
                            data-testid={`tab-${id.toLowerCase()}`}
                        >
                            <Icon className="w-3.5 h-3.5" />
                            {label}
                        </button>
                    );
                })}
            </div>

            {/* Opciones A/B/C (spec 2026-08-12, §3.1): A LA VISTA (no en "Más detalles" — cambia
                cómo se relaciona este servicio con otros, no es un dato descriptivo del servicio
                en sí). Un solo checkbox + select condicional, mismo patrón que cualquier campo
                condicional de la app. El sistema arma el grupo y le pone letra solo (A, B, C…) —
                no hay casillero de "grupo" ni de "letra" a la vista: sería un dato técnico que el
                vendedor no necesita escribir. */}
            <div className="mb-4">
                <label className="flex items-center gap-2 text-sm font-medium text-slate-700 dark:text-slate-300">
                    <input
                        type="checkbox"
                        className="h-4 w-4 rounded border-slate-300 accent-primary dark:border-slate-600"
                        checked={marcarComoAlternativa}
                        onChange={(event) => {
                            setMarcarComoAlternativa(event.target.checked);
                            if (!event.target.checked) setAlternativaDeSeleccionada("");
                        }}
                        data-testid="checkbox-es-alternativa"
                    />
                    Es una alternativa de otro servicio ya cargado
                </label>

                {marcarComoAlternativa && (
                    yaPerteneceAGrupo ? (
                        // Servicio que YA es parte de un grupo (venía así al abrir la ficha): en
                        // esta tanda no se puede cambiar de socio, solo desmarcar para sacarlo del
                        // grupo (ver el comentario largo en cambioDeOpcionPendiente más arriba).
                        <p className="mt-2 text-xs text-slate-500 dark:text-slate-400" data-testid="texto-ya-en-grupo">
                            Ya es una alternativa dentro de «{serviceToEdit.optionGroup}».
                        </p>
                    ) : (
                        <div className="mt-2 max-w-sm">
                            <label className="block text-[11px] font-semibold tracking-wide text-slate-500 mb-1 dark:text-slate-400" htmlFor="select-alternativa-de">
                                ¿Alternativa de cuál?
                            </label>
                            <select
                                id="select-alternativa-de"
                                className="w-full py-2 px-2.5 text-[13px] border rounded-[7px] bg-white text-slate-800 border-slate-300 focus:outline-none focus:ring-1 focus:border-primary focus:ring-primary dark:bg-slate-900 dark:text-slate-100 dark:border-slate-600"
                                value={alternativaDeSeleccionada}
                                onChange={(event) => setAlternativaDeSeleccionada(event.target.value)}
                                data-testid="select-alternativa-de"
                            >
                                <option value="">
                                    {opcionesDeAlternativa.length === 0
                                        ? "Ninguno todavía: es la primera opción"
                                        : "Elegí un servicio ya cargado..."}
                                </option>
                                {opcionesDeAlternativa.map((servicio) => (
                                    <option key={construirClaveServicio(servicio)} value={construirClaveServicio(servicio)}>
                                        {servicio.name}{servicio.date ? ` · ${formatDate(servicio.date)}` : ""}
                                    </option>
                                ))}
                            </select>
                        </div>
                    )
                )}
            </div>

            {/* CONTENIDO DE LA PESTAÑA ACTIVA */}
            {/* `seleccionPendiente`/`onSelectOtherType`/`onConsumirSeleccionPendiente`
                (spec 2026-08-10, D1..D13): cableado del salto de solapa — ver el estado
                y los handlers más arriba. Cada form usa `useSeleccionPendienteDelTipo`
                para mirar si la pendiente le corresponde a ÉL. */}
            <div role="tabpanel">
                {tabActiva === "Hotel" && (
                    <HotelInlineForm
                        reservaId={reservaId}
                        form={formHotel}
                        setForm={setFormHotel}
                        suppliers={suppliers}
                        isEditing={esEdicion}
                        onSelectOtherType={handleSelectOtherType}
                        seleccionPendiente={seleccionPendiente}
                        onConsumirSeleccionPendiente={handleConsumirSeleccionPendiente}
                        // Fix #4 (auditoría 2026-08-10): "sugerido"/"tocado" levantados acá
                        // arriba, junto con `form` — sobreviven al remount del cambio de solapa.
                        camposSugeridos={camposSugeridosHotel}
                        setCamposSugeridos={setCamposSugeridosHotel}
                        precioTocadoPorElUsuario={precioTocadoHotel}
                        setPrecioTocadoPorElUsuario={setPrecioTocadoHotel}
                        monedaTocadaPorElUsuario={monedaTocadaHotel}
                        setMonedaTocadaPorElUsuario={setMonedaTocadaHotel}
                        // Fix regresión #1+#6 (re-review 2026-08-10): separado de
                        // camposSugeridos — ver el comentario largo donde se declara.
                        camposTocadosAMano={camposTocadosAManoHotel}
                        setCamposTocadosAMano={setCamposTocadosAManoHotel}
                    />
                )}
                {tabActiva === "Aereo" && (
                    <FlightInlineForm
                        reservaId={reservaId}
                        form={formVuelo}
                        setForm={setFormVuelo}
                        suppliers={suppliers}
                        isEditing={esEdicion}
                        onSelectOtherType={handleSelectOtherType}
                        seleccionPendiente={seleccionPendiente}
                        onConsumirSeleccionPendiente={handleConsumirSeleccionPendiente}
                        camposSugeridos={camposSugeridosVuelo}
                        setCamposSugeridos={setCamposSugeridosVuelo}
                        precioTocadoPorElUsuario={precioTocadoVuelo}
                        setPrecioTocadoPorElUsuario={setPrecioTocadoVuelo}
                        monedaTocadaPorElUsuario={monedaTocadaVuelo}
                        setMonedaTocadaPorElUsuario={setMonedaTocadaVuelo}
                        camposTocadosAMano={camposTocadosAManoVuelo}
                        setCamposTocadosAMano={setCamposTocadosAManoVuelo}
                    />
                )}
                {tabActiva === "Traslado" && (
                    <TransferInlineForm
                        reservaId={reservaId}
                        form={formTraslado}
                        setForm={setFormTraslado}
                        suppliers={suppliers}
                        isEditing={esEdicion}
                        onSelectOtherType={handleSelectOtherType}
                        seleccionPendiente={seleccionPendiente}
                        onConsumirSeleccionPendiente={handleConsumirSeleccionPendiente}
                        camposSugeridos={camposSugeridosTraslado}
                        setCamposSugeridos={setCamposSugeridosTraslado}
                        precioTocadoPorElUsuario={precioTocadoTraslado}
                        setPrecioTocadoPorElUsuario={setPrecioTocadoTraslado}
                        monedaTocadaPorElUsuario={monedaTocadaTraslado}
                        setMonedaTocadaPorElUsuario={setMonedaTocadaTraslado}
                        camposTocadosAMano={camposTocadosAManoTraslado}
                        setCamposTocadosAMano={setCamposTocadosAManoTraslado}
                    />
                )}
                {tabActiva === "Paquete" && (
                    <PackageInlineForm
                        reservaId={reservaId}
                        form={formPaquete}
                        setForm={setFormPaquete}
                        suppliers={suppliers}
                        isEditing={esEdicion}
                        onSelectOtherType={handleSelectOtherType}
                        seleccionPendiente={seleccionPendiente}
                        onConsumirSeleccionPendiente={handleConsumirSeleccionPendiente}
                        // Paquete no usa useVariantPriceSuggestion: sin precioTocado/monedaTocada.
                        camposSugeridos={camposSugeridosPaquete}
                        setCamposSugeridos={setCamposSugeridosPaquete}
                        camposTocadosAMano={camposTocadosAManoPaquete}
                        setCamposTocadosAMano={setCamposTocadosAManoPaquete}
                    />
                )}
                {tabActiva === "Asistencia" && (
                    <AssistanceInlineForm
                        reservaId={reservaId}
                        form={formAsistencia}
                        setForm={setFormAsistencia}
                        suppliers={suppliers}
                        isEditing={esEdicion}
                        onSelectOtherType={handleSelectOtherType}
                        seleccionPendiente={seleccionPendiente}
                        onConsumirSeleccionPendiente={handleConsumirSeleccionPendiente}
                        // Asistencia no usa useVariantPriceSuggestion: sin precioTocado/monedaTocada.
                        camposSugeridos={camposSugeridosAsistencia}
                        setCamposSugeridos={setCamposSugeridosAsistencia}
                        camposTocadosAMano={camposTocadosAManoAsistencia}
                        setCamposTocadosAMano={setCamposTocadosAManoAsistencia}
                    />
                )}
            </div>

            {/* Leyenda del sugerido (maqueta firmada 2026-08-11, clase `.leyenda-sug`):
                excepción explícita a P-15 ("sin cartelitos aclarativos") — el dueño la dejó
                en la maqueta a propósito, así que ACÁ sí va. Explica de una vez lo que
                significa el amarillo de TODOS los campos sugeridos de arriba (P-21: el
                sistema sugiere, nunca decide — el vendedor lo puede pisar y no vuelve solo). */}
            <p className="mt-2 text-[11.5px] text-amber-700 dark:text-amber-400">
                Lo pintado de amarillo es sugerido — lo podés pisar y no vuelve solo.
            </p>

            {/* FOOTER FIJO: totales + botones */}
            <div className="mt-5 pt-4 border-t border-slate-100 dark:border-slate-800 flex flex-col sm:flex-row justify-between items-start sm:items-center gap-3">
                {/* Izquierda: totales */}
                <div className="text-sm text-slate-700 dark:text-slate-300 flex flex-wrap items-center gap-3">
                    {totalesFooter.mostrar && (
                        <>
                            {/* Bug #26 (Tanda 4, 2026-07-24): antes formatearPrecio() no recibía la
                                moneda y siempre mostraba "$" con formato es-AR, aunque el servicio
                                fuera en USD. formActivo?.currency es la moneda REAL del tab activo
                                (Hotel/Aéreo/Traslado/Paquete/Asistencia tienen todos su propio
                                selector de moneda) — "ARS" es solo el fallback si todavía no se eligió. */}
                            <span>
                                Venta <strong>{formatearPrecio(totalesFooter.ventaTotal, formActivo?.currency || "ARS")}</strong>
                            </span>
                            {/* Ganancia: solo para quien tiene permiso de ver costos */}
                            {canSeeCost && totalesFooter.ganancia !== null && (
                                <span className={totalesFooter.ganancia >= 0 ? "font-semibold text-emerald-600 dark:text-emerald-400" : "font-semibold text-red-600 dark:text-red-400"}>
                                    Ganás {formatearPrecio(totalesFooter.ganancia, formActivo?.currency || "ARS")}
                                </span>
                            )}
                        </>
                    )}
                </div>

                {/* Derecha: cartel de aviso/error + botones */}
                <div className="flex flex-col items-end gap-2 w-full sm:w-auto">
                    {/* Aviso 6 del inventario (spec 2026-07-22, P1=A): bajar el costo del operador
                        por debajo de lo ya pagado no bloquea, pero el motor pide confirmar antes
                        de guardar (genera saldo a favor con ese operador) — va al Cartel
                        Emergente igual que un rechazo, según lo que eligió Gastón. */}
                    <CartelEmergente
                        isOpen={Boolean(avisoCostoMenorAPagado)}
                        variant={CARTEL_EMERGENTE_VARIANTES.CONFIRMACION}
                        // El texto es el message tal cual del motor (es-AR, con el monto exacto
                        // de la diferencia) — el front nunca lo reescribe ni lo calcula.
                        message={avisoCostoMenorAPagado}
                        onClose={handleVolverACorregirCosto}
                        closeLabel="Volver a corregir"
                        closeTestId="confirmar-costo-corregir"
                        onConfirm={handleConfirmarCostoMenor}
                        isConfirming={guardando}
                        actionTestId="confirmar-costo-si"
                        dataTestId="inline-card-confirmar-costo"
                    />
                    {/* Error CORTO de un campo (validarForm): sigue incrustado, pegado a los
                        botones — nunca pasó por el motor, así que no es un rechazo "real". */}
                    {errorValidacion && (
                        <div
                            className="flex flex-col gap-2 text-xs text-red-700 bg-red-50 border border-red-200 rounded-lg px-3 py-2 w-full sm:w-auto max-w-sm dark:text-red-300 dark:bg-red-950/30 dark:border-red-900"
                            role="alert"
                            data-testid="inline-card-error"
                        >
                            <div className="flex items-start gap-2">
                                <AlertCircle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                                <span>{errorValidacion}</span>
                            </div>
                        </div>
                    )}
                    {/* Aviso 3 del inventario (spec 2026-07-22): rechazo LARGO del motor (409/400
                        real) → Cartel Emergente, nunca incrustado. Guía UX ronda 2 sigue firme:
                        la ficha queda abierta con todo lo cargado intacto detrás de la ventana.
                        Sin botón de camino (obra "anular sin factura", 2026-07-23): el mensaje
                        real del motor ya le dice al usuario qué hacer, "Entendido" alcanza. */}
                    <CartelEmergente
                        isOpen={Boolean(rechazoMotor)}
                        variant={CARTEL_EMERGENTE_VARIANTES.BLOQUEO}
                        message={rechazoMotor}
                        onClose={() => setRechazoMotor(null)}
                        dataTestId="inline-card-rechazo-motor"
                    />
                    <div className="flex gap-2">
                        {/* Decisión firmada del dueño (11/08/2026, estándar visual): "Cancelar"
                            pasaba a confundirse con el término del negocio ("cancelar" = abonar
                            el total de la reserva) — este botón solo CIERRA la ficha sin
                            guardar, así que ahora dice "Descartar". El data-testid NO cambia:
                            lo usa el robot de QA para cerrar la ficha (T-6). */}
                        <Button
                            type="button"
                            variant="ghost"
                            onClick={onCancelar}
                            disabled={guardando}
                            data-testid="inline-card-cancelar"
                        >
                            Descartar
                        </Button>
                        {/* Botón primary del sistema (molde único, ver components/ui/button.jsx):
                            antes era un azul suelto (`bg-blue-600`) distinto del resto de la
                            app — ahora es el mismo "azul boleto" que usan todos los botones
                            principales, con altura 40px estándar. */}
                        <Button
                            type="button"
                            onClick={handleGuardar}
                            disabled={guardando}
                            data-testid="inline-card-guardar"
                        >
                            {guardando ? "Guardando…" : labelBotonGuardar}
                        </Button>
                    </div>
                </div>
            </div>
        </div>
    );
}
