/**
 * Tests de lógica pura para las funciones de ServiceList, DeadlinePill y CostConfirmCell.
 *
 * Por qué lógica pura:
 *   Las reglas críticas (gate de permisos, formato de fechas, estado vencido/vigente,
 *   texto de pills, URL del endpoint, flujo confirm-cost) son funciones sin DOM.
 *   Testearlas como lógica pura es más rápido, determinista y no requiere jsdom ni RTL.
 *
 * Cómo correr: node --test src/features/reservas/components/serviceListCostConfirm.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de DeadlinePill.jsx ─────────────────────────────────
// (misma lógica que el componente usa; si cambia allá, actualizar acá)

/**
 * Formatea una fecha ISO al formato "dd/MM".
 * Toma solo la parte YYYY-MM-DD para evitar desfases de zona horaria.
 */
function formatearFechaDdMm(fechaIso) {
    const solofecha = (fechaIso || "").split("T")[0];
    if (!solofecha) return "";
    const [, mes, dia] = solofecha.split("-");
    if (!mes || !dia) return "";
    return `${dia}/${mes}`;
}

/**
 * Devuelve true si la fecha deadline ya venció (hoy >= deadline, ambas date-only).
 * El día de la fecha inclusive ya se considera vencido.
 *
 * AJUSTABLE: si el criterio cambia (ej. "vence al día siguiente"), cambiar solo esta función.
 */
function estaVencida(fechaIso, hoyOverride = null) {
    const solofecha = (fechaIso || "").split("T")[0];
    if (!solofecha) return false;

    let hoyStr;
    if (hoyOverride) {
        // Para tests: se puede inyectar "hoy" como string "YYYY-MM-DD"
        hoyStr = hoyOverride;
    } else {
        const hoy = new Date();
        hoyStr = [
            hoy.getFullYear(),
            String(hoy.getMonth() + 1).padStart(2, "0"),
            String(hoy.getDate()).padStart(2, "0"),
        ].join("-");
    }

    return hoyStr >= solofecha;
}

/**
 * Extrae la fecha límite relevante del servicio según su tipo.
 * Devuelve null si el tipo no tiene deadline para mostrar.
 */
function obtenerDeadlineDelServicio(service) {
    if (service.recordKind === "hotel" || service.recordKind === "package") {
        return service.operatorPaymentDeadline || null;
    }
    if (service.recordKind === "flight") {
        return service.ticketingDeadline || null;
    }
    return null;
}

/**
 * Texto de la pill según tipo de servicio y si venció.
 */
function obtenerTextoPill(service, fechaFormateada, vencida) {
    if (service.recordKind === "flight") {
        return vencida
            ? `Venció emitir el ${fechaFormateada}`
            : `Emitir antes del ${fechaFormateada}`;
    }
    return vencida
        ? `Venció señar el ${fechaFormateada}`
        : `Señar antes del ${fechaFormateada}`;
}

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

// ─── Lógica pura para pill "creado en venta" ─────────────────────────────────

const TEXTOS_PILL_CREADO = {
    hotel: "Hotel creado en venta",
    flight: "Aéreo creado en venta",
    transfer: "Traslado creado en venta",
    package: "Paquete creado en venta",
    assistance: "Asistencia creada en venta",
};

function obtenerTextoPillCreadoEnVenta(service) {
    if (!service.productCreatedInSale) return null;
    return TEXTOS_PILL_CREADO[service.recordKind] || null;
}

// ─── Tests: formatearFechaDdMm ────────────────────────────────────────────────

test("formatearFechaDdMm: fecha ISO completa → dd/MM", () => {
    assert.equal(formatearFechaDdMm("2026-11-30"), "30/11");
});

test("formatearFechaDdMm: fecha ISO con hora → dd/MM (ignora la hora)", () => {
    assert.equal(formatearFechaDdMm("2026-11-30T00:00:00"), "30/11");
});

test("formatearFechaDdMm: fecha en enero → 01/01", () => {
    assert.equal(formatearFechaDdMm("2026-01-05"), "05/01");
});

test("formatearFechaDdMm: string vacío → string vacío", () => {
    assert.equal(formatearFechaDdMm(""), "");
});

test("formatearFechaDdMm: null → string vacío", () => {
    assert.equal(formatearFechaDdMm(null), "");
});

// ─── Tests: estaVencida ───────────────────────────────────────────────────────

test("estaVencida: fecha futura → no vencida", () => {
    // Inyectamos "hoy" como una fecha fija para hacer el test determinista
    assert.equal(estaVencida("2030-12-31", "2026-06-06"), false);
});

test("estaVencida: fecha pasada → vencida", () => {
    assert.equal(estaVencida("2024-01-01", "2026-06-06"), true);
});

test("estaVencida: fecha igual a hoy → vencida (el día inclusive ya vence)", () => {
    // REGLA: hoy >= deadline → vencida. El día de la fecha ya se considera vencido.
    assert.equal(estaVencida("2026-06-06", "2026-06-06"), true);
});

test("estaVencida: un día antes de hoy → vencida", () => {
    assert.equal(estaVencida("2026-06-05", "2026-06-06"), true);
});

test("estaVencida: un día después de hoy → vigente", () => {
    assert.equal(estaVencida("2026-06-07", "2026-06-06"), false);
});

test("estaVencida: fecha vacía → false (no hay fecha que vencer)", () => {
    assert.equal(estaVencida("", "2026-06-06"), false);
});

test("estaVencida: fecha ISO con hora → solo compara date-only", () => {
    assert.equal(estaVencida("2026-06-07T00:00:00", "2026-06-06"), false);
    assert.equal(estaVencida("2026-06-05T23:59:59", "2026-06-06"), true);
});

// ─── Tests: obtenerDeadlineDelServicio ────────────────────────────────────────

test("obtenerDeadlineDelServicio: hotel con operatorPaymentDeadline → devuelve la fecha", () => {
    const service = { recordKind: "hotel", operatorPaymentDeadline: "2026-12-31" };
    assert.equal(obtenerDeadlineDelServicio(service), "2026-12-31");
});

test("obtenerDeadlineDelServicio: paquete con operatorPaymentDeadline → devuelve la fecha", () => {
    const service = { recordKind: "package", operatorPaymentDeadline: "2026-11-15" };
    assert.equal(obtenerDeadlineDelServicio(service), "2026-11-15");
});

test("obtenerDeadlineDelServicio: vuelo con ticketingDeadline → devuelve la fecha", () => {
    const service = { recordKind: "flight", ticketingDeadline: "2026-10-01" };
    assert.equal(obtenerDeadlineDelServicio(service), "2026-10-01");
});

test("obtenerDeadlineDelServicio: traslado → null (sin deadline)", () => {
    const service = { recordKind: "transfer", operatorPaymentDeadline: "2026-10-01" };
    assert.equal(obtenerDeadlineDelServicio(service), null);
});

test("obtenerDeadlineDelServicio: asistencia → null (sin deadline)", () => {
    const service = { recordKind: "assistance", ticketingDeadline: "2026-10-01" };
    assert.equal(obtenerDeadlineDelServicio(service), null);
});

test("obtenerDeadlineDelServicio: genérico → null (sin deadline)", () => {
    const service = { recordKind: "generic" };
    assert.equal(obtenerDeadlineDelServicio(service), null);
});

test("obtenerDeadlineDelServicio: hotel sin fecha → null", () => {
    const service = { recordKind: "hotel" };
    assert.equal(obtenerDeadlineDelServicio(service), null);
});

// ─── Tests: obtenerTextoPill (deadline) ──────────────────────────────────────

test("obtenerTextoPill: hotel vigente → 'Señar antes del dd/MM'", () => {
    const service = { recordKind: "hotel" };
    assert.equal(obtenerTextoPill(service, "30/11", false), "Señar antes del 30/11");
});

test("obtenerTextoPill: hotel vencida → 'Venció señar el dd/MM'", () => {
    const service = { recordKind: "hotel" };
    assert.equal(obtenerTextoPill(service, "30/11", true), "Venció señar el 30/11");
});

test("obtenerTextoPill: paquete vigente → texto de señar", () => {
    const service = { recordKind: "package" };
    assert.equal(obtenerTextoPill(service, "15/10", false), "Señar antes del 15/10");
});

test("obtenerTextoPill: vuelo vigente → 'Emitir antes del dd/MM'", () => {
    const service = { recordKind: "flight" };
    assert.equal(obtenerTextoPill(service, "20/09", false), "Emitir antes del 20/09");
});

test("obtenerTextoPill: vuelo vencida → 'Venció emitir el dd/MM'", () => {
    const service = { recordKind: "flight" };
    assert.equal(obtenerTextoPill(service, "20/09", true), "Venció emitir el 20/09");
});

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

// ─── Tests: pill "creado en venta" ───────────────────────────────────────────

test("pill creado en venta: hotel → texto masculino", () => {
    const service = { recordKind: "hotel", productCreatedInSale: true };
    assert.equal(obtenerTextoPillCreadoEnVenta(service), "Hotel creado en venta");
});

test("pill creado en venta: asistencia → texto FEMENINO 'creada'", () => {
    // Solo asistencia va en femenino por regla de negocio del dueño
    const service = { recordKind: "assistance", productCreatedInSale: true };
    assert.equal(obtenerTextoPillCreadoEnVenta(service), "Asistencia creada en venta");
});

test("pill creado en venta: vuelo → texto masculino 'Aéreo creado en venta'", () => {
    const service = { recordKind: "flight", productCreatedInSale: true };
    assert.equal(obtenerTextoPillCreadoEnVenta(service), "Aéreo creado en venta");
});

test("pill creado en venta: traslado → texto masculino", () => {
    const service = { recordKind: "transfer", productCreatedInSale: true };
    assert.equal(obtenerTextoPillCreadoEnVenta(service), "Traslado creado en venta");
});

test("pill creado en venta: paquete → texto masculino", () => {
    const service = { recordKind: "package", productCreatedInSale: true };
    assert.equal(obtenerTextoPillCreadoEnVenta(service), "Paquete creado en venta");
});

test("pill creado en venta: productCreatedInSale=false → null (no se muestra)", () => {
    const service = { recordKind: "hotel", productCreatedInSale: false };
    assert.equal(obtenerTextoPillCreadoEnVenta(service), null);
});

test("pill creado en venta: sin productCreatedInSale → null", () => {
    const service = { recordKind: "hotel" };
    assert.equal(obtenerTextoPillCreadoEnVenta(service), null);
});

test("pill creado en venta: tipo genérico + productCreatedInSale=true → null (genérico no tiene pill)", () => {
    const service = { recordKind: "generic", productCreatedInSale: true };
    assert.equal(obtenerTextoPillCreadoEnVenta(service), null);
});

// ─── Tests: combinación deadline + estado cancelado ──────────────────────────

test("deadline: servicio cancelado → no se muestra pill (aunque tenga fecha)", () => {
    // La condición de render: fecha cargada && estado !== Cancelado
    const service = {
        recordKind: "hotel",
        operatorPaymentDeadline: "2026-12-31",
        workflowStatus: "Cancelado",
    };
    // La decisión de si mostrar: fecha existe Y no está cancelado
    const mostrar = Boolean(obtenerDeadlineDelServicio(service)) && service.workflowStatus !== "Cancelado";
    assert.equal(mostrar, false);
});

test("deadline: servicio confirmado con fecha → se muestra pill", () => {
    const service = {
        recordKind: "hotel",
        operatorPaymentDeadline: "2026-12-31",
        workflowStatus: "Confirmado",
    };
    const mostrar = Boolean(obtenerDeadlineDelServicio(service)) && service.workflowStatus !== "Cancelado";
    assert.equal(mostrar, true);
});

test("deadline: servicio solicitado (sin workflowStatus) con fecha → se muestra pill", () => {
    const service = {
        recordKind: "package",
        operatorPaymentDeadline: "2026-08-01",
    };
    const mostrar = Boolean(obtenerDeadlineDelServicio(service)) && service.workflowStatus !== "Cancelado";
    assert.equal(mostrar, true);
});

// ─── Tests: flujo completo de pill de deadline ───────────────────────────────

test("flujo deadline hotel vigente: extrae fecha, formatea y determina estado", () => {
    const service = { recordKind: "hotel", operatorPaymentDeadline: "2030-11-30", workflowStatus: "Confirmado" };

    const fechaIso = obtenerDeadlineDelServicio(service);
    assert.ok(fechaIso, "debe tener fecha");

    const fechaFormateada = formatearFechaDdMm(fechaIso);
    assert.equal(fechaFormateada, "30/11");

    // Inyectamos "hoy" en el pasado para que la fecha sea futura (vigente)
    const vencida = estaVencida(fechaIso, "2026-06-06");
    assert.equal(vencida, false);

    const texto = obtenerTextoPill(service, fechaFormateada, vencida);
    assert.equal(texto, "Señar antes del 30/11");
});

test("flujo deadline vuelo vencido: texto correcto con 'Venció emitir'", () => {
    const service = { recordKind: "flight", ticketingDeadline: "2025-01-15", workflowStatus: "Solicitado" };

    const fechaIso = obtenerDeadlineDelServicio(service);
    const fechaFormateada = formatearFechaDdMm(fechaIso);
    assert.equal(fechaFormateada, "15/01");

    const vencida = estaVencida(fechaIso, "2026-06-06");
    assert.equal(vencida, true);

    const texto = obtenerTextoPill(service, fechaFormateada, vencida);
    assert.equal(texto, "Venció emitir el 15/01");
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
