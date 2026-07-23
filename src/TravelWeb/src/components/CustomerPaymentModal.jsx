import { useEffect, useState } from "react";
import { AlignLeft, Calendar, CheckCircle2, CreditCard, DollarSign, FileText, X } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { getApiErrorMessage } from "../lib/errors";
import { getPublicId, getRelatedPublicId } from "../lib/publicIds";
import { formatCurrency } from "../lib/utils";

const FX_SOURCES = [
  { value: 5, label: "Manual" },
  { value: 6, label: "BNA vendedor divisa" },
  { value: 1, label: "BCRA mayorista A3500" },
];

const PAYMENT_CURRENCIES = ["ARS", "USD"];

const today = () => new Date().toISOString().split("T")[0];

function pendingCurrencyLines(reserva) {
  const source = Array.isArray(reserva?.debtByCurrency)
    ? reserva.debtByCurrency.map((line) => ({ currency: line.currency, balance: line.amount }))
    : Array.isArray(reserva?.porMoneda)
      ? reserva.porMoneda
      : [];
  const lines = source.filter((line) => Number(line.balance ?? 0) > 0.01);

  // Las reservas previas al backfill no tienen porMoneda: conservamos compatibilidad sin inventar USD.
  return lines.length > 0
    ? lines
    : Number(reserva?.balance ?? 0) > 0.01
      ? [{ currency: "ARS", balance: reserva.balance }]
      : [];
}

function firstCollectableReserva(reservas) {
  return reservas.find((reserva) => pendingCurrencyLines(reserva).length > 0) || reservas[0] || null;
}

function reservaPublicId(reserva) {
  return reserva?.reservaPublicId || getPublicId(reserva);
}

function isCrossCurrency(payment) {
  return payment?.imputedCurrency != null && payment.imputedCurrency !== payment.currency;
}

/**
 * Cobranza desde la cuenta del cliente. Usa exclusivamente el circuito canónico /payments:
 * éste conserva moneda e imputación y registra el movimiento de caja en la misma operación.
 */
export default function CustomerPaymentModal({
  isOpen,
  onClose,
  paymentToEdit,
  onSave,
  availableReservas = [],
  initialReservaPublicId = null,
  initialLinkedInvoicePublicId = null,
  initialImputedCurrency = null,
}) {
  const [formData, setFormData] = useState({
    amount: "",
    method: "Transferencia",
    paidAt: today(),
    notes: "",
    reservaPublicId: "",
    currency: "ARS",
    imputedCurrency: "ARS",
  });
  const [showOtherCurrency, setShowOtherCurrency] = useState(false);
  const [exchangeRate, setExchangeRate] = useState("");
  const [exchangeRateSource, setExchangeRateSource] = useState(5);
  const [exchangeRateAt, setExchangeRateAt] = useState(today());
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (!isOpen) return;

    const reservaId = paymentToEdit
      ? getRelatedPublicId(paymentToEdit, "reservaPublicId", "reservaId")
      : initialReservaPublicId || reservaPublicId(firstCollectableReserva(availableReservas));
    const reserva = availableReservas.find((item) => String(reservaPublicId(item)) === String(reservaId));
    const defaultCurrency = initialImputedCurrency
      || pendingCurrencyLines(reserva)[0]?.currency
      || paymentToEdit?.currency
      || "ARS";

    setFormData({
      amount: paymentToEdit?.amount?.toString() || "",
      method: paymentToEdit?.method || "Transferencia",
      paidAt: paymentToEdit?.paidAt ? paymentToEdit.paidAt.split("T")[0] : today(),
      notes: paymentToEdit?.notes || "",
      reservaPublicId: reservaId || "",
      currency: paymentToEdit?.currency || defaultCurrency,
      imputedCurrency: paymentToEdit?.imputedCurrency || paymentToEdit?.currency || defaultCurrency,
    });
    setShowOtherCurrency(Boolean(paymentToEdit && isCrossCurrency(paymentToEdit)));
    setExchangeRate(paymentToEdit?.exchangeRate != null ? String(paymentToEdit.exchangeRate) : "");
    setExchangeRateSource(paymentToEdit?.exchangeRateSource || 5);
    setExchangeRateAt(paymentToEdit?.exchangeRateAt ? paymentToEdit.exchangeRateAt.split("T")[0] : today());
  }, [isOpen, paymentToEdit, availableReservas, initialReservaPublicId, initialImputedCurrency]);

  if (!isOpen) return null;

  const selectedReserva = availableReservas.find((item) => String(reservaPublicId(item)) === String(formData.reservaPublicId));
  const currencyLines = pendingCurrencyLines(selectedReserva);
  const crossCurrency = showOtherCurrency && formData.currency !== formData.imputedCurrency;
  const imputedLine = currencyLines.find((line) => line.currency === formData.imputedCurrency);
  const imputedBalance = Number(imputedLine?.balance ?? 0);
  const editableBalance = imputedBalance + (
    paymentToEdit && !isCrossCurrency(paymentToEdit) ? Number(paymentToEdit.amount ?? 0) : 0
  );
  const equivalentAmount = (() => {
    const amount = Number(formData.amount);
    const rate = Number(exchangeRate);
    if (!crossCurrency) return amount;
    if (amount <= 0 || rate <= 0) return 0;
    return formData.currency === "ARS" && formData.imputedCurrency === "USD"
      ? amount / rate
      : amount * rate;
  })();

  const selectReserva = (selectedPublicId) => {
    const reserva = availableReservas.find((item) => String(reservaPublicId(item)) === String(selectedPublicId));
    const currency = pendingCurrencyLines(reserva)[0]?.currency || "ARS";
    setFormData((current) => ({ ...current, reservaPublicId: selectedPublicId, currency, imputedCurrency: currency }));
    setShowOtherCurrency(false);
    setExchangeRate("");
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    if (!formData.reservaPublicId) return showError("Elegí una reserva para imputar la cobranza.");
    if (!paymentToEdit && currencyLines.length === 0) return showError("La reserva elegida no tiene saldo pendiente para cobrar.");
    if (!Number(formData.amount) || Number(formData.amount) <= 0) return showError("El monto tiene que ser mayor a cero.");
    if (crossCurrency && (!Number(exchangeRate) || !exchangeRateAt)) {
      return showError("Para cobrar en otra moneda completá el tipo de cambio y la fecha.");
    }
    if (!isCrossCurrency(paymentToEdit) && equivalentAmount > editableBalance + 0.01) {
      return showError(`El monto excede el saldo pendiente (${formatCurrency(editableBalance, formData.imputedCurrency)}).`);
    }

    setLoading(true);
    try {
      const payload = {
        amount: Number(formData.amount),
        currency: formData.currency,
        method: formData.method,
        paidAt: new Date(formData.paidAt).toISOString(),
        notes: formData.notes,
        linkedInvoicePublicId: initialLinkedInvoicePublicId || undefined,
      };
      if (crossCurrency) {
        Object.assign(payload, {
          imputedCurrency: formData.imputedCurrency,
          imputedAmount: equivalentAmount,
          exchangeRate: Number(exchangeRate),
          exchangeRateSource,
          exchangeRateAt: new Date(exchangeRateAt).toISOString(),
        });
      }

      if (paymentToEdit) {
        // Un cobro cruzado ya registrado es inmutable económicamente: el backend permite solo datos auxiliares.
        await api.put(`/payments/${getPublicId(paymentToEdit)}`, {
          amount: Number(formData.amount),
          method: formData.method,
          notes: formData.notes,
        });
      } else {
        await api.post("/payments", { reservaId: formData.reservaPublicId, ...payload });
      }

      await onSave();
      onClose();
      showSuccess(paymentToEdit ? "La cobranza se actualizó correctamente." : "La cobranza se registró correctamente.");
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo guardar la cobranza."));
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4 backdrop-blur-sm animate-in fade-in duration-200">
      <div className="w-full max-w-md overflow-hidden rounded-xl border bg-card shadow-2xl animate-in zoom-in-95 duration-200">
        <div className="flex items-center justify-between border-b bg-slate-50/50 px-6 py-4 dark:bg-slate-900/50">
          <div>
            <h3 className="text-lg font-bold text-slate-900 dark:text-white">{paymentToEdit ? "Editar cobranza" : "Nueva cobranza"}</h3>
            <p className="text-sm text-muted-foreground">Registrar ingreso de dinero</p>
          </div>
          <button type="button" onClick={onClose} className="text-slate-400 hover:text-slate-500" aria-label="Cerrar"><X className="h-5 w-5" /></button>
        </div>

        {/* noValidate (fix 2026-07-23, mismo bug que RegistrarCobroInline): el input Monto
            tiene required/min nativos — sin noValidate el navegador corta el submit con su
            propio cartelito en inglés y handleSubmit ni llega a correr, así que el mensaje en
            criollo ("El monto tiene que ser mayor a cero.") nunca se mostraba. */}
        <form onSubmit={handleSubmit} className="space-y-4 p-6" noValidate>
          <div className="space-y-1.5">
            <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Reserva a imputar</label>
            <div className="relative">
              <FileText className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
              <select disabled={Boolean(paymentToEdit)} value={formData.reservaPublicId} onChange={(event) => selectReserva(event.target.value)} className="w-full rounded-md border border-input bg-background py-2 pl-9 pr-3 text-sm outline-none focus:ring-2 focus:ring-indigo-500 disabled:opacity-50">
                <option value="">Seleccionar reserva...</option>
                {availableReservas.map((reserva) => <option key={reservaPublicId(reserva)} value={reservaPublicId(reserva)}>{reserva.numeroReserva} - {reserva.name || reserva.fileName}</option>)}
              </select>
            </div>
          </div>

          {currencyLines.length > 0 && (
            <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm font-semibold text-emerald-800 dark:border-emerald-900/40 dark:bg-emerald-950/20 dark:text-emerald-300">
              Cobrás en {formData.imputedCurrency === "USD" ? "US$" : "$"} — saldo {formatCurrency(imputedBalance, formData.imputedCurrency)}
              {!showOtherCurrency && !paymentToEdit && <button type="button" onClick={() => setShowOtherCurrency(true)} className="ml-2 text-xs font-medium underline underline-offset-2">cobrar en otra moneda</button>}
            </div>
          )}

          {showOtherCurrency && (
            <div className="grid grid-cols-2 gap-4">
              <label className="space-y-1.5 text-sm font-medium text-slate-700 dark:text-slate-300">Moneda del cobro
                <select value={formData.currency} disabled={Boolean(paymentToEdit && isCrossCurrency(paymentToEdit))} onChange={(event) => setFormData((current) => ({ ...current, currency: event.target.value }))} className="block w-full rounded-md border border-input bg-background px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 disabled:opacity-50">
                  {PAYMENT_CURRENCIES.map((currency) => <option key={currency} value={currency}>{currency === "USD" ? "Dólares (US$)" : "Pesos ($)"}</option>)}
                </select>
              </label>
              <label className="space-y-1.5 text-sm font-medium text-slate-700 dark:text-slate-300">Imputar a
                <select value={formData.imputedCurrency} disabled={Boolean(paymentToEdit && isCrossCurrency(paymentToEdit))} onChange={(event) => setFormData((current) => ({ ...current, imputedCurrency: event.target.value }))} className="block w-full rounded-md border border-input bg-background px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 disabled:opacity-50">
                  {currencyLines.map((line) => <option key={line.currency} value={line.currency}>{line.currency === "USD" ? "Saldo en US$" : "Saldo en pesos"}</option>)}
                </select>
              </label>
            </div>
          )}

          {crossCurrency && (
            <div className="grid grid-cols-3 gap-3 rounded-lg border border-amber-200 bg-amber-50/60 p-3 dark:border-amber-900/40 dark:bg-amber-950/10">
              <label className="space-y-1 text-xs font-medium">Tipo de cambio<input type="number" min="0.01" step="0.01" value={exchangeRate} disabled={Boolean(paymentToEdit)} onChange={(event) => setExchangeRate(event.target.value)} className="block w-full rounded-md border border-input bg-background px-2 py-2 text-sm disabled:opacity-50" /></label>
              <label className="space-y-1 text-xs font-medium">Fuente<select value={exchangeRateSource} disabled={Boolean(paymentToEdit)} onChange={(event) => setExchangeRateSource(Number(event.target.value))} className="block w-full rounded-md border border-input bg-background px-2 py-2 text-sm disabled:opacity-50">{FX_SOURCES.map((source) => <option key={source.value} value={source.value}>{source.label}</option>)}</select></label>
              <label className="space-y-1 text-xs font-medium">Fecha<input type="date" value={exchangeRateAt} disabled={Boolean(paymentToEdit)} onChange={(event) => setExchangeRateAt(event.target.value)} className="block w-full rounded-md border border-input bg-background px-2 py-2 text-sm disabled:opacity-50" /></label>
              <p className="col-span-3 text-xs text-amber-800 dark:text-amber-300">Imputás {formatCurrency(equivalentAmount, formData.imputedCurrency)} al saldo elegido.</p>
            </div>
          )}

          <div className="grid grid-cols-2 gap-4">
            <label className="space-y-1.5 text-sm font-medium text-slate-700 dark:text-slate-300">Monto<div className="relative"><DollarSign className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" /><input type="number" required min="0.01" step="0.01" value={formData.amount} disabled={Boolean(paymentToEdit && isCrossCurrency(paymentToEdit))} onChange={(event) => setFormData((current) => ({ ...current, amount: event.target.value }))} className="w-full rounded-md border border-input bg-background py-2 pl-9 pr-3 text-sm outline-none focus:ring-2 focus:ring-indigo-500 disabled:opacity-50" /></div></label>
            <label className="space-y-1.5 text-sm font-medium text-slate-700 dark:text-slate-300">Fecha<div className="relative"><Calendar className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" /><input type="date" required value={formData.paidAt} disabled={Boolean(paymentToEdit)} onChange={(event) => setFormData((current) => ({ ...current, paidAt: event.target.value }))} className="w-full rounded-md border border-input bg-background py-2 pl-9 pr-3 text-sm outline-none focus:ring-2 focus:ring-indigo-500 disabled:opacity-50" /></div></label>
          </div>

          <label className="space-y-1.5 text-sm font-medium text-slate-700 dark:text-slate-300">Método de pago<div className="relative"><CreditCard className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" /><select value={formData.method} onChange={(event) => setFormData((current) => ({ ...current, method: event.target.value }))} className="w-full rounded-md border border-input bg-background py-2 pl-9 pr-3 text-sm outline-none focus:ring-2 focus:ring-indigo-500"><option value="Transferencia">Transferencia</option><option value="Efectivo">Efectivo</option><option value="Tarjeta Crédito">Tarjeta crédito</option><option value="Tarjeta Débito">Tarjeta débito</option><option value="Cheque">Cheque</option><option value="Deposito">Depósito</option></select></div></label>
          <label className="space-y-1.5 text-sm font-medium text-slate-700 dark:text-slate-300">Notas (opcional)<div className="relative"><AlignLeft className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" /><textarea rows={2} value={formData.notes} onChange={(event) => setFormData((current) => ({ ...current, notes: event.target.value }))} className="w-full resize-none rounded-md border border-input bg-background py-2 pl-9 pr-3 text-sm outline-none focus:ring-2 focus:ring-indigo-500" placeholder="Referencia o número de comprobante..." /></div></label>

          <div className="flex gap-3 pt-2"><button type="button" onClick={onClose} className="flex-1 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200">Cancelar</button><button type="submit" disabled={loading} className="flex flex-1 items-center justify-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-indigo-700 disabled:opacity-50">{loading ? "Guardando..." : <><CheckCircle2 className="h-4 w-4" />{paymentToEdit ? "Guardar cambios" : "Registrar cobro"}</>}</button></div>
        </form>
      </div>
    </div>
  );
}
