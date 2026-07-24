/**
 * Modal para crear una factura AFIP desde una reserva.
 *
 * Soporta dos modos:
 *   - Moneda ARS (default): comportamiento idéntico al original, sin campos extra.
 *   - Moneda USD (solo si afipSettings.enableMultiCurrencyInvoicing === true):
 *     muestra selector de moneda y campos de tipo de cambio + justificación.
 *
 * La factura se envía a /invoices y queda en estado PENDING hasta que el job de AFIP la procesa.
 */

import { useEffect, useMemo, useState } from "react";
import { AlertCircle, Calculator, Plus, Trash2, X } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { formatDate } from "../lib/utils";

// Alícuotas de IVA según catálogo de ARCA/AFIP.
// El id coincide con el AlicuotaIvaId que espera el backend.
const VAT_RATES = [
  { id: 3, label: "0%", value: 0 },
  { id: 4, label: "10.5%", value: 0.105 },
  { id: 5, label: "21%", value: 0.21 },
  { id: 6, label: "27%", value: 0.27 },
  { id: 8, label: "5%", value: 0.05 },
  { id: 9, label: "2.5%", value: 0.025 },
];

const TRIBUTE_TYPES = [
  { id: 99, label: "Otras percepciones" },
  { id: 1, label: "Impuestos nacionales" },
  { id: 2, label: "Impuestos provinciales" },
  { id: 3, label: "Impuestos municipales" },
  { id: 4, label: "Impuestos internos" },
];

// Monedas soportadas en el MVP multimoneda.
// Solo se muestran si afipSettings.enableMultiCurrencyInvoicing === true.
const CURRENCY_OPTIONS = [
  { value: "ARS", label: "Pesos (ARS)" },
  { value: "USD", label: "Dólares (USD)" },
];

// Valor del enum ExchangeRateSource en el backend para "BNA vendedor divisa".
// El backend NO tiene JsonStringEnumConverter, así que espera el int, NO el string.
// Ver: src/TravelApi.Domain/Entities/ExchangeRateSource.cs — BNA_VendedorDivisa = 6
const EXCHANGE_RATE_SOURCE_BNA_VENDEDOR_DIVISA = 6;

/**
 * Formatea un número como moneda según la moneda activa del formulario.
 * Para ARS usa "es-AR" con símbolo $; para USD usa "en-US" con símbolo US$.
 */
function formatCurrency(amount, currencyCode) {
  if (currencyCode === "USD") {
    return Number(amount).toLocaleString("en-US", {
      style: "currency",
      currency: "USD",
      minimumFractionDigits: 2,
    });
  }
  return Number(amount).toLocaleString("es-AR", {
    style: "currency",
    currency: "ARS",
    minimumFractionDigits: 2,
  });
}

const createDefaultItem = (amount, isMonotributista) => {
  const defaultNet = isMonotributista ? Number(amount || 0) : Number(amount || 0) / 1.21;
  return {
    description: "Servicios Turísticos",
    quantity: 1,
    unitPrice: Number(defaultNet.toFixed(2)),
    alicuotaIvaId: isMonotributista ? 3 : 5,
  };
};

export default function CreateInvoiceModal({
  isOpen,
  onClose,
  onSuccess,
  reservaPublicId,
  reserva,
  initialAmount,
  clientName,
  clientCuit,
}) {
  const [loading, setLoading] = useState(false);
  const [fetchingSettings, setFetchingSettings] = useState(true);
  const [afipSettings, setAfipSettings] = useState(null);
  const [items, setItems] = useState([]);
  const [tributes, setTributes] = useState([]);
  const [forceIssue, setForceIssue] = useState(false);
  const [forceReason, setForceReason] = useState("");

  // P3: cuadre de facturacion de la reserva (vendido / facturado neto / disponible),
  // traido del detalle al abrir el modal. null mientras no carga o si no hay reserva.
  const [cuadreData, setCuadreData] = useState(null);

  // Estado de moneda: solo se activa si el flag enableMultiCurrencyInvoicing está ON.
  // Default ARS para no alterar el flujo normal.
  const [selectedCurrency, setSelectedCurrency] = useState("ARS");
  const [exchangeRate, setExchangeRate] = useState("");
  const [exchangeRateJustification, setExchangeRateJustification] = useState("");

  const isMonotributista =
    afipSettings?.taxCondition?.trim() === "Monotributo" ||
    afipSettings?.taxCondition?.trim() === "Exento";

  // Flag que habilita el selector de moneda en la UI.
  // Si el campo no viene del backend (flag OFF por defecto en settings), queda false.
  const isMultiCurrencyEnabled = Boolean(afipSettings?.enableMultiCurrencyInvoicing);

  // Si la feature está desactivada, siempre trabajamos en ARS (estado invisible).
  const activeCurrency = isMultiCurrencyEnabled ? selectedCurrency : "ARS";
  const isUSD = activeCurrency === "USD";

  const requiresOverride = Boolean(reserva && !reserva.isEconomicallySettled && reserva.canEmitAfipInvoice);
  const isBlockedByDebt = Boolean(reserva && !reserva.isEconomicallySettled && !reserva.canEmitAfipInvoice);

  // useEffect con dependencia [isOpen]: fetchea settings cada vez que se abre el modal.
  useEffect(() => {
    const fetchSettings = async () => {
      setFetchingSettings(true);
      try {
        const response = await api.get("/afip/settings");
        setAfipSettings(response);
      } catch (error) {
        console.error("Error fetching AFIP settings:", error);
        showError("No se pudo obtener la configuración de AFIP.");
      } finally {
        setFetchingSettings(false);
      }
    };

    if (isOpen) {
      fetchSettings();
    }
  }, [isOpen]);

  // P3: al abrir, traemos el cuadre de la reserva desde el detalle, que ya expone
  // vendido (totalSale) / facturado neto / disponible calculados en el backend (fuente
  // unica). Es informativo: si falla, no mostramos el aviso (NO bloquea facturar).
  useEffect(() => {
    if (!isOpen || !reservaPublicId) {
      setCuadreData(null);
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const detalle = await api.get(`/reservas/${reservaPublicId}`);
        if (!cancelled) setCuadreData(detalle);
      } catch {
        if (!cancelled) setCuadreData(null);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [isOpen, reservaPublicId]);

  // useEffect que reinicia el formulario cada vez que se termina de cargar settings.
  // Incluye reseteo de los campos de moneda para no arrastrar estado de una sesión anterior.
  useEffect(() => {
    if (!isOpen || fetchingSettings) {
      return;
    }

    setItems([createDefaultItem(initialAmount || 0, isMonotributista)]);
    setTributes([]);
    setForceIssue(false);
    setForceReason("");
    setSelectedCurrency("ARS");
    setExchangeRate("");
    setExchangeRateJustification("");
  }, [fetchingSettings, initialAmount, isMonotributista, isOpen]);

  const totals = useMemo(() => {
    const net = items.reduce(
      (acc, item) => acc + (Number(item.quantity) || 0) * (Number(item.unitPrice) || 0),
      0
    );
    const vat = items.reduce((acc, item) => {
      // Monotributo (Factura C) no discrimina IVA: el total es el neto.
      if (isMonotributista) {
        return acc;
      }

      const itemNet = (Number(item.quantity) || 0) * (Number(item.unitPrice) || 0);
      const rate = VAT_RATES.find((value) => value.id === Number(item.alicuotaIvaId))?.value || 0;
      return acc + itemNet * rate;
    }, 0);
    const tributeAmount = tributes.reduce((acc, tribute) => acc + (Number(tribute.importe) || 0), 0);
    const total = net + vat + tributeAmount;

    return { net, vat, tributeAmount, total };
  }, [isMonotributista, items, tributes]);

  // P3 (cuadre de facturacion): el backend expone, para la reserva, cuanto se vendio
  // (totalSale), cuanto se facturo NETO (facturadoNeto) y cuanto queda disponible
  // (disponibleParaFacturar = vendido - facturado). Avisamos —SIN bloquear— si este
  // comprobante hace que se facture MAS de lo vendido. La cuenta la hace el backend
  // (fuente unica); aca solo mostramos y comparamos contra el total que se esta cargando.
  const cuadre = useMemo(() => {
    const disponible = Number(cuadreData?.disponibleParaFacturar);
    if (!Number.isFinite(disponible)) return null;
    const excede = totals.total > disponible + 0.5; // tolerancia de medio peso por redondeos
    return {
      vendido: Number(cuadreData?.totalSale) || 0,
      facturadoNeto: Number(cuadreData?.facturadoNeto) || 0,
      disponible,
      excede,
      exceso: excede ? totals.total - disponible : 0,
    };
  }, [cuadreData, totals.total]);

  const handleItemChange = (index, field, value) => {
    setItems((current) =>
      current.map((item, itemIndex) => (itemIndex === index ? { ...item, [field]: value } : item))
    );
  };

  const handleTributeChange = (index, field, value) => {
    setTributes((current) =>
      current.map((tribute, tributeIndex) =>
        tributeIndex === index ? { ...tribute, [field]: value } : tribute
      )
    );
  };

  const handleAddItem = () => {
    setItems((current) => [...current, createDefaultItem(0, isMonotributista)]);
  };

  const handleAddTribute = () => {
    setTributes((current) => [
      ...current,
      { tributeId: 99, description: "", baseImponible: 0, alicuota: 0, importe: 0 },
    ]);
  };

  const handleSubmit = async (event) => {
    event.preventDefault();

    if (items.length === 0) {
      showError("Debes agregar al menos un item.");
      return;
    }

    if (totals.total <= 0) {
      showError("El total debe ser mayor a 0.");
      return;
    }

    if (isBlockedByDebt) {
      showError(reserva?.economicBlockReason || "No se puede facturar: la reserva tiene saldo pendiente.");
      return;
    }

    if (requiresOverride && !forceIssue) {
      showError("Debes confirmar la emisión por excepción.");
      return;
    }

    if (requiresOverride && forceReason.trim().length < 10) {
      showError("Debes indicar un motivo de al menos 10 caracteres.");
      return;
    }

    // Validación de campos multimoneda cuando la moneda elegida es USD.
    // El backend también valida esto, pero lo hacemos acá para dar feedback inmediato al operador.
    if (isUSD) {
      const rateNumber = Number(exchangeRate);
      if (!exchangeRate || isNaN(rateNumber) || rateNumber <= 0) {
        showError("Ingresá el tipo de cambio para facturas en dólares.");
        return;
      }
      // Un dólar no puede valer un peso: si alguien ingresa 1 casi seguro es un error.
      if (rateNumber === 1) {
        showError("El tipo de cambio no puede ser 1. Ingresá el valor en pesos del dólar (ej: 1200).");
        return;
      }
      if (!exchangeRateJustification.trim()) {
        showError("Ingresá la justificación del tipo de cambio (de dónde lo tomaste).");
        return;
      }
    }

    setLoading(true);
    try {
      const payload = {
        reservaId: reservaPublicId,
        items: items.map((item) => ({
          description: item.description,
          quantity: Number(item.quantity),
          unitPrice: Number(item.unitPrice),
          total: Number(item.quantity) * Number(item.unitPrice),
          alicuotaIvaId: Number(item.alicuotaIvaId),
        })),
        tributes: tributes.map((tribute) => ({
          tributeId: Number(tribute.tributeId),
          description: tribute.description,
          baseImponible: Number(tribute.baseImponible),
          alicuota: Number(tribute.alicuota),
          importe: Number(tribute.importe),
        })),
        forceIssue: requiresOverride ? forceIssue : false,
        forceReason: requiresOverride ? forceReason.trim() : null,
      };

      // Campos de multimoneda: solo se agregan al payload cuando la moneda es USD.
      // Para ARS el payload queda idéntico al original (no manda monId ni monCotiz),
      // y el backend usa sus defaults ("PES" / 1) sin cambios de comportamiento.
      if (isUSD) {
        payload.monId = "USD";
        payload.monCotiz = Number(exchangeRate);
        // ExchangeRateSource en el backend es un enum sin JsonStringEnumConverter.
        // El serializador de .NET espera el int, no el string del nombre.
        payload.exchangeRateSource = EXCHANGE_RATE_SOURCE_BNA_VENDEDOR_DIVISA;
        // Momento exacto en que el operador confirmó el TC (ISO 8601 con zona horaria).
        payload.exchangeRateFetchedAt = new Date().toISOString();
        payload.exchangeRateJustification = exchangeRateJustification.trim();
      }

      await api.post("/invoices", payload);
      showSuccess("Comprobante AFIP encolado.");
      onSuccess();
      onClose();
    } catch (error) {
      console.error(error);
      // 409 cuando ya hay una Invoice con Resultado="PENDING" para esta reserva.
      // El backend devuelve { message } con texto accionable — mostrarlo verbatim.
      showError(error?.payload?.message ?? error?.message ?? "Error al crear factura.");
    } finally {
      setLoading(false);
    }
  };

  if (!isOpen) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50 backdrop-blur-sm">
      <div className="bg-white dark:bg-slate-900 rounded-xl shadow-2xl w-full max-w-5xl max-h-[95vh] overflow-hidden flex flex-col border border-gray-200 dark:border-slate-700">
        <div className="px-8 py-6 bg-gradient-to-r from-gray-50 to-white dark:from-slate-800 dark:to-slate-900 border-b border-gray-200 dark:border-slate-700 flex justify-between items-start">
          <div>
            <h2 className="text-2xl font-bold text-gray-900 dark:text-white flex items-center gap-3">
              <div className="p-2 bg-indigo-100 dark:bg-indigo-900/30 rounded-lg">
                <Calculator className="w-6 h-6 text-indigo-600 dark:text-indigo-400" />
              </div>
              Nueva Factura AFIP
            </h2>
            <div className="mt-4 flex flex-col gap-1">
              <span className="text-xs font-semibold text-gray-500 uppercase tracking-wider">Cliente</span>
              <div className="text-lg font-medium text-gray-900 dark:text-white">{clientName || "Consumidor Final"}</div>
              <div className="text-sm text-gray-500 font-mono">{clientCuit ? `CUIT: ${clientCuit}` : "Sin CUIT registrado"}</div>
            </div>
          </div>
          <div className="flex flex-col items-end gap-3">
            <button onClick={onClose} className="text-gray-400 hover:text-gray-600 dark:text-slate-500 dark:hover:text-slate-300" type="button">
              <X className="w-6 h-6" />
            </button>
            <div className="text-right">
              <span className="text-xs font-semibold text-gray-500 uppercase tracking-wider block">Fecha</span>
              {/* Fecha de HOY en Argentina, sin importar el huso del navegador (regla del dueño). */}
              <span className="text-sm font-medium text-gray-900 dark:text-white">{formatDate(new Date())}</span>
            </div>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto p-6 space-y-8">
          {(requiresOverride || isBlockedByDebt) && (
            <div
              className={`rounded-xl border px-4 py-4 ${
                isBlockedByDebt
                  ? "border-rose-200 bg-rose-50 dark:border-rose-900/40 dark:bg-rose-900/20"
                  : "border-amber-200 bg-amber-50 dark:border-amber-900/40 dark:bg-amber-900/20"
              }`}
            >
              <div
                className={`flex items-start gap-3 ${
                  isBlockedByDebt ? "text-rose-700 dark:text-rose-300" : "text-amber-700 dark:text-amber-300"
                }`}
              >
                <AlertCircle className="w-5 h-5 mt-0.5 flex-shrink-0" />
                <div className="space-y-2">
                  <div className="font-semibold">
                    {isBlockedByDebt ? "AFIP bloqueado por deuda" : "Emisión por excepción habilitada"}
                  </div>
                  <p className="text-sm">
                    {reserva?.economicBlockReason || "La reserva todavía no está cancelada económicamente."}
                  </p>
                  {typeof reserva?.balance === "number" && (
                    <p className="text-sm font-medium">
                      Saldo pendiente actual:{" "}
                      {Number(reserva.balance).toLocaleString("es-AR", {
                        style: "currency",
                        currency: "ARS",
                        minimumFractionDigits: 2,
                      })}
                    </p>
                  )}
                </div>
              </div>

              {requiresOverride && (
                <div className="mt-4 space-y-3">
                  <label className="flex items-start gap-3 text-sm font-medium text-slate-800 dark:text-slate-100">
                    <input
                      type="checkbox"
                      checked={forceIssue}
                      onChange={(event) => setForceIssue(event.target.checked)}
                      className="mt-1 rounded border-slate-300"
                    />
                    Confirmo que el agente decide emitir AFIP aun con deuda pendiente.
                  </label>
                  <div>
                    <label className="block text-xs font-semibold uppercase tracking-wider text-slate-500 mb-1.5">
                      Motivo del override
                    </label>
                    <textarea
                      value={forceReason}
                      onChange={(event) => setForceReason(event.target.value)}
                      rows={3}
                      placeholder="Ej: emisión anticipada autorizada por el agente por pedido del cliente."
                      className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white px-3 py-2 text-sm"
                    />
                  </div>
                </div>
              )}
            </div>
          )}

          {/* P3 - Cuadre de facturacion: muestra vendido / ya facturado / disponible y avisa
              (sin bloquear) si este comprobante supera lo vendido en la reserva. */}
          {cuadre && (
            <div
              data-testid="cuadre-facturacion"
              className={`rounded-xl border px-4 py-3 text-sm ${
                cuadre.excede
                  ? "border-amber-300 bg-amber-50 text-amber-800 dark:border-amber-800 dark:bg-amber-900/20 dark:text-amber-200"
                  : "border-slate-200 bg-slate-50 text-slate-600 dark:border-slate-700 dark:bg-slate-800/40 dark:text-slate-300"
              }`}
            >
              <div className="flex flex-wrap gap-x-6 gap-y-1">
                <span>Vendido: <strong>{formatCurrency(cuadre.vendido, "ARS")}</strong></span>
                <span>Ya facturado: <strong>{formatCurrency(cuadre.facturadoNeto, "ARS")}</strong></span>
                <span>Disponible: <strong>{formatCurrency(cuadre.disponible, "ARS")}</strong></span>
                <span>Este comprobante: <strong>{formatCurrency(totals.total, "ARS")}</strong></span>
              </div>
              {cuadre.excede && (
                <div className="mt-2 font-semibold">
                  Estás facturando {formatCurrency(cuadre.exceso, "ARS")} más de lo vendido en la reserva.
                  Podés continuar, pero revisá que sea correcto.
                </div>
              )}
            </div>
          )}

          {/* Selector de moneda: visible únicamente si el flag enableMultiCurrencyInvoicing está activo.
              Si el flag está OFF, este bloque no renderiza y el comportamiento es idéntico al original. */}
          {isMultiCurrencyEnabled && (
            <div
              data-testid="selector-moneda"
              className="rounded-xl border border-indigo-200 dark:border-indigo-800 bg-indigo-50 dark:bg-indigo-900/20 px-4 py-4 space-y-4"
            >
              <div className="flex items-center gap-3">
                <span className="text-xs font-semibold text-indigo-600 dark:text-indigo-400 uppercase tracking-wider">
                  Moneda de la factura
                </span>
              </div>

              <div className="flex items-center gap-4">
                {CURRENCY_OPTIONS.map((option) => (
                  <label
                    key={option.value}
                    className="flex items-center gap-2 cursor-pointer text-sm font-medium text-slate-700 dark:text-slate-300"
                  >
                    <input
                      type="radio"
                      name="currency"
                      value={option.value}
                      checked={selectedCurrency === option.value}
                      onChange={() => {
                        setSelectedCurrency(option.value);
                        // Limpiar los campos de TC al cambiar de moneda para no mandar datos viejos.
                        setExchangeRate("");
                        setExchangeRateJustification("");
                      }}
                      data-testid={`radio-currency-${option.value}`}
                    />
                    {option.label}
                  </label>
                ))}
              </div>

              {/* Campos de tipo de cambio: solo se muestran cuando la moneda elegida es USD. */}
              {isUSD && (
                <div className="space-y-3 pt-2 border-t border-indigo-200 dark:border-indigo-700">
                  {/* Informamos la fuente del TC (fija en el MVP = BNA vendedor divisa).
                      No es un dropdown porque en el MVP solo hay una fuente válida. */}
                  <div className="flex items-center gap-2 text-xs text-indigo-700 dark:text-indigo-300 bg-indigo-100 dark:bg-indigo-900/40 px-3 py-2 rounded-lg">
                    <AlertCircle className="w-3.5 h-3.5 flex-shrink-0" />
                    Fuente del TC: BNA vendedor divisa (dólar del día hábil anterior)
                  </div>

                  <div className="flex flex-col sm:flex-row gap-3">
                    <div className="w-full sm:w-40">
                      <label
                        htmlFor="exchange-rate-input"
                        className="block text-xs font-semibold uppercase tracking-wider text-slate-500 mb-1.5"
                      >
                        Tipo de cambio ($/USD) *
                      </label>
                      <input
                        id="exchange-rate-input"
                        type="number"
                        step="0.01"
                        min="1.01"
                        value={exchangeRate}
                        onChange={(event) => setExchangeRate(event.target.value)}
                        placeholder="Ej: 1200.50"
                        className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white text-right"
                        data-testid="input-tipo-cambio"
                      />
                    </div>

                    <div className="flex-1">
                      <label
                        htmlFor="exchange-rate-justification"
                        className="block text-xs font-semibold uppercase tracking-wider text-slate-500 mb-1.5"
                      >
                        Justificación del TC *
                      </label>
                      <textarea
                        id="exchange-rate-justification"
                        value={exchangeRateJustification}
                        onChange={(event) => setExchangeRateJustification(event.target.value)}
                        rows={2}
                        placeholder="Ej: Dólar vendedor divisa BNA del 29/05/2026, $1200.50 según web oficial."
                        className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white px-3 py-2 text-sm"
                        data-testid="input-justificacion-tc"
                      />
                    </div>
                  </div>

                  {/* Fecha del TC: se usa la fecha actual al momento del submit.
                      Se muestra read-only para que el operador sepa qué fecha va a quedar registrada. */}
                  <div className="text-xs text-slate-500 dark:text-slate-400">
                    Fecha del TC que se registrará:{" "}
                    <span className="font-medium text-slate-700 dark:text-slate-200">
                      {/* Regla del dueño: la fecha/hora que se muestra es SIEMPRE la de Argentina,
                          sin importar el huso del navegador del operador. */}
                      {new Date().toLocaleDateString("es-AR", {
                        day: "2-digit",
                        month: "2-digit",
                        year: "numeric",
                        hour: "2-digit",
                        minute: "2-digit",
                        timeZone: "America/Argentina/Buenos_Aires",
                      })}
                    </span>
                  </div>
                </div>
              )}
            </div>
          )}

          <div>
            <div className="flex justify-between items-center mb-3">
              <h3 className="text-sm font-medium text-gray-700 dark:text-slate-300 uppercase tracking-wider">Items / Servicios</h3>
              {isMonotributista && (
                <div className="flex items-center gap-2 text-xs font-semibold text-amber-600 dark:text-amber-400 bg-amber-50 dark:bg-amber-900/20 px-3 py-1 rounded-full border border-amber-100 dark:border-amber-900/30">
                  <AlertCircle className="w-3 h-3" />
                  Factura C: no discrimina IVA
                </div>
              )}
            </div>
            <div className="space-y-3">
              {items.map((item, index) => (
                <div key={index} className="flex flex-col md:flex-row gap-3 items-end bg-gray-50 dark:bg-slate-800/50 p-3 rounded-lg border border-gray-100 dark:border-slate-700">
                  <div className="flex-1">
                    <label className="block text-xs font-medium text-gray-500 mb-1">Descripción</label>
                    <input
                      type="text"
                      value={item.description}
                      onChange={(event) => handleItemChange(index, "description", event.target.value)}
                      className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white"
                      placeholder="Ej. Servicios turísticos"
                    />
                  </div>
                  <div className="w-24">
                    <label className="block text-xs font-medium text-gray-500 mb-1">Cant.</label>
                    <input
                      type="number"
                      step="0.01"
                      value={item.quantity}
                      onChange={(event) => handleItemChange(index, "quantity", event.target.value)}
                      className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white text-right"
                    />
                  </div>
                  <div className="w-32">
                    {/* El prefijo del precio cambia según la moneda activa para no confundir al operador. */}
                    <label className="block text-xs font-medium text-gray-500 mb-1">
                      Precio Unit. {isUSD ? "(USD)" : ""}
                    </label>
                    <div className="relative">
                      <span className="absolute left-2 top-1.5 text-gray-400 text-xs">
                        {isUSD ? "US$" : "$"}
                      </span>
                      <input
                        type="number"
                        step="0.01"
                        value={item.unitPrice}
                        onChange={(event) => handleItemChange(index, "unitPrice", event.target.value)}
                        className="w-full text-sm pl-8 rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white text-right"
                      />
                    </div>
                  </div>
                  {!isMonotributista && (
                    <div className="w-32">
                      <label className="block text-xs font-medium text-gray-500 mb-1">IVA</label>
                      <select
                        value={item.alicuotaIvaId}
                        onChange={(event) => handleItemChange(index, "alicuotaIvaId", event.target.value)}
                        className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white"
                      >
                        {VAT_RATES.map((rate) => (
                          <option key={rate.id} value={rate.id}>
                            {rate.label}
                          </option>
                        ))}
                      </select>
                    </div>
                  )}
                  <div className="w-32 text-right pb-2 font-medium text-gray-900 dark:text-white">
                    {/* Subtotal por ítem: usa la moneda activa para el formateo */}
                    {formatCurrency((item.quantity || 0) * (item.unitPrice || 0), activeCurrency)}
                  </div>
                  <button
                    onClick={() => setItems((current) => current.filter((_, itemIndex) => itemIndex !== index))}
                    className="p-2 text-red-500 hover:bg-red-50 dark:hover:bg-red-900/20 rounded-md transition-colors"
                    title="Eliminar item"
                    type="button"
                  >
                    <Trash2 className="w-4 h-4" />
                  </button>
                </div>
              ))}
              <button onClick={handleAddItem} type="button" className="flex items-center gap-2 text-sm text-indigo-600 dark:text-indigo-400 hover:text-indigo-700 font-medium mt-2">
                <Plus className="w-4 h-4" />
                Agregar item
              </button>
            </div>
          </div>

          <div className="pt-4 border-t border-gray-200 dark:border-slate-700">
            <div className="flex justify-between items-center mb-3">
              <h3 className="text-sm font-medium text-gray-700 dark:text-slate-300 uppercase tracking-wider">Tributos / Percepciones</h3>
            </div>
            <div className="space-y-3">
              {tributes.map((tribute, index) => (
                <div key={index} className="flex flex-col md:flex-row gap-3 items-end bg-orange-50 dark:bg-orange-900/10 p-3 rounded-lg border border-orange-100 dark:border-orange-900/30">
                  <div className="w-48">
                    <label className="block text-xs font-medium text-gray-500 mb-1">Tipo</label>
                    <select
                      value={tribute.tributeId}
                      onChange={(event) => handleTributeChange(index, "tributeId", event.target.value)}
                      className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white"
                    >
                      {TRIBUTE_TYPES.map((type) => (
                        <option key={type.id} value={type.id}>
                          {type.label}
                        </option>
                      ))}
                    </select>
                  </div>
                  <div className="flex-1">
                    <label className="block text-xs font-medium text-gray-500 mb-1">Descripción</label>
                    <input
                      type="text"
                      value={tribute.description}
                      onChange={(event) => handleTributeChange(index, "description", event.target.value)}
                      className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white"
                      placeholder="Detalle del tributo"
                    />
                  </div>
                  <div className="w-32">
                    <label className="block text-xs font-medium text-gray-500 mb-1">Importe</label>
                    <div className="relative">
                      <span className="absolute left-2 top-1.5 text-gray-400 text-xs">
                        {isUSD ? "US$" : "$"}
                      </span>
                      <input
                        type="number"
                        step="0.01"
                        value={tribute.importe}
                        onChange={(event) => handleTributeChange(index, "importe", event.target.value)}
                        className="w-full text-sm pl-6 rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white text-right"
                      />
                    </div>
                  </div>
                  <button
                    onClick={() => setTributes((current) => current.filter((_, tributeIndex) => tributeIndex !== index))}
                    className="p-2 text-red-500 hover:bg-red-50 dark:hover:bg-red-900/20 rounded-md transition-colors"
                    title="Eliminar tributo"
                    type="button"
                  >
                    <Trash2 className="w-4 h-4" />
                  </button>
                </div>
              ))}
              <button onClick={handleAddTribute} type="button" className="flex items-center gap-2 text-sm text-orange-600 dark:text-orange-400 hover:text-orange-700 font-medium mt-2">
                <Plus className="w-4 h-4" />
                Agregar tributo
              </button>
            </div>
          </div>
        </div>

        <div className="bg-gray-50 dark:bg-slate-800 px-6 py-4 border-t border-gray-200 dark:border-slate-700">
          <div className="flex flex-col md:flex-row justify-between items-center gap-4">
            <div className="text-xs text-gray-500 max-w-md">
              <p className="flex items-center gap-1">
                <AlertCircle className="w-3 h-3" />
                Los montos se enviarán a AFIP para autorización.
                {isUSD && (
                  <span className="ml-1 font-semibold text-indigo-600 dark:text-indigo-400">
                    Factura en USD — TC: {exchangeRate ? `$${Number(exchangeRate).toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}` : "pendiente"}
                  </span>
                )}
              </p>
            </div>
            <div className="flex items-center gap-8 w-full md:w-auto">
              <div className="text-right space-y-1">
                <div className="text-sm text-gray-500">
                  Neto:{" "}
                  <span className="text-gray-900 dark:text-white font-medium">
                    {formatCurrency(totals.net, activeCurrency)}
                  </span>
                </div>
                {!isMonotributista && (
                  <div className="text-sm text-gray-500">
                    IVA:{" "}
                    <span className="text-gray-900 dark:text-white font-medium">
                      {formatCurrency(totals.vat, activeCurrency)}
                    </span>
                  </div>
                )}
                {totals.tributeAmount > 0 && (
                  <div className="text-sm text-orange-600">
                    Tributos:{" "}
                    <span className="font-medium">
                      {formatCurrency(totals.tributeAmount, activeCurrency)}
                    </span>
                  </div>
                )}
              </div>
              <div className="text-right">
                <span className="block text-xs text-gray-500 uppercase font-semibold">Total final</span>
                <span className="text-2xl font-bold text-gray-900 dark:text-white">
                  {formatCurrency(totals.total, activeCurrency)}
                </span>

                {/* Equivalente en pesos: se muestra solo cuando la moneda es USD
                    y el operador ya cargó un TC válido (mayor a 0 y distinto de 1).
                    El cálculo es: total en USD × tipo de cambio ingresado.
                    Si el TC todavía no fue cargado, mostramos un guión para no confundir. */}
                {isUSD && (
                  <div
                    data-testid="equivalente-pesos"
                    className="mt-1 text-xs text-indigo-600 dark:text-indigo-400 font-medium"
                  >
                    {Number(exchangeRate) > 1
                      ? `≈ ${(totals.total * Number(exchangeRate)).toLocaleString("es-AR", {
                          style: "currency",
                          currency: "ARS",
                          minimumFractionDigits: 2,
                        })} (TC $${Number(exchangeRate).toLocaleString("es-AR", {
                          minimumFractionDigits: 2,
                          maximumFractionDigits: 2,
                        })})`
                      : "Equivalente en pesos: — (ingresá el TC)"}
                  </div>
                )}
              </div>
            </div>
          </div>
          <div className="mt-4 flex justify-end gap-3">
            <button
              onClick={onClose}
              className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 dark:bg-slate-800 dark:text-slate-300 dark:border-slate-600 dark:hover:bg-slate-700 transition-colors"
              type="button"
            >
              Cancelar
            </button>
            <button
              onClick={handleSubmit}
              disabled={
                loading ||
                totals.total <= 0 ||
                isBlockedByDebt ||
                (requiresOverride && !forceIssue) ||
                // Si la moneda es USD, bloqueamos hasta que el operador ingrese
                // un TC válido (> 0 y distinto de 1) y una justificación.
                // Esto evita que el submit llegue al handleSubmit con datos incompletos.
                (isUSD && (!(Number(exchangeRate) > 0) || Number(exchangeRate) === 1 || !exchangeRateJustification.trim()))
              }
              data-testid="btn-emitir-factura"
              className="px-4 py-2 text-sm font-medium text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 focus:ring-4 focus:ring-indigo-300 dark:focus:ring-indigo-900 disabled:opacity-50 flex items-center gap-2"
              type="button"
            >
              {loading
                ? "Emitiendo..."
                : requiresOverride
                  ? "Emitir por excepción"
                  : isUSD
                    ? "Emitir factura en USD"
                    : "Emitir factura"}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
