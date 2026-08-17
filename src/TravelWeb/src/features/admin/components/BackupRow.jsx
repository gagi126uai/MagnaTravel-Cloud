import { ChevronDown } from "lucide-react";
import { Badge } from "../../../components/ui/badge";
import { Button } from "../../../components/ui/button";
import { formatDateTime } from "../../../lib/utils";
import { textoTiempoRelativo } from "../../cancellations/debitNoteInboxLogic";
import {
    formatearTamanioArchivo,
    construirBadgeVersionResguardo,
    resolverPorQueSeGuardo,
    construirSufijoTestIdBackup,
} from "../lib/dangerRestoreLogic";
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
    // F9 (deuda 30/07): los testid/id de esta fila ya NO llevan el nombre de archivo interno
    // del resguardo — ver el docstring de construirSufijoTestIdBackup. El `key` en el padre
    // (CopiasDeSeguridadTab) y `onToggle`/`isOpen` siguen identificando la fila por
    // `backup.archivo` como antes: eso no cambia, es la clave real del pedido de restaurar.
    const sufijoTestId = construirSufijoTestIdBackup(backup);

    return (
        <>
            <tr className="border-b border-slate-100 dark:border-slate-800" data-testid={`backup-row-${sufijoTestId}`}>
                <td className="px-4 py-3 align-top">
                    <div className="font-semibold text-slate-900 dark:text-white">{formatDateTime(backup.fechaUtc)}</div>
                    <div className="text-xs text-slate-500 dark:text-slate-400">{textoTiempoRelativo(backup.fechaUtc)}</div>
                    {badge && (
                        <Badge
                            variant="outline"
                            data-testid={`backup-badge-${sufijoTestId}`}
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
                    <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        data-testid={`backup-usar-${sufijoTestId}`}
                        onClick={onToggle}
                        aria-expanded={isOpen}
                        aria-controls={`backup-ficha-${sufijoTestId}`}
                        className="gap-1"
                    >
                        {isOpen ? "Cerrar" : "Usar esta"}
                        <ChevronDown className={`h-3.5 w-3.5 transition-transform ${isOpen ? "rotate-180" : ""}`} aria-hidden="true" />
                    </Button>
                </td>
            </tr>
            {isOpen && (
                <tr id={`backup-ficha-${sufijoTestId}`} data-testid={`backup-ficha-${sufijoTestId}`}>
                    <td colSpan={4} className="bg-slate-50 px-4 pb-4 dark:bg-slate-900/40">
                        <div className="rounded-[14px] border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
                            <RestoreBackupFicha backup={backup} onSuccessTotal={onSuccessTotal} />
                        </div>
                    </td>
                </tr>
            )}
        </>
    );
}
