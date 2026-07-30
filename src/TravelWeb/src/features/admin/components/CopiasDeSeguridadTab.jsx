import { useCallback, useEffect, useState } from "react";
import { ShieldCheck, Loader2, Inbox, RotateCcw, X, CheckCircle2 } from "lucide-react";
import { api } from "../../../api";
import { getApiErrorMessage } from "../../../lib/errors";
import { formatDateTime } from "../../../lib/utils";
import { BackupRow } from "./BackupRow";
import { EmpezarDeCeroInline } from "./EmpezarDeCeroInline";

/**
 * Solapa "Copias de seguridad" de Administración (rediseño 2026-07-30, reemplaza el viejo
 * modal "Volver atrás" + el modal "Empezar de cero" — spec
 * docs/ux/2026-07-30-rediseno-pantalla-copias-de-seguridad.md, respuestas 1A..12A). Todo el
 * flujo vive EN LA PÁGINA (P-5): la lista de copias arriba, con una ficha en línea por copia
 * elegida (RestoreBackupFicha, dentro de BackupRow), y el bloque "Empezar de cero" abajo.
 *
 * Único lugar que sabe refrescar la lista después de una acción que crea un resguardo nuevo
 * (Empezar de cero, o "Volver a esta copia" que además guarda uno del estado anterior) y el
 * único que muestra el cartel verde de éxito arriba de la lista (spec §4.6): "Volver a esta
 * copia" no tiene ventana de éxito propia, por eso avisa acá subiendo el resultado.
 */
export default function CopiasDeSeguridadTab() {
    const [backups, setBackups] = useState([]);
    const [loadingBackups, setLoadingBackups] = useState(true);
    const [backupsError, setBackupsError] = useState(null);
    const [archivoAbierto, setArchivoAbierto] = useState(null);
    const [bannerExito, setBannerExito] = useState(null); // { mensaje }

    const cargarBackups = useCallback(async () => {
        setLoadingBackups(true);
        setBackupsError(null);
        try {
            const data = await api.get("/admin/danger/backups");
            setBackups(data?.backups || []);
        } catch (error) {
            setBackupsError(getApiErrorMessage(error, "No pudimos traer las copias guardadas."));
        } finally {
            setLoadingBackups(false);
        }
    }, []);

    // useEffect con dependencia vacia: la lista se pide UNA sola vez al entrar a la solapa,
    // no en cada render — `cargarBackups` después se reusa a mano (retry, refresco post-acción).
    useEffect(() => {
        cargarBackups();
    }, [cargarBackups]);

    // Solo una ficha abierta a la vez (spec §4.3): abrir otra copia cierra la anterior.
    const alternarFicha = (archivo) => {
        setArchivoAbierto((actual) => (actual === archivo ? null : archivo));
    };

    const handleSuccessTotal = ({ backup, resumenTotal }) => {
        setBannerExito({
            mensaje: resumenTotal.mensaje,
            fecha: formatDateTime(backup.fechaUtc),
        });
        setArchivoAbierto(null);
        cargarBackups();
    };

    return (
        <div className="space-y-6">
            <div className="flex items-start gap-3">
                <div className="rounded-xl bg-indigo-50 p-2 dark:bg-indigo-950/40">
                    <ShieldCheck className="h-5 w-5 text-indigo-600 dark:text-indigo-400" />
                </div>
                <div>
                    <h2 className="text-lg font-bold text-slate-900 dark:text-white">Copias de seguridad</h2>
                    <p className="mt-1 max-w-2xl text-sm text-slate-500 dark:text-slate-400">
                        El sistema guarda una copia entera cada vez que se usa "Empezar de cero" y cada vez
                        que se vuelve a una copia anterior.
                    </p>
                </div>
            </div>

            {/* Éxito de "Volver a esta copia" (spec §4.6, P9=A): cartel verde en la página, NO
                una ventana. El mensaje central es el que arma el motor, tal cual (P-13). */}
            {bannerExito && (
                <div
                    role="status"
                    data-testid="restore-total-banner-exito"
                    className="flex items-start gap-3 rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-100"
                >
                    <CheckCircle2 className="mt-0.5 h-5 w-5 shrink-0" aria-hidden="true" />
                    <div className="flex-1">
                        {/* Fix de review (spec §4.6, hallazgo bloqueante): el banner encabeza con la
                            fecha de la copia (texto propio de la pantalla), y el mensaje del motor va
                            DEBAJO, tal cual (P-13) — antes solo se mostraba el mensaje del motor, sin
                            la fecha que el spec pide como título. */}
                        <p className="font-semibold">Listo: el sistema volvió a como estaba el {bannerExito.fecha}.</p>
                        <p className="mt-1 text-xs text-emerald-800 dark:text-emerald-200">{bannerExito.mensaje}</p>
                        <p className="mt-1 text-xs text-emerald-800 dark:text-emerald-200">
                            Antes de traer los datos guardamos una copia de cómo estaba hasta recién: la vas a
                            ver primera en la lista de abajo.
                        </p>
                        <p className="mt-1 text-xs font-semibold text-emerald-800 dark:text-emerald-200">
                            Los demás usuarios van a tener que volver a entrar.
                        </p>
                    </div>
                    <button
                        type="button"
                        onClick={() => setBannerExito(null)}
                        aria-label="Cerrar aviso"
                        className="shrink-0 rounded-full p-1 text-emerald-700 hover:bg-emerald-100 dark:text-emerald-300 dark:hover:bg-emerald-900/40"
                    >
                        <X className="h-4 w-4" />
                    </button>
                </div>
            )}

            <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
                {loadingBackups ? (
                    <div className="flex items-center justify-center gap-2 py-10 text-sm text-slate-500">
                        <Loader2 className="h-5 w-5 animate-spin" />
                        Buscando las copias guardadas…
                    </div>
                ) : backupsError ? (
                    <div
                        role="alert"
                        className="flex items-center justify-between gap-3 px-6 py-6 text-sm text-rose-800 dark:text-rose-200"
                    >
                        <span>{backupsError}</span>
                        <button
                            type="button"
                            onClick={cargarBackups}
                            data-testid="backups-retry"
                            className="inline-flex shrink-0 items-center gap-1.5 rounded-lg border border-rose-300 px-3 py-1.5 text-xs font-bold text-rose-700 hover:bg-rose-50 dark:border-rose-800 dark:text-rose-300 dark:hover:bg-rose-950/30"
                        >
                            <RotateCcw className="h-3.5 w-3.5" />
                            Probar de nuevo
                        </button>
                    </div>
                ) : backups.length === 0 ? (
                    <div className="flex flex-col items-center gap-2 px-6 py-10 text-center text-slate-500 dark:text-slate-400">
                        <Inbox className="h-8 w-8" />
                        <p className="text-sm">Todavía no hay ninguna copia guardada.</p>
                        <p className="text-xs">El sistema guarda una sola cada vez que se usa "Empezar de cero".</p>
                    </div>
                ) : (
                    <table className="w-full text-sm">
                        <thead className="border-b border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-800/50">
                            <tr>
                                <th className="px-4 py-2 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">Cuándo se guardó</th>
                                <th className="px-4 py-2 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">Por qué se guardó</th>
                                <th className="px-4 py-2 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">Tamaño</th>
                                <th className="px-4 py-2" />
                            </tr>
                        </thead>
                        <tbody>
                            {backups.map((backup) => (
                                <BackupRow
                                    key={backup.archivo}
                                    backup={backup}
                                    isOpen={archivoAbierto === backup.archivo}
                                    onToggle={() => alternarFicha(backup.archivo)}
                                    onSuccessTotal={handleSuccessTotal}
                                />
                            ))}
                        </tbody>
                    </table>
                )}
            </div>

            <EmpezarDeCeroInline onBorradoExitoso={cargarBackups} />
        </div>
    );
}
