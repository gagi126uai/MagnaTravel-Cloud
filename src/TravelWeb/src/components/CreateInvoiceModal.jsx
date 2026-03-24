import { useEffect, useMemo, useState } from "react";
import { AlertCircle, Calculator, Plus, Trash2, X } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";

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

  const isMonotributista =
    afipSettings?.taxCondition?.trim() === "Monotributo" ||
    afipSettings?.taxCondition?.trim() === "Exento";

  const requiresOverride = Boolean(reserva && !reserva.isEconomicallySettled && reserva.canEmitAfipInvoice);
  const isBlockedByDebt = Boolean(reserva && !reserva.isEconomicallySettled && !reserva.canEmitAfipInvoice);

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

  useEffect(() => {
    if (!isOpen || fetchingSettings) {
      return;
    }

    setItems([createDefaultItem(initialAmount || 0, isMonotributista)]);
    setTributes([]);
    setForceIssue(false);
    setForceReason("");
  }, [fetchingSettings, initialAmount, isMonotributista, isOpen]);

  const totals = useMemo(() => {
    const net = items.reduce(
      (acc, item) => acc + (Number(item.quantity) || 0) * (Number(item.unitPrice) || 0),
      0
    );
    const vat = items.reduce((acc, item) => {
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
      showError(reserva?.economicBlockReason || "La reserva tiene deuda y AFIP está bloqueado.");
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

      await api.post("/invoices", payload);
      showSuccess("Comprobante AFIP encolado.");
      onSuccess();
      onClose();
    } catch (error) {
      console.error(error);
      showError(error.message || "Error al crear factura.");
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
              <span className="text-sm font-medium text-gray-900 dark:text-white">{new Date().toLocaleDateString()}</span>
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
                    <label className="block text-xs font-medium text-gray-500 mb-1">Precio Unit.</label>
                    <div className="relative">
                      <span className="absolute left-2 top-1.5 text-gray-400 text-xs">$</span>
                      <input
                        type="number"
                        step="0.01"
                        value={item.unitPrice}
                        onChange={(event) => handleItemChange(index, "unitPrice", event.target.value)}
                        className="w-full text-sm pl-6 rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white text-right"
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
                    {((item.quantity || 0) * (item.unitPrice || 0)).toLocaleString("es-AR", {
                      style: "currency",
                      currency: "ARS",
                      minimumFractionDigits: 2,
                    })}
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
                      <span className="absolute left-2 top-1.5 text-gray-400 text-xs">$</span>
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
              </p>
            </div>
            <div className="flex items-center gap-8 w-full md:w-auto">
              <div className="text-right space-y-1">
                <div className="text-sm text-gray-500">
                  Neto:{" "}
                  <span className="text-gray-900 dark:text-white font-medium">
                    {totals.net.toLocaleString("es-AR", { style: "currency", currency: "ARS", minimumFractionDigits: 2 })}
                  </span>
                </div>
                {!isMonotributista && (
                  <div className="text-sm text-gray-500">
                    IVA:{" "}
                    <span className="text-gray-900 dark:text-white font-medium">
                      {totals.vat.toLocaleString("es-AR", { style: "currency", currency: "ARS", minimumFractionDigits: 2 })}
                    </span>
                  </div>
                )}
                {totals.tributeAmount > 0 && (
                  <div className="text-sm text-orange-600">
                    Tributos:{" "}
                    <span className="font-medium">
                      {totals.tributeAmount.toLocaleString("es-AR", { style: "currency", currency: "ARS", minimumFractionDigits: 2 })}
                    </span>
                  </div>
                )}
              </div>
              <div className="text-right">
                <span className="block text-xs text-gray-500 uppercase font-semibold">Total final</span>
                <span className="text-2xl font-bold text-gray-900 dark:text-white">
                  {totals.total.toLocaleString("es-AR", { style: "currency", currency: "ARS", minimumFractionDigits: 2 })}
                </span>
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
              disabled={loading || totals.total <= 0 || isBlockedByDebt || (requiresOverride && !forceIssue)}
              className="px-4 py-2 text-sm font-medium text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 focus:ring-4 focus:ring-indigo-300 dark:focus:ring-indigo-900 disabled:opacity-50 flex items-center gap-2"
              type="button"
            >
              {loading ? "Emitiendo..." : requiresOverride ? "Emitir por excepción" : "Emitir factura"}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
