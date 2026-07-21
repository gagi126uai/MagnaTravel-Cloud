/**
 * Cartel de error compartido por "Deshacer" y "Corregir reserva" (Tanda P2, spec
 * 2026-07-22). Muestra el mensaje del motor TAL CUAL (nunca se reescribe en el front) y,
 * si el rechazo es EXACTAMENTE "el cliente ya usó ese saldo a favor" (P4=B de la spec),
 * suma el botón "Ir a la cuenta del cliente" para que el usuario pueda resolverlo ahí.
 *
 * Se usa en DeshacerReembolsoInline y CorregirReembolsoInline — factorizado acá para no
 * repetir el mismo bloque dos veces (mismo mensaje, mismo botón, mismo criterio).
 *
 * Props:
 *   - mensaje: string — texto de error a mostrar (ya resuelto con getApiErrorMessage).
 *   - mostrarBotonCuentaCliente: bool — true cuando el error es REFUND_CREDIT_ALREADY_USED.
 *   - clientePublicId: string — GUID del cliente titular de la reserva de esta fila.
 */

import { AlertTriangle, ArrowRight } from "lucide-react";
import { Link } from "react-router-dom";

export function ErrorAccionReembolsoBanner({ mensaje, mostrarBotonCuentaCliente, clientePublicId }) {
  if (!mensaje) return null;

  return (
    <div
      role="alert"
      className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex flex-col gap-2"
      data-testid="reembolso-accion-error"
    >
      <div className="flex items-start gap-2">
        <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
        <span>{mensaje}</span>
      </div>
      {mostrarBotonCuentaCliente && clientePublicId && (
        <Link
          to={`/customers/${clientePublicId}/account`}
          className="inline-flex items-center gap-1.5 self-start rounded-lg border border-rose-300 bg-white px-3 py-1.5 text-xs font-bold text-rose-700 hover:bg-rose-50 dark:bg-slate-800 dark:text-rose-200 dark:border-rose-800 dark:hover:bg-slate-700 transition-colors"
          data-testid="reembolso-ir-cuenta-cliente"
        >
          Ir a la cuenta del cliente
          <ArrowRight className="h-3.5 w-3.5" aria-hidden="true" />
        </Link>
      )}
    </div>
  );
}
