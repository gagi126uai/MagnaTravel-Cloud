import { ChevronDown } from "lucide-react";
import { Badge } from "../../../components/ui/badge";
import { formatDateTime } from "../../../lib/utils";
import { textoTiempoRelativo } from "../../cancellations/debitNoteInboxLogic";
import { formatearTamanioArchivo, construirBadgeVersionResguardo, resolverPorQueSeGuardo } from "../lib/dangerRestoreLogic";
import { RestoreBackupFicha } from "./RestoreBackupFicha";

// ADR-052 (2026-07-29): mismo trío de colores que el resto de esta pantalla (ámbar/rosa/gris).
const BADGE_VERSION_CLASSES = {
    ambar: "border-amber-300 bg-amber-50 text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/20 dark:text-amber-300",
    rosa: "border-rose-300 bg-rose-50 text-rose-700 dark:border-rose-800 dark:bg-rose-950/30 dark:text-rose-300",
    gris: "border-slate-300 bg-slate-100 text-slate-600 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-300",
};

/**
 * Una fila de la tabla "Copias de seguridad" (rediseño 2026-07-30): fecha completa + "hace
 * cuánto" + badge de versión, por qué se guardó, tamaño, y el botón "Usar esta ▾" que abre
 * la ficha de trabajo EN LÍNEA (RestoreBackupFicha) debajo de la misma fila (P-5). Solo una
 * fila puede estar abierta a la vez — lo controla el padre (CopiasDeSeguridadTab) con
 * `isOpen`/`onToggle`, así abrir otra copia cierra automáticamente la anterior.
 */
export function BackupRow({ backup, isOpen, onToggle, onSuccessTotal }) {
    const badge = construirBadgeVersionResguardo(backup.versionResguardo);

    return (
        <>
            <tr className="border-b border-slate-100 dark:border-slate-800" data-testid={`backup-row-${backup.archivo}`}>
                <td className="px-4 py-3 align-top">
                    <div className="font-semibold text-slate-900 dark:text-white">{formatDateTime(backup.fechaUtc)}</div>
                    <div className="text-xs text-slate-500 dark:text-slate-400">{textoTiempoRelativo(backup.fechaUtc)}</div>
                    {badge && (
                        <Badge
                            variant="outline"
                            data-testid={`backup-badge-${backup.archivo}`}
                            data-version={badge.color}
                            className={`${BADGE_VERSION_CLASSES[badge.color]} mt-1`}
                        >
                            {badge.texto}
                        </Badge>
                    )}
                </td>
                <td className="px-4 py-3 align-top text-sm text-slate-600 dark:text-slate-300">
                    {resolverPorQueSeGuardo(backup.porQueSeGuardo)}
                </td>
                <td className="px-4 py-3 align-top text-sm text-slate-600 dark:text-slate-300">
                    {formatearTamanioArchivo(backup.tamanioBytes)}
                </td>
                <td className="px-4 py-3 align-top text-right">
                    <button
                        type="button"
                        data-testid={`backup-usar-${backup.archivo}`}
                        onClick={onToggle}
                        aria-expanded={isOpen}
                        aria-controls={`backup-ficha-${backup.archivo}`}
                        className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-bold text-slate-700 hover:border-indigo-300 hover:text-indigo-700 dark:border-slate-700 dark:text-slate-200 dark:hover:border-indigo-700"
                    >
                        {isOpen ? "Cerrar" : "Usar esta"}
                        <ChevronDown className={`h-3.5 w-3.5 transition-transform ${isOpen ? "rotate-180" : ""}`} aria-hidden="true" />
                    </button>
                </td>
            </tr>
            {isOpen && (
                <tr id={`backup-ficha-${backup.archivo}`} data-testid={`backup-ficha-${backup.archivo}`}>
                    <td colSpan={4} className="bg-slate-50 px-4 pb-4 dark:bg-slate-900/40">
                        <div className="rounded-xl border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
                            <RestoreBackupFicha backup={backup} onSuccessTotal={onSuccessTotal} />
                        </div>
                    </td>
                </tr>
            )}
        </>
    );
}
