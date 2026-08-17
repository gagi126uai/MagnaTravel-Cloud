import { useEffect, useState } from "react";
import { AlertTriangle, CalendarClock, DollarSign, FileWarning, GitBranch, IdCard, Settings2, ShieldAlert, TrendingUp, X } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { Button } from "./ui/button";
import { TREASURY_FX_ASSUMED_BY, OPCIONES_ASUME_AJUSTE_DOLAR_AGENCIA } from "../lib/treasuryFxAssumedBy";

const defaultSettings = {
  requireFullPaymentForOperativeStatus: true,
  requireFullPaymentForVoucher: true,
  afipInvoiceControlMode: "AllowAgentOverrideWithReason",
  enableUpcomingUnpaidReservationNotifications: true,
  upcomingUnpaidReservationAlertDays: 7,
  // Spec firmada 2026-08-06 (§4.5, M-8): "el saldo tiene que estar completo N días antes de
  // la salida". Default 21, un solo número para todas las reservas — nadie carga fechas a
  // mano en ninguna reserva (mismo endpoint de configuración operativa, contrato E).
  fullPaymentDueDaysBeforeDeparture: 21,
  // OFF por defecto: el sistema factura solo en pesos hasta que el dueño lo active manualmente.
  // Ver: EmitirFacturaInline.jsx — el selector ARS/USD solo aparece cuando este flag es true.
  enableMultiCurrencyInvoicing: false,
  // ADR-020: enableSoldToSettleStates fue eliminado. El ciclo nuevo es directo y sin flags.
  // OFF por defecto: ZONA FISCAL. Cuando se prende, el sistema emite una Nota de Débito real
  // a ARCA cada vez que se aprueba una cancelación con penalidad. Requiere que el flujo de
  // cancelación nuevo (EnableNewCancellationFlow) ya esté activo en la base de datos.
  // Si no está activo, el backend responde con un error 400 explicando la pre-condición.
  enableCancellationDebitNote: false,
  // OFF por defecto: la campanita no muestra avisos de fechas límite. Con ON, cada vendedor
  // recibe avisos de señas y emisión pendientes de sus reservas; los admins ven todos.
  enableServiceDeadlineAlerts: false,
  // Semáforo de DNI vencido para cabotaje (2026-08-03, spec firmada D): OFF por defecto. Con
  // ON, la solapa Pasajeros marca en rojo al pasajero cuyo DNI se vence antes de un viaje
  // Nacional. Ver PassengerList.jsx (chip) y DniExpiryRules (motor).
  enableDomesticDniExpiryAlert: false,
  // Días de anticipación para los avisos de fechas límite (el DTO valida Range(1,60): fuera de rango = 400).
  serviceDeadlineAlertDays: 7,
  // OFF por defecto: el sistema no calcula comisiones para vendedores.
  // Con ON, calcula un % de la ganancia de cada reserva como comisión del vendedor que la cargó.
  // La comisión se gana al cobrar (no al confirmar la reserva).
  enableSellerCommissions: false,
  // % de ganancia que le corresponde al vendedor como comisión. Rango: 0-100.
  // El backend valida el rango; este default no se envía hasta que el usuario lo edite.
  sellerCommissionPercent: 0,
  // G6 (2026-06-24): días de caducidad de presupuestos. 0 = no caduca nunca.
  // El backend lo valida (Range 0..3650); el frontend lo guarda como número.
  budgetExpirationDays: 0,
  // G6 (2026-06-24): días de caducidad de cotizaciones. 0 = no caduca nunca.
  // Eje SEPARADO del de presupuesto — se configura de forma independiente.
  quotationExpirationDays: 0,
  // ADR-044 T4 (2026-07-10): quién asume por defecto el "Ajuste por el dólar" de las
  // multas del operador (regla dura: la frase "diferencia de cambio" nunca se muestra;
  // el rótulo visible es siempre "Ajuste por el dólar"). Default: el cliente — cada
  // operador puede apartarse con su propio campo opcional (ficha del operador).
  treasuryFxAssumedByDefault: TREASURY_FX_ASSUMED_BY.Client,
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
      // Fix 2026-08-07: `enableCatalogFindOrCreate` ya no existe en el backend (M-10). Si
      // `form` todavía lo arrastra de una carga vieja, lo sacamos ANTES de mandar el PUT
      // en vez de perpetuarlo en cada guardado.
      const { enableCatalogFindOrCreate: _llaveTarifarioMuerta, ...formSinLlaveMuerta } = form;
      await api.put("/settings/operational-finance", {
        ...formSinLlaveMuerta,
        upcomingUnpaidReservationAlertDays: Number(form.upcomingUnpaidReservationAlertDays || 0),
        // M-8: mismo criterio que el resto de los campos numéricos de este form — se manda
        // como número entero, el backend valida el rango (ver contrato E del brief).
        fullPaymentDueDaysBeforeDeparture: Number(form.fullPaymentDueDaysBeforeDeparture || 21),
        // serviceDeadlineAlertDays: convertido a número. Ojo: el DTO valida [Range(1,60)] →
        // fuera de rango el server devuelve 400 con mensaje (no clamp silencioso); el
        // min/max del input cubre el caso típico.
        serviceDeadlineAlertDays: Number(form.serviceDeadlineAlertDays || 7),
        // sellerCommissionPercent: convertido a número. El backend valida Range(0,100).
        sellerCommissionPercent: Number(form.sellerCommissionPercent ?? 0),
        // G6 (2026-06-24): días de caducidad. 0 = no caduca nunca (según spec).
        // Se guardan como número entero; el backend valida Range(0,3650).
        budgetExpirationDays: Number(form.budgetExpirationDays || 0),
        quotationExpirationDays: Number(form.quotationExpirationDays || 0),
        // ADR-044 T4: enum como INT (Client=0, Agency=1) — el backend no tiene
        // JsonStringEnumConverter, mismo criterio que el resto de los enums del sistema.
        treasuryFxAssumedByDefault: Number(form.treasuryFxAssumedByDefault),
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
      <div className="bg-white dark:bg-slate-900 rounded-[14px] border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
        <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center gap-3 bg-slate-50/50 dark:bg-slate-800/20">
          <div className="p-2 bg-blue-100 dark:bg-blue-900/30 rounded-[10px] text-blue-600 dark:text-blue-400">
            <Settings2 className="h-5 w-5" />
          </div>
          <div>
            <h3 className="font-semibold text-slate-900 dark:text-white">Operativa, Cobranzas y Facturación</h3>
            <p className="text-xs text-slate-500">Reglas de dinero de tu agencia: cuándo se libera una reserva, cuándo se emite un voucher y cómo se factura.</p>
          </div>
        </div>

        <div className="p-6 space-y-8">
          <div className="grid gap-6 md:grid-cols-2">
            <label className="rounded-[14px] border border-slate-200 dark:border-slate-800 p-4 flex items-start gap-3">
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

            <label className="rounded-[14px] border border-slate-200 dark:border-slate-800 p-4 flex items-start gap-3">
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
            <label className="rounded-[14px] border border-slate-200 dark:border-slate-800 p-4 flex items-start gap-3">
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
                  Permite facturar a tus clientes en dólares además de en pesos, eligiendo
                  la cotización del día.{" "}
                  <span className="font-semibold text-amber-600 dark:text-amber-400">
                    Activalo solo si tu contador confirma que tu agencia puede facturar en dólares.
                    Mientras esté apagado, todas las facturas salen en pesos, como hasta ahora.
                  </span>
                </div>
              </div>
            </label>

            {/* ADR-020: la llave "Ciclo extendido (Vendida / A liquidar)" fue eliminada.
                El ciclo nuevo (Cotizacion → Presupuesto → En gestion → Confirmada → ...)
                es el UNICO ciclo y no tiene flag. Quedo el estado "A liquidar" como desvio
                opcional sin necesidad de activacion. */}

            {/* Tarifario find-or-create: la llave DESAPARECIÓ de acá (spec firmada 2026-08-06,
                P8=A / M-10) Y del backend. El buscador que aprende de las ventas ya no se
                apaga para nadie: sale directo para todos los que pueden ver el Tarifario.
                Fix 2026-08-07: el submit YA NO manda `enableCatalogFindOrCreate` (el campo
                puede seguir viviendo en `form` si el GET inicial todavía lo trae de una
                config vieja, pero no lo reenviamos) — ver handleSubmit más abajo y
                ReservaDetailPage.jsx (ya no lee este flag en ningún lado). */}

            {/* Avisos de próximos inicios.
                Con ON, la campanita avisa unos días antes de que arranque cada reserva.
                El backend calcula desde la primera fecha de inicio de los servicios (firstStartDate). */}
            <div
              className="rounded-[14px] border border-slate-200 dark:border-slate-800 p-4 space-y-4"
              aria-label="Avisos de próximos inicios"
            >
              <label className="flex items-start gap-3">
                <input
                  type="checkbox"
                  checked={form.enableServiceDeadlineAlerts}
                  onChange={(event) => updateField("enableServiceDeadlineAlerts", event.target.checked)}
                  className="mt-1 rounded border-slate-300"
                  disabled={loading}
                  data-testid="toggle-service-deadline-alerts"
                  aria-label="Avisos de próximos inicios"
                />
                <div>
                  <div className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
                    <CalendarClock className="w-4 h-4 text-amber-500" aria-hidden="true" />
                    Avisos de próximos inicios
                  </div>
                  <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                    La campanita avisa unos días antes de que empiece cada reserva. Cada vendedor ve las suyas; los admins, todas.
                  </div>
                </div>
              </label>

              {/* Campo numérico de días de anticipación — siempre habilitado (independiente del toggle) */}
              <div>
                <label
                  htmlFor="service-deadline-alert-days"
                  className="block text-xs font-semibold uppercase tracking-wider text-slate-500 mb-1.5"
                >
                  Días de anticipación del aviso
                </label>
                <input
                  id="service-deadline-alert-days"
                  type="number"
                  min="1"
                  max="60"
                  value={form.serviceDeadlineAlertDays}
                  onChange={(event) => updateField("serviceDeadlineAlertDays", event.target.value)}
                  className="w-full rounded-[10px] border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2 text-sm"
                  disabled={loading}
                  data-testid="input-deadline-alert-days"
                />
              </div>
            </div>

            {/* Semáforo de DNI vencido para cabotaje (2026-08-03, spec firmada D).
                Con ON, la solapa Pasajeros de la reserva marca en rojo al pasajero cuyo DNI
                se vence antes de un viaje Nacional. Solo avisa; nunca frena nada (P-11). */}
            <label className="rounded-[14px] border border-slate-200 dark:border-slate-800 p-4 flex items-start gap-3">
              <input
                type="checkbox"
                checked={form.enableDomesticDniExpiryAlert}
                onChange={(event) => updateField("enableDomesticDniExpiryAlert", event.target.checked)}
                className="mt-1 rounded border-slate-300"
                disabled={loading}
                data-testid="toggle-domestic-dni-expiry-alert"
                aria-label="Avisar cuando el DNI de un pasajero esté vencido para un viaje dentro del país"
              />
              <div>
                <div className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
                  <IdCard className="w-4 h-4 text-rose-500" aria-hidden="true" />
                  Avisar cuando el DNI de un pasajero esté vencido para un viaje dentro del país
                </div>
                <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                  En la solapa Pasajeros de la reserva, el pasajero cuyo DNI se vence antes del
                  viaje queda marcado en rojo. Para volar dentro del país piden DNI vigente (o
                  pasaporte vigente). Solo avisa; nunca frena nada. Apagado, no se muestra ningún aviso.
                </div>
              </div>
            </label>
          </div>

            {/* Comisiones de vendedor.
                Con ON, el sistema calcula un % de la ganancia de cada reserva y se lo
                acredita al vendedor que la cargó, cuando se cobra. El % se configura acá.
                La pantalla de consulta (solo admin) está en el menú lateral → "Comisiones". */}
            <div
              className="rounded-[14px] border border-slate-200 dark:border-slate-800 p-4 space-y-4"
              aria-label="Comisiones a vendedores"
            >
              <label className="flex items-start gap-3">
                <input
                  type="checkbox"
                  checked={form.enableSellerCommissions}
                  onChange={(event) => updateField("enableSellerCommissions", event.target.checked)}
                  className="mt-1 rounded border-slate-300"
                  disabled={loading}
                  data-testid="toggle-seller-commissions"
                  aria-label="Comisiones a vendedores"
                />
                <div>
                  <div className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
                    <TrendingUp className="w-4 h-4 text-blue-500" aria-hidden="true" />
                    Comisiones a vendedores
                  </div>
                  <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                    El sistema calcula una comisión para el vendedor de cada reserva, como un % de la ganancia.
                    Se gana al cobrar. Apagado, no se calcula nada.
                  </div>
                </div>
              </label>

              {/* Campo de % — visible siempre para que el admin lo configure antes de activar.
                  El backend valida que esté entre 0 y 100. */}
              <div>
                <label
                  htmlFor="seller-commission-percent"
                  className="block text-xs font-semibold uppercase tracking-wider text-slate-500 mb-1.5"
                >
                  % de comisión sobre la ganancia
                </label>
                <div className="relative max-w-[10rem]">
                  <input
                    id="seller-commission-percent"
                    type="number"
                    min="0"
                    max="100"
                    step="0.1"
                    value={form.sellerCommissionPercent}
                    onChange={(event) => updateField("sellerCommissionPercent", event.target.value)}
                    className="w-full rounded-[10px] border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2 pr-8 text-sm"
                    disabled={loading}
                    data-testid="input-seller-commission-percent"
                    aria-describedby="seller-commission-percent-hint"
                  />
                  {/* Sufijo "%" visual, no funcional */}
                  <span
                    className="pointer-events-none absolute inset-y-0 right-0 flex items-center pr-3 text-slate-400 text-sm"
                    aria-hidden="true"
                  >
                    %
                  </span>
                </div>
                <p
                  id="seller-commission-percent-hint"
                  className="mt-1 text-xs text-slate-400 dark:text-slate-500"
                >
                  Ej.: 10 = el vendedor gana el 10% de la ganancia de cada reserva que cobre.
                </p>
              </div>
            </div>

          {/* ================================================================
              G6 (2026-06-24): Caducidad automática de presupuestos y cotizaciones.
              SIN interruptor — los dos casilleros numéricos son directamente el control.
              0 = no caduca nunca (aclarado con textito al lado de cada casillero).
              Los ejes son INDEPENDIENTES: se puede tener cotización que caduca y
              presupuesto que no, o viceversa.
              Spec: guia-ux-gaston.md sección "TEMA B: Configuración de caducidad" (2026-06-24).
              ================================================================ */}
          <div
            className="rounded-[14px] border border-slate-200 dark:border-slate-800 p-4 space-y-4"
            aria-label="Caducidad de presupuestos y cotizaciones"
          >
            <div>
              <p className="text-sm font-semibold text-slate-900 dark:text-white">
                Caducidad de presupuestos y cotizaciones
              </p>
              <p className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                Si no avanzan, pasan solas a &quot;Perdida&quot;.
              </p>
            </div>

            {/* Casillero 1: Cotización */}
            <div className="flex items-center gap-3 flex-wrap">
              <label
                htmlFor="quotation-expiration-days"
                className="text-sm font-medium text-slate-700 dark:text-slate-300 whitespace-nowrap"
              >
                Caducar cotización a los
              </label>
              <input
                id="quotation-expiration-days"
                type="number"
                min="0"
                max="3650"
                value={form.quotationExpirationDays}
                onChange={(event) => updateField("quotationExpirationDays", event.target.value)}
                className="w-24 rounded-[10px] border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2 text-sm text-right"
                disabled={loading}
                data-testid="input-quotation-expiration-days"
              />
              <span className="text-sm text-slate-700 dark:text-slate-300">días</span>
              <span className="text-xs text-slate-400 dark:text-slate-500">0 = no caduca nunca</span>
            </div>

            {/* Casillero 2: Presupuesto */}
            <div className="flex items-center gap-3 flex-wrap">
              <label
                htmlFor="budget-expiration-days"
                className="text-sm font-medium text-slate-700 dark:text-slate-300 whitespace-nowrap"
              >
                Caducar presupuesto a los
              </label>
              <input
                id="budget-expiration-days"
                type="number"
                min="0"
                max="3650"
                value={form.budgetExpirationDays}
                onChange={(event) => updateField("budgetExpirationDays", event.target.value)}
                className="w-24 rounded-[10px] border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2 text-sm text-right"
                disabled={loading}
                data-testid="input-budget-expiration-days"
              />
              <span className="text-sm text-slate-700 dark:text-slate-300">días</span>
              <span className="text-xs text-slate-400 dark:text-slate-500">0 = no caduca nunca</span>
            </div>
          </div>

          {/* ================================================================
              ADR-044 T4 (2026-07-10): "Ajuste por el dólar" en las multas.
              Un solo control (radio), sin toggle de encender/apagar — coherente con
              el estilo del bloque de caducidad de arriba. La etiqueta visible NUNCA
              dice "diferencia de cambio" (regla dura de multimoneda, 2026-06-09).
              Cada operador puede apartarse de este default desde su propia ficha
              (campo opcional, ver la solapa "Datos" de cada operador).
              ================================================================ */}
          <div
            className="rounded-[14px] border border-slate-200 dark:border-slate-800 p-4 space-y-3"
            aria-label="Ajuste por el dólar en las multas"
          >
            <div>
              <p className="text-sm font-semibold text-slate-900 dark:text-white">
                Ajuste por el dólar en las multas
              </p>
              <p className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                ¿Quién lo asume por defecto? Cada operador puede tener su propia excepción.
              </p>
            </div>
            <div className="flex flex-wrap gap-4">
              {OPCIONES_ASUME_AJUSTE_DOLAR_AGENCIA.map((opcion) => (
                <label key={opcion.value} className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                  <input
                    type="radio"
                    name="treasuryFxAssumedByDefault"
                    checked={Number(form.treasuryFxAssumedByDefault) === opcion.value}
                    onChange={() => updateField("treasuryFxAssumedByDefault", opcion.value)}
                    disabled={loading}
                    data-testid={`treasury-fx-assumed-by-${opcion.value}`}
                  />
                  {opcion.label}
                </label>
              ))}
            </div>
          </div>

          {/* ================================================================
              ZONA PELIGROSA: Nota de Débito por penalidad en cancelaciones.
              Este flag dispara emisión de comprobantes fiscales reales a ARCA.
              Se muestra separado del resto con estilo de advertencia para que
              sea imposible activarlo "de casualidad".
              ================================================================ */}
          <div className="rounded-[14px] border border-red-200 dark:border-red-900/40 overflow-hidden">
            <div className="px-4 py-3 bg-red-50 dark:bg-red-900/20 flex items-center gap-2 border-b border-red-200 dark:border-red-900/40">
              <FileWarning className="w-4 h-4 text-red-600 dark:text-red-400 flex-shrink-0" aria-hidden="true" />
              <span className="text-sm font-semibold text-red-800 dark:text-red-300">
                Facturación automática — emite comprobantes reales
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
                      Cuando cancelás una reserva con penalidad, el sistema le factura esa penalidad
                      al cliente con una <span className="font-semibold">nota de débito</span>, sin que
                      tengas que cargarla a mano.
                    </p>
                    <p className="font-semibold text-red-700 dark:text-red-400">
                      Emite comprobantes fiscales de verdad, que después no se pueden borrar.
                      Activalo solo cuando tengas todo verificado con tu contador.
                    </p>
                  </div>
                </div>
              </label>
            </div>
          </div>

          <div className="grid gap-6 md:grid-cols-2">
            <div className="rounded-[14px] border border-slate-200 dark:border-slate-800 p-4">
              <div className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
                <ShieldAlert className="w-4 h-4 text-amber-500" />
                Facturar con deuda pendiente
              </div>
              <p className="text-xs text-slate-500 dark:text-slate-400 mt-1 mb-4">
                Elegí si una reserva con saldo se puede facturar igual. Podés exigir el pago total o dejar que el vendedor lo decida, siempre con un motivo.
              </p>
              <select
                value={form.afipInvoiceControlMode}
                onChange={(event) => updateField("afipInvoiceControlMode", event.target.value)}
                className="w-full rounded-[10px] border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2 text-sm"
                disabled={loading}
              >
                <option value="FullPaymentRequired">Exigir pago total para facturar</option>
                <option value="AllowAgentOverrideWithReason">Permitir facturar con deuda (el vendedor indica el motivo)</option>
              </select>
            </div>

            {/* Spec firmada 2026-08-06 (§4.5, M-8): "El saldo tiene que estar completo N días
                antes de la salida" (default 21) decide cuándo una reserva aparece VENCIDA en
                la lista de deudores + el aviso de la campanita. Es un número DISTINTO del de
                abajo (días para el aviso de "Debe — no viaja", ya firmado el 2026-06-21 y que
                no se toca): quedan separados a propósito, con etiquetas bien distintas, para
                que no se lean como la misma cosa (detalle abierto D1 de la spec). */}
            <div className="rounded-[14px] border border-slate-200 dark:border-slate-800 p-4 space-y-4">
              <div>
                <p className="text-sm font-semibold text-slate-900 dark:text-white">Cobranzas</p>
              </div>

              <div className="flex flex-wrap items-center gap-3">
                <label htmlFor="full-payment-due-days" className="text-sm text-slate-700 dark:text-slate-300">
                  El saldo tiene que estar completo
                </label>
                <input
                  id="full-payment-due-days"
                  type="number"
                  min="1"
                  max="365"
                  value={form.fullPaymentDueDaysBeforeDeparture}
                  onChange={(event) => updateField("fullPaymentDueDaysBeforeDeparture", event.target.value)}
                  className="w-20 rounded-[10px] border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2 text-sm text-right"
                  disabled={loading}
                  data-testid="input-full-payment-due-days"
                />
                <span className="text-sm text-slate-700 dark:text-slate-300">días antes de la salida.</span>
              </div>

              <div className="border-t border-slate-100 dark:border-slate-800 pt-4 space-y-3">
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
                    className="w-full rounded-[10px] border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2 text-sm"
                    disabled={loading}
                  />
                </div>
              </div>
            </div>
          </div>

          <div className="rounded-[14px] border border-amber-200 bg-amber-50 dark:border-amber-900/40 dark:bg-amber-900/20 p-4 text-sm text-amber-900 dark:text-amber-200">
            <div className="flex items-start gap-3">
              <AlertTriangle className="w-5 h-5 mt-0.5 flex-shrink-0" />
              <div className="space-y-1">
                <div className="font-semibold">Tené en cuenta</div>
                <p>
                  La excepción para facturar con deuda solo aplica a la factura. Pasar la reserva a operativo y emitir el voucher siguen bloqueados mientras haya saldo pendiente.
                </p>
              </div>
            </div>
          </div>
        </div>

        <div className="px-6 py-4 bg-slate-50 dark:bg-slate-900/50 border-t border-slate-100 dark:border-slate-800 flex justify-end">
          <Button type="submit" disabled={saving || loading || loadError} className="px-6">
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

        ================================================================ */}
    {showDebitNoteConfirmDialog && (
      <div
        className="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
        role="dialog"
        aria-modal="true"
        aria-labelledby="debit-note-dialog-title"
        aria-describedby="debit-note-dialog-desc"
      >
        <div className="relative bg-white dark:bg-slate-900 rounded-[14px] shadow-xl max-w-md w-full mx-4 p-6 space-y-4">
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
            <div className="p-2 bg-red-100 dark:bg-red-900/30 rounded-[10px]">
              <FileWarning className="w-6 h-6 text-red-600 dark:text-red-400" aria-hidden="true" />
            </div>
            <h2
              id="debit-note-dialog-title"
              className="text-base font-semibold text-slate-900 dark:text-white"
            >
              Vas a activar la facturación automática de penalidades
            </h2>
          </div>

          <div id="debit-note-dialog-desc" className="text-sm text-slate-600 dark:text-slate-300 space-y-3">
            <p>
              A partir de ahora, cada vez que canceles una reserva con penalidad, el sistema
              le va a <strong>facturar esa penalidad al cliente automáticamente</strong>.
            </p>
            <p>
              Son comprobantes fiscales de verdad: una vez emitidos, <strong>no se pueden borrar</strong>{" "}
              y quedan en el historial de tu agencia.
            </p>
            <div className="rounded-[10px] border border-amber-200 bg-amber-50 dark:border-amber-800 dark:bg-amber-900/20 px-4 py-3 text-amber-800 dark:text-amber-200 text-xs">
              <p>
                Activalo solo cuando tengas todo verificado con tu contador. Si todavía falta algún
                paso, el sistema no te va a dejar guardar y te lo va a avisar — sin emitir nada.
              </p>
            </div>
          </div>

          <div className="flex gap-3 pt-2">
            {/* Botón cancelar primero: es la acción más segura y recibe el foco inicial */}
            <Button
              type="button"
              variant="outline"
              onClick={handleDebitNoteDialogCancelled}
              autoFocus
              className="flex-1"
            >
              Cancelar, no activar
            </Button>
            {/* P-14: destructiva discreta, JAMAS relleno solido de rojo — esta ventana
                YA ES la confirmacion, no hace falta gritar mas fuerte que el aviso. */}
            <Button
              type="button"
              variant="destructive"
              onClick={handleDebitNoteConfirmed}
              className="flex-1"
              data-testid="confirm-debit-note-activation"
            >
              Sí, activar
            </Button>
          </div>
        </div>
      </div>
    )}
    </>
  );
}
