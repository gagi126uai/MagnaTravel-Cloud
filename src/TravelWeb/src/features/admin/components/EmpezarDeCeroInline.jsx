import { useEffect, useMemo, useState, useCallback } from "react";
import { Loader2, AlertOctagon, AlertTriangle, Trash2, CheckCircle2, ShieldAlert, CheckSquare } from "lucide-react";
import { api } from "../../../api";
import { showConfirm } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { formatDateTime } from "../../../lib/utils";
import { CartelEmergente, CARTEL_EMERGENTE_VARIANTES } from "../../../components/CartelEmergente";
import {
    FRASE_CONFIRMACION_WIPE,
    WIPE_GRUPO_CONFIGURACION,
    construirGruposWipeParaMostrar,
    calcularSeleccionEfectivaWipe,
    calcularGruposArrastradosWipe,
    alternarGrupoWipe,
    seleccionarTodosLosGruposWipe,
    construirResumenCompletoSeleccionWipe,
    construirConfirmacionEmpezarDeCero,
    construirResumenExitoWipe,
    construirMotivoWipeDeshabilitado,
    puedeConfirmarWipe,
} from "../lib/dangerWipeLogic";

/**
 * Bloque "Empezar de cero" de la solapa "Copias de seguridad" (rediseño 2026-07-30, P2=A:
 * antes vivía en una ventana flotante propia dentro de Mantenimiento → Zona peligrosa; ahora
 * es el segundo bloque de esta misma página, EN LÍNEA, sin ventana — P-5). Borra POR GRUPOS
 * lo cargado en el sistema (reservas y su plata, clientes, operadores, tarifario, países y
 * destinos, clientes potenciales o la configuración) dejando siempre usuarios y auditoría.
 * Antes de borrar, el motor guarda un resguardo completo que aparece arriba, en la lista.
 *
 * Regla del dueño (2026-07-27, "tilda solo y avisa"): si un grupo elegido arrastra a otro
 * (ej. "clientes" arrastra a "reservas y su plata"), ese otro se tilda solo y queda
 * bloqueado mientras el que lo arrastra siga tildado.
 */
export function EmpezarDeCeroInline({ onBorradoExitoso }) {
    const [preview, setPreview] = useState(null);
    const [loadingPreview, setLoadingPreview] = useState(true);
    const [previewError, setPreviewError] = useState(null);

    const [gruposManual, setGruposManual] = useState([]);
    const [frase, setFrase] = useState("");
    const [password, setPassword] = useState("");

    const [ejecutando, setEjecutando] = useState(false);
    const [rejectionMessage, setRejectionMessage] = useState(null);
    const [resultadoExitoso, setResultadoExitoso] = useState(null);

    const cargarPreview = useCallback(async () => {
        setLoadingPreview(true);
        setPreviewError(null);
        try {
            const data = await api.get("/admin/danger/wipe/preview");
            setPreview(data);
        } catch (error) {
            setPreviewError(getApiErrorMessage(error, "No se pudo consultar qué hay para borrar."));
        } finally {
            setLoadingPreview(false);
        }
    }, []);

    // useEffect con dependencia vacia: el preview se pide UNA sola vez al montar este bloque,
    // no cada vez que el usuario tilda un grupo o tipea la frase.
    useEffect(() => {
        cargarPreview();
    }, [cargarPreview]);

    const bloqueado = Boolean(preview?.bloqueado);
    const dependencias = preview?.dependencias || {};
    const gruposParaMostrar = construirGruposWipeParaMostrar(preview?.conteos);

    const gruposSeleccionados = useMemo(
        () => calcularSeleccionEfectivaWipe(gruposManual, dependencias),
        [gruposManual, dependencias]
    );
    const gruposArrastrados = useMemo(
        () => calcularGruposArrastradosWipe(gruposManual, dependencias),
        [gruposManual, dependencias]
    );
    const resumenCompleto = construirResumenCompletoSeleccionWipe(gruposSeleccionados, preview?.conteos);

    const canSubmit = !loadingPreview && !previewError
        && puedeConfirmarWipe({ grupos: gruposSeleccionados, frase, password, bloqueado, ejecutando });

    const motivoSubmitDeshabilitado = (loadingPreview || previewError || ejecutando)
        ? null
        : construirMotivoWipeDeshabilitado({
            grupos: gruposSeleccionados,
            frase,
            password,
            bloqueado,
            motivoBloqueo: preview?.motivoBloqueo,
        });

    const alternarGrupo = (grupo, tildar) => {
        setGruposManual((actual) => alternarGrupoWipe({ gruposManual: actual, grupo, dependencias, tildar }));
    };

    const handleSubmit = async () => {
        if (!canSubmit) return;

        const confirmado = await showConfirm(construirConfirmacionEmpezarDeCero(gruposSeleccionados));
        if (!confirmado) return;

        setEjecutando(true);
        setRejectionMessage(null);
        try {
            const resultado = await api.post("/admin/danger/wipe", {
                password,
                phrase: frase,
                grupos: gruposSeleccionados,
            });
            setResultadoExitoso(resultado);
            setFrase("");
            setGruposManual([]);
            onBorradoExitoso?.();
        } catch (error) {
            setRejectionMessage(getApiErrorMessage(error, "No se pudo completar el borrado."));
        } finally {
            setPassword("");
            setEjecutando(false);
        }
    };

    const volverAElegir = () => {
        setResultadoExitoso(null);
        cargarPreview();
    };

    // Solo tiene sentido armar el resumen de exito cuando ya hubo un borrado exitoso — se
    // calcula ACA (no dentro del JSX) para no repetir la misma llamada tres veces al pintar
    // el mensaje, el detalle del backup y el "se borro X".
    const resumenWipeExitoso = resultadoExitoso
        ? construirResumenExitoWipe({
            borrado: resultadoExitoso.borrado,
            backupArchivo: resultadoExitoso.backupArchivo,
            gruposBorrados: resultadoExitoso.gruposBorrados,
            formatearFecha: formatDateTime,
        })
        : null;

    return (
        <div className="rounded-2xl border-2 border-rose-200 bg-rose-50/40 p-6 shadow-sm dark:border-rose-900/50 dark:bg-rose-950/10">
            <div className="flex items-start gap-3">
                <div className="rounded-xl bg-rose-100 p-2 dark:bg-rose-900/40">
                    <AlertOctagon className="h-5 w-5 text-rose-600 dark:text-rose-400" />
                </div>
                <div>
                    <h2 className="text-lg font-bold text-rose-900 dark:text-rose-200">Empezar de cero</h2>
                    <p className="mt-1 max-w-2xl text-sm text-slate-600 dark:text-slate-300">
                        Elegí qué grupos borrar: reservas y su plata, clientes, operadores, tarifario, países y
                        destinos, clientes potenciales o la configuración de la agencia. Los usuarios y la
                        auditoría <span className="font-semibold">siempre quedan</span>. Antes de borrar se hace
                        una copia completa, que después vas a poder usar desde la lista de arriba.
                    </p>
                </div>
            </div>

            <div className="mt-5 space-y-4">
                {resumenWipeExitoso ? (
                    <div className="space-y-4">
                        <div className="flex items-center gap-2 text-sm font-bold text-emerald-800 dark:text-emerald-300">
                            <CheckCircle2 className="h-4 w-4" />
                            Se empezó de cero
                        </div>
                        <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-100">
                            <p className="font-semibold">Se borró: {resumenWipeExitoso.resumenConteos}</p>
                            {resumenWipeExitoso.mensajeBackup && (
                                <p className="mt-2 text-xs text-emerald-800 dark:text-emerald-200">{resumenWipeExitoso.mensajeBackup}</p>
                            )}
                        </div>
                        {/* Fix de review (regresión fiscal, hallazgo bloqueante): el modal viejo avisaba
                            si la configuración (AFIP, reglas de multas/comisiones) también voló o se
                            conservó — se había perdido en la migración a inline. */}
                        <p className="text-sm text-slate-600 dark:text-slate-300">{resumenWipeExitoso.mensajeConfiguracion}</p>
                        <div className="rounded-xl border border-amber-200 bg-amber-50 p-3 text-xs text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-300">
                            Los demás usuarios van a tener que volver a entrar al sistema.
                        </div>
                        <button
                            type="button"
                            onClick={volverAElegir}
                            className="text-xs font-bold text-slate-600 hover:underline dark:text-slate-300"
                        >
                            Volver a elegir
                        </button>
                    </div>
                ) : loadingPreview ? (
                    <div className="flex items-center justify-center py-8 text-slate-500">
                        <Loader2 className="h-5 w-5 animate-spin mr-2" /> Consultando qué hay para borrar...
                    </div>
                ) : previewError ? (
                    <div
                        role="alert"
                        className="flex items-center justify-between gap-3 rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200"
                    >
                        <span>{previewError}</span>
                        <button
                            type="button"
                            onClick={cargarPreview}
                            data-testid="wipe-preview-retry"
                            className="inline-flex shrink-0 items-center gap-1.5 rounded-lg border border-rose-300 px-3 py-1.5 text-xs font-bold text-rose-700 hover:bg-rose-100 dark:border-rose-800 dark:text-rose-300 dark:hover:bg-rose-950/40"
                        >
                            Probar de nuevo
                        </button>
                    </div>
                ) : (
                    <>
                        <div className="flex items-center justify-between">
                            <label className="text-xs font-bold uppercase text-slate-500">Elegí qué borrar</label>
                            <button
                                type="button"
                                data-testid="danger-wipe-selectall"
                                onClick={() => setGruposManual(seleccionarTodosLosGruposWipe())}
                                disabled={ejecutando}
                                className="inline-flex items-center gap-1 text-xs font-bold text-rose-700 hover:text-rose-800 disabled:opacity-40 dark:text-rose-400"
                            >
                                <CheckSquare className="h-3.5 w-3.5" />
                                Seleccionar todo
                            </button>
                        </div>

                        <div className="space-y-2">
                            {gruposParaMostrar.map((grupo) => {
                                const estaSeleccionado = gruposSeleccionados.includes(grupo.clave);
                                const responsables = gruposArrastrados[grupo.clave];
                                const estaBloqueado = Boolean(responsables);

                                return (
                                    <div key={grupo.clave} className="rounded-lg border border-slate-200 bg-white p-3 dark:border-slate-700 dark:bg-slate-900">
                                        <label className={`flex items-start gap-2 select-none ${estaBloqueado ? "cursor-not-allowed" : "cursor-pointer"}`}>
                                            <input
                                                type="checkbox"
                                                data-testid={`danger-grupo-${grupo.clave}`}
                                                checked={estaSeleccionado}
                                                disabled={ejecutando || estaBloqueado}
                                                onChange={(event) => alternarGrupo(grupo.clave, event.target.checked)}
                                                className="mt-0.5 h-4 w-4 rounded border-slate-300 text-rose-600 focus:ring-rose-500 disabled:opacity-60"
                                            />
                                            <span className="flex-1">
                                                <span className="block text-sm font-semibold text-slate-800 dark:text-slate-100">{grupo.etiqueta}</span>
                                                {grupo.detalleConteo && (
                                                    <span className="block text-xs text-slate-500 dark:text-slate-400">{grupo.detalleConteo}</span>
                                                )}
                                            </span>
                                        </label>
                                        {estaBloqueado && (
                                            <p className="mt-1.5 ml-6 text-xs text-amber-700 dark:text-amber-400">
                                                Se borra también porque depende de{" "}
                                                {responsables.map((clave) => gruposParaMostrar.find((g) => g.clave === clave)?.etiqueta || clave).join(" y ")}.
                                            </p>
                                        )}
                                        {grupo.clave === WIPE_GRUPO_CONFIGURACION && estaSeleccionado && (
                                            <p className="mt-1.5 ml-6 text-xs text-amber-700 dark:text-amber-400">
                                                Después vas a tener que volver a configurar AFIP (certificado incluido) antes de poder facturar.
                                            </p>
                                        )}
                                    </div>
                                );
                            })}
                        </div>

                        {gruposSeleccionados.length > 0 && (
                            <div className="rounded-lg border border-rose-200 bg-rose-50/60 p-3 dark:border-rose-900/40 dark:bg-rose-950/20">
                                <p className="mb-1.5 text-xs font-bold uppercase text-rose-700 dark:text-rose-400">Esto vuela para siempre</p>
                                <ul className="space-y-1 text-sm text-rose-900 dark:text-rose-200">
                                    {resumenCompleto.map((fila) => (
                                        <li key={fila.clave}>
                                            <span className="font-semibold">{fila.etiqueta}</span>
                                            {fila.detalleConteo && <span> — {fila.detalleConteo}</span>}
                                        </li>
                                    ))}
                                </ul>
                            </div>
                        )}

                        {bloqueado && (
                            <div className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200">
                                <div className="flex items-start gap-2">
                                    <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" />
                                    <div><strong className="font-bold">Bloqueado:</strong> {preview.motivoBloqueo}</div>
                                </div>
                            </div>
                        )}

                        <div>
                            <label htmlFor="wipe-frase-inline" className="mb-1 block text-xs font-bold uppercase text-slate-500">
                                Escribí <span className="font-mono">{FRASE_CONFIRMACION_WIPE}</span> para confirmar
                            </label>
                            <input
                                id="wipe-frase-inline"
                                type="text"
                                data-testid="danger-wipe-phrase"
                                value={frase}
                                onChange={(event) => setFrase(event.target.value)}
                                disabled={ejecutando}
                                autoComplete="off"
                                placeholder={FRASE_CONFIRMACION_WIPE}
                                className="w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm font-mono dark:border-slate-700 dark:bg-slate-800"
                            />
                        </div>

                        <div>
                            <label htmlFor="wipe-password-inline" className="mb-1 block text-xs font-bold uppercase text-slate-500">
                                Tu contraseña
                            </label>
                            <input
                                id="wipe-password-inline"
                                type="password"
                                data-testid="danger-wipe-password"
                                value={password}
                                onChange={(event) => setPassword(event.target.value)}
                                disabled={ejecutando}
                                autoComplete="current-password"
                                className="w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                            />
                        </div>

                        <div className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs text-slate-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300">
                            <ShieldAlert className="mr-1 inline-block h-3.5 w-3.5 text-slate-400" />
                            Los usuarios y la auditoría <span className="font-semibold">siempre quedan</span>.
                        </div>

                        <div className="flex flex-col items-start gap-2">
                            <button
                                type="button"
                                onClick={handleSubmit}
                                disabled={!canSubmit}
                                data-testid="danger-wipe-submit"
                                className="inline-flex items-center gap-2 rounded-lg bg-rose-600 px-5 py-2.5 text-sm font-bold text-white hover:bg-rose-700 disabled:opacity-50"
                            >
                                {ejecutando ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
                                {ejecutando ? "Borrando..." : "Empezar de cero..."}
                            </button>
                            {motivoSubmitDeshabilitado && (
                                <p className="text-xs font-medium text-amber-600 dark:text-amber-400" data-testid="danger-wipe-submit-hint">
                                    {motivoSubmitDeshabilitado}
                                </p>
                            )}
                        </div>
                    </>
                )}
            </div>

            <CartelEmergente
                isOpen={Boolean(rejectionMessage)}
                variant={CARTEL_EMERGENTE_VARIANTES.BLOQUEO}
                message={rejectionMessage}
                onClose={() => setRejectionMessage(null)}
                dataTestId="danger-wipe-cartel-rechazo"
            />
        </div>
    );
}
