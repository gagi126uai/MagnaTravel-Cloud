/**
 * Lista de movimientos de caja (cobranzas, pagos a proveedor, ajustes manuales).
 *
 * Multimoneda (2026-06-11): cada movimiento lleva una columna "Moneda" con cartelito
 * $/US$ y el monto se formatea con la moneda real del movimiento (movement.currency).
 * Regla ③: si todos los movimientos son de una moneda, la columna igual aparece
 * (simplifica el diseño; el cartelito muestra "$" para todos los movimientos en ARS).
 */
import { useEffect, useState } from "react";
import {
  ArrowDownLeft,
  ArrowUpRight,
  Landmark,
  Pencil,
  Plus,
  Trash2,
  X,
} from "lucide-react";
import {
  DataGrid,
  DataGridActionCell,
  DataGridBody,
  DataGridCell,
  DataGridEmptyState,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridHeaderRow,
  DataGridRow,
} from "../../../components/ui/DataGrid";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { getApiErrorMessage } from "../../../lib/errors";
import { formatCurrency } from "../lib/financeUtils";
import { formatDate, formatDateTime } from "../../../lib/utils";
import { debeApagarBotonesMovimiento, obtenerEstadoBadgeMovimiento } from "../lib/cashMovementBadgeLogic";
import { esCategoriaDeSistema, mapearCategoriaMovimiento, mapearMetodoMovimiento } from "../lib/cashMovementLabels";

// Formateador legacy mantenido solo para llamadas locales sin moneda explícita (compatibilidad interna)
const currency = new Intl.NumberFormat("es-AR", {
  style: "currency",
  currency: "ARS",
  minimumFractionDigits: 2,
});

const sourceLabels = {
  CustomerPayment: "Cobranza",
  SupplierPayment: "Pago a proveedor",
  ManualAdjustment: "Ajuste manual",
};

const emptyForm = {
  direction: "Income",
  amount: "",
  occurredAt: "",
  method: "Transferencia",
  category: "",
  description: "",
  reference: "",
  relatedReservaPublicId: "",
  relatedSupplierPublicId: "",
};

const toLocalDateTime = (value) => {
  if (!value) {
    return "";
  }

  const date = new Date(value);
  const offset = date.getTimezoneOffset();
  return new Date(date.getTime() - offset * 60000).toISOString().slice(0, 16);
};

function ManualMovementModal({ open, onClose, onSubmit, movement }) {
  const [form, setForm] = useState(emptyForm);
  const [saving, setSaving] = useState(false);
  // H12 (barrido E2E 2026-07-25): mensaje de validación propio en español, mostrado en un
  // cartel dentro de la ficha (P-6/P-7) — nunca el cartelito nativo del navegador en inglés.
  const [errorValidacion, setErrorValidacion] = useState(null);

  useEffect(() => {
    if (!open) {
      return;
    }

    setForm(
      movement
        ? {
            direction: movement.direction || "Income",
            amount: movement.amount || "",
            occurredAt: toLocalDateTime(movement.occurredAt),
            method: movement.method || "Transferencia",
            category: movement.category || "",
            description: movement.description || "",
            reference: movement.reference || "",
            relatedReservaPublicId: movement.reservaPublicId || movement.relatedReservaPublicId || "",
            relatedSupplierPublicId: movement.supplierPublicId || movement.relatedSupplierPublicId || "",
          }
        : emptyForm
    );
    setErrorValidacion(null);
  }, [movement, open]);

  if (!open) {
    return null;
  }

  const handleChange = (field, value) => {
    setForm((current) => ({ ...current, [field]: value }));
  };

  // Hallazgo menor (barrido de estándares, 2026-07-27): las categorías que arma el motor
  // solo (ClientCreditWithdrawal/ClientCreditReversal, ver ManualCashMovementBuilder) NO
  // se pueden re-tipear acá — si dejáramos el campo editable y el cajero guardara sin
  // tocarlo, el input mostraría el texto en criollo pero al reenviarlo se perdería el
  // token que el motor usa para la trazabilidad de ese movimiento. Por eso el campo queda
  // de solo lectura (con el texto ya traducido) para estas categorías puntuales; el resto
  // de las categorías (texto libre del usuario) siguen 100% editables como siempre.
  const categoriaEsDeSistema = Boolean(movement && esCategoriaDeSistema(movement.category));

  const handleSubmit = async (event) => {
    event.preventDefault();
    setErrorValidacion(null);

    // H12: validación propia en español. Antes, con el navegador validando el "required"/
    // "min" nativo de estos inputs, el cartelito que aparecía era el del navegador (en
    // inglés) y handleSubmit ni se ejecutaba — el mensaje en criollo nunca llegaba a verse
    // (mismo bug que ya se corrigió en RegistrarCobroInline.jsx, obra C1). Con noValidate
    // en el <form>, el control de estos 4 campos queda 100% en React.
    if (!form.amount || Number(form.amount) <= 0) {
      setErrorValidacion("El monto tiene que ser mayor a 0.");
      return;
    }
    if (!form.method.trim()) {
      setErrorValidacion("El método es obligatorio.");
      return;
    }
    if (!form.category.trim()) {
      setErrorValidacion("La categoría es obligatoria.");
      return;
    }
    if (!form.description.trim()) {
      setErrorValidacion("La descripción es obligatoria.");
      return;
    }

    setSaving(true);
    try {
      await onSubmit({
        direction: form.direction,
        amount: Number(form.amount),
        occurredAt: form.occurredAt ? new Date(form.occurredAt).toISOString() : new Date().toISOString(),
        method: form.method,
        category: form.category,
        description: form.description,
        reference: form.reference || null,
        relatedReservaPublicId: form.relatedReservaPublicId || null,
        relatedSupplierPublicId: form.relatedSupplierPublicId || null,
      });
      onClose();
    } catch (error) {
      // Fix menor (revisión 2026-07-27): antes el rechazo del motor solo se veía en un
      // toast que se cierra solo, y la promesa quedaba rechazada sin manejar (onSubmit →
      // useFinanceActions la relanza). Mismo patrón que CustomerFormModal (P-6/P-7): el
      // motivo queda EN LÍNEA, a la vista, para que el usuario lo lea con calma.
      setErrorValidacion(getApiErrorMessage(error, "No se pudo guardar el movimiento."));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4 backdrop-blur-sm">
      <div className="w-full max-w-2xl overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-2xl dark:border-slate-800 dark:bg-slate-900">
        <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
          <div>
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
              {movement ? "Editar ajuste manual" : "Nuevo ajuste manual"}
            </h3>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Este movimiento impacta caja pero no modifica balances de reservas.
            </p>
          </div>
          <button type="button" onClick={onClose} className="text-slate-400 hover:text-slate-600">
            <X className="h-5 w-5" />
          </button>
        </div>

        {/* noValidate (H12, obra C1): sin esto, el navegador cortaba el submit con SU propio
            cartelito de validación en inglés (por el min="0.01"/required de estos inputs) y
            el mensaje propio en criollo de handleSubmit nunca llegaba a mostrarse. */}
        <form onSubmit={handleSubmit} className="grid gap-4 p-6 md:grid-cols-2" noValidate>
          <div>
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Direccion</label>
            <select
              value={form.direction}
              onChange={(event) => handleChange("direction", event.target.value)}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 dark:border-slate-700 dark:bg-slate-950 dark:text-white"
            >
              <option value="Income">Ingreso</option>
              <option value="Expense">Egreso</option>
            </select>
          </div>

          <div>
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Monto</label>
            <input
              type="number"
              step="0.01"
              min="0.01"
              value={form.amount}
              onChange={(event) => handleChange("amount", event.target.value)}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 dark:border-slate-700 dark:bg-slate-950 dark:text-white"
              required
            />
          </div>

          <div>
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Fecha</label>
            <input
              type="datetime-local"
              value={form.occurredAt}
              onChange={(event) => handleChange("occurredAt", event.target.value)}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 dark:border-slate-700 dark:bg-slate-950 dark:text-white"
            />
          </div>

          <div>
            <label htmlFor="movimiento-manual-method" className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Metodo</label>
            {/* Fix bloqueante data-exposure (review): mismo problema que Categoría — un
                movimiento de sistema (retiro/devolución de saldo a favor) trae Method="Cash"/
                "Transfer" crudo (ver ManualCashMovementBuilder en el motor). Se muestra
                traducido y de solo lectura; form.method sigue guardando el token crudo para
                no corromper el dato si se re-guarda sin tocar este campo. */}
            <input
              id="movimiento-manual-method"
              type="text"
              value={categoriaEsDeSistema ? mapearMetodoMovimiento(form.method) : form.method}
              onChange={(event) => handleChange("method", event.target.value)}
              readOnly={categoriaEsDeSistema}
              aria-describedby={categoriaEsDeSistema ? "movimiento-manual-method-hint" : undefined}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 read-only:cursor-not-allowed read-only:bg-slate-50 read-only:text-slate-500 dark:border-slate-700 dark:bg-slate-950 dark:text-white dark:read-only:bg-slate-900"
              required={!categoriaEsDeSistema}
              data-testid="movimiento-manual-method"
            />
            {categoriaEsDeSistema && (
              <p id="movimiento-manual-method-hint" className="mt-1 text-xs text-slate-400">
                Método generado por el sistema, no se puede editar acá.
              </p>
            )}
          </div>

          <div>
            <label htmlFor="movimiento-manual-category" className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Categoria</label>
            <input
              id="movimiento-manual-category"
              type="text"
              value={categoriaEsDeSistema ? mapearCategoriaMovimiento(form.category) : form.category}
              onChange={(event) => handleChange("category", event.target.value)}
              readOnly={categoriaEsDeSistema}
              aria-describedby={categoriaEsDeSistema ? "movimiento-manual-category-hint" : undefined}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 read-only:cursor-not-allowed read-only:bg-slate-50 read-only:text-slate-500 dark:border-slate-700 dark:bg-slate-950 dark:text-white dark:read-only:bg-slate-900"
              required={!categoriaEsDeSistema}
              data-testid="movimiento-manual-category"
            />
            {categoriaEsDeSistema && (
              <p id="movimiento-manual-category-hint" className="mt-1 text-xs text-slate-400">
                Categoría generada por el sistema, no se puede editar acá.
              </p>
            )}
          </div>

          <div>
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Referencia</label>
            <input
              type="text"
              value={form.reference}
              onChange={(event) => handleChange("reference", event.target.value)}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 dark:border-slate-700 dark:bg-slate-950 dark:text-white"
            />
          </div>

          <div className="md:col-span-2">
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Descripcion</label>
            <textarea
              value={form.description}
              onChange={(event) => handleChange("description", event.target.value)}
              rows={3}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 dark:border-slate-700 dark:bg-slate-950 dark:text-white"
              required
            />
          </div>

          {/* Cartel de error (P-6/P-7): en línea, arriba de los botones, se queda a la vista
              mientras el usuario corrige — nunca un toast que desaparece solo. */}
          {errorValidacion && (
            <div
              className="md:col-span-2 rounded-lg bg-rose-50 border border-rose-200 px-4 py-3 text-sm text-rose-700 dark:bg-rose-950/20 dark:border-rose-900/40 dark:text-rose-300"
              role="alert"
              data-testid="movimiento-manual-error"
            >
              {errorValidacion}
            </div>
          )}

          <div className="md:col-span-2 flex justify-end gap-3 pt-2">
            <button
              type="button"
              onClick={onClose}
              className="rounded-xl border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 dark:border-slate-700 dark:text-slate-200"
            >
              Cancelar
            </button>
            <button
              type="submit"
              disabled={saving}
              className="rounded-xl bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:opacity-60"
            >
              {saving ? "Guardando..." : movement ? "Guardar cambios" : "Registrar movimiento"}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

export function MovementsTab({
  movements,
  isAdmin,
  onCreateManualMovement,
  onUpdateManualMovement,
  onDeleteManualMovement,
  showHeader = true,
}) {
  const [editingMovement, setEditingMovement] = useState(null);
  const [showModal, setShowModal] = useState(false);

  const openCreate = () => {
    setEditingMovement(null);
    setShowModal(true);
  };

  const openEdit = (movement) => {
    setEditingMovement(movement);
    setShowModal(true);
  };

  const closeModal = () => {
    setEditingMovement(null);
    setShowModal(false);
  };

  const handleSubmit = async (payload) => {
    if (editingMovement) {
      await onUpdateManualMovement(editingMovement.sourcePublicId, payload);
      return;
    }

    await onCreateManualMovement(payload);
  };

  return (
    <div className="space-y-6">
      {showHeader ? (
        <div className="flex flex-col justify-between gap-4 md:flex-row md:items-center">
          <div>
            <h2 className="text-lg font-semibold text-slate-900 dark:text-white">Caja</h2>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Libro de caja con ingresos por cobranzas, egresos a proveedores y ajustes manuales.
            </p>
          </div>
          {isAdmin ? (
            <button
              type="button"
              onClick={openCreate}
              className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800"
            >
              <Plus className="h-4 w-4" />
              Nuevo ajuste manual
            </button>
          ) : null}
        </div>
      ) : null}

      {!showHeader && isAdmin ? (
        <div className="flex justify-end">
          <button
            type="button"
            onClick={openCreate}
            className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800"
          >
            <Plus className="h-4 w-4" />
            Nuevo ajuste manual
          </button>
        </div>
      ) : null}

      <DataGrid minWidth="1050px">
        <DataGridHeader>
          <DataGridHeaderRow>
            <DataGridHeaderCell>Fecha</DataGridHeaderCell>
            <DataGridHeaderCell>Origen</DataGridHeaderCell>
            <DataGridHeaderCell>Detalle</DataGridHeaderCell>
            <DataGridHeaderCell>Metodo</DataGridHeaderCell>
            {/* Columna Moneda: siempre visible (decisión D, 2026-06-11) */}
            <DataGridHeaderCell>Moneda</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Monto</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Accion</DataGridHeaderCell>
          </DataGridHeaderRow>
        </DataGridHeader>
        <DataGridBody>
          {movements.length === 0 ? (
            <DataGridEmptyState
              colSpan={6}
              icon={Landmark}
              title="Caja sin movimientos"
              description="Todavia no hay ingresos o egresos registrados en caja."
            />
          ) : (
            movements.map((movement) => {
              const isIncome = movement.direction === "Income";
              const isManual = movement.isManual;
              // Obra 2 (firma 2026-07-27): un par de Caja por EDICIÓN dice "Reemplazado",
              // distinto del par por anulación real que sigue diciendo "Anulado" — el
              // cajero distingue qué pasó con el movimiento sin tener que abrir nada.
              const estadoBadge = obtenerEstadoBadgeMovimiento(movement);
              const botonesApagados = debeApagarBotonesMovimiento(movement);

              return (
                // H14 (2026-07-25): key = movement.publicId, el PublicId ESTABLE del propio asiento
                // de caja que ahora manda el motor. Reemplaza la key sintética
                // "sourceType-sourcePublicId-direction-índice" (parche de H4): un movimiento manual
                // y su contra-asiento comparten sourcePublicId, pero cada uno tiene su PROPIO
                // publicId, así que ya no hace falta armar nada a mano en el front.
                <DataGridRow key={movement.publicId}>
                  {/* fix 2026-07-22: movement.occurredAt sale de CashLedgerEntry.OccurredAt, que para
                      cobros/pagos ES payment.PaidAt (día de negocio elegido por el cajero, guardado
                      como medianoche UTC — no un instante con hora real). formatDateTime() no lo
                      convierte a hora local del navegador; eso era lo que corría el día un dia menos. */}
                  <DataGridCell>{formatDateTime(movement.occurredAt)}</DataGridCell>
                  <DataGridCell>
                    <div className="flex items-center gap-3">
                      <div className={`rounded-lg p-2 ${isIncome ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-950/30 dark:text-emerald-400" : "bg-rose-50 text-rose-600 dark:bg-rose-950/30 dark:text-rose-400"}`}>
                        {isIncome ? <ArrowDownLeft className="h-4 w-4" /> : <ArrowUpRight className="h-4 w-4" />}
                      </div>
                      <div>
                        <div className="flex items-center gap-2">
                          <div className="text-sm font-semibold text-slate-900 dark:text-white">
                            {sourceLabels[movement.sourceType] || movement.sourceType}
                          </div>
                          {/* H14/Obra 2: badge "Anulado" o "Reemplazado" en AMBAS filas del par (el
                              asiento viejo y su contra-asiento) — antes ninguna de las dos filas
                              avisaba nada.
                              Fix del reviewer (2026-07-27): testid ESTABLE (no cambia de nombre
                              según el estado) + data-estado para que QA pueda leer cuál es sin
                              tener que adivinar el nombre del selector. */}
                          {estadoBadge && (
                            <span
                              className="inline-flex items-center rounded-full bg-slate-100 px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide text-slate-500 dark:bg-slate-800 dark:text-slate-400"
                              data-testid={`movimiento-estado-badge-${movement.publicId}`}
                              data-estado={estadoBadge.estado}
                            >
                              {estadoBadge.etiqueta}
                            </span>
                          )}
                        </div>
                        <div className="text-xs text-slate-500 dark:text-slate-400">
                          {movement.numeroReserva ? `Reserva ${movement.numeroReserva}` : movement.supplierName || "Sin vinculo"}
                        </div>
                      </div>
                    </div>
                  </DataGridCell>
                  <DataGridCell>
                    <div className="text-sm font-medium text-slate-900 dark:text-white">{movement.description}</div>
                    {movement.reference ? (
                      <div className="text-xs text-slate-500 dark:text-slate-400">Ref. {movement.reference}</div>
                    ) : null}
                  </DataGridCell>
                  <DataGridCell>{mapearMetodoMovimiento(movement.method)}</DataGridCell>
                  {/* Columna Moneda: cartelito $/US$ según la moneda del movimiento */}
                  <DataGridCell>
                    <CurrencyBadge currency={movement.currency || "ARS"} size="sm" />
                  </DataGridCell>
                  <DataGridCell align="right">
                    <span className={`text-sm font-bold ${isIncome ? "text-emerald-600 dark:text-emerald-400" : "text-rose-600 dark:text-rose-400"}`}>
                      {isIncome ? "+" : "-"}
                      {formatCurrency(movement.amount, movement.currency || "ARS")}
                    </span>
                  </DataGridCell>
                  <DataGridActionCell>
                    {isManual && isAdmin ? (
                      // H14/Obra 2 (P-9): un movimiento ya anulado O reemplazado no se puede volver
                      // a editar ni anular — los botones quedan APAGADOS, con el motivo siempre a
                      // la vista al lado (nunca solo en un tooltip).
                      <div className="flex flex-col items-end gap-1">
                        <div className="flex items-center gap-1">
                          <button
                            type="button"
                            onClick={() => !botonesApagados && openEdit(movement)}
                            disabled={botonesApagados}
                            aria-disabled={botonesApagados}
                            aria-label="Editar"
                            data-testid={`movimiento-editar-${movement.publicId}`}
                            className={`rounded-lg p-2 transition-colors ${
                              botonesApagados
                                ? "cursor-not-allowed text-slate-300 dark:text-slate-700"
                                : "text-slate-500 hover:bg-slate-100 hover:text-indigo-600 dark:hover:bg-slate-800"
                            }`}
                          >
                            <Pencil className="h-4 w-4" />
                          </button>
                          <button
                            type="button"
                            onClick={() => !botonesApagados && onDeleteManualMovement(movement)}
                            disabled={botonesApagados}
                            aria-disabled={botonesApagados}
                            aria-label="Anular"
                            data-testid={`movimiento-anular-${movement.publicId}`}
                            className={`rounded-lg p-2 transition-colors ${
                              botonesApagados
                                ? "cursor-not-allowed text-slate-300 dark:text-slate-700"
                                : "text-slate-500 hover:bg-slate-100 hover:text-rose-600 dark:hover:bg-slate-800"
                            }`}
                          >
                            <Trash2 className="h-4 w-4" />
                          </button>
                        </div>
                        {estadoBadge && (
                          <span className="text-[9px] text-slate-400 text-right">
                            {estadoBadge.motivoBotonesApagados}
                          </span>
                        )}
                      </div>
                    ) : (
                      // El badge "Anulado"/"Reemplazado" para estos movimientos automáticos ya se
                      // muestra junto al origen (columna Origen, arriba) — no se repite acá (P-16).
                      <span className="text-xs font-semibold uppercase tracking-wider text-slate-400">Automatico</span>
                    )}
                  </DataGridActionCell>
                </DataGridRow>
              );
            })
          )}
        </DataGridBody>
      </DataGrid>

      {movements.length === 0 ? (
        <ListEmptyState
          icon={Landmark}
          title="Caja sin movimientos"
          description="Todavia no hay ingresos o egresos registrados en caja."
          className="md:hidden rounded-xl border border-dashed border-slate-200 bg-slate-50/50 dark:border-slate-800 dark:bg-slate-800/20"
        />
      ) : (
        <MobileRecordList>
          {movements.map((movement) => {
            const isIncome = movement.direction === "Income";
            const isManual = movement.isManual;
            // Obra 2 (firma 2026-07-27): mismo criterio que la tabla desktop — un par de
            // Caja por edición dice "Reemplazado", uno por anulación real sigue diciendo
            // "Anulado".
            const estadoBadge = obtenerEstadoBadgeMovimiento(movement);
            const botonesApagados = debeApagarBotonesMovimiento(movement);

            return (
              <MobileRecordCard
                // H14: misma key estable que la tabla desktop (movement.publicId).
                key={movement.publicId}
                accentSlot={
                  <div className={`rounded-xl p-2 ${isIncome ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-950/30 dark:text-emerald-400" : "bg-rose-50 text-rose-600 dark:bg-rose-950/30 dark:text-rose-400"}`}>
                    {isIncome ? <ArrowDownLeft className="h-4 w-4" /> : <ArrowUpRight className="h-4 w-4" />}
                  </div>
                }
                statusSlot={
                  // Fix del reviewer (2026-07-27): mismo testid estable que la tabla
                  // desktop (con sufijo "-mobile-" para no duplicar el id en el DOM,
                  // ya que ambas vistas conviven ocultas por CSS) + data-estado.
                  estadoBadge ? (
                    <span
                      className="inline-flex items-center rounded-full bg-slate-100 px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide text-slate-500 dark:bg-slate-800 dark:text-slate-400"
                      data-testid={`movimiento-estado-badge-mobile-${movement.publicId}`}
                      data-estado={estadoBadge.estado}
                    >
                      {estadoBadge.etiqueta}
                    </span>
                  ) : null
                }
                title={sourceLabels[movement.sourceType] || movement.sourceType}
                subtitle={`${formatDate(movement.occurredAt)} · ${mapearMetodoMovimiento(movement.method)}`}
                meta={
                  <>
                    <div className="text-xs text-slate-500 dark:text-slate-400">{movement.description}</div>
                    {movement.numeroReserva || movement.supplierName ? (
                      <div className="text-xs text-slate-500 dark:text-slate-400">
                        {movement.numeroReserva ? `Reserva ${movement.numeroReserva}` : movement.supplierName}
                      </div>
                    ) : null}
                    {movement.reference ? (
                      <div className="text-xs text-slate-500 dark:text-slate-400">Ref. {movement.reference}</div>
                    ) : null}
                  </>
                }
                footer={
                  <div className={`text-sm font-black inline-flex items-center gap-1 ${isIncome ? "text-emerald-600 dark:text-emerald-400" : "text-rose-600 dark:text-rose-400"}`}>
                    {isIncome ? "+" : "-"}
                    <CurrencyBadge currency={movement.currency || "ARS"} />
                    {formatCurrency(movement.amount, movement.currency || "ARS", { withSymbol: false })}
                  </div>
                }
                footerActions={
                  isManual && isAdmin ? (
                    // H14/Obra 2 (P-9): mismo criterio que la tabla desktop — botones
                    // apagados + motivo a la vista cuando el movimiento ya fue anulado
                    // o reemplazado por una edición.
                    <div className="flex flex-col items-end gap-1">
                      <div className="flex items-center gap-1">
                        <button
                          type="button"
                          onClick={() => !botonesApagados && openEdit(movement)}
                          disabled={botonesApagados}
                          aria-disabled={botonesApagados}
                          aria-label="Editar"
                          data-testid={`movimiento-editar-mobile-${movement.publicId}`}
                          className={`rounded-lg p-2 transition-colors ${
                            botonesApagados
                              ? "cursor-not-allowed text-slate-300 dark:text-slate-700"
                              : "text-slate-500 hover:bg-slate-100 hover:text-indigo-600 dark:hover:bg-slate-800"
                          }`}
                        >
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button
                          type="button"
                          onClick={() => !botonesApagados && onDeleteManualMovement(movement)}
                          disabled={botonesApagados}
                          aria-disabled={botonesApagados}
                          aria-label="Anular"
                          data-testid={`movimiento-anular-mobile-${movement.publicId}`}
                          className={`rounded-lg p-2 transition-colors ${
                            botonesApagados
                              ? "cursor-not-allowed text-slate-300 dark:text-slate-700"
                              : "text-slate-500 hover:bg-slate-100 hover:text-rose-600 dark:hover:bg-slate-800"
                          }`}
                        >
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                      {estadoBadge && (
                        <span className="text-[9px] text-slate-400 text-right">
                          {estadoBadge.motivoBotonesApagados}
                        </span>
                      )}
                    </div>
                  ) : (
                    <span className="text-[10px] font-semibold uppercase tracking-wider text-slate-400">Automatico</span>
                  )
                }
              />
            );
          })}
        </MobileRecordList>
      )}

      <ManualMovementModal
        open={showModal}
        onClose={closeModal}
        onSubmit={handleSubmit}
        movement={editingMovement}
      />
    </div>
  );
}
