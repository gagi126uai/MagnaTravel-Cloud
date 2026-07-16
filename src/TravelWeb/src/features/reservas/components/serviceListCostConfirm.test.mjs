/**
 * Tests de lógica pura para las funciones de ServiceList y CostConfirmCell.
 *
 * Por qué lógica pura:
 *   Las reglas críticas (gate de permisos, formato de fechas, URL del endpoint,
 *   flujo confirm-cost) son funciones sin DOM.
 *   Testearlas como lógica pura es más rápido, determinista y no requiere jsdom ni RTL.
 *
 * Cómo correr: node --test src/features/reservas/components/serviceListCostConfirm.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de CostConfirmCell.jsx ───────────────────────────────

const ENDPOINT_POR_TIPO = {
    hotel: "hotels",
    flight: "flights",
    transfer: "transfers",
    package: "packages",
    assistance: "assistances",
};

function buildConfirmCostUrl(reservaId, service) {
    const segmento = ENDPOINT_POR_TIPO[service.recordKind];
    const serviceId = service.publicId || service.id || "";
    return `/reservas/${reservaId}/${segmento}/${serviceId}/confirm-cost`;
}

/**
 * Simula la lógica de validación del costo antes de confirmar:
 * retorna true si debe mostrarse el diálogo de $0.
 */
function debeAbrirDialogoCero(valorCosto) {
    return (Number(valorCosto) || 0) === 0;
}

// ─── Lógica pura de gate de permisos (ServiceList) ───────────────────────────

/**
 * Simula la decisión de qué gate usar para mostrar el costo.
 * Con flag OFF: isAdmin(); con flag ON: hasPermission("cobranzas.see_cost").
 */
function resolverGateCosto(isCatalogFindOrCreateEnabled, isAdminUser, hasSeeCostPermission) {
    if (isCatalogFindOrCreateEnabled) {
        // Admin siempre tiene todos los permisos (bypass en hasPermission)
        return hasSeeCostPermission || isAdminUser;
    }
    return isAdminUser;
}

/**
 * Simula si el usuario puede interactuar con confirm-cost
 * (flag ON + permiso see_cost).
 */
function puedeConfirmarCosto(isCatalogFindOrCreateEnabled, mostrarCosto) {
    return isCatalogFindOrCreateEnabled && mostrarCosto;
}

// ─── Tests: buildConfirmCostUrl ───────────────────────────────────────────────

test("buildConfirmCostUrl: hotel → URL correcta", () => {
    const service = { recordKind: "hotel", publicId: "hotel-abc123" };
    assert.equal(
        buildConfirmCostUrl("reserva-xyz", service),
        "/reservas/reserva-xyz/hotels/hotel-abc123/confirm-cost"
    );
});

test("buildConfirmCostUrl: vuelo → URL correcta con 'flights'", () => {
    const service = { recordKind: "flight", publicId: "flight-456" };
    assert.equal(
        buildConfirmCostUrl("reserva-xyz", service),
        "/reservas/reserva-xyz/flights/flight-456/confirm-cost"
    );
});

test("buildConfirmCostUrl: traslado → URL correcta con 'transfers'", () => {
    const service = { recordKind: "transfer", publicId: "transfer-789" };
    assert.equal(
        buildConfirmCostUrl("reserva-xyz", service),
        "/reservas/reserva-xyz/transfers/transfer-789/confirm-cost"
    );
});

test("buildConfirmCostUrl: paquete → URL correcta con 'packages'", () => {
    const service = { recordKind: "package", publicId: "pack-001" };
    assert.equal(
        buildConfirmCostUrl("reserva-xyz", service),
        "/reservas/reserva-xyz/packages/pack-001/confirm-cost"
    );
});

test("buildConfirmCostUrl: asistencia → URL correcta con 'assistances'", () => {
    const service = { recordKind: "assistance", publicId: "asist-222" };
    assert.equal(
        buildConfirmCostUrl("reserva-xyz", service),
        "/reservas/reserva-xyz/assistances/asist-222/confirm-cost"
    );
});

// ─── Tests: debeAbrirDialogoCero ─────────────────────────────────────────────

test("debeAbrirDialogoCero: valor 0 → debe abrir diálogo", () => {
    assert.equal(debeAbrirDialogoCero("0"), true);
});

test("debeAbrirDialogoCero: valor vacío → debe abrir diálogo (vacío = 0)", () => {
    assert.equal(debeAbrirDialogoCero(""), true);
});

test("debeAbrirDialogoCero: valor positivo → NO debe abrir diálogo", () => {
    assert.equal(debeAbrirDialogoCero("350000"), false);
});

test("debeAbrirDialogoCero: valor negativo → NO abre diálogo (el server lo rechaza)", () => {
    // Costos negativos los rechaza el server; el front no los valida, los envía directo.
    assert.equal(debeAbrirDialogoCero("-100"), false);
});

test("debeAbrirDialogoCero: valor numérico 0 → debe abrir diálogo", () => {
    assert.equal(debeAbrirDialogoCero(0), true);
});

// ─── Tests: gate de permisos (resolverGateCosto) ─────────────────────────────

test("gate de costo: flag OFF + admin → ve el costo (comportamiento original)", () => {
    assert.equal(resolverGateCosto(false, true, false), true);
});

test("gate de costo: flag OFF + no admin → NO ve el costo (comportamiento original)", () => {
    assert.equal(resolverGateCosto(false, false, false), false);
});

test("gate de costo: flag OFF + tiene permiso see_cost pero no admin → NO ve el costo (flag OFF ignora el permiso)", () => {
    // Con flag OFF, el gate es isAdmin() — hasPermission se ignora (comportamiento original intacto)
    assert.equal(resolverGateCosto(false, false, true), false);
});

test("gate de costo: flag ON + admin → ve el costo (admin pasa siempre)", () => {
    assert.equal(resolverGateCosto(true, true, false), true);
});

test("gate de costo: flag ON + permiso see_cost → ve el costo", () => {
    assert.equal(resolverGateCosto(true, false, true), true);
});

test("gate de costo: flag ON + sin admin + sin permiso see_cost → NO ve el costo", () => {
    assert.equal(resolverGateCosto(true, false, false), false);
});

// ─── Tests: puedeConfirmarCosto ──────────────────────────────────────────────

test("puedeConfirmarCosto: flag OFF → nunca puede (aunque vea el costo)", () => {
    assert.equal(puedeConfirmarCosto(false, true), false);
});

test("puedeConfirmarCosto: flag ON + mostrarCosto=true → puede", () => {
    assert.equal(puedeConfirmarCosto(true, true), true);
});

test("puedeConfirmarCosto: flag ON + mostrarCosto=false → no puede", () => {
    assert.equal(puedeConfirmarCosto(true, false), false);
});

// ─── Tests: flag OFF = render idéntico (gate invariante) ─────────────────────

test("flag OFF: admin ve el costo (gate original intacto)", () => {
    const mostrar = resolverGateCosto(false, true, false);
    assert.equal(mostrar, true);
});

test("flag OFF: no admin sin permiso no ve el costo (gate original intacto)", () => {
    const mostrar = resolverGateCosto(false, false, false);
    assert.equal(mostrar, false);
});

test("flag OFF: puedeConfirmarCosto siempre false", () => {
    // Con flag OFF ningún usuario puede acceder a confirm-cost (feature no existe)
    assert.equal(puedeConfirmarCosto(false, true), false);
    assert.equal(puedeConfirmarCosto(false, false), false);
});

// ─── Tests: upsertServiceInReservaSnapshot (lógica del hook) ─────────────────

/**
 * Simulación de la función del hook para tests de lógica pura.
 */
function upsertServiceInReservaSnapshot(reserva, servicioActualizado, recordKind) {
    if (!reserva || !servicioActualizado) return reserva;

    const COLLECTION_KEYS = {
        hotel: "hotelBookings",
        flight: "flightSegments",
        transfer: "transferBookings",
        package: "packageBookings",
        assistance: "assistanceBookings",
    };

    const collectionKey = COLLECTION_KEYS[recordKind];
    if (!collectionKey || !Array.isArray(reserva[collectionKey])) return reserva;

    const servicioId = String(servicioActualizado.publicId || servicioActualizado.id || "");
    const coleccionActual = reserva[collectionKey];
    const existeEnColeccion = coleccionActual.some((item) => {
        const itemId = String(item.publicId || item.id || "");
        return itemId === servicioId;
    });

    const coleccionActualizada = existeEnColeccion
        ? coleccionActual.map((item) => {
            const itemId = String(item.publicId || item.id || "");
            return itemId === servicioId ? servicioActualizado : item;
        })
        : [...coleccionActual, servicioActualizado];

    return { ...reserva, [collectionKey]: coleccionActualizada };
}

test("upsertServiceInReservaSnapshot: reemplaza un hotel existente en el snapshot", () => {
    const reserva = {
        hotelBookings: [
            { publicId: "hotel-1", netCost: 1000, costToConfirm: true },
            { publicId: "hotel-2", netCost: 2000, costToConfirm: false },
        ],
    };

    const servicioActualizado = { publicId: "hotel-1", netCost: 1500, costToConfirm: false };
    const resultado = upsertServiceInReservaSnapshot(reserva, servicioActualizado, "hotel");

    assert.equal(resultado.hotelBookings.length, 2, "debe seguir teniendo 2 hoteles");
    const hotelActualizado = resultado.hotelBookings.find((h) => h.publicId === "hotel-1");
    assert.equal(hotelActualizado.netCost, 1500, "el costo debe estar actualizado");
    assert.equal(hotelActualizado.costToConfirm, false, "costToConfirm debe quedar en false tras confirmar");
});

test("upsertServiceInReservaSnapshot: no modifica los otros servicios de la colección", () => {
    const reserva = {
        hotelBookings: [
            { publicId: "hotel-1", netCost: 1000 },
            { publicId: "hotel-2", netCost: 2000 },
        ],
    };

    const resultado = upsertServiceInReservaSnapshot(
        reserva,
        { publicId: "hotel-1", netCost: 1500 },
        "hotel"
    );

    const hotel2 = resultado.hotelBookings.find((h) => h.publicId === "hotel-2");
    assert.equal(hotel2.netCost, 2000, "hotel-2 no debe cambiar");
});

test("upsertServiceInReservaSnapshot: funciona para vuelos (flightSegments)", () => {
    const reserva = {
        flightSegments: [{ publicId: "flight-1", netCost: 5000, costToConfirm: true }],
    };

    const resultado = upsertServiceInReservaSnapshot(
        reserva,
        { publicId: "flight-1", netCost: 4800, costToConfirm: false },
        "flight"
    );

    assert.equal(resultado.flightSegments[0].netCost, 4800);
    assert.equal(resultado.flightSegments[0].costToConfirm, false);
});

test("upsertServiceInReservaSnapshot: no toca otras colecciones de la reserva", () => {
    const reserva = {
        hotelBookings: [{ publicId: "hotel-1", netCost: 1000, costToConfirm: true }],
        flightSegments: [{ publicId: "flight-1", netCost: 5000 }],
    };

    const resultado = upsertServiceInReservaSnapshot(
        reserva,
        { publicId: "hotel-1", netCost: 1200, costToConfirm: false },
        "hotel"
    );

    // flightSegments no debe haberse tocado
    assert.equal(resultado.flightSegments[0].netCost, 5000);
    assert.equal(resultado.flightSegments[0].publicId, "flight-1");
});

test("upsertServiceInReservaSnapshot: si el servicio no existe en la colección, hace upsert al final", () => {
    const reserva = {
        hotelBookings: [{ publicId: "hotel-1", netCost: 1000 }],
    };

    const resultado = upsertServiceInReservaSnapshot(
        reserva,
        { publicId: "hotel-nuevo", netCost: 800 },
        "hotel"
    );

    assert.equal(resultado.hotelBookings.length, 2, "debe tener el servicio viejo + el nuevo");
    assert.ok(resultado.hotelBookings.some((h) => h.publicId === "hotel-nuevo"), "el nuevo debe estar presente");
});

// ─── Tests: ajuste de totalCost en handleServiceUpdated (fix B2) ──────────────

/**
 * Simulación de la lógica de handleServiceUpdated que ajusta totalCost.
 * COPIA de la lógica del hook (useReservaDetail.js:handleServiceUpdated).
 * Si cambia allá, actualizar acá.
 *
 * El hook real usa setReservaSnapshot(fn) para actualizar el estado de React.
 * Acá simulamos esa función con el currentReserva directamente.
 */
function simularHandleServiceUpdated(currentReserva, servicioActualizado, recordKind) {
    const COLLECTION_KEYS = {
        hotel: "hotelBookings",
        flight: "flightSegments",
        transfer: "transferBookings",
        package: "packageBookings",
        assistance: "assistanceBookings",
    };

    const collectionKey = COLLECTION_KEYS[recordKind];
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

    const reservaConServicioActualizado = upsertServiceInReservaSnapshot(
        currentReserva,
        servicioActualizado,
        recordKind
    );

    if (!servicioViejo || !reservaConServicioActualizado) {
        return reservaConServicioActualizado;
    }

    const deltaCosto = (servicioActualizado.netCost || 0) - (servicioViejo.netCost || 0);
    return {
        ...reservaConServicioActualizado,
        totalCost: (reservaConServicioActualizado.totalCost || 0) + deltaCosto,
    };
}

test("handleServiceUpdated: ajusta totalCost por delta cuando el costo sube", () => {
    const reserva = {
        totalCost: 3000,
        hotelBookings: [
            { publicId: "hotel-1", netCost: 1000, costToConfirm: true },
            { publicId: "hotel-2", netCost: 2000 },
        ],
    };

    const servicioActualizado = { publicId: "hotel-1", netCost: 1500, costToConfirm: false };
    const resultado = simularHandleServiceUpdated(reserva, servicioActualizado, "hotel");

    // Delta: 1500 - 1000 = +500; totalCost: 3000 + 500 = 3500
    assert.equal(resultado.totalCost, 3500, "totalCost debe subir 500");
    assert.equal(resultado.hotelBookings[0].netCost, 1500, "el hotel debe tener el costo nuevo");
});

test("handleServiceUpdated: ajusta totalCost por delta cuando el costo baja", () => {
    const reserva = {
        totalCost: 5000,
        flightSegments: [{ publicId: "flight-1", netCost: 5000, costToConfirm: true }],
    };

    const servicioActualizado = { publicId: "flight-1", netCost: 4500, costToConfirm: false };
    const resultado = simularHandleServiceUpdated(reserva, servicioActualizado, "flight");

    // Delta: 4500 - 5000 = -500; totalCost: 5000 - 500 = 4500
    assert.equal(resultado.totalCost, 4500, "totalCost debe bajar 500");
});

test("handleServiceUpdated: no modifica totalCost si el servicio no existía (caso agregar)", () => {
    const reserva = {
        totalCost: 2000,
        hotelBookings: [{ publicId: "hotel-1", netCost: 2000 }],
    };

    // Servicio nuevo que no está en la colección
    const servicioNuevo = { publicId: "hotel-nuevo", netCost: 800 };
    const resultado = simularHandleServiceUpdated(reserva, servicioNuevo, "hotel");

    // totalCost no se ajusta (comportamiento previo al fix: sin delta)
    assert.equal(resultado.totalCost, 2000, "totalCost no debe cambiar para servicio nuevo");
    assert.equal(resultado.hotelBookings.length, 2, "el servicio nuevo debe haberse agregado igual");
});

test("handleServiceUpdated: delta cero cuando costo no cambió (confirm sin modificar valor)", () => {
    const reserva = {
        totalCost: 1000,
        transferBookings: [{ publicId: "transfer-1", netCost: 1000, costToConfirm: true }],
    };

    const servicioActualizado = { publicId: "transfer-1", netCost: 1000, costToConfirm: false };
    const resultado = simularHandleServiceUpdated(reserva, servicioActualizado, "transfer");

    // Delta: 0; totalCost sin cambio
    assert.equal(resultado.totalCost, 1000, "totalCost no debe cambiar si el costo no varió");
    assert.equal(resultado.transferBookings[0].costToConfirm, false, "costToConfirm debe actualizarse igual");
});

// ─── Tests: etiquetaEstadoServicio (FIX 3: En espera vs Solicitado) ───────────
// Regla Gaston 2026-06-08: en Cotización/Presupuesto el badge dice "En espera"
// (nada se pidió al operador); recién en En gestión en adelante dice "Solicitado".
// "Confirmado" siempre pasa directo (es un estado del backend).
// 2026-07-16 (vocabulario "Cancelar" vs "Anular"): workflowStatus "Cancelado" (nombre
// histórico del campo del backend) ahora se muestra como "Anulado" — ver el mapeo real
// en ServiceList.jsx (misma función, con más casos: Finalizado, Lost/Cancelled, etc.).
// Esta réplica local solo cubre los casos de este archivo de test.

function etiquetaEstadoServicio(workflowStatus, reservaStatus) {
    if (workflowStatus === 'Cancelado') {
        return 'Anulado';
    }
    if (workflowStatus && workflowStatus !== 'Solicitado') {
        return workflowStatus;
    }
    const estaEnEtapaPrevia = reservaStatus === 'Quotation' || reservaStatus === 'Budget';
    return estaEnEtapaPrevia ? 'En espera' : 'Solicitado';
}

test("etiquetaEstadoServicio: workflowStatus null + Quotation → 'En espera'", () => {
    assert.equal(etiquetaEstadoServicio(null, 'Quotation'), 'En espera');
});

test("etiquetaEstadoServicio: workflowStatus null + Budget → 'En espera'", () => {
    assert.equal(etiquetaEstadoServicio(null, 'Budget'), 'En espera');
});

test("etiquetaEstadoServicio: workflowStatus null + InManagement → 'Solicitado'", () => {
    assert.equal(etiquetaEstadoServicio(null, 'InManagement'), 'Solicitado');
});

test("etiquetaEstadoServicio: workflowStatus null + Confirmed → 'Solicitado'", () => {
    assert.equal(etiquetaEstadoServicio(null, 'Confirmed'), 'Solicitado');
});

test("etiquetaEstadoServicio: workflowStatus null + Traveling → 'Solicitado'", () => {
    assert.equal(etiquetaEstadoServicio(null, 'Traveling'), 'Solicitado');
});

test("etiquetaEstadoServicio: workflowStatus 'Confirmado' → siempre 'Confirmado' sin importar la etapa", () => {
    assert.equal(etiquetaEstadoServicio('Confirmado', 'Quotation'), 'Confirmado');
    assert.equal(etiquetaEstadoServicio('Confirmado', 'InManagement'), 'Confirmado');
});

test("etiquetaEstadoServicio: workflowStatus 'Cancelado' → siempre 'Anulado' (2026-07-16, ya no dice 'Cancelado')", () => {
    assert.equal(etiquetaEstadoServicio('Cancelado', 'Budget'), 'Anulado');
    assert.equal(etiquetaEstadoServicio('Cancelado', 'Confirmed'), 'Anulado');
});

test("etiquetaEstadoServicio: workflowStatus 'Emitido' → pasa directo (estado del backend)", () => {
    // Otros valores del backend como 'Emitido', 'HK', etc. se muestran tal cual
    assert.equal(etiquetaEstadoServicio('Emitido', 'InManagement'), 'Emitido');
});

test("etiquetaEstadoServicio: 'Solicitado' + Cotizacion/Presupuesto → 'En espera' (el caso real que estaba roto)", () => {
    // Los servicios traen workflowStatus 'Solicitado' como valor real (no vacio),
    // asi que la conversion a 'En espera' debe contemplar tambien ese valor.
    assert.equal(etiquetaEstadoServicio('Solicitado', 'Quotation'), 'En espera');
    assert.equal(etiquetaEstadoServicio('Solicitado', 'Budget'), 'En espera');
});

test("etiquetaEstadoServicio: 'Solicitado' + En gestion en adelante → 'Solicitado'", () => {
    assert.equal(etiquetaEstadoServicio('Solicitado', 'InManagement'), 'Solicitado');
    assert.equal(etiquetaEstadoServicio('Solicitado', 'Confirmed'), 'Solicitado');
});

// ─── Tests: regla de 0 pasajeros (FIX 4a) ────────────────────────────────────
// Regla Gaston 2026-06-08: NUNCA avanzar con 0 pasajeros.

function totalPasajeros(adults, children, infants) {
    return adults + children + infants;
}

function botonDeshabilitadoPorPasajeros(adults, children, infants, slotsToFillLength, allValid, blockingCount) {
    const total = totalPasajeros(adults, children, infants);
    const tieneCero = total === 0;
    return tieneCero ||
        blockingCount > 0 ||
        (slotsToFillLength > 0 && !allValid);
}

test("regla 0 pasajeros: adultos=0, menores=0, infantes=0 → botón deshabilitado", () => {
    assert.equal(botonDeshabilitadoPorPasajeros(0, 0, 0, 0, true, 0), true);
});

test("regla 0 pasajeros: 1 adulto → botón habilitado (0 pendientes, valid)", () => {
    assert.equal(botonDeshabilitadoPorPasajeros(1, 0, 0, 0, true, 0), false);
});

test("regla 0 pasajeros: solo infante (0 adultos, 0 menores, 1 infante) → habilitado", () => {
    // Un infante solo ya cumple el mínimo de 1
    assert.equal(botonDeshabilitadoPorPasajeros(0, 0, 1, 0, true, 0), false);
});

test("regla 0 pasajeros: formulario incompleto → botón deshabilitado aunque haya 1 adulto", () => {
    assert.equal(botonDeshabilitadoPorPasajeros(1, 0, 0, 1, false, 0), true);
});

test("regla 0 pasajeros: bloqueo por razón no-pax → deshabilitado aunque haya pasajeros", () => {
    assert.equal(botonDeshabilitadoPorPasajeros(2, 1, 0, 0, true, 1), true);
});
