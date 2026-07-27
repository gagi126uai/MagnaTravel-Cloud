import { useEffect, useState } from "react";
import { X, Loader2, AlertOctagon, AlertTriangle, Trash2, CheckCircle2, ShieldAlert } from "lucide-react";
import { api } from "../../../api";
import { showConfirm } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { CartelEmergente, CARTEL_EMERGENTE_VARIANTES } from "../../../components/CartelEmergente";
import {
    FRASE_CONFIRMACION_WIPE,
    construirFilasConteosWipe,
    construirConfirmacionEmpezarDeCero,
    construirResumenExitoWipe,
    puedeConfirmarWipe,
} from "../lib/dangerWipeLogic";

/**
 * Modal de "Empezar de cero" (Zona peligrosa, Mantenimiento → Administración).
 * Borra TODO lo cargado en el sistema (reservas, clientes, operadores, tarifario,
 * países y destinos, facturas, caja, archivos) dejando siempre usuarios y auditoría.
 * Requiere escribir la frase exacta "BORRAR TODO" + la contraseña del que ejecuta,
 * y hace una doble confirmación antes de disparar el borrado.
 *
 * Molde clonado de RevertStatusModal.jsx (bloqueos duros que apagan el submit +
 * doble confirmación + cartel de error del motor).
 */
export function EmpezarDeCeroModal({ onClose }) {
    const [preview, setPreview] = useState(null);
    const [loadingPreview, setLoadingPreview] = useState(true);
    const [previewError, setPreviewError] = useState(null);

    const [incluirConfiguracion, setIncluirConfiguracion] = useState(false);
    const [frase, setFrase] = useState("");
    const [password, setPassword] = useState("");

    const [ejecutando, setEjecutando] = useState(false);
    const [rejectionMessage, setRejectionMessage] = useState(null);
    const [resultadoExitoso, setResultadoExitoso] = useState(null);

    // useEffect con dependencia vacia: el preview se pide UNA sola vez al abrir el
    // modal, no cada vez que el usuario tipea la frase o tilda la configuracion.
    useEffect(() => {
        let cancelado = false;
        (async () => {
            setLoadingPreview(true);
            setPreviewError(null);
            try {
                const data = await api.get("/admin/danger/wipe/preview");
                if (!cancelado) setPreview(data);
            } catch (error) {
                if (!cancelado) {
                    setPreviewError(getApiErrorMessage(error, "No se pudo consultar qué hay para borrar."));
                }
            } finally {
                if (!cancelado) setLoadingPreview(false);
            }
        })();
        return () => { cancelado = true; };
    }, []);

    const bloqueado = Boolean(preview?.bloqueado);
    const filasConteos = construirFilasConteosWipe(preview?.conteos);
    const hayAlgoParaBorrar = filasConteos.some((fila) => fila.cantidad > 0);

    const canSubmit = !loadingPreview && !previewError
        && puedeConfirmarWipe({ frase, password, bloqueado, ejecutando });

    const handleSubmit = async () => {
        if (!canSubmit) return;

        // Regla del dueño: nunca se dispara un borrado total sin una doble confirmación
        // explícita, con el texto cambiando si además se va a borrar la configuración.
        const confirmado = await showConfirm(construirConfirmacionEmpezarDeCero(incluirConfiguracion));
        if (!confirmado) return;

        setEjecutando(true);
        setRejectionMessage(null);
        try {
            const resultado = await api.post("/admin/danger/wipe", {
                password,
                phrase: frase,
                incluirConfiguracion,
            });
            setResultadoExitoso(resultado);
        } catch (error) {
            // P-13: el mensaje del motor se muestra tal cual (ej. candado fiscal, contraseña
            // incorrecta, frase mal escrita si el backend la re-valida). Al ser una acción
            // destructiva de alto riesgo, el rechazo va SIEMPRE al Cartel emergente único,
            // nunca a un toast que el usuario pueda no llegar a leer.
            setRejectionMessage(getApiErrorMessage(error, "No se pudo completar el borrado."));
        } finally {
            // La contraseña se limpia SIEMPRE después de intentar el POST, tanto en éxito
            // como en falla (ej. contraseña incorrecta) — no tiene sentido dejarla en memoria
            // del componente esperando un reintento; el usuario la vuelve a tipear.
            setPassword("");
            setEjecutando(false);
        }
    };

    // Panel de éxito: se muestra DENTRO del mismo modal (no se cierra solo) para que
    // el admin vea el resumen con calma antes de cerrar a mano.
    if (resultadoExitoso) {
        const resumen = construirResumenExitoWipe({
            borrado: resultadoExitoso.borrado,
            backupArchivo: resultadoExitoso.backupArchivo,
            configuracionBorrada: resultadoExitoso.configuracionBorrada,
        });

        return (
            <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
                <div className="w-full max-w-lg rounded-2xl border bg-card shadow-2xl max-h-[90vh] overflow-y-auto">
                    <div className="px-6 py-4 border-b bg-emerald-50/60 dark:bg-emerald-950/20 flex items-center gap-2">
                        <CheckCircle2 className="h-5 w-5 text-emerald-600 dark:text-emerald-400" />
                        <h3 className="text-lg font-bold text-slate-900 dark:text-white">Se empezó de cero</h3>
                    </div>
                    <div className="p-6 space-y-4">
                        <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-100">
                            <p className="font-semibold">Se borró: {resumen.resumenConteos}</p>
                            {resumen.backupArchivo && (
                                <p className="mt-2 text-xs text-emerald-800 dark:text-emerald-200">
                                    Backup guardado antes de borrar: <span className="font-mono">{resumen.backupArchivo}</span>
                                </p>
                            )}
                        </div>
                        <p className="text-sm text-slate-600 dark:text-slate-300">{resumen.mensajeConfiguracion}</p>
                        <div className="rounded-xl border border-amber-200 bg-amber-50 p-3 text-xs text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-300">
                            Los demás usuarios van a tener que volver a entrar al sistema.
                        </div>
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
                <div className="px-6 py-4 border-b bg-rose-50/60 dark:bg-rose-950/20 flex items-center justify-between">
                    <div className="flex items-center gap-2">
                        <AlertOctagon className="h-5 w-5 text-rose-600 dark:text-rose-400" />
                        <h3 className="text-lg font-bold text-slate-900 dark:text-white">Empezar de cero</h3>
                    </div>
                    <button onClick={onClose} disabled={ejecutando} className="text-slate-400 hover:text-slate-600 transition-colors disabled:opacity-40">
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <div className="p-6 space-y-4">
                    {loadingPreview ? (
                        <div className="flex items-center justify-center py-10 text-slate-500">
                            <Loader2 className="h-5 w-5 animate-spin mr-2" /> Consultando qué hay para borrar...
                        </div>
                    ) : previewError ? (
                        <div className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200">
                            {previewError}
                        </div>
                    ) : (
                        <>
                            <div>
                                <label className="text-xs font-bold uppercase text-slate-500 mb-1 block">Esto se borra</label>
                                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-700 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200">
                                    {hayAlgoParaBorrar
                                        ? filasConteos
                                            .filter((fila) => fila.cantidad > 0)
                                            .map((fila) => `${fila.cantidad} ${fila.etiqueta}`)
                                            .join(" · ")
                                        : "Por ahora no hay datos de negocio cargados para borrar."}
                                </div>
                            </div>

                            <div className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs text-slate-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300">
                                <ShieldAlert className="h-3.5 w-3.5 inline-block mr-1 text-slate-400" />
                                Los usuarios y la auditoría <span className="font-semibold">siempre quedan</span>. Antes de borrar se hace un backup completo; la restauración la gestiona soporte.
                            </div>

                            {bloqueado && (
                                <div className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200">
                                    <div className="flex items-start gap-2">
                                        <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" />
                                        <div>
                                            <strong className="font-bold">Bloqueado:</strong> {preview.motivoBloqueo}
                                        </div>
                                    </div>
                                </div>
                            )}

                            <div className="rounded-lg border border-slate-200 bg-white p-3 dark:border-slate-700 dark:bg-slate-900">
                                <label className="flex items-start gap-2 cursor-pointer select-none">
                                    <input
                                        type="checkbox"
                                        data-testid="danger-wipe-config-check"
                                        checked={incluirConfiguracion}
                                        onChange={(event) => setIncluirConfiguracion(event.target.checked)}
                                        disabled={ejecutando}
                                        className="mt-0.5 h-4 w-4 rounded border-slate-300 text-rose-600 focus:ring-rose-500"
                                    />
                                    <span className="text-sm text-slate-700 dark:text-slate-200">
                                        Borrar también la configuración (AFIP, datos de la agencia, reglas)
                                    </span>
                                </label>
                                {incluirConfiguracion && (
                                    <p className="mt-2 ml-6 text-xs text-amber-700 dark:text-amber-400">
                                        Si tildás esto, después hay que volver a configurar AFIP (certificado incluido) antes de poder facturar.
                                    </p>
                                )}
                            </div>

                            <div>
                                <label htmlFor="danger-wipe-phrase-input" className="text-xs font-bold uppercase text-slate-500 mb-1 block">
                                    Escribí <span className="font-mono">{FRASE_CONFIRMACION_WIPE}</span> para confirmar
                                </label>
                                <input
                                    id="danger-wipe-phrase-input"
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
                                <label htmlFor="danger-wipe-password-input" className="text-xs font-bold uppercase text-slate-500 mb-1 block">
                                    Tu contraseña
                                </label>
                                <input
                                    id="danger-wipe-password-input"
                                    type="password"
                                    data-testid="danger-wipe-password"
                                    value={password}
                                    onChange={(event) => setPassword(event.target.value)}
                                    disabled={ejecutando}
                                    autoComplete="current-password"
                                    className="w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                                />
                            </div>
                        </>
                    )}
                </div>

                <div className="px-6 py-4 border-t bg-slate-50/50 dark:bg-slate-900/50 flex justify-end gap-3">
                    <button
                        type="button"
                        onClick={onClose}
                        disabled={ejecutando}
                        className="px-4 py-2 rounded-lg text-sm font-bold text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800 transition-colors disabled:opacity-50"
                    >
                        Cancelar
                    </button>
                    <button
                        type="button"
                        onClick={handleSubmit}
                        disabled={!canSubmit}
                        data-testid="danger-wipe-submit"
                        title={bloqueado ? preview?.motivoBloqueo : undefined}
                        className="px-4 py-2 rounded-lg text-sm font-bold text-white bg-rose-600 hover:bg-rose-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                    >
                        {ejecutando ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
                        {ejecutando ? "Borrando..." : "Empezar de cero..."}
                    </button>
                </div>
            </div>

            {/* Rechazo del motor (candado fiscal, contraseña incorrecta, etc): siempre en el
                Cartel emergente único, nunca en un toast fugaz — es la accion mas destructiva
                del sistema y el usuario tiene que leer el motivo con calma. */}
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
