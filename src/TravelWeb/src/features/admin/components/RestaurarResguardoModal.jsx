import { useEffect, useState } from "react";
import { X, Loader2, RotateCcw, ShieldCheck, Search, CheckCircle2, Inbox, AlertTriangle } from "lucide-react";
import { api } from "../../../api";
import { showConfirm } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { formatDateTime } from "../../../lib/utils";
import { CartelEmergente, CARTEL_EMERGENTE_VARIANTES } from "../../../components/CartelEmergente";
import { BackupListItem } from "./BackupListItem";
import {
    FRASE_CONFIRMACION_RESTORE,
    RESTORE_MODO_PRUEBA,
    RESTORE_MODO_REAL,
    TABLAS_CONFIGURACION_RESTORE,
    construirEtiquetaBackup,
    puedeConfirmarRestore,
    construirMotivoRestoreDeshabilitado,
    construirConfirmacionRestore,
    construirTextoVerificacionRestore,
    construirResumenExitoPruebaRestore,
    construirResumenExitoRealRestore,
} from "../lib/dangerRestoreLogic";

/**
 * Modal "Volver atrás" (Zona peligrosa, Mantenimiento → Administración, obra 2026-07-27
 * Parte B, firma del dueño "el usuario tiene que poder volver atrás"): lista los
 * resguardos que quedaron guardados de un "Empezar de cero" anterior y permite restaurar
 * uno, en dos modos bien distintos:
 * - "Probar en una copia": restaura el resguardo COMPLETO en una base separada, de
 *   mentira, y muestra los conteos como prueba de que el resguardo sirve. Nunca toca
 *   los datos reales.
 * - "Restaurar configuración": restaura la configuración de la agencia (AFIP, políticas,
 *   bot de WhatsApp, reglas de multas/comisiones) sobre los datos reales — NO es todo o
 *   nada: repone lo que esté vacío ahora mismo y SALTEA (nunca pisa) lo que ya tenga datos
 *   cargados. Si repone la conexión con AFIP, vuelve siempre forzada a modo homologación
 *   (candado fiscal del motor: nunca deja un backup viejo reactivando facturación
 *   productiva sin que nadie se dé cuenta).
 *
 * Misma frase+contraseña "a prueba de dedos" que Empezar de cero, y mismo patrón de
 * rechazo del motor en el Cartel emergente único.
 */
export function RestaurarResguardoModal({ onClose }) {
    const [backups, setBackups] = useState([]);
    const [loadingBackups, setLoadingBackups] = useState(true);
    const [backupsError, setBackupsError] = useState(null);

    const [archivoSeleccionado, setArchivoSeleccionado] = useState(null);
    const [verificando, setVerificando] = useState(false);
    const [textoVerify, setTextoVerify] = useState(null);

    const [frase, setFrase] = useState("");
    const [password, setPassword] = useState("");
    const [accionEnCurso, setAccionEnCurso] = useState(null); // null | "prueba" | "real"
    const [rejectionMessage, setRejectionMessage] = useState(null);
    const [resultadoExitoso, setResultadoExitoso] = useState(null); // { modo, data }

    // useEffect con dependencia vacia: la lista de resguardos se pide UNA sola vez al
    // abrir el modal, no cada vez que el usuario elige un resguardo o tipea la frase.
    useEffect(() => {
        let cancelado = false;
        (async () => {
            setLoadingBackups(true);
            setBackupsError(null);
            try {
                const data = await api.get("/admin/danger/backups");
                if (!cancelado) setBackups(data?.backups || []);
            } catch (error) {
                if (!cancelado) {
                    setBackupsError(getApiErrorMessage(error, "No se pudieron consultar los resguardos disponibles."));
                }
            } finally {
                if (!cancelado) setLoadingBackups(false);
            }
        })();
        return () => { cancelado = true; };
    }, []);

    const seleccionarBackup = (archivo) => {
        setArchivoSeleccionado(archivo);
        setTextoVerify(null); // el resultado de "ver que contiene" era de OTRO resguardo.
    };

    const handleVerify = async () => {
        if (!archivoSeleccionado || verificando) return;
        setVerificando(true);
        setTextoVerify(null);
        try {
            const resultado = await api.post("/admin/danger/restore/verify", { archivo: archivoSeleccionado });
            setTextoVerify(construirTextoVerificacionRestore(resultado));
        } catch (error) {
            setRejectionMessage(getApiErrorMessage(error, "No se pudo revisar este resguardo."));
        } finally {
            setVerificando(false);
        }
    };

    const ejecutando = Boolean(accionEnCurso);
    const puedeConfirmar = Boolean(archivoSeleccionado)
        && !ejecutando
        && puedeConfirmarRestore({ frase, password, ejecutando });

    // Fix de review (P-9/P-10, "prohibido tooltip"): las dos acciones comparten el mismo
    // gate, así que un solo motivo alcanza — se muestra SIEMPRE como texto debajo de los
    // botones, nunca solo en un title. Mientras hay una acción en curso no hace falta motivo
    // (los botones ya muestran "Probando.../Restaurando..." con el spinner).
    const motivoAccionDeshabilitada = ejecutando
        ? null
        : construirMotivoRestoreDeshabilitado({ archivoSeleccionado, frase, password });

    const handleRestaurar = async (modo) => {
        if (!puedeConfirmar) return;

        // Doble confirmación explícita, mismo criterio que Empezar de cero: el texto
        // cambia según el modo porque el alcance real es MUY distinto.
        const confirmado = await showConfirm(construirConfirmacionRestore(modo));
        if (!confirmado) return;

        setAccionEnCurso(modo);
        setRejectionMessage(null);
        try {
            const resultado = await api.post("/admin/danger/restore", {
                archivo: archivoSeleccionado,
                password,
                phrase: frase,
                modo,
                // Modo "real" solo puede tocar las 5 tablas de configuración conocidas —
                // esta pantalla no ofrece elegir tablas sueltas, siempre pide las 5.
                tablas: modo === RESTORE_MODO_REAL ? TABLAS_CONFIGURACION_RESTORE : undefined,
            });
            setResultadoExitoso({ modo, data: resultado });
        } catch (error) {
            setRejectionMessage(getApiErrorMessage(error, "No se pudo completar la restauración."));
        } finally {
            // La contraseña se limpia siempre después de intentar, éxito o falla — igual
            // que en Empezar de cero, no tiene sentido dejarla en memoria del componente.
            setPassword("");
            setAccionEnCurso(null);
        }
    };

    // Panel de éxito: reemplaza el cuerpo del modal (no se cierra solo) para que el
    // admin lea el resultado con calma antes de cerrar a mano.
    if (resultadoExitoso) {
        const esPrueba = resultadoExitoso.modo === RESTORE_MODO_PRUEBA;
        const resumen = esPrueba
            ? construirResumenExitoPruebaRestore(resultadoExitoso.data)
            : construirResumenExitoRealRestore(resultadoExitoso.data);

        return (
            <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
                <div className="w-full max-w-lg rounded-2xl border bg-card shadow-2xl max-h-[90vh] overflow-y-auto">
                    <div className="px-6 py-4 border-b bg-emerald-50/60 dark:bg-emerald-950/20 flex items-center gap-2">
                        <CheckCircle2 className="h-5 w-5 text-emerald-600 dark:text-emerald-400" />
                        <h3 className="text-lg font-bold text-slate-900 dark:text-white">
                            {esPrueba ? "Resguardo probado" : "Configuración restaurada"}
                        </h3>
                    </div>
                    <div className="p-6 space-y-4">
                        {esPrueba ? (
                            <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-100">
                                <p className="font-semibold">La copia de prueba quedó con: {resumen.resumenConteos}</p>
                                {/* Reassurance repetida a propósito (fix de review): ya se dijo antes de
                                    confirmar, pero después de una acción sobre un resguardo de TODA la
                                    base de datos, conviene reafirmarlo en el resultado — que el admin no
                                    se quede con la duda de si tocó algo real. */}
                                <p className="mt-2 text-xs text-emerald-800 dark:text-emerald-200">
                                    Esto se hizo en una base de prueba separada: no se tocó ningún dato real.
                                </p>
                            </div>
                        ) : (
                            // Modo real: el "mensaje" viene ARMADO por el motor (qué se repuso, qué se
                            // salteó por ya tener datos, y el aviso de AFIP si corresponde) — se muestra
                            // TAL CUAL, sin reescribirlo acá (mismo criterio que el Cartel emergente para
                            // los rechazos). Si tocó la conexión con AFIP, se destaca en ámbar en vez del
                            // verde neutro, para que no pase desapercibido.
                            <div
                                className={`rounded-xl border p-4 text-sm whitespace-pre-line ${
                                    resumen.incluyeAfip
                                        ? "border-amber-300 bg-amber-50 text-amber-900 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-100"
                                        : "border-emerald-200 bg-emerald-50 text-emerald-900 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-100"
                                }`}
                            >
                                {resumen.incluyeAfip && (
                                    <p className="mb-1.5 flex items-center gap-1.5 font-bold uppercase text-xs tracking-wide">
                                        <AlertTriangle className="h-3.5 w-3.5" />
                                        Se tocó la conexión con AFIP
                                    </p>
                                )}
                                <p className="font-semibold">{resumen.mensaje}</p>
                            </div>
                        )}
                        {resumen.advertencia && (
                            <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-xs text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-300">
                                {resumen.advertencia}
                            </div>
                        )}
                    </div>
                    <div className="px-6 py-4 border-t bg-slate-50/50 dark:bg-slate-900/50 flex justify-end">
                        <button
                            type="button"
                            onClick={onClose}
                            className="px-4 py-2 rounded-lg text-sm font-bold text-white bg-slate-700 hover:bg-slate-800 transition-colors"
                        >
                            Cerrar
                        </button>
                    </div>
                </div>
            </div>
        );
    }

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
            <div className="w-full max-w-lg rounded-2xl border bg-card shadow-2xl max-h-[90vh] overflow-y-auto">
                <div className="px-6 py-4 border-b bg-indigo-50/60 dark:bg-indigo-950/20 flex items-center justify-between">
                    <div className="flex items-center gap-2">
                        <RotateCcw className="h-5 w-5 text-indigo-600 dark:text-indigo-400" />
                        <h3 className="text-lg font-bold text-slate-900 dark:text-white">Volver atrás</h3>
                    </div>
                    <button onClick={onClose} disabled={ejecutando} className="text-slate-400 hover:text-slate-600 transition-colors disabled:opacity-40">
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <div className="p-6 space-y-4">
                    {loadingBackups ? (
                        <div className="flex items-center justify-center py-10 text-slate-500">
                            <Loader2 className="h-5 w-5 animate-spin mr-2" /> Buscando resguardos disponibles...
                        </div>
                    ) : backupsError ? (
                        <div className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200">
                            {backupsError}
                        </div>
                    ) : backups.length === 0 ? (
                        <div className="flex flex-col items-center gap-2 py-10 text-center text-slate-500 dark:text-slate-400">
                            <Inbox className="h-8 w-8" />
                            <p className="text-sm">Todavía no hay ningún resguardo guardado.</p>
                            <p className="text-xs">Se genera uno automáticamente cada vez que se usa "Empezar de cero".</p>
                        </div>
                    ) : (
                        <>
                            <div>
                                <label className="text-xs font-bold uppercase text-slate-500 mb-1 block">Elegí un resguardo</label>
                                <div className="space-y-2">
                                    {backups.map((backup) => (
                                        <BackupListItem
                                            key={backup.archivo}
                                            archivo={backup.archivo}
                                            etiqueta={construirEtiquetaBackup(backup, formatDateTime)}
                                            isSelected={archivoSeleccionado === backup.archivo}
                                            onSelect={seleccionarBackup}
                                            disabled={ejecutando}
                                        />
                                    ))}
                                </div>
                            </div>

                            <button
                                type="button"
                                data-testid="danger-restore-verify"
                                onClick={handleVerify}
                                disabled={!archivoSeleccionado || verificando || ejecutando}
                                className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-bold text-slate-700 hover:bg-slate-50 disabled:opacity-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
                            >
                                {verificando ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
                                {verificando ? "Revisando..." : "Ver qué contiene"}
                            </button>

                            {textoVerify && (
                                <div className="rounded-lg border border-indigo-200 bg-indigo-50 p-3 text-sm text-indigo-900 dark:border-indigo-900 dark:bg-indigo-950/30 dark:text-indigo-100">
                                    {textoVerify}
                                </div>
                            )}

                            <div className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs text-slate-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300">
                                <ShieldCheck className="h-3.5 w-3.5 inline-block mr-1 text-slate-400" />
                                "Probar en una copia" NUNCA toca los datos reales. "Restaurar configuración" solo repone partes que estén vacías ahora mismo.
                            </div>

                            <div>
                                <label htmlFor="danger-restore-phrase-input" className="text-xs font-bold uppercase text-slate-500 mb-1 block">
                                    Escribí <span className="font-mono">{FRASE_CONFIRMACION_RESTORE}</span> para confirmar
                                </label>
                                <input
                                    id="danger-restore-phrase-input"
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
                                <label htmlFor="danger-restore-password-input" className="text-xs font-bold uppercase text-slate-500 mb-1 block">
                                    Tu contraseña
                                </label>
                                <input
                                    id="danger-restore-password-input"
                                    type="password"
                                    data-testid="danger-restore-password"
                                    value={password}
                                    onChange={(event) => setPassword(event.target.value)}
                                    disabled={ejecutando}
                                    autoComplete="current-password"
                                    className="w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                                />
                            </div>

                            <div className="flex flex-col gap-2 sm:flex-row">
                                <button
                                    type="button"
                                    data-testid="danger-restore-prueba"
                                    onClick={() => handleRestaurar(RESTORE_MODO_PRUEBA)}
                                    disabled={!puedeConfirmar}
                                    className="flex-1 inline-flex items-center justify-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-bold text-white hover:bg-indigo-700 disabled:opacity-50"
                                >
                                    {accionEnCurso === RESTORE_MODO_PRUEBA && <Loader2 className="h-4 w-4 animate-spin" />}
                                    {accionEnCurso === RESTORE_MODO_PRUEBA ? "Probando..." : "Probar en una copia"}
                                </button>
                                <button
                                    type="button"
                                    data-testid="danger-restore-real"
                                    onClick={() => handleRestaurar(RESTORE_MODO_REAL)}
                                    disabled={!puedeConfirmar}
                                    className="flex-1 inline-flex items-center justify-center gap-2 rounded-lg border border-amber-300 bg-amber-50 px-4 py-2 text-sm font-bold text-amber-800 hover:bg-amber-100 disabled:opacity-50 dark:border-amber-900/40 dark:bg-amber-950/20 dark:text-amber-300"
                                >
                                    {accionEnCurso === RESTORE_MODO_REAL && <Loader2 className="h-4 w-4 animate-spin" />}
                                    {accionEnCurso === RESTORE_MODO_REAL ? "Restaurando..." : "Restaurar configuración"}
                                </button>
                            </div>
                            {/* Fix de review (P-9/P-10): el motivo va SIEMPRE como texto visible debajo
                                de los botones, nunca solo en un tooltip. */}
                            {motivoAccionDeshabilitada && (
                                <p
                                    className="text-xs font-medium text-amber-600 dark:text-amber-400"
                                    data-testid="danger-restore-accion-hint"
                                >
                                    {motivoAccionDeshabilitada}
                                </p>
                            )}
                        </>
                    )}
                </div>

                <div className="px-6 py-4 border-t bg-slate-50/50 dark:bg-slate-900/50 flex justify-end">
                    <button
                        type="button"
                        onClick={onClose}
                        disabled={ejecutando}
                        className="px-4 py-2 rounded-lg text-sm font-bold text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800 transition-colors disabled:opacity-50"
                    >
                        Cerrar
                    </button>
                </div>
            </div>

            {/* Rechazo del motor (resguardo inválido, frase/contraseña incorrecta, tablas con
                datos, etc): siempre en el Cartel emergente único, mismo criterio que Empezar
                de cero — el usuario tiene que leer el motivo con calma. */}
            <CartelEmergente
                isOpen={Boolean(rejectionMessage)}
                variant={CARTEL_EMERGENTE_VARIANTES.BLOQUEO}
                message={rejectionMessage}
                onClose={() => setRejectionMessage(null)}
                dataTestId="danger-restore-cartel-rechazo"
            />
        </div>
    );
}
