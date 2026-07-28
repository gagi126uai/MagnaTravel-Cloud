import { test } from "node:test";
import assert from "node:assert/strict";
import {
    FRASE_CONFIRMACION_RESTORE,
    RESTORE_MODO_PRUEBA,
    RESTORE_MODO_REAL,
    TABLAS_CONFIGURACION_RESTORE,
    formatearTamanioArchivo,
    construirEtiquetaBackup,
    puedeConfirmarRestore,
    construirMotivoRestoreDeshabilitado,
    construirConfirmacionRestore,
    construirTextoVerificacionRestore,
    construirResumenExitoPruebaRestore,
    construirResumenExitoRealRestore,
} from "./dangerRestoreLogic.js";

test("formatearTamanioArchivo: bytes chicos se muestran en B", () => {
    assert.equal(formatearTamanioArchivo(512), "512 B");
});

test("formatearTamanioArchivo: usa coma decimal (es-AR), no punto", () => {
    assert.equal(formatearTamanioArchivo(1_258_291), "1,2 MB");
});

test("formatearTamanioArchivo: KB sin decimales cuando el valor es >= 10", () => {
    assert.equal(formatearTamanioArchivo(348_160), "340 KB");
});

test("formatearTamanioArchivo: nulo/invalido no explota, se trata como 0", () => {
    assert.equal(formatearTamanioArchivo(null), "0 B");
    assert.equal(formatearTamanioArchivo(undefined), "0 B");
});

test("construirEtiquetaBackup: arma 'Resguardo del <fecha> — <tamaño>'", () => {
    const backup = { archivo: "wipe-20260727-101500.dump", fechaUtc: "2026-07-28T01:33:00Z", tamanioBytes: 1_258_291 };
    const etiqueta = construirEtiquetaBackup(backup, (fecha) => `FECHA(${fecha})`);
    assert.equal(etiqueta, "Resguardo del FECHA(2026-07-28T01:33:00Z) — 1,2 MB");
});

test("puedeConfirmarRestore: habilita solo con frase exacta + password + sin ejecucion en curso", () => {
    assert.equal(
        puedeConfirmarRestore({ frase: FRASE_CONFIRMACION_RESTORE, password: "1234", ejecutando: false }),
        true
    );
});

test("puedeConfirmarRestore: la frase no admite variaciones", () => {
    const base = { password: "1234", ejecutando: false };
    assert.equal(puedeConfirmarRestore({ ...base, frase: "restaurar todo" }), false);
    assert.equal(puedeConfirmarRestore({ ...base, frase: "RESTAURAR TODO " }), false);
    assert.equal(puedeConfirmarRestore({ ...base, frase: "" }), false);
});

test("puedeConfirmarRestore: sin password no habilita", () => {
    assert.equal(puedeConfirmarRestore({ frase: FRASE_CONFIRMACION_RESTORE, password: "", ejecutando: false }), false);
});

test("puedeConfirmarRestore: mientras se ejecuta, no se puede volver a disparar", () => {
    assert.equal(puedeConfirmarRestore({ frase: FRASE_CONFIRMACION_RESTORE, password: "1234", ejecutando: true }), false);
});

test("construirMotivoRestoreDeshabilitado: sin resguardo elegido, pide elegir uno", () => {
    const motivo = construirMotivoRestoreDeshabilitado({ archivoSeleccionado: null, frase: FRASE_CONFIRMACION_RESTORE, password: "1234" });
    assert.equal(motivo, "Elegí un resguardo para continuar.");
});

test("construirMotivoRestoreDeshabilitado: con resguardo pero sin frase correcta, pide escribirla", () => {
    const motivo = construirMotivoRestoreDeshabilitado({ archivoSeleccionado: "wipe-20260727-101500.dump", frase: "", password: "1234" });
    assert.ok(motivo.includes(FRASE_CONFIRMACION_RESTORE));
});

test("construirMotivoRestoreDeshabilitado: con resguardo y frase pero sin password, pide la contraseña", () => {
    const motivo = construirMotivoRestoreDeshabilitado({ archivoSeleccionado: "wipe-20260727-101500.dump", frase: FRASE_CONFIRMACION_RESTORE, password: "" });
    assert.equal(motivo, "Cargá tu contraseña para confirmar.");
});

test("construirMotivoRestoreDeshabilitado: todo en orden, no hay motivo (null)", () => {
    const motivo = construirMotivoRestoreDeshabilitado({ archivoSeleccionado: "wipe-20260727-101500.dump", frase: FRASE_CONFIRMACION_RESTORE, password: "1234" });
    assert.equal(motivo, null);
});

test("construirConfirmacionRestore: modo prueba avisa que NO toca datos reales", () => {
    const confirmacion = construirConfirmacionRestore(RESTORE_MODO_PRUEBA);
    assert.ok(confirmacion.text.includes("base de PRUEBA"));
    assert.ok(confirmacion.text.includes("NO toca ningún dato real"));
});

test("construirConfirmacionRestore: modo real avisa el alcance acotado a configuracion vacia", () => {
    const confirmacion = construirConfirmacionRestore(RESTORE_MODO_REAL);
    assert.ok(confirmacion.text.includes("configuración de la agencia"));
    assert.ok(confirmacion.text.includes("NO toca"));
    assert.ok(confirmacion.text.includes("vacías"));
});

test("construirTextoVerificacionRestore: invalido devuelve el motivo del motor tal cual", () => {
    const texto = construirTextoVerificacionRestore({ valido: false, motivo: "El archivo no existe en el servidor.", tieneTablasClave: false });
    assert.equal(texto, "El archivo no existe en el servidor.");
});

test("construirTextoVerificacionRestore: valido y completo, mensaje positivo sin jerga tecnica", () => {
    const texto = construirTextoVerificacionRestore({ valido: true, motivo: null, tieneTablasClave: true });
    assert.equal(texto, "Se pudo leer el resguardo: tiene toda la información necesaria para restaurar.");
    assert.equal(texto.toLowerCase().includes("tabla"), false);
});

test("construirTextoVerificacionRestore: valido pero incompleto, aviso de revisar con cuidado", () => {
    const texto = construirTextoVerificacionRestore({ valido: true, motivo: null, tieneTablasClave: false });
    assert.ok(texto.includes("podría faltarle alguna parte clave"));
});

test("construirResumenExitoPruebaRestore: reusa las mismas filas de conteo que Empezar de cero", () => {
    const resumen = construirResumenExitoPruebaRestore({ conteos: { reservas: 30, clientes: 16 }, advertencia: null });
    assert.equal(resumen.resumenConteos, "30 reservas · 16 clientes");
    assert.equal(resumen.advertencia, null);
});

test("construirResumenExitoPruebaRestore: propaga la advertencia del motor si vino", () => {
    const resumen = construirResumenExitoPruebaRestore({
        conteos: { reservas: 1 },
        advertencia: "Backup de una version anterior: algunos conteos no se pudieron calcular.",
    });
    assert.equal(resumen.advertencia, "Backup de una version anterior: algunos conteos no se pudieron calcular.");
});

test("construirResumenExitoPruebaRestore: resguardo vacio usa SU PROPIO texto, no el de Empezar de cero ('...para borrar')", () => {
    // Fix de review: antes reusaba construirResumenConteosWipe entero, que devuelve "Por
    // ahora no hay datos de negocio cargados PARA BORRAR" — una frase de otro flujo (borrar)
    // que no tiene sentido en el panel de exito de "probar un resguardo".
    const resumen = construirResumenExitoPruebaRestore({ conteos: { reservas: 0, clientes: 0 }, advertencia: null });
    assert.equal(resumen.resumenConteos, "El resguardo no tenía datos de negocio cargados (quedó vacío).");
    assert.equal(resumen.resumenConteos.includes("para borrar"), false);
});

test("construirResumenExitoRealRestore: muestra el mensaje del motor TAL CUAL (no lo arma ni lo traduce)", () => {
    const resumen = construirResumenExitoRealRestore({
        mensaje: "Se repuso: Los datos generales de la agencia. Las reglas de aprobación ya tenían datos, así que no se tocaron.",
        tablasRestauradas: ["los datos generales de la agencia"],
        advertencia: null,
    });
    assert.equal(resumen.mensaje, "Se repuso: Los datos generales de la agencia. Las reglas de aprobación ya tenían datos, así que no se tocaron.");
});

test("construirResumenExitoRealRestore: detecta que hay que destacar el aviso de AFIP cuando el motor la repuso", () => {
    const resumen = construirResumenExitoRealRestore({
        mensaje: "Se repuso: la conexión con AFIP. La conexión con AFIP se restauró en modo homologación; si necesitás productivo, activalo a mano.",
        tablasRestauradas: ["la conexión con AFIP"],
        advertencia: null,
    });
    assert.equal(resumen.incluyeAfip, true);
});

test("construirResumenExitoRealRestore: si AFIP no se tocó (no vino en tablasRestauradas), no destaca nada", () => {
    const resumen = construirResumenExitoRealRestore({
        mensaje: "Se repuso: las reglas de aprobación.",
        tablasRestauradas: ["las reglas de aprobación"],
        advertencia: null,
    });
    assert.equal(resumen.incluyeAfip, false);
});

test("construirResumenExitoRealRestore: sin nada restaurado ni salteado, usa el mensaje del motor ('No había nada para restaurar')", () => {
    const resumen = construirResumenExitoRealRestore({
        mensaje: "No había nada para restaurar.",
        tablasRestauradas: [],
        advertencia: null,
    });
    assert.equal(resumen.mensaje, "No había nada para restaurar.");
    assert.equal(resumen.incluyeAfip, false);
});

test("construirResumenExitoRealRestore: mensaje nulo (no deberia pasar) no rompe, usa un fallback en criollo", () => {
    const resumen = construirResumenExitoRealRestore({ mensaje: null, tablasRestauradas: [], advertencia: null });
    assert.equal(resumen.mensaje, "No había nada para restaurar.");
});

test("TABLAS_CONFIGURACION_RESTORE: son las 5 tablas conocidas del backend (WipeGroups.ConfiguracionTables)", () => {
    // Ojo: .sort() ordena el array EN EL LUGAR (in place). Como TABLAS_CONFIGURACION_RESTORE
    // es la constante real que el componente manda en el POST, hay que copiarla con [...],
    // nunca ordenar el original — si no, este test dejaría el array reordenado para
    // cualquier otro test/módulo que lo importe después en la misma corrida.
    assert.deepEqual([...TABLAS_CONFIGURACION_RESTORE].sort(), [
        "AfipSettings",
        "AgencySettings",
        "ApprovalPolicies",
        "OperationalFinanceSettings",
        "WhatsAppBotConfigs",
    ].sort());
});
