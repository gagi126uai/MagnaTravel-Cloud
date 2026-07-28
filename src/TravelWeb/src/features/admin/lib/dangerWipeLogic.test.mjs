import { test } from "node:test";
import assert from "node:assert/strict";
import {
    FRASE_CONFIRMACION_WIPE,
    WIPE_GRUPO_RESERVAS_Y_PLATA,
    WIPE_GRUPO_CLIENTES,
    WIPE_GRUPO_OPERADORES,
    WIPE_GRUPO_TARIFARIO,
    WIPE_GRUPO_CONFIGURACION,
    TODOS_LOS_GRUPOS_WIPE,
    construirFilasConteosWipe,
    construirResumenConteosWipe,
    construirDetalleConteoGrupoWipe,
    construirGruposWipeParaMostrar,
    calcularSeleccionEfectivaWipe,
    estaForzadoPorOtroGrupo,
    calcularGruposArrastradosWipe,
    alternarGrupoWipe,
    seleccionarTodosLosGruposWipe,
    puedeConfirmarWipe,
    construirMotivoWipeDeshabilitado,
    construirResumenCompletoSeleccionWipe,
    construirConfirmacionEmpezarDeCero,
    construirResumenExitoWipe,
} from "./dangerWipeLogic.js";

// Mapa de dependencias real que manda el backend (WipeGroups.ForcedDependencies), usado
// en todos los tests de esta seccion para no tener que reinventarlo en cada test.
const DEPENDENCIAS_REALES = {
    [WIPE_GRUPO_RESERVAS_Y_PLATA]: [],
    [WIPE_GRUPO_CLIENTES]: [WIPE_GRUPO_RESERVAS_Y_PLATA],
    [WIPE_GRUPO_OPERADORES]: [WIPE_GRUPO_RESERVAS_Y_PLATA],
    [WIPE_GRUPO_TARIFARIO]: [],
    paisesYDestinos: [],
    posiblesClientes: [],
    [WIPE_GRUPO_CONFIGURACION]: [],
};

test("construirFilasConteosWipe: arma una fila por grupo, con singular/plural segun cantidad", () => {
    const filas = construirFilasConteosWipe({ reservas: 1, clientes: 16, facturas: 0 });
    const reservas = filas.find((f) => f.clave === "reservas");
    const clientes = filas.find((f) => f.clave === "clientes");
    const facturas = filas.find((f) => f.clave === "facturas");

    assert.equal(reservas.cantidad, 1);
    assert.equal(reservas.etiqueta, "reserva");
    assert.equal(clientes.cantidad, 16);
    assert.equal(clientes.etiqueta, "clientes");
    assert.equal(facturas.cantidad, 0);
    assert.equal(facturas.etiqueta, "facturas");
});

test("construirFilasConteosWipe: sin conteos (null/undefined) no explota, devuelve vacio", () => {
    assert.deepEqual(construirFilasConteosWipe(null), []);
    assert.deepEqual(construirFilasConteosWipe(undefined), []);
});

test("construirResumenConteosWipe: junta los grupos con cantidad > 0, separados por bullet", () => {
    const resumen = construirResumenConteosWipe({ reservas: 30, clientes: 16, operadores: 8, facturas: 0 });
    assert.equal(resumen, "30 reservas · 16 clientes · 8 operadores");
});

test("construirResumenConteosWipe: si todo esta en cero, avisa que no hay nada para borrar", () => {
    const resumen = construirResumenConteosWipe({ reservas: 0, clientes: 0 });
    assert.equal(resumen, "Por ahora no hay datos de negocio cargados para borrar.");
});

test("construirDetalleConteoGrupoWipe: filtra el conteo a las claves del grupo pedido", () => {
    const conteos = { reservas: 59, pasajeros: 3, facturas: 12, cobros: 142, movimientosCaja: 8, archivos: 4, clientes: 16 };
    const detalle = construirDetalleConteoGrupoWipe(WIPE_GRUPO_RESERVAS_Y_PLATA, conteos);
    assert.equal(detalle, "59 reservas · 3 pasajeros · 12 facturas · 142 cobros · 8 movimientos de caja · 4 archivos");

    const detalleClientes = construirDetalleConteoGrupoWipe(WIPE_GRUPO_CLIENTES, conteos);
    assert.equal(detalleClientes, "16 clientes");
});

test("construirDetalleConteoGrupoWipe: 'configuracion' no tiene conteo, devuelve null", () => {
    assert.equal(construirDetalleConteoGrupoWipe(WIPE_GRUPO_CONFIGURACION, { reservas: 1 }), null);
});

test("construirDetalleConteoGrupoWipe: grupo en cero avisa que no hay datos, en vez de un string vacio", () => {
    const detalle = construirDetalleConteoGrupoWipe(WIPE_GRUPO_OPERADORES, { operadores: 0 });
    assert.equal(detalle, "sin datos cargados por ahora");
});

test("construirGruposWipeParaMostrar: arma los 7 grupos con etiqueta y detalle", () => {
    const grupos = construirGruposWipeParaMostrar({ reservas: 30, clientes: 16 });
    assert.equal(grupos.length, 7);
    assert.ok(grupos.some((g) => g.clave === WIPE_GRUPO_CONFIGURACION && g.detalleConteo === null));
    assert.ok(grupos.some((g) => g.clave === WIPE_GRUPO_CLIENTES && g.detalleConteo === "16 clientes"));
});

test("calcularSeleccionEfectivaWipe: tildar clientes arrastra reservasYPlata (dependencia real)", () => {
    const efectiva = calcularSeleccionEfectivaWipe([WIPE_GRUPO_CLIENTES], DEPENDENCIAS_REALES);
    assert.ok(efectiva.includes(WIPE_GRUPO_CLIENTES));
    assert.ok(efectiva.includes(WIPE_GRUPO_RESERVAS_Y_PLATA));
    assert.equal(efectiva.length, 2);
});

test("calcularSeleccionEfectivaWipe: grupo sin dependencias no arrastra a nadie", () => {
    const efectiva = calcularSeleccionEfectivaWipe([WIPE_GRUPO_TARIFARIO], DEPENDENCIAS_REALES);
    assert.deepEqual(efectiva, [WIPE_GRUPO_TARIFARIO]);
});

test("calcularGruposArrastradosWipe: reservasYPlata queda marcado como arrastrado por clientes", () => {
    const arrastrados = calcularGruposArrastradosWipe([WIPE_GRUPO_CLIENTES], DEPENDENCIAS_REALES);
    assert.deepEqual(arrastrados, { [WIPE_GRUPO_RESERVAS_Y_PLATA]: [WIPE_GRUPO_CLIENTES] });
});

test("calcularGruposArrastradosWipe: fix 'click mudo' — si el usuario TAMBIEN tildo a mano el grupo arrastrado, sigue apareciendo como arrastrado (con motivo)", () => {
    // Caso real reportado: 'Seleccionar todo' tilda los 7 grupos a mano, incluido
    // reservasYPlata — que igual sigue siendo arrastrado por clientes/operadores. Antes del
    // fix, este caso devolvia {} (el checkbox se veia habilitado pero destildarlo no hacia
    // nada, sin ninguna explicacion).
    const arrastrados = calcularGruposArrastradosWipe(
        [WIPE_GRUPO_CLIENTES, WIPE_GRUPO_RESERVAS_Y_PLATA],
        DEPENDENCIAS_REALES
    );
    assert.deepEqual(arrastrados, { [WIPE_GRUPO_RESERVAS_Y_PLATA]: [WIPE_GRUPO_CLIENTES] });
});

test("estaForzadoPorOtroGrupo: reservasYPlata sigue forzado por clientes aunque el usuario TAMBIEN lo haya tildado a mano", () => {
    assert.equal(
        estaForzadoPorOtroGrupo(WIPE_GRUPO_RESERVAS_Y_PLATA, [WIPE_GRUPO_CLIENTES, WIPE_GRUPO_RESERVAS_Y_PLATA], DEPENDENCIAS_REALES),
        true
    );
});

test("estaForzadoPorOtroGrupo: sin ningun grupo forzador tildado, no esta forzado", () => {
    assert.equal(
        estaForzadoPorOtroGrupo(WIPE_GRUPO_RESERVAS_Y_PLATA, [WIPE_GRUPO_RESERVAS_Y_PLATA], DEPENDENCIAS_REALES),
        false
    );
});

test("estaForzadoPorOtroGrupo: 'Seleccionar todo' (los 7 tildados a mano) igual marca reservasYPlata como forzado", () => {
    assert.equal(
        estaForzadoPorOtroGrupo(WIPE_GRUPO_RESERVAS_Y_PLATA, TODOS_LOS_GRUPOS_WIPE, DEPENDENCIAS_REALES),
        true
    );
});

test("calcularGruposArrastradosWipe: con clientes Y operadores tildados, reservasYPlata lista a los dos responsables", () => {
    const arrastrados = calcularGruposArrastradosWipe(
        [WIPE_GRUPO_CLIENTES, WIPE_GRUPO_OPERADORES],
        DEPENDENCIAS_REALES
    );
    assert.deepEqual(arrastrados[WIPE_GRUPO_RESERVAS_Y_PLATA].sort(), [WIPE_GRUPO_CLIENTES, WIPE_GRUPO_OPERADORES].sort());
});

test("alternarGrupoWipe: tildar siempre se permite", () => {
    const resultado = alternarGrupoWipe({
        gruposManual: [],
        grupo: WIPE_GRUPO_CLIENTES,
        dependencias: DEPENDENCIAS_REALES,
        tildar: true,
    });
    assert.deepEqual(resultado, [WIPE_GRUPO_CLIENTES]);
});

test("alternarGrupoWipe: NO deja destildar reservasYPlata mientras clientes siga tildado", () => {
    const gruposManual = [WIPE_GRUPO_CLIENTES];
    const resultado = alternarGrupoWipe({
        gruposManual,
        grupo: WIPE_GRUPO_RESERVAS_Y_PLATA,
        dependencias: DEPENDENCIAS_REALES,
        tildar: false,
    });
    // El intento de destildar no cambia nada: sigue sin estar en la lista manual (nunca estuvo,
    // el bloqueo es que tampoco se puede "agregar y sacar" para forzarlo desde afuera).
    assert.deepEqual(resultado, gruposManual);
});

test("alternarGrupoWipe: una vez destildado clientes, reservasYPlata (tildado a mano antes) se puede destildar", () => {
    // El usuario tildo clientes Y ademas tildo reservasYPlata a mano.
    let gruposManual = [WIPE_GRUPO_CLIENTES, WIPE_GRUPO_RESERVAS_Y_PLATA];

    // Ahora destilda clientes.
    gruposManual = alternarGrupoWipe({ gruposManual, grupo: WIPE_GRUPO_CLIENTES, dependencias: DEPENDENCIAS_REALES, tildar: false });
    assert.deepEqual(gruposManual, [WIPE_GRUPO_RESERVAS_Y_PLATA]);

    // Reservas y su plata ya no esta bloqueado (nadie mas lo arrastra): se puede destildar.
    gruposManual = alternarGrupoWipe({ gruposManual, grupo: WIPE_GRUPO_RESERVAS_Y_PLATA, dependencias: DEPENDENCIAS_REALES, tildar: false });
    assert.deepEqual(gruposManual, []);
});

test("alternarGrupoWipe: fix 'click mudo' — con 'Seleccionar todo' tildado, destildar reservasYPlata sigue bloqueado (no hace nada)", () => {
    const gruposManual = seleccionarTodosLosGruposWipe(); // los 7, incluido reservasYPlata a mano.
    const resultado = alternarGrupoWipe({
        gruposManual,
        grupo: WIPE_GRUPO_RESERVAS_Y_PLATA,
        dependencias: DEPENDENCIAS_REALES,
        tildar: false,
    });
    assert.deepEqual(resultado, gruposManual); // sin cambios: clientes/operadores lo siguen arrastrando.
});

test("seleccionarTodosLosGruposWipe: devuelve los 7 grupos (atajo 'Seleccionar todo')", () => {
    assert.deepEqual(seleccionarTodosLosGruposWipe(), TODOS_LOS_GRUPOS_WIPE);
    assert.equal(seleccionarTodosLosGruposWipe().length, 7);
});

test("puedeConfirmarWipe: habilita solo con grupo + frase exacta + password + sin bloqueo + sin ejecucion en curso", () => {
    const base = {
        grupos: [WIPE_GRUPO_CLIENTES],
        frase: FRASE_CONFIRMACION_WIPE,
        password: "1234",
        bloqueado: false,
        ejecutando: false,
    };
    assert.equal(puedeConfirmarWipe(base), true);
});

test("puedeConfirmarWipe: sin ningun grupo seleccionado, no habilita aunque el resto este perfecto", () => {
    assert.equal(
        puedeConfirmarWipe({ grupos: [], frase: FRASE_CONFIRMACION_WIPE, password: "1234", bloqueado: false, ejecutando: false }),
        false
    );
});

test("puedeConfirmarWipe: la frase no admite variaciones (mayusculas, espacios, trim)", () => {
    const base = { grupos: [WIPE_GRUPO_CLIENTES], password: "1234", bloqueado: false, ejecutando: false };
    assert.equal(puedeConfirmarWipe({ ...base, frase: "borrar todo" }), false);
    assert.equal(puedeConfirmarWipe({ ...base, frase: "BORRAR TODO " }), false);
    assert.equal(puedeConfirmarWipe({ ...base, frase: " BORRAR TODO" }), false);
    assert.equal(puedeConfirmarWipe({ ...base, frase: "BORRAR  TODO" }), false);
    assert.equal(puedeConfirmarWipe({ ...base, frase: "" }), false);
});

test("puedeConfirmarWipe: sin password no habilita aunque la frase sea correcta", () => {
    assert.equal(
        puedeConfirmarWipe({ grupos: [WIPE_GRUPO_CLIENTES], frase: FRASE_CONFIRMACION_WIPE, password: "", bloqueado: false, ejecutando: false }),
        false
    );
});

test("puedeConfirmarWipe: si el preview vino bloqueado, nunca habilita (P-9: se apaga con motivo)", () => {
    assert.equal(
        puedeConfirmarWipe({ grupos: [WIPE_GRUPO_CLIENTES], frase: FRASE_CONFIRMACION_WIPE, password: "1234", bloqueado: true, ejecutando: false }),
        false
    );
});

test("puedeConfirmarWipe: mientras el borrado esta en curso, no se puede volver a disparar", () => {
    assert.equal(
        puedeConfirmarWipe({ grupos: [WIPE_GRUPO_CLIENTES], frase: FRASE_CONFIRMACION_WIPE, password: "1234", bloqueado: false, ejecutando: true }),
        false
    );
});

test("construirMotivoWipeDeshabilitado: sin grupos elegidos, pide elegir al menos uno", () => {
    const motivo = construirMotivoWipeDeshabilitado({ grupos: [], frase: FRASE_CONFIRMACION_WIPE, password: "1234", bloqueado: false });
    assert.equal(motivo, "Elegí al menos un grupo para borrar.");
});

test("construirMotivoWipeDeshabilitado: con grupo pero sin frase correcta, pide escribir la frase", () => {
    const motivo = construirMotivoWipeDeshabilitado({ grupos: [WIPE_GRUPO_CLIENTES], frase: "", password: "1234", bloqueado: false });
    assert.ok(motivo.includes(FRASE_CONFIRMACION_WIPE));
});

test("construirMotivoWipeDeshabilitado: con grupo y frase pero sin password, pide la contraseña", () => {
    const motivo = construirMotivoWipeDeshabilitado({ grupos: [WIPE_GRUPO_CLIENTES], frase: FRASE_CONFIRMACION_WIPE, password: "", bloqueado: false });
    assert.equal(motivo, "Cargá tu contraseña para confirmar.");
});

test("construirMotivoWipeDeshabilitado: bloqueado por el motor, muestra el motivo del motor por sobre cualquier otro", () => {
    const motivo = construirMotivoWipeDeshabilitado({
        grupos: [WIPE_GRUPO_CLIENTES],
        frase: FRASE_CONFIRMACION_WIPE,
        password: "1234",
        bloqueado: true,
        motivoBloqueo: "Hay facturas sin homologar todavía.",
    });
    assert.equal(motivo, "Hay facturas sin homologar todavía.");
});

test("construirMotivoWipeDeshabilitado: todo en orden, no hay motivo (null)", () => {
    const motivo = construirMotivoWipeDeshabilitado({
        grupos: [WIPE_GRUPO_CLIENTES],
        frase: FRASE_CONFIRMACION_WIPE,
        password: "1234",
        bloqueado: false,
    });
    assert.equal(motivo, null);
});

test("construirResumenCompletoSeleccionWipe: lista completa de lo que vuela, en el orden del catalogo", () => {
    const resumen = construirResumenCompletoSeleccionWipe(
        [WIPE_GRUPO_OPERADORES, WIPE_GRUPO_CLIENTES, WIPE_GRUPO_RESERVAS_Y_PLATA],
        { reservas: 30, clientes: 16, operadores: 8 }
    );
    assert.deepEqual(resumen.map((fila) => fila.clave), [WIPE_GRUPO_RESERVAS_Y_PLATA, WIPE_GRUPO_CLIENTES, WIPE_GRUPO_OPERADORES]);
    assert.equal(resumen.find((f) => f.clave === WIPE_GRUPO_CLIENTES).detalleConteo, "16 clientes");
});

test("construirConfirmacionEmpezarDeCero: sin configuracion, no la menciona en la lista de grupos", () => {
    const confirmacion = construirConfirmacionEmpezarDeCero([WIPE_GRUPO_CLIENTES, WIPE_GRUPO_RESERVAS_Y_PLATA]);
    assert.equal(confirmacion.confirmColor, "red");
    assert.ok(confirmacion.text.includes("Clientes"));
    assert.ok(confirmacion.text.includes("Reservas y su plata"));
    assert.equal(confirmacion.text.includes("AFIP"), false);
});

test("construirConfirmacionEmpezarDeCero: con configuracion elegida, avisa de AFIP y reconfigurar", () => {
    const confirmacion = construirConfirmacionEmpezarDeCero([WIPE_GRUPO_CONFIGURACION]);
    assert.ok(confirmacion.text.includes("Configuración de la agencia"));
    assert.ok(confirmacion.text.includes("AFIP"));
    assert.ok(confirmacion.text.includes("TAMBIÉN"));
});

test("construirResumenExitoWipe: arma el resumen final y el aviso de configuracion segun corresponda", () => {
    const formatearFecha = (fecha) => `FECHA(${fecha})`;

    const sinConfig = construirResumenExitoWipe({
        borrado: { reservas: 30, clientes: 16 },
        backupArchivo: "wipe-20260727-101500.dump",
        gruposBorrados: [WIPE_GRUPO_RESERVAS_Y_PLATA, WIPE_GRUPO_CLIENTES],
        ahora: "2026-07-27T22:33:00Z",
        formatearFecha,
    });
    assert.equal(sinConfig.resumenConteos, "30 reservas · 16 clientes");
    assert.ok(sinConfig.mensajeConfiguracion.includes("se conservó"));

    const conConfig = construirResumenExitoWipe({
        borrado: { reservas: 30 },
        backupArchivo: "wipe-20260727-101500.dump",
        gruposBorrados: [WIPE_GRUPO_RESERVAS_Y_PLATA, WIPE_GRUPO_CONFIGURACION],
        ahora: "2026-07-27T22:33:00Z",
        formatearFecha,
    });
    assert.ok(conConfig.mensajeConfiguracion.includes("AFIP"));
});

test("construirResumenExitoWipe: NUNCA muestra el nombre tecnico del archivo de backup (T-5)", () => {
    const resumen = construirResumenExitoWipe({
        borrado: { reservas: 1 },
        backupArchivo: "wipe-20260727-101500.dump",
        gruposBorrados: [WIPE_GRUPO_RESERVAS_Y_PLATA],
        ahora: "2026-07-27T22:33:00Z",
        formatearFecha: (fecha) => `FECHA(${fecha})`,
    });
    assert.equal(resumen.mensajeBackup.includes("wipe-20260727-101500.dump"), false);
    assert.equal(resumen.mensajeBackup.includes(".dump"), false);
    assert.ok(resumen.mensajeBackup.includes("Volver atrás"));
    assert.ok(resumen.mensajeBackup.includes("FECHA(2026-07-27T22:33:00Z)"));
});

test("construirResumenExitoWipe: sin backupArchivo (no deberia pasar), mensajeBackup es null en vez de romper", () => {
    const resumen = construirResumenExitoWipe({
        borrado: { reservas: 1 },
        backupArchivo: null,
        gruposBorrados: [WIPE_GRUPO_RESERVAS_Y_PLATA],
        formatearFecha: (fecha) => `FECHA(${fecha})`,
    });
    assert.equal(resumen.mensajeBackup, null);
});
