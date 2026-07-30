import { test } from "node:test";
import assert from "node:assert/strict";
import {
    FRASE_CONFIRMACION_RESTORE,
    MOTIVO_RESTAURAR_TODO_MIN_LENGTH,
    RESTORE_MODO_PRUEBA,
    RESTORE_MODO_REAL,
    RESTORE_MODO_TOTAL,
    TABLAS_CONFIGURACION_RESTORE,
    VERSION_RESGUARDO_ACTUAL,
    VERSION_RESGUARDO_ANTERIOR,
    VERSION_RESGUARDO_POSTERIOR,
    VERSION_RESGUARDO_DESCONOCIDA,
    formatearTamanioArchivo,
    construirEtiquetaBackup,
    puedeConfirmarRestore,
    construirMotivoRestoreDeshabilitado,
    motivoRestaurarTodoEsValido,
    construirMotivoRestaurarTodoDeshabilitado,
    construirConfirmacionRestore,
    esErrorDeMantenimiento,
    construirResumenExitoPruebaRestore,
    construirResumenExitoRealRestore,
    construirResumenExitoTotalRestore,
    normalizarVersionResguardo,
    construirBadgeVersionResguardo,
    construirAvisoVersionResguardo,
    construirTextoMarcaRechazo,
    construirPasosEsperaRestoreTotal,
    resolverPorQueSeGuardo,
    PASO_RESTORE_DATOS,
    PASO_RESTORE_RESGUARDO,
    PASO_RESTORE_ACTUALIZACION,
    ORIGEN_BACKUP_EMPEZAR_DE_CERO,
    ORIGEN_BACKUP_VOLVER_A_COPIA,
    ORIGEN_BACKUP_MANUAL,
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

test("motivoRestaurarTodoEsValido: vacio o corto no alcanza", () => {
    assert.equal(motivoRestaurarTodoEsValido(""), false);
    assert.equal(motivoRestaurarTodoEsValido("corto"), false);
    assert.equal(motivoRestaurarTodoEsValido(null), false);
    assert.equal(motivoRestaurarTodoEsValido(undefined), false);
});

test("motivoRestaurarTodoEsValido: exactamente el minimo (recortando espacios) ya alcanza", () => {
    const motivoJusto = "a".repeat(MOTIVO_RESTAURAR_TODO_MIN_LENGTH);
    assert.equal(motivoRestaurarTodoEsValido(motivoJusto), true);
    // Mismo criterio que el backend (Trim antes de medir): los espacios de los extremos
    // no cuentan como parte del motivo.
    assert.equal(motivoRestaurarTodoEsValido(`  ${motivoJusto}  `), true);
    assert.equal(motivoRestaurarTodoEsValido(" ".repeat(MOTIVO_RESTAURAR_TODO_MIN_LENGTH)), false);
});

test("construirMotivoRestaurarTodoDeshabilitado: motivo vacio, pide escribirlo nombrando la accion nueva 'Volver a esta copia' (rediseño 2026-07-30, P-9)", () => {
    const motivo = construirMotivoRestaurarTodoDeshabilitado("");
    assert.equal(
        motivo,
        `Para "Volver a esta copia" falta escribir el motivo (mínimo ${MOTIVO_RESTAURAR_TODO_MIN_LENGTH} caracteres).`
    );
});

test("construirMotivoRestaurarTodoDeshabilitado: motivo corto, mismo aviso que vacio", () => {
    const motivo = construirMotivoRestaurarTodoDeshabilitado("corto");
    assert.ok(motivo.includes("caracteres"));
    assert.equal(motivo.toLowerCase().includes("letras"), false);
});

test("construirMotivoRestaurarTodoDeshabilitado: motivo valido, no hay motivo de bloqueo (null)", () => {
    const motivo = construirMotivoRestaurarTodoDeshabilitado("Se detectó un problema y hay que volver atrás");
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

test("construirConfirmacionRestore: ADR-052, resguardo 'anterior' en modo total agrega la linea extra firmada", () => {
    const confirmacion = construirConfirmacionRestore(RESTORE_MODO_TOTAL, {
        fechaBackup: "27/07/2026 22:33",
        versionResguardo: VERSION_RESGUARDO_ANTERIOR,
    });
    assert.ok(confirmacion.text.includes(
        "Este resguardo es más viejo: después de traer los datos, el sistema se pone al día solo."
    ));
});

test("construirConfirmacionRestore: 'actual'/'posterior'/'desconocida'/sin dato en modo total NO agregan la linea extra", () => {
    for (const version of [VERSION_RESGUARDO_ACTUAL, VERSION_RESGUARDO_POSTERIOR, VERSION_RESGUARDO_DESCONOCIDA, undefined]) {
        const confirmacion = construirConfirmacionRestore(RESTORE_MODO_TOTAL, { fechaBackup: "27/07/2026 22:33", versionResguardo: version });
        assert.equal(confirmacion.text.includes("se pone al día solo"), false, `no deberia agregar la linea para version=${version}`);
    }
});

test("construirConfirmacionRestore: la linea extra de ADR-052 SOLO aplica al modo total, no a prueba/real", () => {
    const confirmacionPrueba = construirConfirmacionRestore(RESTORE_MODO_PRUEBA, { versionResguardo: VERSION_RESGUARDO_ANTERIOR });
    const confirmacionReal = construirConfirmacionRestore(RESTORE_MODO_REAL, { versionResguardo: VERSION_RESGUARDO_ANTERIOR });
    assert.equal(confirmacionPrueba.text.includes("se pone al día solo"), false);
    assert.equal(confirmacionReal.text.includes("se pone al día solo"), false);
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

test("construirResumenExitoPruebaRestore: reusa las mismas filas de conteo que Empezar de cero, en una lista", () => {
    const resumen = construirResumenExitoPruebaRestore({ conteos: { reservas: 30, clientes: 16 }, advertencia: null });
    assert.equal(resumen.encabezado, "Esto es lo que contiene la copia:");
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
    assert.equal(resumen.mensajeSinDatos, "La copia no tiene datos de negocio cargados.");
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

// ADR-052 (2026-07-29, "Restaurar todo acepta resguardos de versiones anteriores"): la lista
// marca la versión del resguardo, el modal avisa, y NINGÚN botón se apaga por esto.

test("normalizarVersionResguardo: los 4 valores conocidos pasan tal cual", () => {
    assert.equal(normalizarVersionResguardo(VERSION_RESGUARDO_ACTUAL), VERSION_RESGUARDO_ACTUAL);
    assert.equal(normalizarVersionResguardo(VERSION_RESGUARDO_ANTERIOR), VERSION_RESGUARDO_ANTERIOR);
    assert.equal(normalizarVersionResguardo(VERSION_RESGUARDO_POSTERIOR), VERSION_RESGUARDO_POSTERIOR);
    assert.equal(normalizarVersionResguardo(VERSION_RESGUARDO_DESCONOCIDA), VERSION_RESGUARDO_DESCONOCIDA);
});

test("normalizarVersionResguardo: compatibilidad hacia atras, ausente/invalido se trata como 'desconocida' (NUNCA 'actual')", () => {
    assert.equal(normalizarVersionResguardo(undefined), VERSION_RESGUARDO_DESCONOCIDA);
    assert.equal(normalizarVersionResguardo(null), VERSION_RESGUARDO_DESCONOCIDA);
    assert.equal(normalizarVersionResguardo(""), VERSION_RESGUARDO_DESCONOCIDA);
    assert.equal(normalizarVersionResguardo("algo-que-no-existe"), VERSION_RESGUARDO_DESCONOCIDA);
});

test("construirBadgeVersionResguardo: 'actual' no lleva badge (null, es el camino de hoy)", () => {
    assert.equal(construirBadgeVersionResguardo(VERSION_RESGUARDO_ACTUAL), null);
});

test("construirBadgeVersionResguardo: 'anterior' es ambar con texto real, nunca solo color", () => {
    assert.deepEqual(construirBadgeVersionResguardo(VERSION_RESGUARDO_ANTERIOR), { texto: "Versión anterior", color: "ambar" });
});

test("construirBadgeVersionResguardo: 'posterior' es rosa", () => {
    assert.deepEqual(construirBadgeVersionResguardo(VERSION_RESGUARDO_POSTERIOR), { texto: "Versión más nueva", color: "rosa" });
});

test("construirBadgeVersionResguardo: 'desconocida' es gris", () => {
    assert.deepEqual(construirBadgeVersionResguardo(VERSION_RESGUARDO_DESCONOCIDA), { texto: "Versión desconocida", color: "gris" });
});

test("construirBadgeVersionResguardo: campo ausente (API vieja/cache) se comporta como 'desconocida', CON badge gris", () => {
    assert.deepEqual(construirBadgeVersionResguardo(undefined), { texto: "Versión desconocida", color: "gris" });
    assert.deepEqual(construirBadgeVersionResguardo(null), { texto: "Versión desconocida", color: "gris" });
});

test("construirAvisoVersionResguardo: 'actual' no muestra cartel (null)", () => {
    assert.equal(construirAvisoVersionResguardo(VERSION_RESGUARDO_ACTUAL), null);
});

// Textos literales de guia-ux-gaston.md, sección "Textos finales implementados (2026-07-29,
// fuente única — si se cambian, se cambia acá primero)" — fix de review B1 (bloqueante): la
// versión anterior de este archivo tenía una paráfrasis, no el texto firmado.

test("construirAvisoVersionResguardo: 'anterior' — titulo+texto literales de la guia (cartel *anterior*, ámbar)", () => {
    // Fix de review (item 12, firmado por Gastón el 2026-07-30): la única mención que cambió en este
    // literal es el nombre del botón ("Restaurar todo" → "Volver a esta copia"); el resto es palabra por
    // palabra igual al firmado en guia-ux-gaston.md.
    const aviso = construirAvisoVersionResguardo(VERSION_RESGUARDO_ANTERIOR);
    assert.equal(aviso.color, "ambar");
    assert.equal(aviso.titulo, "Este resguardo es más viejo que el sistema de hoy.");
    assert.equal(
        aviso.texto,
        "Se puede usar igual: primero se traen los datos y después el sistema se pone al " +
        "día solo. Puede tardar un poco más de lo normal. Si ese último paso falla, el " +
        "sistema vuelve solo a como está ahora, sin perder nada. Esto vale para " +
        "\"Volver a esta copia\": las otras dos acciones pueden avisarte que este resguardo no " +
        "les sirve."
    );
});

test("construirAvisoVersionResguardo: 'posterior' — titulo+texto literales de la guia (cartel *posterior*, rosa)", () => {
    const aviso = construirAvisoVersionResguardo(VERSION_RESGUARDO_POSTERIOR);
    assert.equal(aviso.color, "rosa");
    assert.equal(aviso.titulo, "Este resguardo parece de una versión más nueva que el sistema de hoy.");
    assert.equal(
        aviso.texto,
        "Lo más probable es que no se pueda usar: antes de tocar nada, el sistema lo " +
        "revisa y, si es así, lo rechaza y te avisa sin haber cambiado nada. Si igual " +
        "necesitás volver a este punto, avisale al equipo técnico."
    );
});

test("construirAvisoVersionResguardo: 'desconocida' — titulo+texto literales de la guia (cartel *desconocida*, gris)", () => {
    const aviso = construirAvisoVersionResguardo(VERSION_RESGUARDO_DESCONOCIDA);
    assert.equal(aviso.color, "gris");
    assert.equal(aviso.titulo, "No pudimos determinar de qué versión es este resguardo.");
    assert.equal(aviso.texto, "Podés intentar igual: si no se puede usar, te lo avisamos antes de tocar nada.");
});

test("construirAvisoVersionResguardo: campo ausente se comporta como 'desconocida'", () => {
    assert.deepEqual(construirAvisoVersionResguardo(undefined), construirAvisoVersionResguardo(VERSION_RESGUARDO_DESCONOCIDA));
});

test("T-5: ningun texto de badge ni de cartel nombra 'migracion', 'esquema' ni 'version de base de datos'", () => {
    const textos = [
        ...[VERSION_RESGUARDO_ANTERIOR, VERSION_RESGUARDO_POSTERIOR, VERSION_RESGUARDO_DESCONOCIDA].map(
            (v) => construirBadgeVersionResguardo(v).texto
        ),
        ...[VERSION_RESGUARDO_ANTERIOR, VERSION_RESGUARDO_POSTERIOR, VERSION_RESGUARDO_DESCONOCIDA].flatMap(
            (v) => [construirAvisoVersionResguardo(v).titulo, construirAvisoVersionResguardo(v).texto]
        ),
    ].join(" ").toLowerCase();
    assert.equal(textos.includes("migraci"), false);
    assert.equal(textos.includes("esquema"), false);
    assert.equal(textos.includes("base de datos"), false);
});

// Rediseño 2026-07-30 ("Copias de seguridad", solapa propia en Administración, spec
// docs/ux/2026-07-30-rediseno-pantalla-copias-de-seguridad.md, 12 respuestas 1A..12A).

test("construirTextoMarcaRechazo: modo total (P10=A, texto literal de la spec)", () => {
    assert.equal(construirTextoMarcaRechazo(RESTORE_MODO_TOTAL), "No se pudo volver a esta copia. No se cambió nada.");
});

test("construirTextoMarcaRechazo: modo real y modo prueba tienen su propio texto, nombrando la accion que fallo", () => {
    assert.ok(construirTextoMarcaRechazo(RESTORE_MODO_REAL).toLowerCase().includes("reponer"));
    assert.ok(construirTextoMarcaRechazo(RESTORE_MODO_PRUEBA).toLowerCase().includes("ver el contenido"));
});

test("resolverPorQueSeGuardo: las 3 frases conocidas pasan tal cual (lista blanca, mismo criterio que normalizarVersionResguardo)", () => {
    assert.equal(resolverPorQueSeGuardo(ORIGEN_BACKUP_EMPEZAR_DE_CERO), ORIGEN_BACKUP_EMPEZAR_DE_CERO);
    assert.equal(resolverPorQueSeGuardo(ORIGEN_BACKUP_VOLVER_A_COPIA), ORIGEN_BACKUP_VOLVER_A_COPIA);
    assert.equal(resolverPorQueSeGuardo(ORIGEN_BACKUP_MANUAL), ORIGEN_BACKUP_MANUAL);
});

test("resolverPorQueSeGuardo: vacio/ausente cae en 'Guardada a mano', nunca undefined", () => {
    assert.equal(resolverPorQueSeGuardo(undefined), ORIGEN_BACKUP_MANUAL);
    assert.equal(resolverPorQueSeGuardo(null), ORIGEN_BACKUP_MANUAL);
    assert.equal(resolverPorQueSeGuardo(""), ORIGEN_BACKUP_MANUAL);
});

test("resolverPorQueSeGuardo: un valor DESCONOCIDO (origen nuevo que este front no contempla) tambien cae en 'Guardada a mano', nunca se muestra tal cual", () => {
    assert.equal(resolverPorQueSeGuardo("Un origen inventado que el motor todavia no manda"), ORIGEN_BACKUP_MANUAL);
    assert.equal(resolverPorQueSeGuardo("antes de empezar de cero"), ORIGEN_BACKUP_MANUAL); // no admite variaciones de mayus/minus
});

test("construirPasosEsperaRestoreTotal: paso null (sin restauracion en curso) no marca ningun estado (spec 8A)", () => {
    const pasos = construirPasosEsperaRestoreTotal({ paso: null, pasoTexto: null });
    assert.equal(pasos.length, 3);
    assert.ok(pasos.every((paso) => paso.estado === "pending"));
});

test("construirPasosEsperaRestoreTotal: paso 'datos' (el primero, orden real del motor) marca solo el actual", () => {
    const pasos = construirPasosEsperaRestoreTotal({ paso: PASO_RESTORE_DATOS, pasoTexto: "Trayendo los datos de la copia elegida" });
    assert.deepEqual(pasos.map((paso) => [paso.codigo, paso.estado]), [
        [PASO_RESTORE_DATOS, "doing"],
        [PASO_RESTORE_RESGUARDO, "pending"],
        [PASO_RESTORE_ACTUALIZACION, "pending"],
    ]);
});

test("construirPasosEsperaRestoreTotal: paso 'resguardo' (el del medio) marca 'datos' como hecho", () => {
    const pasos = construirPasosEsperaRestoreTotal({ paso: PASO_RESTORE_RESGUARDO, pasoTexto: "Guardamos una copia de cómo está el sistema ahora" });
    assert.deepEqual(pasos.map((paso) => [paso.codigo, paso.estado]), [
        [PASO_RESTORE_DATOS, "done"],
        [PASO_RESTORE_RESGUARDO, "doing"],
        [PASO_RESTORE_ACTUALIZACION, "pending"],
    ]);
});

test("construirPasosEsperaRestoreTotal: paso 'actualizacion' (el ultimo) marca los otros dos como hechos", () => {
    const pasos = construirPasosEsperaRestoreTotal({ paso: PASO_RESTORE_ACTUALIZACION, pasoTexto: "Poniendo el sistema al día" });
    assert.deepEqual(pasos.map((paso) => [paso.codigo, paso.estado]), [
        [PASO_RESTORE_DATOS, "done"],
        [PASO_RESTORE_RESGUARDO, "done"],
        [PASO_RESTORE_ACTUALIZACION, "doing"],
    ]);
});

test("construirPasosEsperaRestoreTotal: el paso EN CURSO usa el texto que manda el motor (P-13), no lo reescribe", () => {
    const pasos = construirPasosEsperaRestoreTotal({ paso: PASO_RESTORE_DATOS, pasoTexto: "texto de prueba distinto" });
    assert.equal(pasos[0].texto, "texto de prueba distinto");
});

test("construirPasosEsperaRestoreTotal: un codigo desconocido (version vieja del motor) tampoco marca nada", () => {
    const pasos = construirPasosEsperaRestoreTotal({ paso: "algo-que-no-existe", pasoTexto: "x" });
    assert.ok(pasos.every((paso) => paso.estado === "pending"));
});
