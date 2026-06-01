import { useEffect, useState } from "react";
import { AlertTriangle, DollarSign, FileWarning, GitBranch, Settings2, ShieldAlert, X } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { Button } from "./ui/button";

const defaultSettings = {
  requireFullPaymentForOperativeStatus: true,
  requireFullPaymentForVoucher: true,
  afipInvoiceControlMode: "AllowAgentOverrideWithReason",
  enableUpcomingUnpaidReservationNotifications: true,
  upcomingUnpaidReservationAlertDays: 7,
  // OFF por defecto: el sistema factura solo en pesos hasta que el dueño lo active manualmente.
  // Ver: CreateInvoiceModal.jsx — el selector ARS/USD solo aparece cuando este flag es true.
  enableMultiCurrencyInvoicing: false,
  // OFF por defecto: la UI de reservas muestra el ciclo clásico (sin "Vendida" ni "A liquidar").
  // Con ON se habilitan las pestañas y la botonera del ciclo extendido.
  enableSoldToSettleStates: false,
  // OFF por defecto: ZONA FISCAL. Cuando se prende, el sistema emite una Nota de Débito real
  // a ARCA cada vez que se aprueba una cancelación con penalidad. Requiere que el flujo de
  // cancelación nuevo (EnableNewCancellationFlow) ya esté activo en la base de datos.
  // Si no está activo, el backend responde con un error 400 explicando la pre-condición.
  enableCancellationDebitNote: false,
};

export default function OperationalFinanceSettingsTab() {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [form, setForm] = useState(defaultSettings);

  // Estado del diálogo de confirmación que aparece SOLO cuando el admin
  // intenta prender enableCancellationDebitNote. Apagarlo no requiere confirmación
  // porque no dispara ninguna acción fiscal nueva — solo evita futuros comprobantes.
  const [showDebitNoteConfirmDialog, setShowDebitNoteConfirmDialog] = useState(false);

  // Si la carga inicial (GET) falla, el form queda con los DEFAULTS (todos los flags
  // en false). Guardar en ese estado pisaría la config real del servidor con defaults
  // (clobber). Por eso, ante un error de carga, bloqueamos el guardado: el admin no
  // puede pisar lo que no pudimos leer. Se rehabilita solo con una carga exitosa.
  const [loadError, setLoadError] = useState(false);

  useEffect(() => {
    const loadSettings = async () => {
      setLoading(true);
      setLoadError(false);
      try {
        const response = await api.get("/settings/operational-finance");
        setForm({ ...defaultSettings, ...response });
      } catch (error) {
        console.error("Error loading operational finance settings:", error);
        setLoadError(true);
        showError("No se pudo cargar la configuración operativa. No se puede guardar hasta recargar correctamente.");
      } finally {
        setLoading(false);
      }
    };

    loadSettings();
  }, []);

  const updateField = (field, value) => {
    setForm((current) => ({ ...current, [field]: value }));
  };

  /**
   * Handler especial para el toggle de Nota de Débito por cancelación.
   *
   * Decisión de UX: prender este flag dispara emisión de comprobantes fiscales reales
   * a ARCA (Notas de Débito). Es irreversible en el sentido de que las NDs ya emitidas
   * no se pueden deshacer solo apagando el flag. Por eso pedimos confirmación explícita
   * ANTES de encender, pero no antes de apagar (apagar solo frena futuras emisiones).
   *
   * Si el admin cancela el diálogo, el toggle vuelve a su posición anterior sin tocar
   * el formulario.
   */
  const handleDebitNoteToggleChange = (newValue) => {
    if (newValue === true) {
      // Prender el flag: abrimos el diálogo de confirmación.
      // El form NO se actualiza todavía — solo si el admin confirma.
      setShowDebitNoteConfirmDialog(true);
    } else {
      // Apagar el flag: sin confirmación, actualización directa.
      updateField("enableCancellationDebitNote", false);
    }
  };

  // Se llama cuando el admin confirma en el diálogo de advertencia.
  const handleDebitNoteConfirmed = () => {
    updateField("enableCancellationDebitNote", true);
    setShowDebitNoteConfirmDialog(false);
  };

  // Se llama cuando el admin cancela el diálogo — el toggle NO se enciende.
  const handleDebitNoteDialogCancelled = () => {
    setShowDebitNoteConfirmDialog(false);
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    // Red de seguridad: nunca guardar si la carga inicial falló (evita pisar la
    // config real con los defaults del form). El botón ya viene deshabilitado en
    // ese caso; este guard cubre un submit por Enter u otra vía.
    if (loadError) {
      showError("No se puede guardar: la configuración no se cargó correctamente. Recargá la página.");
      return;
    }
    setSaving(true);
    try {
      await api.put("/settings/operational-finance", {
        ...form,
        upcomingUnpaidReservationAlertDays: Number(form.upcomingUnpaidReservationAlertDays || 0),
      });
      showSuccess("Configuración operativa guardada.");
    } catch (error) {
      showError(error.message || "No se pudo guardar la configuración.");
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
    <form onSubmit={handleSubmit} className="space-y-6">
      <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
        <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center gap-3 bg-slate-50/50 dark:bg-slate-800/20">
          <div className="p-2 bg-indigo-100 dark:bg-indigo-900/30 rounded-lg text-indigo-600 dark:text-indigo-400">
            <Settings2 className="h-5 w-5" />
          </div>
          <div>
            <h3 className="font-semibold text-slate-900 dark:text-white">Operativa, Cobranzas y Facturación</h3>
            <p className="text-xs text-slate-500">Reglas económicas que gobiernan operativo, vouchers, AFIP y alertas.</p>
          </div>
        </div>

        <div className="p-6 space-y-8">
          <div className="grid gap-6 md:grid-cols-2">
            <label className="rounded-2xl border border-slate-200 dark:border-slate-800 p-4 flex items-start gap-3">
              <input
                type="checkbox"
                checked={form.requireFullPaymentForOperativeStatus}
                onChange={(event) => updateField("requireFullPaymentForOperativeStatus", event.target.checked)}
                className="mt-1 rounded border-slate-300"
                disabled={loading}
              />
              <div>
                <div className="text-sm font-semibold text-slate-900 dark:text-white">Exigir pago total para pasar a Operativo</div>
                <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                  Si está activo, una reserva con deuda no puede pasar al estado operativo.
                </div>
              </div>
            </label>

            <label className="rounded-2xl border border-slate-200 dark:border-slate-800 p-4 flex items-start gap-3">
              <input
                type="checkbox"
                checked={form.requireFullPaymentForVoucher}
                onChange={(event) => updateField("requireFullPaymentForVoucher", event.target.checked)}
                className="mt-1 rounded border-slate-300"
                disabled={loading}
              />
              <div>
                <div className="text-sm font-semibold text-slate-900 dark:text-white">Exigir pago total para emitir voucher</div>
                <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                  Afecta tanto el PDF como el envío del voucher por WhatsApp.
                </div>
              </div>
            </label>
          </div>

          {/* ================================================================
              Bloque de feature flags de comportamiento y fiscales.
              Estos flags se guardan en la misma llamada PUT que el resto de la
              configuración. Son patch-like: el backend solo modifica los que
              vienen con valor, así que mandar el objeto completo no pisa nada
              que no deba pisarse.
              ================================================================ */}
          <div className="space-y-4">
            <h4 className="text-sm font-semibold text-slate-700 dark:text-slate-300 uppercase tracking-wider">
              Funciones avanzadas
            </h4>

            {/* Bloque de facturación en moneda extranjera.
                Solo afecta el modal CreateInvoice (muestra/oculta el selector ARS/USD).
                Antes de prender esto en producción hay que tener homologación ARCA aprobada
                y confirmación del contador. El backend también valida con su propio flag. */}
            <label className="rounded-2xl border border-slate-200 dark:border-slate-800 p-4 flex items-start gap-3">
              <input
                type="checkbox"
                checked={form.enableMultiCurrencyInvoicing}
                onChange={(event) => updateField("enableMultiCurrencyInvoicing", event.target.checked)}
                className="mt-1 rounded border-slate-300"
                disabled={loading}
                data-testid="toggle-multicurrency"
                aria-label="Habilitar facturación en moneda extranjera (dólares)"
              />
              <div>
                <div className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
                  <DollarSign className="w-4 h-4 text-emerald-500" aria-hidden="true" />
                  Habilitar facturación en moneda extranjera (dólares)
                </div>
                <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                  Al activarlo, aparece el selector Pesos/Dólares al crear facturas y se habilita
                  el campo de tipo de cambio manual.{" "}
                  <span className="font-semibold text-amber-600 dark:text-amber-400">
                    Activá esto solo cuando hayas probado en homologación y tu contador lo confirme.
                    Mientras esté apagado, el sistema factura solo en pesos como siempre.
                  </span>
                </div>
              </div>
            </label>

            {/* Ciclo extendido de estados de reserva (Vendida / A liquidar).
                Flag de comportamiento puro: no emite comprobantes fiscales, no
                bloquea nada. Con ON aparecen las pestañas "Vendidas" y "A liquidar"
                en la lista de reservas y la botonera de detalle cambia. */}
            <label className="rounded-2xl border border-slate-200 dark:border-slate-800 p-4 flex items-start gap-3">
              <input
                type="checkbox"
                checked={form.enableSoldToSettleStates}
                onChange={(event) => updateField("enableSoldToSettleStates", event.target.checked)}
                className="mt-1 rounded border-slate-300"
                disabled={loading}
                data-testid="toggle-sold-to-settle"
                aria-label="Habilitar ciclo extendido de estados de reserva (Vendida / A liquidar)"
              />
              <div>
                <div className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
                  <GitBranch className="w-4 h-4 text-indigo-500" aria-hidden="true" />
                  Ciclo extendido de estados de reserva (Vendida / A liquidar)
                </div>
                <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                  Al activarlo, el ciclo de vida de las reservas pasa a tener dos etapas
                  nuevas: <span className="font-medium">Vendida</span> (antes de la confirmación del operador) y{" "}
                  <span className="font-medium">A liquidar</span> (apartar manualmente las reservas que
                  requieran liquidación con el operador post-viaje). Con el flag apagado, el sistema
                  funciona exactamente como antes.
                </div>
              </div>
            </label>
          </div>

          {/* ================================================================
              ZONA PELIGROSA: Nota de Débito por penalidad en cancelaciones.
              Este flag dispara emisión de comprobantes fiscales reales a ARCA.
              Se muestra separado del resto con estilo de advertencia para que
              sea imposible activarlo "de casualidad".
              ================================================================ */}
          <div className="rounded-2xl border border-red-200 dark:border-red-900/40 overflow-hidden">
            <div className="px-4 py-3 bg-red-50 dark:bg-red-900/20 flex items-center gap-2 border-b border-red-200 dark:border-red-900/40">
              <FileWarning className="w-4 h-4 text-red-600 dark:text-red-400 flex-shrink-0" aria-hidden="true" />
              <span className="text-sm font-semibold text-red-800 dark:text-red-300">
                Zona fiscal — requiere homologación ARCA y firma del contador
              </span>
            </div>

            <div className="p-4">
              <label className="flex items-start gap-3">
                <input
                  type="checkbox"
                  checked={form.enableCancellationDebitNote}
                  onChange={(event) => handleDebitNoteToggleChange(event.target.checked)}
                  className="mt-1 rounded border-slate-300"
                  disabled={loading}
                  data-testid="toggle-cancellation-debit-note"
                  aria-label="Habilitar nota de débito por penalidad en cancelaciones"
                  aria-describedby="debit-note-warning"
                />
                <div>
                  <div className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
                    Nota de débito por penalidad en cancelaciones
                  </div>
                  <div
                    id="debit-note-warning"
                    className="text-xs text-slate-600 dark:text-slate-400 mt-1 space-y-1"
                  >
                    <p>
                      Al activarlo, cada vez que se aprueba una cancelación con penalidad, el sistema
                      emite automáticamente una{" "}
                      <span className="font-semibold">Nota de Débito real a ARCA/AFIP</span>. Ese
                      comprobante tiene CAE y no se puede borrar.
                    </p>
                    <p className="font-semibold text-red-700 dark:text-red-400">
                      Antes de prender este flag: necesitás tener el flujo de cancelación nuevo activo
                      (EnableNewCancellationFlow), homologación ARCA aprobada y la confirmación de tu
                      contador. Si falta alguna de estas condiciones, el sistema rechazará la activación
                      con un mensaje explicativo.
                    </p>
                  </div>
                </div>
              </label>
            </div>
          </div>

          <div className="grid gap-6 md:grid-cols-2">
            <div className="rounded-2xl border border-slate-200 dark:border-slate-800 p-4">
              <div className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
                <ShieldAlert className="w-4 h-4 text-amber-500" />
                Control de AFIP
              </div>
              <p className="text-xs text-slate-500 dark:text-slate-400 mt-1 mb-4">
                Define si AFIP requiere pago total o si el agente puede tener la última palabra con motivo obligatorio.
              </p>
              <select
                value={form.afipInvoiceControlMode}
                onChange={(event) => updateField("afipInvoiceControlMode", event.target.value)}
                className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2 text-sm"
                disabled={loading}
              >
                <option value="FullPaymentRequired">Pago total obligatorio</option>
                <option value="AllowAgentOverrideWithReason">Permitir override del agente con motivo</option>
              </select>
            </div>

            <div className="rounded-2xl border border-slate-200 dark:border-slate-800 p-4 space-y-4">
              <label className="flex items-start gap-3">
                <input
                  type="checkbox"
                  checked={form.enableUpcomingUnpaidReservationNotifications}
                  onChange={(event) => updateField("enableUpcomingUnpaidReservationNotifications", event.target.checked)}
                  className="mt-1 rounded border-slate-300"
                  disabled={loading}
                />
                <div>
                  <div className="text-sm font-semibold text-slate-900 dark:text-white">Alertas por reservas próximas con deuda</div>
                  <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                    Notifica al responsable de la reserva y a los administradores.
                  </div>
                </div>
              </label>

              <div>
                <label className="block text-xs font-semibold uppercase tracking-wider text-slate-500 mb-1.5">
                  Días previos para alertar
                </label>
                <input
                  type="number"
                  min="1"
                  max="60"
                  value={form.upcomingUnpaidReservationAlertDays}
                  onChange={(event) => updateField("upcomingUnpaidReservationAlertDays", event.target.value)}
                  className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2 text-sm"
                  disabled={loading}
                />
              </div>
            </div>
          </div>

          <div className="rounded-2xl border border-amber-200 bg-amber-50 dark:border-amber-900/40 dark:bg-amber-900/20 p-4 text-sm text-amber-900 dark:text-amber-200">
            <div className="flex items-start gap-3">
              <AlertTriangle className="w-5 h-5 mt-0.5 flex-shrink-0" />
              <div className="space-y-1">
                <div className="font-semibold">Reglas duras de esta etapa</div>
                <p>
                  El override del agente aplica solo a AFIP. Operativo y voucher siguen bloqueados si la reserva tiene deuda.
                </p>
              </div>
            </div>
          </div>
        </div>

        <div className="px-6 py-4 bg-slate-50 dark:bg-slate-900/50 border-t border-slate-100 dark:border-slate-800 flex justify-end">
          <Button type="submit" disabled={saving || loading || loadError} className="bg-indigo-600 hover:bg-indigo-700 text-white rounded-lg px-6">
            {saving ? "Guardando..." : "Guardar configuración"}
          </Button>
        </div>
      </div>
    </form>

    {/* ================================================================
        Diálogo de confirmación para prender la Nota de Débito fiscal.

        Decisión de UX: usamos un overlay modal simple (no una librería)
        para mantener la dependencia mínima y seguir el estilo del resto
        del proyecto. El foco va al botón "Cancelar" por defecto
        (el más seguro) — así si el admin apretó Enter por error, la
        acción predeterminada es NO activar.

        No usamos el componente Button para el botón rojo porque queremos
        type="button" explícito y no heredar estilos del form padre.
        ================================================================ */}
    {showDebitNoteConfirmDialog && (
      <div
        className="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
        role="dialog"
        aria-modal="true"
        aria-labelledby="debit-note-dialog-title"
        aria-describedby="debit-note-dialog-desc"
      >
        <div className="relative bg-white dark:bg-slate-900 rounded-2xl shadow-xl max-w-md w-full mx-4 p-6 space-y-4">
          {/* Botón de cierre en la esquina — alternativa de escape sin teclado */}
          <button
            type="button"
            onClick={handleDebitNoteDialogCancelled}
            className="absolute top-4 right-4 text-slate-400 hover:text-slate-600 dark:hover:text-slate-200"
            aria-label="Cerrar diálogo"
          >
            <X className="w-5 h-5" aria-hidden="true" />
          </button>

          <div className="flex items-center gap-3">
            <div className="p-2 bg-red-100 dark:bg-red-900/30 rounded-lg">
              <FileWarning className="w-6 h-6 text-red-600 dark:text-red-400" aria-hidden="true" />
            </div>
            <h2
              id="debit-note-dialog-title"
              className="text-base font-semibold text-slate-900 dark:text-white"
            >
              Estás por habilitar la Nota de Débito fiscal
            </h2>
          </div>

          <div id="debit-note-dialog-desc" className="text-sm text-slate-600 dark:text-slate-300 space-y-3">
            <p>
              Esto habilita la <strong>emisión automática de Notas de Débito reales a AFIP/ARCA</strong>{" "}
              cada vez que se aprueba una cancelación con penalidad.
            </p>
            <p>
              Esos comprobantes tienen CAE y <strong>no se pueden borrar</strong> — quedan en el historial
              fiscal de la agencia.
            </p>
            <div className="rounded-xl border border-amber-200 bg-amber-50 dark:border-amber-800 dark:bg-amber-900/20 px-4 py-3 text-amber-800 dark:text-amber-200 text-xs space-y-1">
              <p className="font-semibold">Antes de continuar, confirmá que:</p>
              <ul className="list-disc list-inside space-y-0.5">
                <li>El flujo de cancelación nuevo (FC1.2) ya está activo en la base de datos.</li>
                <li>Realizaste la homologación de Notas de Débito con ARCA.</li>
                <li>Tu contador revisó y aprobó la configuración.</li>
              </ul>
            </div>
            <p className="text-xs text-slate-500 dark:text-slate-400">
              Si alguna de esas condiciones no se cumple, el sistema te va a mostrar un error
              al guardar — sin emitir ningún comprobante.
            </p>
          </div>

          <div className="flex gap-3 pt-2">
            {/* Botón cancelar primero: es la acción más segura y recibe el foco inicial */}
            <button
              type="button"
              onClick={handleDebitNoteDialogCancelled}
              autoFocus
              className="flex-1 rounded-xl border border-slate-300 dark:border-slate-600 px-4 py-2 text-sm font-medium text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800"
            >
              Cancelar, no activar
            </button>
            <button
              type="button"
              onClick={handleDebitNoteConfirmed}
              className="flex-1 rounded-xl bg-red-600 hover:bg-red-700 px-4 py-2 text-sm font-medium text-white"
              data-testid="confirm-debit-note-activation"
            >
              Sí, activar Nota de Débito
            </button>
          </div>
        </div>
      </div>
    )}
    </>
  );
}
