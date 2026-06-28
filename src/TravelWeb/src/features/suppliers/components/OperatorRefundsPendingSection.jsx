/**
 * Sección "Reembolsos a cobrar del operador" reutilizable.
 *
 * Cuando se anula una reserva que ya tenía pagos al operador, el operador
 * queda debiendo devolver esa plata. Este componente lista esas deudas con un
 * semáforo visual y permite gestionar casos atrasados.
 *
 * Se usa en DOS lugares:
 *   1. La bandeja global (/operator-refunds): todas las deudas de todos los operadores.
 *   2. La ficha del proveedor (SupplierAccountPage): solo las de ese operador.
 *
 * Comportamiento del semáforo (viene como entero del backend, sin JsonStringEnumConverter):
 *   0 = OnTime → gris neutro "A tiempo"
 *   1 = DueSoon → naranja "Por vencer"
 *   2 = Overdue → rojo "Vencido" (NO desaparece de la lista — es un requerimiento explícito)
 *   3 = Abandoned → pizarra "Abandonado" (el job lo dio por perdido, pero el operador puede pagar tarde)
 *
 * Props:
 *   - supplierPublicId (string|null): si se pasa, filtra por ese proveedor.
 *     Si es null (default), muestra todos.
 *   - showSupplierColumn (bool): muestra la columna "Operador" — true en la bandeja global,
 *     false en la ficha del proveedor (donde el operador ya está en el encabezado).
 */

import { useState, useCallback } from "react";
import { Link } from "react-router-dom";
import { RefreshCw, ExternalLink, AlertTriangle, Clock, Check } from "lucide-react";
import { hasPermission } from "../../../auth";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { useOperatorRefundsPending } from "../hooks/useOperatorRefundsPending";
import { operatorRefundsApi } from "../api/operatorRefundsApi";

// ─── Configuración del semáforo ───────────────────────────────────────────────

/**
 * Mapeo de valores enteros del enum OperatorRefundPendingSemaphore del backend
 * a configuración de color y etiqueta para la UI.
 *
 * IMPORTANTE: el backend serializa este enum como INTEGER (no string), porque
 * no tiene JsonStringEnumConverter global en Program.cs.
 * Si el backend cambia a string, actualizar las claves de este objeto.
 */
const SEMAPHORE_CONFIG = {
  0: {
    label: "A tiempo",
    badgeClass: "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400",
    rowClass: "",
    icon: null,
  },
  1: {
    label: "Por vencer",
    badgeClass: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
    rowClass: "border-l-2 border-amber-400",
    icon: Clock,
  },
  2: {
    label: "Vencido",
    badgeClass: "bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-300",
    rowClass: "border-l-2 border-rose-500 bg-rose-50/40 dark:bg-rose-950/10",
    icon: AlertTriangle,
  },
  3: {
    label: "Abandonado",
    badgeClass: "bg-slate-200 text-slate-700 dark:bg-slate-700 dark:text-slate-300",
    rowClass: "border-l-2 border-slate-400",
    icon: null,
  },
};

// Fallback para semáforos no reconocidos (por si el backend agrega un valor nuevo)
const SEMAPHORE_UNKNOWN = {
  label: "Desconocido",
  badgeClass: "bg-slate-100 text-slate-500",
  rowClass: "",
  icon: null,
};

// ─── Componente: mini-form de reembolso tardío ────────────────────────────────

/**
 * Mini-formulario en línea para reabrir una cancelación abandonada.
 *
 * Aparece solo cuando el item tiene semaphore = 3 (Abandoned) y el usuario tiene
 * permiso caja.edit (que es el permiso del endpoint de reopen).
 *
 * El motivo es OBLIGATORIO (mínimo 10 caracteres). El backend también lo valida.
 *
 * Props:
 *   - cancellationPublicId: string — GUID de la cancelación a reabrir.
 *   - onReopenSuccess: () => void — se llama al completar exitosamente para recargar la lista.
 */
function FormReembolsoTardio({ cancellationPublicId, onReopenSuccess }) {
  const [abierto, setAbierto] = useState(false);
  const [motivo, setMotivo] = useState("");
  const [guardando, setGuardando] = useState(false);
  const [exitoso, setExitoso] = useState(false);
  const [errorLocal, setErrorLocal] = useState(null);

  const motivoValido = motivo.trim().length >= 10;

  const handleAbrir = () => {
    setAbierto(true);
    setMotivo("");
    setErrorLocal(null);
    setExitoso(false);
  };

  const handleCancelar = () => {
    setAbierto(false);
    setMotivo("");
    setErrorLocal(null);
  };

  const handleConfirmar = useCallback(async () => {
    // Validación local antes de llamar al backend
    if (!motivoValido) {
      setErrorLocal("El motivo debe tener al menos 10 caracteres.");
      return;
    }

    setGuardando(true);
    setErrorLocal(null);

    try {
      await operatorRefundsApi.reopenForLateRefund(cancellationPublicId, motivo.trim());
      setExitoso(true);
      setAbierto(false);
      // Notificar al padre para que recargue la lista
      if (onReopenSuccess) onReopenSuccess();
    } catch (err) {
      const mensaje = getApiErrorMessage(err, "No se pudo reabrir la cancelación.");
      showError(mensaje, "Error al reabrir");
      setErrorLocal(mensaje);
    } finally {
      setGuardando(false);
    }
  }, [cancellationPublicId, motivo, motivoValido, onReopenSuccess]);

  // Estado de éxito: muestra el mensaje y el link a Caja
  if (exitoso) {
    return (
      <div
        className="mt-3 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 dark:border-emerald-900/40 dark:bg-emerald-950/20"
        data-testid="late-refund-success"
      >
        <div className="flex items-center gap-1.5 text-xs font-semibold text-emerald-700 dark:text-emerald-300">
          <Check className="h-3.5 w-3.5" />
          Cancelación reabierta. El ingreso del reembolso se registra desde Caja.
        </div>
        <div className="mt-1">
          <Link
            to="/cash"
            className="inline-flex items-center gap-1 text-xs text-emerald-700 underline underline-offset-2 hover:text-emerald-900 dark:text-emerald-400"
          >
            Ir a Caja <ExternalLink className="h-3 w-3" />
          </Link>
        </div>
      </div>
    );
  }

  // Botón "Registrar reembolso tardío" (no abierto)
  if (!abierto) {
    return (
      <button
        type="button"
        onClick={handleAbrir}
        data-testid={`btn-late-refund-${cancellationPublicId}`}
        className="flex-shrink-0 rounded-xl border border-indigo-300 bg-indigo-50 px-3 py-2 text-xs font-bold text-indigo-700 hover:bg-indigo-100 dark:border-indigo-700 dark:bg-indigo-950/30 dark:text-indigo-300 dark:hover:bg-indigo-900/40 transition-colors"
      >
        Registrar reembolso tardío
      </button>
    );
  }

  // Formulario abierto en línea (no modal)
  return (
    <div
      className="mt-3 w-full rounded-xl border border-indigo-200 bg-indigo-50/60 p-3 dark:border-indigo-900/40 dark:bg-indigo-950/20"
      data-testid={`form-late-refund-${cancellationPublicId}`}
    >
      <p className="text-xs font-semibold text-indigo-800 dark:text-indigo-300 mb-2">
        Registrar reembolso tardío
      </p>
      <p className="text-xs text-slate-600 dark:text-slate-400 mb-2">
        Esta cancelación fue dada por perdida. Al reabrirla, vas a poder registrar
        el ingreso del reembolso desde Caja. Explicá brevemente por qué el operador pagó tarde.
      </p>

      <textarea
        value={motivo}
        onChange={(e) => {
          setMotivo(e.target.value);
          // Limpiar error al escribir
          if (errorLocal) setErrorLocal(null);
        }}
        placeholder="Motivo de la reapertura (mínimo 10 caracteres)..."
        rows={2}
        disabled={guardando}
        className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-xs dark:border-indigo-800 dark:bg-slate-900 dark:text-white disabled:opacity-60 focus:outline-none focus:ring-1 focus:ring-indigo-400 resize-none"
        aria-label="Motivo del reembolso tardío"
        aria-required="true"
        data-testid={`input-late-refund-reason-${cancellationPublicId}`}
      />

      {/* Contador de caracteres: ayuda al usuario a saber cuándo puede confirmar */}
      <p className={`text-[10px] mt-0.5 ${motivoValido ? "text-slate-400" : "text-amber-600 dark:text-amber-400"}`}>
        {motivo.trim().length} / 10 caracteres mínimos
      </p>

      {errorLocal && (
        <p className="text-xs text-rose-600 dark:text-rose-400 mt-1" role="alert">
          {errorLocal}
        </p>
      )}

      <div className="flex items-center gap-2 mt-3">
        <button
          type="button"
          onClick={handleConfirmar}
          disabled={guardando || !motivoValido}
          data-testid={`btn-confirm-late-refund-${cancellationPublicId}`}
          className="rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
          {guardando ? "Reabriendo..." : "Confirmar"}
        </button>
        <button
          type="button"
          onClick={handleCancelar}
          disabled={guardando}
          className="rounded-lg border border-slate-300 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-100 dark:border-slate-700 dark:text-slate-400 dark:hover:bg-slate-800 disabled:opacity-50 transition-colors"
        >
          Cancelar
        </button>
      </div>
    </div>
  );
}

// ─── Componente: fila del listado ─────────────────────────────────────────────

/**
 * Fila individual del listado de reembolsos pendientes.
 *
 * Muestra: operador (opcional), reserva, cliente, montos estimados, semáforo y
 * fecha límite. Para items abandonados muestra el botón/form de reembolso tardío.
 *
 * Regla de montos ESTIMADOS: el wording explícito "estimado, sujeto a deducciones
 * del operador" es un requerimiento del backend (ver OperatorRefundEstimatedAmountDto).
 * NUNCA presentar estos montos como cifras firmes.
 *
 * Props:
 *   - item: OperatorRefundPendingItemDto del backend
 *   - showSupplierColumn: bool — mostrar el nombre del operador en la fila
 *   - canEdit: bool — el usuario tiene permiso caja.edit para reabrir cancelaciones
 *   - onReload: () => void — recarga la lista tras una acción exitosa
 */
function FilaReembolsoPendiente({ item, showSupplierColumn, canEdit, onReload }) {
  const semaphoreConfig = SEMAPHORE_CONFIG[item.semaphore] ?? SEMAPHORE_UNKNOWN;
  const SemaphoreIcon = semaphoreConfig.icon;

  // Semáforo 3 = Abandoned: único estado que permite reembolso tardío
  const esAbandonado = item.semaphore === 3;

  const fechaLimite = item.operatorRefundDueBy
    ? formatDate(item.operatorRefundDueBy)
    : "—";

  return (
    <div
      className={`flex flex-col gap-3 px-6 py-4 ${semaphoreConfig.rowClass}`}
      data-testid={`refund-row-${item.bookingCancellationPublicId}`}
    >
      <div className="flex flex-col sm:flex-row sm:items-start sm:justify-between gap-3">
        {/* Datos principales */}
        <div className="space-y-1 min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            {/* Número de reserva como link navegable */}
            <Link
              to={`/reservas/${item.reservaPublicId}`}
              className="font-semibold text-sm text-slate-900 dark:text-white hover:underline inline-flex items-center gap-1"
              title="Ver reserva"
            >
              Reserva #{item.numeroReserva}
              <ExternalLink className="h-3 w-3 text-muted-foreground" />
            </Link>

            {/* Semáforo visual */}
            <span
              className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-bold uppercase tracking-wider ${semaphoreConfig.badgeClass}`}
              data-testid={`semaphore-${item.bookingCancellationPublicId}`}
            >
              {SemaphoreIcon && <SemaphoreIcon className="h-2.5 w-2.5" />}
              {semaphoreConfig.label}
            </span>

            {/* Días vencido: solo cuando hay un vencimiento real */}
            {item.semaphore === 2 && item.daysOverdue > 0 && (
              <span className="text-[10px] font-semibold text-rose-600 dark:text-rose-400">
                {item.daysOverdue} día{item.daysOverdue !== 1 ? "s" : ""} vencido
              </span>
            )}
          </div>

          {/* Detalle de la fila */}
          <div className="text-xs text-slate-500 dark:text-slate-400 flex flex-wrap gap-x-4 gap-y-0.5">
            {showSupplierColumn && item.supplierName && (
              <span>
                Operador: <strong className="text-slate-700 dark:text-slate-300">{item.supplierName}</strong>
              </span>
            )}
            {item.clienteNombre && (
              <span>
                Cliente: <strong className="text-slate-700 dark:text-slate-300">{item.clienteNombre}</strong>
              </span>
            )}
            {item.operatorRefundDueBy && (
              <span>
                Vence: <strong className="text-slate-700 dark:text-slate-300">{fechaLimite}</strong>
              </span>
            )}
          </div>

          {/* Montos estimados por moneda.
              Regla clave: SIEMPRE etiquetados como "estimado, sujeto a deducciones".
              Si amountsMasked=true, el backend envió 0 en todos los montos → mostramos "—". */}
          {(item.estimatedRefundsByCurrency ?? []).length > 0 && (
            <div className="mt-1 flex flex-wrap gap-2">
              {item.estimatedRefundsByCurrency.map((linea) => (
                <div
                  key={linea.currency}
                  className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-slate-50 px-2.5 py-1 dark:border-slate-700 dark:bg-slate-800/40"
                  data-testid={`refund-amount-${item.bookingCancellationPublicId}-${linea.currency}`}
                >
                  <span className="text-[9px] font-black uppercase tracking-wider text-muted-foreground">
                    {linea.currency}
                  </span>
                  <span className="font-mono font-semibold text-xs text-slate-800 dark:text-slate-200">
                    {item.amountsMasked
                      ? "—"
                      : formatCurrency(linea.estimatedAmount, linea.currency)
                    }
                  </span>
                  {/* Etiqueta "estimado": requerimiento explícito del dominio.
                      NUNCA presentar este monto como cifra firme (el operador puede deducir penalidades, etc.) */}
                  <span className="text-[9px] text-amber-600 dark:text-amber-400 font-medium italic">
                    estimado
                  </span>
                </div>
              ))}
            </div>
          )}

          {/* Aviso de montos enmascarados: sin permiso cobranzas.see_cost */}
          {item.amountsMasked && (
            <p className="text-[10px] text-muted-foreground mt-0.5">
              No tenés permiso para ver los montos.
            </p>
          )}
        </div>

        {/* Acción: reembolso tardío solo para abandonados con permiso caja.edit */}
        {esAbandonado && canEdit && (
          <div className="flex-shrink-0">
            <FormReembolsoTardio
              cancellationPublicId={item.bookingCancellationPublicId}
              onReopenSuccess={onReload}
            />
          </div>
        )}
      </div>
    </div>
  );
}

// ─── Componente principal exportado ───────────────────────────────────────────

/**
 * Sección de reembolsos pendientes del operador.
 *
 * Gateada por permiso tesoreria.supplier_payments: si el usuario no lo tiene,
 * no se muestra nada (ni siquiera el encabezado). El backend valida el mismo
 * permiso server-side, así que este gate es solo visual.
 *
 * Props:
 *   - supplierPublicId (string|null): null = bandeja global, GUID = por proveedor.
 *   - showSupplierColumn (bool): mostrar columna "Operador" (default true en global, false en ficha).
 */
export function OperatorRefundsPendingSection({ supplierPublicId = null, showSupplierColumn = false }) {
  // Gate de permiso: sin tesoreria.supplier_payments no mostramos nada.
  // El backend valida el mismo permiso — esto es solo para no confundir al usuario.
  const puedeVer = hasPermission("tesoreria.supplier_payments");

  // caja.edit es el permiso para reabrir cancelaciones (endpoint reopen-for-late-refund).
  const puedeReabrir = hasPermission("caja.edit");

  const { items, loading, error, reload } = useOperatorRefundsPending(supplierPublicId);

  if (!puedeVer) {
    return null;
  }

  const totalVencidos = items.filter((i) => i.semaphore === 2).length;

  return (
    <div
      className="overflow-hidden rounded-xl border bg-card shadow-sm"
      data-testid="operator-refunds-section"
    >
      {/* ── Encabezado ── */}
      <div className="flex items-center justify-between border-b px-6 py-4 flex-wrap gap-2">
        <div className="min-w-0">
          <h2 className="font-semibold text-slate-900 dark:text-white">
            Reembolsos a cobrar del operador
          </h2>
          <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">
            Cancelaciones donde el operador tiene que devolver plata. Los montos son{" "}
            <strong>estimados</strong>, sujetos a las deducciones del operador al momento de pagar.
          </p>
        </div>

        <div className="flex items-center gap-3">
          {/* Badge de vencidos: advertencia cuando hay items en rojo */}
          {totalVencidos > 0 && (
            <span
              className="inline-flex items-center gap-1 rounded-full bg-rose-100 px-2.5 py-0.5 text-xs font-bold text-rose-700 dark:bg-rose-900/30 dark:text-rose-300"
              data-testid="badge-vencidos"
            >
              <AlertTriangle className="h-3 w-3" />
              {totalVencidos} vencido{totalVencidos !== 1 ? "s" : ""}
            </span>
          )}

          {/* Conteo total */}
          {!loading && items.length > 0 && (
            <span className="text-sm font-semibold text-slate-600 dark:text-slate-300">
              {items.length} caso{items.length !== 1 ? "s" : ""}
            </span>
          )}

          {/* Botón de actualizar manual */}
          <button
            type="button"
            onClick={reload}
            disabled={loading}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 dark:border-slate-600 px-3 py-1.5 text-xs font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50"
            aria-label="Actualizar lista de reembolsos pendientes"
            data-testid="refresh-operator-refunds"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? "animate-spin" : ""}`} />
            Actualizar
          </button>
        </div>
      </div>

      {/* ── Cuerpo: estados loading / error / vacío / lista ── */}
      {loading ? (
        <div
          className="px-6 py-10 text-center text-sm text-slate-500"
          data-testid="operator-refunds-loading"
        >
          Cargando reembolsos pendientes…
        </div>
      ) : error ? (
        <div
          className="px-6 py-10 text-center space-y-2"
          data-testid="operator-refunds-error"
        >
          <p className="text-sm text-rose-600 dark:text-rose-400">
            No se pudo cargar la información. Intentá de nuevo.
          </p>
          <button
            type="button"
            onClick={reload}
            className="text-xs text-indigo-600 hover:underline dark:text-indigo-400"
          >
            Reintentar
          </button>
        </div>
      ) : items.length === 0 ? (
        // Empty state: es el estado normal cuando no hay anulaciones esperando reembolso.
        <div
          className="px-6 py-10 text-center text-sm text-slate-500 dark:text-slate-400"
          data-testid="operator-refunds-empty"
        >
          No hay reembolsos pendientes del operador. Todo al día.
        </div>
      ) : (
        <div className="divide-y divide-slate-100 dark:divide-slate-800">
          {items.map((item) => (
            <FilaReembolsoPendiente
              key={item.bookingCancellationPublicId}
              item={item}
              showSupplierColumn={showSupplierColumn}
              canEdit={puedeReabrir}
              onReload={reload}
            />
          ))}
        </div>
      )}
    </div>
  );
}
