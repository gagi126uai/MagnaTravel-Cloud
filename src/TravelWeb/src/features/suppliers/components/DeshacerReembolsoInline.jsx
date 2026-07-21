/**
 * Ficha EN LÍNEA para deshacer un reembolso del operador ya registrado (Tanda P2, spec
 * docs/ux/2026-07-22-p2-deshacer-reasociar-reembolso.md, sección 5).
 *
 * Se abre debajo de la fila en el bloque "Reembolsos ya registrados" cuando el usuario
 * aprieta "Deshacer". Mismo molde visual que FormReembolsoTardio (misma solapa): motivo
 * obligatorio con contador de caracteres, sin paso de confirmación extra (P2=A: el motivo
 * ya es el freno).
 *
 * Al confirmar: DELETE /operator-refunds/allocations/{id} con { reason }. La fila queda
 * tachada como "Deshecho" — libera la plata para volver a imputarla bien.
 *
 * Props:
 *   - item: OperatorRefundRegisteredItemDto — la fila que se está deshaciendo.
 *   - onCerrar: () => void — cierra la ficha sin guardar.
 *   - onCompletado: () => void — se llama al deshacer con éxito; el padre recarga la solapa
 *     entera (los dos bloques + el encabezado, regla de coherencia de la spec).
 */

import { useState } from "react";
import { Loader2, Undo2, X } from "lucide-react";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { formatCurrency } from "../../../lib/utils";
import { operatorRefundsApi } from "../api/operatorRefundsApi";
import {
  MOTIVO_ACCION_REEMBOLSO_MIN,
  esErrorCreditoYaUsado,
  puedeConfirmarDeshacer,
  validarMotivoAccionReembolso,
} from "../lib/operatorRefundRegisteredLogic";
import { ErrorAccionReembolsoBanner } from "./ErrorAccionReembolsoBanner";

export function DeshacerReembolsoInline({ item, onCerrar, onCompletado }) {
  const [motivo, setMotivo] = useState("");
  const [guardando, setGuardando] = useState(false);
  const [errorMensaje, setErrorMensaje] = useState(null);
  // P4=B: solo se muestra el botón "Ir a la cuenta del cliente" cuando el rechazo es
  // EXACTAMENTE el caso "el cliente ya usó ese saldo a favor".
  const [mostrarBotonCuentaCliente, setMostrarBotonCuentaCliente] = useState(false);

  const motivoValido = validarMotivoAccionReembolso(motivo) === null;
  const puedeConfirmar = puedeConfirmarDeshacer({ motivo, submitting: guardando });

  // Línea de contexto: dice qué plata y qué reserva se tocan. Sin cobranzas.see_cost
  // (amountsMasked=true) se omite el monto — el backend ya lo manda en 0 para ese caso.
  const textoContexto = item.amountsMasked
    ? `Vas a deshacer el reembolso imputado a la reserva #${item.numeroReserva}.`
    : `Vas a deshacer el reembolso de ${formatCurrency(item.netAmount, item.currency)} imputado a la reserva #${item.numeroReserva}.`;

  const handleConfirmar = async () => {
    if (!puedeConfirmar) return;

    setGuardando(true);
    setErrorMensaje(null);
    setMostrarBotonCuentaCliente(false);

    try {
      await operatorRefundsApi.voidAllocation(item.publicId, motivo.trim());
      showSuccess("Reembolso deshecho. La plata quedó pendiente de nuevo.");
      onCompletado();
    } catch (error) {
      // La ficha queda abierta con el motivo intacto para poder reintentar sin
      // re-escribir todo (regla de la spec: nunca se pierde lo cargado en un rechazo).
      setErrorMensaje(getApiErrorMessage(error, "No se pudo deshacer el reembolso."));
      setMostrarBotonCuentaCliente(esErrorCreditoYaUsado(error));
    } finally {
      setGuardando(false);
    }
  };

  return (
    <div
      className="mt-3 rounded-xl border-2 border-indigo-200 bg-indigo-50/60 p-4 space-y-3 dark:border-indigo-900/40 dark:bg-indigo-950/20"
      data-testid={`form-deshacer-reembolso-${item.publicId}`}
    >
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Undo2 className="h-4 w-4 text-indigo-600 dark:text-indigo-400" aria-hidden="true" />
          <h4 className="text-sm font-bold text-slate-900 dark:text-white">Deshacer este reembolso</h4>
        </div>
        <button
          type="button"
          onClick={onCerrar}
          disabled={guardando}
          className="rounded p-1 text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 disabled:opacity-50"
          aria-label="Cerrar ficha de deshacer reembolso"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      <p className="text-xs text-slate-600 dark:text-slate-400">{textoContexto}</p>

      <ErrorAccionReembolsoBanner
        mensaje={errorMensaje}
        mostrarBotonCuentaCliente={mostrarBotonCuentaCliente}
        clientePublicId={item.clientePublicId}
      />

      <div>
        <label htmlFor={`deshacer-motivo-${item.publicId}`} className="sr-only">
          Motivo para deshacer el reembolso
        </label>
        <textarea
          id={`deshacer-motivo-${item.publicId}`}
          value={motivo}
          onChange={(e) => setMotivo(e.target.value)}
          placeholder="Contá por qué lo deshacés (mínimo 20 caracteres)…"
          rows={2}
          disabled={guardando}
          className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-xs dark:border-indigo-800 dark:bg-slate-900 dark:text-white disabled:opacity-60 focus:outline-none focus:ring-1 focus:ring-indigo-400 resize-none"
          aria-required="true"
          data-testid="reembolso-deshacer-motivo"
        />
        {/* Contador: ámbar hasta llegar al mínimo, gris cuando ya es válido (spec §5) */}
        <p className={`text-[10px] mt-0.5 ${motivoValido ? "text-slate-400" : "text-amber-600 dark:text-amber-400"}`}>
          {motivo.trim().length} / {MOTIVO_ACCION_REEMBOLSO_MIN} caracteres mínimos
        </p>
      </div>

      <div className="flex items-center justify-end gap-2">
        <button
          type="button"
          onClick={onCerrar}
          disabled={guardando}
          className="rounded-lg border border-slate-300 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-100 dark:border-slate-700 dark:text-slate-400 dark:hover:bg-slate-800 disabled:opacity-50 transition-colors"
        >
          Cancelar
        </button>
        <button
          type="button"
          onClick={handleConfirmar}
          disabled={!puedeConfirmar}
          data-testid="reembolso-deshacer-confirmar"
          className="flex items-center gap-2 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-bold text-white hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
          {guardando && <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />}
          {guardando ? "Deshaciendo…" : "Deshacer reembolso"}
        </button>
      </div>
    </div>
  );
}
