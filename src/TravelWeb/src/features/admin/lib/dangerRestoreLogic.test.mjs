import { test } from "node:test";
import assert from "node:assert/strict";
import {
    FRASE_CONFIRMACION_RESTORE,
    RESTORE_MODO_PRUEBA,
    RESTORE_MODO_REAL,
    RESTORE_MODO_TOTAL,
    TABLAS_CONFIGURACION_RESTORE,
    formatearTamanioArchivo,
    construirEtiquetaBackup,
    puedeConfirmarRestore,
    construirMotivoRestoreDeshabilitado,
    construirConfirmacionRestore,
    construirExplicacionAccionesRestore,
    esErrorDeMantenimiento,
    construirResumenExitoPruebaRestore,
    construirResumenExitoRealRestore,
    construirResumenExitoTotalRestore,
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

test("construirConfirmacionRestore: modo prueba (boton 'Ver que contiene') avisa que NO toca datos reales", () => {
    const confirmacion = construirConfirmacionRestore(RESTORE_MODO_PRUEBA);
    assert.ok(confirmacion.text.includes("base de PRUEBA"));
    assert.ok(confirmacion.text.includes("NO toca ningún dato real"));
});

test("construirConfirmacionRestore: modo total avisa duro que TODO el sistema vuelve para atras y que se pierde lo cargado despues", () => {
    const confirmacion = construirConfirmacionRestore(RESTORE_MODO_TOTAL, { fechaBackup: "27/07/2026 22:33" });
    assert.equal(confirmacion.title, "¿Restaurar TODO el sistema?");
    assert.ok(confirmacion.text.includes("TODO el sistema a como estaba el 27/07/2026 22:33"));
    assert.ok(confirmacion.text.includes("se pierde"));
    assert.ok(confirmacion.text.includes("se guarda un resguardo del estado actual"));
    assert.equal(confirmacion.confirmColor, "red");
});

test("construirConfirmacionRestore: modo total sin fecha (no deberia pasar) no rompe, usa un fallback generico", () => {
    const confirmacion = construirConfirmacionRestore(RESTORE_MODO_TOTAL, {});
    assert.ok(confirmacion.text.includes("como estaba el de este resguardo"));
});

test("esErrorDeMantenimiento: 503 + code MAINTENANCE es mantenimiento", () => {
    assert.equal(esErrorDeMantenimiento({ status: 503, code: "MAINTENANCE" }), true);
});

test("esErrorDeMantenimiento: un 503 sin ese code exacto NO es mantenimiento (puede ser la base de datos caida por otro motivo)", () => {
    assert.equal(esErrorDeMantenimiento({ status: 503, code: null }), false);
    assert.equal(esErrorDeMantenimiento({ status: 503, code: "database_unavailable" }), false);
});

test("esErrorDeMantenimiento: el code MAINTENANCE en otro status (ej. 500) no cuenta", () => {
    assert.equal(esErrorDeMantenimiento({ status: 500, code: "MAINTENANCE" }), false);
});

test("construirExplicacionAccionesRestore: explica las 3 acciones sin nombres tecnicos", () => {
    const lineas = construirExplicacionAccionesRestore();
    assert.equal(lineas.length, 3);
    assert.ok(lineas[0].startsWith("Ver qué contiene"));
    assert.ok(lineas[1].startsWith("Restaurar configuración"));
    assert.ok(lineas[2].startsWith("Restaurar todo"));
    // P-1: nunca se dice "tabla" en pantalla, es jerga de base de datos.
    assert.equal(lineas.join(" ").toLowerCase().includes("tabla"), false);
});

test("construirResumenExitoPruebaRestore: reusa las mismas filas de conteo que Empezar de cero, en una lista", () => {
    const resumen = construirResumenExitoPruebaRestore({ conteos: { reservas: 30, clientes: 16 }, advertencia: null });
    assert.equal(resumen.encabezado, "Esto es lo que contiene el resguardo:");
    assert.equal(resumen.sinDatos, false);
    assert.deepEqual(resumen.filas, [
        { clave: "reservas", etiqueta: "reservas", cantidad: 30 },
        { clave: "clientes", etiqueta: "clientes", cantidad: 16 },
    ]);
    assert.equal(resumen.advertencia, null);
});

test("construirResumenExitoPruebaRestore: explica el proceso (copia aparte, se conto, se borro)", () => {
    const resumen = construirResumenExitoPruebaRestore({ conteos: { reservas: 1 }, advertencia: null });
    assert.ok(resumen.comoSeHizo.includes("copia aparte"));
    assert.ok(resumen.comoSeHizo.includes("no se tocaron"));
});

test("construirResumenExitoPruebaRestore: propaga la advertencia del motor si vino", () => {
    const resumen = construirResumenExitoPruebaRestore({
        conteos: { reservas: 1 },
        advertencia: "Backup de una version anterior: algunos conteos no se pudieron calcular.",
    });
    assert.equal(resumen.advertencia, "Backup de una version anterior: algunos conteos no se pudieron calcular.");
});

test("construirResumenExitoPruebaRestore: conteos todos en cero, dice claramente que no hay datos de negocio", () => {
    // Caso pedido explicitamente: antes esto quedaba ambiguo ("no hizo nada"). Ahora tiene
    // que quedar clarísimo que el resguardo en si no tenia datos cargados.
    const resumen = construirResumenExitoPruebaRestore({ conteos: { reservas: 0, clientes: 0 }, advertencia: null });
    assert.equal(resumen.sinDatos, true);
    assert.deepEqual(resumen.filas, []);
    assert.equal(resumen.mensajeSinDatos, "El resguardo no tiene datos de negocio cargados.");
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

test("construirResumenExitoTotalRestore: muestra el mensaje del motor TAL CUAL, junto con backupPrevio y restauradoDe", () => {
    const resumen = construirResumenExitoTotalRestore({
        mensaje: "El sistema se restauró desde el resguardo del 27/07/2026 22:33.",
        backupPrevio: "wipe-20260727-233000.dump",
        restauradoDe: "Resguardo del 27/07/2026 22:33 — 1,2 MB",
    });
    assert.equal(resumen.mensaje, "El sistema se restauró desde el resguardo del 27/07/2026 22:33.");
    assert.equal(resumen.backupPrevio, "wipe-20260727-233000.dump");
    assert.equal(resumen.restauradoDe, "Resguardo del 27/07/2026 22:33 — 1,2 MB");
});

test("construirResumenExitoTotalRestore: valores nulos (no deberian pasar) no rompen, usan fallbacks seguros", () => {
    const resumen = construirResumenExitoTotalRestore({ mensaje: null, backupPrevio: null, restauradoDe: null });
    assert.equal(resumen.mensaje, "El sistema se restauró correctamente.");
    assert.equal(resumen.backupPrevio, null);
    assert.equal(resumen.restauradoDe, null);
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
