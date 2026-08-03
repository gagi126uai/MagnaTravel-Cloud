import { useState } from "react";
import { Loader2, RotateCcw } from "lucide-react";
import { api } from "../../../api";
import { showConfirm } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { formatDateTime } from "../../../lib/utils";
import { CartelEmergente, CARTEL_EMERGENTE_VARIANTES } from "../../../components/CartelEmergente";
import { activateMaintenance, deactivateMaintenance, marcarPedidoLocalPerdido } from "../../../maintenanceState";
import { RestoreResultadoInline } from "./RestoreResultadoInline";
import {
    FRASE_CONFIRMACION_RESTORE,
    MOTIVO_RESTAURAR_TODO_MIN_LENGTH,
    RESTORE_MODO_PRUEBA,
    RESTORE_MODO_REAL,
    RESTORE_MODO_TOTAL,
    TABLAS_CONFIGURACION_RESTORE,
    puedeConfirmarRestore,
    construirMotivoRestoreDeshabilitado,
    construirMotivoRestaurarTodoDeshabilitado,
    motivoRestaurarTodoEsValido,
    construirConfirmacionRestore,
    construirResumenExitoTotalRestore,
    construirAvisoVersionResguardo,
    construirTextoMarcaRechazo,
    debeSeguirEsperandoTrasErrorDeRestoreTotal,
} from "../lib/dangerRestoreLogic";

// ADR-052 (2026-07-29): mismos 3 colores que el badge de la fila, en formato caja informativa.
const AVISO_VERSION_CLASSES = {
    ambar: "border-amber-200 bg-amber-50 text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-300",
    rosa: "border-rose-200 bg-rose-50 text-rose-800 dark:border-rose-800 dark:bg-rose-950/30 dark:text-rose-200",
    gris: "border-slate-200 bg-slate-50 text-slate-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300",
};
const clasesAvisoVersion = (color) => AVISO_VERSION_CLASSES[color] ?? AVISO_VERSION_CLASSES.gris;

/**
 * Ficha de trabajo EN LÍNEA (P-5) de una copia elegida: se abre debajo de su fila en la
 * tabla de "Copias de seguridad" (rediseño 2026-07-30, reemplaza el viejo modal "Volver
 * atrás"). Ofrece las TRES acciones sobre el resguardo elegido:
 * - "Volver a esta copia" (acción PRINCIPAL, botón grande, ex "Restaurar todo"): vuelve TODO
 *   el sistema al estado de este resguardo. Mientras corre, prende la pantalla de
 *   mantenimiento global (ver maintenanceState.js) porque el motor tumba toda la API.
 * - "Ver qué contiene" / "Reponer configuración" (acciones secundarias, links chicos al
 *   costado, spec P6=A): restauran a una base de prueba o solo la configuración vacía,
 *   respectivamente. Su resultado se muestra EN UN PANEL DENTRO de esta misma ficha
 *   (RestoreResultadoInline), sin desmontar los campos — retoque en vivo de Gastón
 *   (2026-07-30): antes el resultado reemplazaba toda la ficha y se perdía lo tipeado
 *   (frase/contraseña/motivo), rozando P-7.
 *
 * Misma frase+contraseña "a prueba de dedos" que Empezar de cero, mismo "¿Seguro?" de
 * siempre (showConfirm) y mismo Cartel emergente único para el rechazo del motor (P-4/P-13).
 */
export function RestoreBackupFicha({ backup, onSuccessTotal }) {
    const [frase, setFrase] = useState("");
    const [password, setPassword] = useState("");
    const [motivoRestaurarTodo, setMotivoRestaurarTodo] = useState("");
    const [accionEnCurso, setAccionEnCurso] = useState(null); // null | "prueba" | "real" | "total"
    const [resultadoInline, setResultadoInline] = useState(null); // { modo, data } de "prueba"/"real"

    // El mensaje del motor queda guardado SIEMPRE (aunque el cartel se cierre), para que
    // "Ver el motivo" pueda reabrir el mismo Cartel emergente con el mismo texto (P-7: no se
    // pierde nada al cerrar el aviso). `cartelAbierto` es solo la visibilidad del cartel.
    const [rejectionInfo, setRejectionInfo] = useState(null); // { modo, mensaje }
    const [cartelAbierto, setCartelAbierto] = useState(false);

    const avisoVersion = construirAvisoVersionResguardo(backup.versionResguardo);
    const ejecutando = Boolean(accionEnCurso);

    const puedeConfirmar = !ejecutando && puedeConfirmarRestore({ frase, password, ejecutando });
    const puedeConfirmarTotal = puedeConfirmar && motivoRestaurarTodoEsValido(motivoRestaurarTodo);

    // P-9/P-10 (prohibido tooltip): el motivo por el que la acción principal está apagada
    // siempre se ve como texto, nunca solo en un title.
    const motivoAccionDeshabilitada = ejecutando
        ? null
        : construirMotivoRestoreDeshabilitado({ archivoSeleccionado: backup.archivo, frase, password });
    const motivoRestaurarTodoDeshabilitado = ejecutando || motivoAccionDeshabilitada
        ? null
        : construirMotivoRestaurarTodoDeshabilitado(motivoRestaurarTodo);
    const motivoTotalEsCorto = motivoRestaurarTodo.length > 0 && !motivoRestaurarTodoEsValido(motivoRestaurarTodo);

    const handleAccion = async (modo) => {
        const puedeConfirmarEsteModo = modo === RESTORE_MODO_TOTAL ? puedeConfirmarTotal : puedeConfirmar;
        if (!puedeConfirmarEsteModo) return;

        const fechaBackup = formatDateTime(backup.fechaUtc);
        const confirmado = await showConfirm(
            construirConfirmacionRestore(modo, { fechaBackup, versionResguardo: backup.versionResguardo })
        );
        if (!confirmado) return;

        const esModoTotal = modo === RESTORE_MODO_TOTAL;
        if (esModoTotal) {
            // Mientras dura una restauración TOTAL, cualquier pedido a la API devuelve 503 —
            // prendemos el cartel de mantenimiento de entrada, con la fecha de este resguardo
            // (solo esta pestaña la conoce, ver maintenanceState.js).
            activateMaintenance({ awaitingLocalResult: true, fechaResguardo: fechaBackup });
        }

        setAccionEnCurso(modo);
        setRejectionInfo(null);
        // F6 (medio 31/07): si el pedido de un restore TOTAL se corta por el reinicio de la
        // API a mitad de camino, NO es un rechazo — seguimos esperando (ver el helper). Esta
        // bandera evita que el `finally` de abajo apague la pantalla de mantenimiento antes de
        // tiempo en ese caso.
        let siguioEsperandoTrasCorteDeProxy = false;
        try {
            const resultado = await api.post("/admin/danger/restore", {
                archivo: backup.archivo,
                password,
                phrase: frase,
                modo,
                tablas: modo === RESTORE_MODO_REAL ? TABLAS_CONFIGURACION_RESTORE : undefined,
                motivo: esModoTotal ? motivoRestaurarTodo.trim() : undefined,
            });

            if (esModoTotal) {
                // No hay ventana de éxito para esta acción (spec §4.6): el cartel verde vive
                // arriba de la lista, en la página — lo arma el padre, que además refresca la
                // lista (la copia recién creada del estado anterior aparece primera).
                onSuccessTotal({
                    backup,
                    resumenTotal: construirResumenExitoTotalRestore(resultado),
                });
            } else {
                setResultadoInline({ modo, data: resultado });
            }
        } catch (error) {
            if (esModoTotal && debeSeguirEsperandoTrasErrorDeRestoreTotal(error)) {
                // No mostramos rechazo ni apagamos mantenimiento: el sondeo de MaintenanceScreen
                // (ya prendido más arriba, con awaitingLocalResult=true) es quien va a apagar el
                // cartel apenas el sistema vuelva a responder de verdad.
                siguioEsperandoTrasCorteDeProxy = true;
                // Fix bug real (plan tanda F): este mismo pedido (el que armó esta promesa) ya
                // rechazó y no se reintenta — aunque el motor termine bien de fondo, ESTA ficha
                // nunca va a recibir el resumen de éxito para mostrarlo (onSuccessTotal no se
                // llama en este catch). Avisamos al store para que, cuando el sistema vuelva,
                // MaintenanceScreen haga un reload duro en vez de solo apagar el cartel y dejar
                // a este usuario mirando la SPA con los datos de la base vieja.
                marcarPedidoLocalPerdido();
            } else {
                setRejectionInfo({ modo, mensaje: getApiErrorMessage(error, "No se pudo completar la operación.") });
                setCartelAbierto(true);
            }
        } finally {
            setPassword("");
            setAccionEnCurso(null);
            if (esModoTotal && !siguioEsperandoTrasCorteDeProxy) deactivateMaintenance();
        }
    };

    return (
        <div className="space-y-4">
            {/* Fix de review (accesibilidad, hallazgo bloqueante — patrón que ya tenía el modal viejo):
                el contenedor con role="status" queda SIEMPRE montado (vacío y oculto visualmente con
                "sr-only" cuando el resguardo es "actual", sin aviso). Si el div recién se monta AL MISMO
                TIEMPO que aparece el texto, algunos lectores de pantalla no llegan a "engancharse" a la
                región y no anuncian esa primera aparición — mantenerlo siempre en el DOM garantiza que el
                cambio de contenido se anuncie también la primera vez. */}
            <div
                role="status"
                aria-live="polite"
                data-testid="restore-ficha-aviso-version"
                className={
                    avisoVersion
                        ? `rounded-lg border p-3 text-xs ${clasesAvisoVersion(avisoVersion.color)}`
                        : "sr-only"
                }
            >
                {avisoVersion && (
                    <>
                        <p className="font-bold">{avisoVersion.titulo}</p>
                        <p>{avisoVersion.texto}</p>
                    </>
                )}
            </div>

            {/* Marca roja fija (spec §4.7/P10=A): queda pegada a la ficha después de un
                rechazo, con lo cargado intacto (P-7). "Ver el motivo" reabre el MISMO cartel. */}
            {rejectionInfo && !cartelAbierto && (
                <div
                    role="alert"
                    data-testid="restore-ficha-marca-rechazo"
                    className="flex items-center justify-between gap-3 rounded-lg border border-rose-300 bg-rose-50 p-3 text-xs text-rose-800 dark:border-rose-800 dark:bg-rose-950/30 dark:text-rose-200"
                >
                    <span>{construirTextoMarcaRechazo(rejectionInfo.modo)}</span>
                    <button
                        type="button"
                        onClick={() => setCartelAbierto(true)}
                        className="shrink-0 rounded-md border border-rose-300 px-2 py-1 font-bold hover:bg-rose-100 dark:border-rose-800 dark:hover:bg-rose-900/30"
                    >
                        Ver el motivo
                    </button>
                </div>
            )}

            {/* Retoque en vivo de Gastón (2026-07-30): el resultado de "Ver qué contiene" /
                "Reponer configuración" ya NO reemplaza la ficha — es un panel más, arriba de
                los campos, que se puede cerrar sin perder nada de lo tipeado abajo (P-7). */}
            {resultadoInline && (
                <RestoreResultadoInline resultadoInline={resultadoInline} onCerrar={() => setResultadoInline(null)} />
            )}

            <div>
                <label htmlFor={`restore-frase-${backup.archivo}`} className="mb-1 block text-xs font-bold uppercase text-slate-500">
                    Escribí <span className="font-mono">{FRASE_CONFIRMACION_RESTORE}</span>
                </label>
                <input
                    id={`restore-frase-${backup.archivo}`}
                    type="text"
                    data-testid="danger-restore-phrase"
                    value={frase}
                    onChange={(event) => setFrase(event.target.value)}
                    disabled={ejecutando}
                    autoComplete="off"
                    placeholder={FRASE_CONFIRMACION_RESTORE}
                    className="w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm font-mono dark:border-slate-700 dark:bg-slate-800"
                />
            </div>

            <div>
                <label htmlFor={`restore-password-${backup.archivo}`} className="mb-1 block text-xs font-bold uppercase text-slate-500">
                    Tu contraseña
                </label>
                <input
                    id={`restore-password-${backup.archivo}`}
                    type="password"
                    data-testid="danger-restore-password"
                    value={password}
                    onChange={(event) => setPassword(event.target.value)}
                    disabled={ejecutando}
                    autoComplete="current-password"
                    className="w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                />
            </div>

            <div>
                <label htmlFor={`restore-motivo-${backup.archivo}`} className="mb-1 block text-xs font-bold uppercase text-slate-500">
                    ¿Por qué volvés a esta copia?
                </label>
                <textarea
                    id={`restore-motivo-${backup.archivo}`}
                    data-testid="danger-restore-total-motivo"
                    value={motivoRestaurarTodo}
                    onChange={(event) => setMotivoRestaurarTodo(event.target.value)}
                    disabled={ejecutando}
                    rows={2}
                    maxLength={1000}
                    placeholder="Contá en una frase el motivo; queda registrado en el historial."
                    aria-describedby={motivoTotalEsCorto ? `restore-motivo-error-${backup.archivo}` : undefined}
                    aria-invalid={motivoTotalEsCorto}
                    className="w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                />
                {motivoTotalEsCorto && (
                    <div id={`restore-motivo-error-${backup.archivo}`} role="alert" className="mt-1 text-xs text-rose-600">
                        El motivo debe tener al menos {MOTIVO_RESTAURAR_TODO_MIN_LENGTH} caracteres.
                    </div>
                )}
            </div>

            <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
                <button
                    type="button"
                    data-testid="danger-restore-total"
                    onClick={() => handleAccion(RESTORE_MODO_TOTAL)}
                    disabled={!puedeConfirmarTotal}
                    className="inline-flex items-center justify-center gap-2 rounded-lg bg-rose-600 px-5 py-2.5 text-sm font-bold text-white hover:bg-rose-700 disabled:opacity-50"
                >
                    {accionEnCurso === RESTORE_MODO_TOTAL ? (
                        <Loader2 className="h-4 w-4 animate-spin" />
                    ) : (
                        <RotateCcw className="h-4 w-4" />
                    )}
                    {accionEnCurso === RESTORE_MODO_TOTAL ? "Volviendo..." : "Volver a esta copia"}
                </button>

                <span className="text-xs font-medium text-slate-500 dark:text-slate-400">
                    <button
                        type="button"
                        data-testid="danger-restore-ver-contenido"
                        onClick={() => handleAccion(RESTORE_MODO_PRUEBA)}
                        disabled={!puedeConfirmar}
                        className="text-indigo-600 hover:underline disabled:opacity-50 disabled:no-underline dark:text-indigo-400"
                    >
                        {accionEnCurso === RESTORE_MODO_PRUEBA ? "Buscando..." : "Ver qué contiene"}
                    </button>
                    {" · "}
                    <button
                        type="button"
                        data-testid="danger-restore-real"
                        onClick={() => handleAccion(RESTORE_MODO_REAL)}
                        disabled={!puedeConfirmar}
                        className="text-indigo-600 hover:underline disabled:opacity-50 disabled:no-underline dark:text-indigo-400"
                    >
                        {accionEnCurso === RESTORE_MODO_REAL ? "Reponiendo..." : "Reponer configuración"}
                    </button>
                </span>
            </div>

            {motivoAccionDeshabilitada && (
                <p className="text-xs font-medium text-amber-600 dark:text-amber-400" data-testid="danger-restore-accion-hint">
                    {motivoAccionDeshabilitada}
                </p>
            )}
            {!motivoAccionDeshabilitada && motivoRestaurarTodoDeshabilitado && (
                <p className="text-xs font-medium text-amber-600 dark:text-amber-400" data-testid="danger-restore-total-motivo-hint">
                    {motivoRestaurarTodoDeshabilitado}
                </p>
            )}

            <CartelEmergente
                isOpen={cartelAbierto}
                variant={CARTEL_EMERGENTE_VARIANTES.BLOQUEO}
                message={rejectionInfo?.mensaje}
                onClose={() => setCartelAbierto(false)}
                dataTestId="danger-restore-cartel-rechazo"
            />
        </div>
    );
}
