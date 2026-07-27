/**
 * Tests de la Tanda 3 (2026-07-24), fix #34: botones "Marcar confirmado"/"Marcar emitido"/
 * "No requiere confirmación" desde la ficha de la reserva (spec docs/ux/guia-ux-gaston.md,
 * sección "Confirmar un servicio DESDE LA FICHA de la reserva").
 *
 * Corre con: node --test src/features/reservas/lib/serviceResolutionActions.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
    resolverAccionesParaServicioPendiente,
    construirRequestResolverServicio,
    resolverMensajeExito,
    debeMostrarCartelEmergente,
    mapearTipoEspanolARecordKind,
    debeMostrarBotonPrimarioEnCuentaOperador,
    debeMostrarAvisoSinNombresParaElegir,
} from "./serviceResolutionActions.js";

// ─── resolverAccionesParaServicioPendiente: elegibilidad de botón por tipo (P3=A) ─────

test("aereo: un solo boton 'Marcar emitido', con casillero", () => {
    const acciones = resolverAccionesParaServicioPendiente("flight");
    assert.equal(acciones.length, 1);
    assert.deepEqual(acciones[0], { tipo: "mark-issued", etiqueta: "Marcar emitido", necesitaCasillero: true });
});

test("hotel: un solo boton 'Marcar confirmado', con casillero", () => {
    const acciones = resolverAccionesParaServicioPendiente("hotel");
    assert.equal(acciones.length, 1);
    assert.deepEqual(acciones[0], { tipo: "confirm-status", etiqueta: "Marcar confirmado", necesitaCasillero: true });
});

test("paquete: un solo boton 'Marcar confirmado', con casillero", () => {
    const acciones = resolverAccionesParaServicioPendiente("package");
    assert.equal(acciones.length, 1);
    assert.equal(acciones[0].etiqueta, "Marcar confirmado");
});

test("asistencia: un solo boton 'Marcar confirmado', con casillero", () => {
    const acciones = resolverAccionesParaServicioPendiente("assistance");
    assert.equal(acciones.length, 1);
    assert.equal(acciones[0].etiqueta, "Marcar confirmado");
});

test("traslado: DOS botones a la vez ('Marcar confirmado' + 'No requiere confirmación')", () => {
    const acciones = resolverAccionesParaServicioPendiente("transfer");
    assert.equal(acciones.length, 2);
    assert.deepEqual(acciones[0], { tipo: "confirm-status", etiqueta: "Marcar confirmado", necesitaCasillero: true });
    assert.deepEqual(acciones[1], { tipo: "no-confirmation", etiqueta: "No requiere confirmación", necesitaCasillero: false });
});

test("generico: sin botones (nunca tuvo confirmacion de operador)", () => {
    assert.deepEqual(resolverAccionesParaServicioPendiente("generic"), []);
});

test("tipo desconocido: sin botones (conservador)", () => {
    assert.deepEqual(resolverAccionesParaServicioPendiente("no-existe"), []);
});

// ─── construirRequestResolverServicio: payload/endpoint por accion ────────────────────

test("mark-issued (aereo): POST a /reservas/{id}/flights/{id}/mark-issued con ticketNumber", () => {
    const req = construirRequestResolverServicio({
        tipo: "mark-issued",
        recordKind: "flight",
        reservaId: "R1",
        servicePublicId: "S1",
        numero: "ABC123",
    });
    assert.equal(req.method, "post");
    assert.equal(req.url, "/reservas/R1/flights/S1/mark-issued");
    assert.deepEqual(req.body, { ticketNumber: "ABC123" });
});

test("mark-issued: casillero vacio -> ticketNumber null (P2=B: numero es opcional)", () => {
    const req = construirRequestResolverServicio({
        tipo: "mark-issued",
        recordKind: "flight",
        reservaId: "R1",
        servicePublicId: "S1",
        numero: "   ",
    });
    assert.deepEqual(req.body, { ticketNumber: null });
});

test("no-confirmation (traslado mudo): POST sin body", () => {
    const req = construirRequestResolverServicio({
        tipo: "no-confirmation",
        recordKind: "transfer",
        reservaId: "R1",
        servicePublicId: "S1",
        numero: null,
    });
    assert.equal(req.method, "post");
    assert.equal(req.url, "/reservas/R1/transfers/S1/no-confirmation");
    assert.equal(req.body, undefined);
});

test("confirm-status (hotel): PATCH absoluto a hotel-bookings con status Confirmado", () => {
    const req = construirRequestResolverServicio({
        tipo: "confirm-status",
        recordKind: "hotel",
        reservaId: "R1", // no se usa en este endpoint (es absoluto), pero no debe romper
        servicePublicId: "S1",
        numero: "HTL-999",
    });
    assert.equal(req.method, "patch");
    assert.equal(req.url, "/hotel-bookings/S1/status");
    assert.deepEqual(req.body, { status: "Confirmado", confirmationNumber: "HTL-999" });
});

test("confirm-status (paquete): PATCH a package-bookings", () => {
    const req = construirRequestResolverServicio({
        tipo: "confirm-status", recordKind: "package", reservaId: "R1", servicePublicId: "S1", numero: null,
    });
    assert.equal(req.url, "/package-bookings/S1/status");
});

test("confirm-status (asistencia): PATCH a assistance-bookings", () => {
    const req = construirRequestResolverServicio({
        tipo: "confirm-status", recordKind: "assistance", reservaId: "R1", servicePublicId: "S1", numero: null,
    });
    assert.equal(req.url, "/assistance-bookings/S1/status");
});

test("confirm-status (traslado): PATCH a transfer-bookings", () => {
    const req = construirRequestResolverServicio({
        tipo: "confirm-status", recordKind: "transfer", reservaId: "R1", servicePublicId: "S1", numero: null,
    });
    assert.equal(req.url, "/transfer-bookings/S1/status");
});

test("confirm-status con recordKind sin endpoint conocido (ej. 'flight'): devuelve null, no arma un pedido roto", () => {
    // El aereo NUNCA usa "confirm-status" (usa "mark-issued"), pero si algo lo intentara
    // por error, no debe armar una URL con "undefined" adentro.
    const req = construirRequestResolverServicio({
        tipo: "confirm-status", recordKind: "flight", reservaId: "R1", servicePublicId: "S1", numero: null,
    });
    assert.equal(req, null);
});

test("tipo desconocido: devuelve null", () => {
    const req = construirRequestResolverServicio({
        tipo: "no-existe", recordKind: "hotel", reservaId: "R1", servicePublicId: "S1", numero: null,
    });
    assert.equal(req, null);
});

// ─── resolverMensajeExito ──────────────────────────────────────────────────────────────

test("mensajes de exito por tipo de accion", () => {
    assert.equal(resolverMensajeExito("mark-issued"), "Vuelo marcado como emitido.");
    assert.equal(resolverMensajeExito("no-confirmation"), "Traslado marcado como que no requiere confirmación.");
    assert.equal(resolverMensajeExito("confirm-status"), "Servicio confirmado.");
    assert.equal(resolverMensajeExito("no-existe"), "Listo.");
});

// ─── debeMostrarCartelEmergente: rechazo largo del motor -> ventana, corto -> en linea ──

test("mensaje corto (menos de 80 caracteres) -> NO va al cartel emergente", () => {
    assert.equal(debeMostrarCartelEmergente("Mínimo 10 caracteres"), false);
});

test("mensaje largo (mas de 80 caracteres) -> SI va al cartel emergente", () => {
    const mensajeLargo = "No se puede confirmar este servicio porque la reserva tiene un candado activo y hace falta autorización de un administrador para destrabarla.";
    assert.ok(mensajeLargo.length > 80);
    assert.equal(debeMostrarCartelEmergente(mensajeLargo), true);
});

test("mensaje vacio o null -> nunca abre el cartel", () => {
    assert.equal(debeMostrarCartelEmergente(""), false);
    assert.equal(debeMostrarCartelEmergente(null), false);
    assert.equal(debeMostrarCartelEmergente(undefined), false);
});

// ─── mapearTipoEspanolARecordKind (P4=A: cuenta del operador reusa la ficha) ────────────

test("mapea los 5 tipos en espanol al recordKind en ingles", () => {
    assert.equal(mapearTipoEspanolARecordKind("Hotel"), "hotel");
    assert.equal(mapearTipoEspanolARecordKind("Vuelo"), "flight");
    assert.equal(mapearTipoEspanolARecordKind("Traslado"), "transfer");
    assert.equal(mapearTipoEspanolARecordKind("Paquete"), "package");
    assert.equal(mapearTipoEspanolARecordKind("Asistencia"), "assistance");
});

test("tipo desconocido o ausente -> 'generic' (conservador, sin boton)", () => {
    assert.equal(mapearTipoEspanolARecordKind("Otro"), "generic");
    assert.equal(mapearTipoEspanolARecordKind(null), "generic");
    assert.equal(mapearTipoEspanolARecordKind(undefined), "generic");
});

// ─── debeMostrarBotonPrimarioEnCuentaOperador (P4=A: EstadoServicioCell) ────────────────

test("todas las condiciones cumplidas -> muestra el boton primario", () => {
    const resultado = debeMostrarBotonPrimarioEnCuentaOperador({
        canEdit: true, status: "Solicitado", recordKind: "hotel", reservaPublicId: "R1",
    });
    assert.equal(resultado, true);
});

test("sin permiso de editar -> NO muestra el boton (mismo gate que el desplegable)", () => {
    const resultado = debeMostrarBotonPrimarioEnCuentaOperador({
        canEdit: false, status: "Solicitado", recordKind: "hotel", reservaPublicId: "R1",
    });
    assert.equal(resultado, false);
});

test("servicio ya Confirmado -> NO muestra el boton (nada que avanzar, va el desplegable)", () => {
    const resultado = debeMostrarBotonPrimarioEnCuentaOperador({
        canEdit: true, status: "Confirmado", recordKind: "hotel", reservaPublicId: "R1",
    });
    assert.equal(resultado, false);
});

test("tipo generico -> NO muestra el boton (nunca tuvo confirmacion de operador)", () => {
    const resultado = debeMostrarBotonPrimarioEnCuentaOperador({
        canEdit: true, status: "Solicitado", recordKind: "generic", reservaPublicId: "R1",
    });
    assert.equal(resultado, false);
});

test("sin reserva asociada -> NO muestra el boton (los endpoints son reserva-scoped)", () => {
    const resultado = debeMostrarBotonPrimarioEnCuentaOperador({
        canEdit: true, status: "Solicitado", recordKind: "hotel", reservaPublicId: null,
    });
    assert.equal(resultado, false);
});

// ─── debeMostrarAvisoSinNombresParaElegir (H19, barrido 2026-07-25, decision firmada 9) ────

test("aereo -> muestra el aviso de sin nombres", () => {
    assert.equal(debeMostrarAvisoSinNombresParaElegir("flight"), true);
});

test("traslado -> muestra el aviso de sin nombres", () => {
    assert.equal(debeMostrarAvisoSinNombresParaElegir("transfer"), true);
});

test("hotel -> NO muestra el aviso (antes salia en todas las filas)", () => {
    assert.equal(debeMostrarAvisoSinNombresParaElegir("hotel"), false);
});

test("paquete -> NO muestra el aviso", () => {
    assert.equal(debeMostrarAvisoSinNombresParaElegir("package"), false);
});

test("asistencia -> NO muestra el aviso", () => {
    assert.equal(debeMostrarAvisoSinNombresParaElegir("assistance"), false);
});

test("generico -> NO muestra el aviso", () => {
    assert.equal(debeMostrarAvisoSinNombresParaElegir("generic"), false);
});
