/**
 * Tests de las reglas puras de "Opciones A/B/C" (spec docs/ux/2026-08-12-spec-pdf-presupuesto-ui.md,
 * §3). Import directo del módulo real (a diferencia de hotelInlineForm.test.mjs, acá no hace falta
 * copiar lógica: optionGroupLogic.js es un .js plano, sin JSX, así que node --test lo importa tal cual).
 *
 * Cómo correr: node --test src/features/reservas/lib/optionGroupLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
    letraDeOpcionPorIndice,
    calcularAsignacionDeOpcion,
    mensajeBannerGrupoPendiente,
    mensajeConfirmarEleccionDeOpcion,
    esRechazoPorOpcionesSinResolver,
    agruparServiciosPorOpcion,
    obtenerGruposDeOpcionesPendientes,
    grupoPendienteDeServicio,
    letraDeOpcion,
    contarMiembrosVivosDelGrupo,
} from "./optionGroupLogic.js";

// ─── letraDeOpcionPorIndice ─────────────────────────────────────────────────

test("letraDeOpcionPorIndice: las primeras posiciones dan A, B, C", () => {
    assert.equal(letraDeOpcionPorIndice(0), "A");
    assert.equal(letraDeOpcionPorIndice(1), "B");
    assert.equal(letraDeOpcionPorIndice(2), "C");
});

test("letraDeOpcionPorIndice: la última letra del alfabeto (25) da Z", () => {
    assert.equal(letraDeOpcionPorIndice(25), "Z");
});

test("letraDeOpcionPorIndice: más allá de la Z cae al fallback numérico, no explota", () => {
    // LETRAS[26] es undefined -> el "||" cae al número (indice+1), nunca undefined/NaN/excepción.
    assert.equal(letraDeOpcionPorIndice(26), "27");
    assert.equal(letraDeOpcionPorIndice(100), "101");
});

// ─── calcularAsignacionDeOpcion ─────────────────────────────────────────────

test("calcularAsignacionDeOpcion: socio SIN grupo previo -> backfillea al socio con 'A' y el nuevo lleva 'B'", () => {
    const socio = { recordKind: "hotel", publicId: "socio-1", name: "Hotel Riu Cancún", optionGroup: null };
    const resultado = calcularAsignacionDeOpcion({
        servicioSocio: socio,
        todosLosServicios: [socio],
        publicIdAExcluir: null,
    });

    // El grupo nuevo se llama como el socio (nombre legible, tal cual se ve en la lista).
    assert.equal(resultado.optionGroup, "Hotel Riu Cancún");
    // El servicio NUEVO (el que se está marcando como alternativa) es la segunda opción.
    assert.equal(resultado.optionLabel, "B");
    // Hace falta backfillear al socio: todavía era un servicio "normal".
    assert.equal(resultado.socioNecesitaBackfill, true);
    assert.equal(resultado.socioOptionLabel, "A");
});

test("calcularAsignacionDeOpcion: socio YA tiene grupo -> letra siguiente por orden de carga, SIN backfill", () => {
    const miembroA = { recordKind: "hotel", publicId: "a-1", name: "Hotel Riu Cancún", optionGroup: "Hotel Riu Cancún" };
    const miembroB = { recordKind: "hotel", publicId: "b-1", name: "Hotel Barceló Cancún", optionGroup: "Hotel Riu Cancún" };
    // El socio elegido en el select es cualquiera de los dos ya cargados (acá, miembroB) — el
    // grupo YA existe con 2 miembros vivos (A y B), así que el nuevo (C) sigue el orden de carga.
    const resultado = calcularAsignacionDeOpcion({
        servicioSocio: miembroB,
        todosLosServicios: [miembroA, miembroB],
        publicIdAExcluir: null,
    });

    assert.equal(resultado.optionGroup, "Hotel Riu Cancún");
    assert.equal(resultado.optionLabel, "C");
    // El socio ya tenía grupo: no hace falta ningún PUT de backfill.
    assert.equal(resultado.socioNecesitaBackfill, false);
});

test("calcularAsignacionDeOpcion: excluye al propio servicio (edición) del conteo de miembros", () => {
    // Editando el servicio "b-1" (ya parte del grupo) y volviendo a elegir el mismo socio: no debe
    // contarse a sí mismo dos veces.
    const miembroA = { recordKind: "hotel", publicId: "a-1", name: "Hotel Riu Cancún", optionGroup: "Hotel Riu Cancún" };
    const miembroB = { recordKind: "hotel", publicId: "b-1", name: "Hotel Barceló Cancún", optionGroup: "Hotel Riu Cancún" };
    const resultado = calcularAsignacionDeOpcion({
        servicioSocio: miembroA,
        todosLosServicios: [miembroA, miembroB],
        publicIdAExcluir: "b-1",
    });

    // Sin "b-1" en la cuenta, el grupo tiene 1 miembro vivo (A) -> el nuevo/reeditado es "B" de nuevo.
    assert.equal(resultado.optionLabel, "B");
});

// ─── mensajeBannerGrupoPendiente / mensajeConfirmarEleccionDeOpcion ─────────

test("mensajeBannerGrupoPendiente: singular cuando queda 1 sola otra opción", () => {
    const grupo = { nombreVisible: "Hotel Riu Cancún", miembros: [{}, {}] }; // 2 miembros -> 1 "otra"
    assert.equal(
        mensajeBannerGrupoPendiente(grupo),
        'Elegí cuál se confirma para "Hotel Riu Cancún" — las otras 1 opción se anula.'
    );
});

test("mensajeBannerGrupoPendiente: plural cuando quedan 2+ otras opciones", () => {
    const grupo = { nombreVisible: "Hotel Riu Cancún", miembros: [{}, {}, {}] }; // 3 miembros -> 2 "otras"
    assert.equal(
        mensajeBannerGrupoPendiente(grupo),
        'Elegí cuál se confirma para "Hotel Riu Cancún" — las otras 2 opciones se anulan.'
    );
});

test("mensajeConfirmarEleccionDeOpcion: singular con 1 otra opción", () => {
    const grupo = { nombreVisible: "Hotel Riu Cancún", miembros: [{}, {}] };
    assert.equal(
        mensajeConfirmarEleccionDeOpcion(grupo),
        "¿Esta es la que el cliente eligió? Las otras 1 opción se anula."
    );
});

test("mensajeConfirmarEleccionDeOpcion: plural con 2+ otras opciones", () => {
    const grupo = { nombreVisible: "Hotel Riu Cancún", miembros: [{}, {}, {}] };
    assert.equal(
        mensajeConfirmarEleccionDeOpcion(grupo),
        "¿Esta es la que el cliente eligió? Las otras 2 opciones se anulan."
    );
});

test("mensajes: grupo vacío/sin miembros no revienta (0 otras, singular por defecto de Math.max)", () => {
    const grupoVacio = { nombreVisible: "X", miembros: [] };
    assert.doesNotThrow(() => mensajeBannerGrupoPendiente(grupoVacio));
    assert.doesNotThrow(() => mensajeConfirmarEleccionDeOpcion(grupoVacio));
});

// ─── esRechazoPorOpcionesSinResolver ────────────────────────────────────────

test("esRechazoPorOpcionesSinResolver: true con el prefijo EXACTO que manda ReservaService.cs", () => {
    assert.equal(
        esRechazoPorOpcionesSinResolver('Elegí qué opción quedó de "Hotel Riu Cancún" antes de confirmar.'),
        true
    );
});

test("esRechazoPorOpcionesSinResolver: false con un texto de rechazo distinto (otro motivo del motor)", () => {
    assert.equal(
        esRechazoPorOpcionesSinResolver("Agregá al menos un servicio antes de marcar que el cliente aceptó."),
        false
    );
});

test("esRechazoPorOpcionesSinResolver: false con null/undefined/no-string, sin explotar", () => {
    assert.equal(esRechazoPorOpcionesSinResolver(null), false);
    assert.equal(esRechazoPorOpcionesSinResolver(undefined), false);
    assert.equal(esRechazoPorOpcionesSinResolver(42), false);
});

// ─── Agrupamiento / grupos pendientes (cobertura de soporte para el banner y el chip) ─────

test("obtenerGruposDeOpcionesPendientes: un grupo con 2+ vivos queda pendiente; uno con 1 solo no", () => {
    const servicios = [
        { recordKind: "hotel", publicId: "a", name: "Hotel A", optionGroup: "Grupo1", workflowStatus: "Solicitado" },
        { recordKind: "hotel", publicId: "b", name: "Hotel B", optionGroup: "Grupo1", workflowStatus: "Solicitado" },
        { recordKind: "hotel", publicId: "c", name: "Hotel C", optionGroup: "Grupo2", workflowStatus: "Solicitado" },
    ];
    const pendientes = obtenerGruposDeOpcionesPendientes(servicios);
    assert.equal(pendientes.size, 1);
    const [unicoGrupo] = pendientes.values();
    assert.equal(unicoGrupo.nombreVisible, "Grupo1");
    assert.equal(unicoGrupo.miembros.length, 2);
});

test("obtenerGruposDeOpcionesPendientes: un servicio Cancelado no compite por el grupo (mismo criterio que el motor)", () => {
    const servicios = [
        { recordKind: "hotel", publicId: "a", name: "Hotel A", optionGroup: "Grupo1", workflowStatus: "Solicitado" },
        { recordKind: "hotel", publicId: "b", name: "Hotel B", optionGroup: "Grupo1", workflowStatus: "Cancelado" },
    ];
    const pendientes = obtenerGruposDeOpcionesPendientes(servicios);
    assert.equal(pendientes.size, 0); // solo queda 1 vivo -> ya no es ambiguo
});

test("obtenerGruposDeOpcionesPendientes: agrupa case-insensitive, igual que el backend", () => {
    const servicios = [
        { recordKind: "hotel", publicId: "a", name: "Hotel A", optionGroup: "hoteles", workflowStatus: "Solicitado" },
        { recordKind: "hotel", publicId: "b", name: "Hotel B", optionGroup: "Hoteles", workflowStatus: "Solicitado" },
    ];
    const pendientes = obtenerGruposDeOpcionesPendientes(servicios);
    assert.equal(pendientes.size, 1);
});

test("grupoPendienteDeServicio: encuentra el grupo del servicio, o null si no pertenece a ninguno", () => {
    const servicios = [
        { recordKind: "hotel", publicId: "a", name: "Hotel A", optionGroup: "Grupo1", workflowStatus: "Solicitado" },
        { recordKind: "hotel", publicId: "b", name: "Hotel B", optionGroup: "Grupo1", workflowStatus: "Solicitado" },
        { recordKind: "hotel", publicId: "c", name: "Hotel C", optionGroup: null, workflowStatus: "Solicitado" },
    ];
    const pendientes = obtenerGruposDeOpcionesPendientes(servicios);
    const [a, , c] = servicios;
    assert.notEqual(grupoPendienteDeServicio(a, pendientes), null);
    assert.equal(grupoPendienteDeServicio(c, pendientes), null);
});

test("letraDeOpcion: usa optionLabel guardado si existe; si falta, calcula por posición en el grupo", () => {
    const conLabel = { recordKind: "hotel", publicId: "a", optionLabel: "b" }; // minúscula -> se normaliza
    assert.equal(letraDeOpcion(conLabel, { miembros: [] }), "B");

    const sinLabel = { recordKind: "hotel", publicId: "b", optionLabel: null };
    const grupo = {
        miembros: [
            { recordKind: "hotel", publicId: "a" },
            { recordKind: "hotel", publicId: "b" },
        ],
    };
    assert.equal(letraDeOpcion(sinLabel, grupo), "B"); // segunda posición del grupo
});

test("contarMiembrosVivosDelGrupo: cuenta solo vivos del grupo pedido, excluyendo el publicId indicado", () => {
    const servicios = [
        { recordKind: "hotel", publicId: "a", optionGroup: "Grupo1", workflowStatus: "Solicitado" },
        { recordKind: "hotel", publicId: "b", optionGroup: "Grupo1", workflowStatus: "Solicitado" },
        { recordKind: "hotel", publicId: "c", optionGroup: "Grupo1", workflowStatus: "Cancelado" },
        { recordKind: "hotel", publicId: "d", optionGroup: "Grupo2", workflowStatus: "Solicitado" },
    ];
    assert.equal(contarMiembrosVivosDelGrupo("Grupo1", servicios, null), 2);
    assert.equal(contarMiembrosVivosDelGrupo("Grupo1", servicios, "a"), 1);
    assert.equal(contarMiembrosVivosDelGrupo("", servicios, null), 0);
});

test("agruparServiciosPorOpcion: servicios sin optionGroup no arman ningún grupo", () => {
    const servicios = [{ recordKind: "hotel", publicId: "a", optionGroup: null, workflowStatus: "Solicitado" }];
    assert.equal(agruparServiciosPorOpcion(servicios).size, 0);
});
