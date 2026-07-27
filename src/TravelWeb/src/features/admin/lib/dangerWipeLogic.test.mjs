import { test } from "node:test";
import assert from "node:assert/strict";
import {
    FRASE_CONFIRMACION_WIPE,
    construirFilasConteosWipe,
    construirResumenConteosWipe,
    puedeConfirmarWipe,
    construirConfirmacionEmpezarDeCero,
    construirResumenExitoWipe,
} from "./dangerWipeLogic.js";

test("puedeConfirmarWipe: habilita solo con frase exacta + password + sin bloqueo + sin ejecucion en curso", () => {
    const base = { frase: FRASE_CONFIRMACION_WIPE, password: "1234", bloqueado: false, ejecutando: false };
    assert.equal(puedeConfirmarWipe(base), true);
});

test("puedeConfirmarWipe: la frase no admite variaciones (mayusculas, espacios, trim)", () => {
    const base = { password: "1234", bloqueado: false, ejecutando: false };
    assert.equal(puedeConfirmarWipe({ ...base, frase: "borrar todo" }), false);
    assert.equal(puedeConfirmarWipe({ ...base, frase: "BORRAR TODO " }), false);
    assert.equal(puedeConfirmarWipe({ ...base, frase: " BORRAR TODO" }), false);
    assert.equal(puedeConfirmarWipe({ ...base, frase: "BORRAR  TODO" }), false);
    assert.equal(puedeConfirmarWipe({ ...base, frase: "" }), false);
});

test("puedeConfirmarWipe: sin password no habilita aunque la frase sea correcta", () => {
    assert.equal(
        puedeConfirmarWipe({ frase: FRASE_CONFIRMACION_WIPE, password: "", bloqueado: false, ejecutando: false }),
        false
    );
});

test("puedeConfirmarWipe: si el preview vino bloqueado, nunca habilita (P-9: se apaga con motivo)", () => {
    assert.equal(
        puedeConfirmarWipe({ frase: FRASE_CONFIRMACION_WIPE, password: "1234", bloqueado: true, ejecutando: false }),
        false
    );
});

test("puedeConfirmarWipe: mientras el borrado esta en curso, no se puede volver a disparar", () => {
    assert.equal(
        puedeConfirmarWipe({ frase: FRASE_CONFIRMACION_WIPE, password: "1234", bloqueado: false, ejecutando: true }),
        false
    );
});

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

test("construirConfirmacionEmpezarDeCero: sin tilde de configuracion, no menciona AFIP", () => {
    const confirmacion = construirConfirmacionEmpezarDeCero(false);
    assert.equal(confirmacion.confirmColor, "red");
    assert.equal(confirmacion.text.includes("AFIP"), false);
});

test("construirConfirmacionEmpezarDeCero: con tilde de configuracion, avisa de AFIP y reconfigurar", () => {
    const confirmacion = construirConfirmacionEmpezarDeCero(true);
    assert.ok(confirmacion.text.includes("AFIP"));
    assert.ok(confirmacion.text.includes("TAMBIÉN"));
});

test("construirResumenExitoWipe: arma el resumen final y el aviso de configuracion segun corresponda", () => {
    const sinConfig = construirResumenExitoWipe({
        borrado: { reservas: 30, clientes: 16 },
        backupArchivo: "wipe-20260727-101500.dump",
        configuracionBorrada: false,
    });
    assert.equal(sinConfig.resumenConteos, "30 reservas · 16 clientes");
    assert.equal(sinConfig.backupArchivo, "wipe-20260727-101500.dump");
    assert.ok(sinConfig.mensajeConfiguracion.includes("se conservó"));

    const conConfig = construirResumenExitoWipe({
        borrado: { reservas: 30 },
        backupArchivo: "wipe-20260727-101500.dump",
        configuracionBorrada: true,
    });
    assert.ok(conConfig.mensajeConfiguracion.includes("AFIP"));
});
