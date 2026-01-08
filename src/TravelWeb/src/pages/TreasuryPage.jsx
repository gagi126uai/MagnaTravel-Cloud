import { useEffect, useMemo, useState } from "react";
import { apiRequest } from "../api";
import { showError, showSuccess } from "../alerts";

const initialReceiptForm = {
  reference: "",
  method: "Transfer",
  currency: "USD",
  amount: "",
  receivedAt: "",
  notes: "",
};

const initialApplicationForm = {
  receiptId: "",
  reservationId: "",
  amountApplied: "",
};

export default function TreasuryPage() {
  const [receipts, setReceipts] = useState([]);
  const [reservations, setReservations] = useState([]);
  const [receiptForm, setReceiptForm] = useState(initialReceiptForm);
  const [applicationForm, setApplicationForm] = useState(initialApplicationForm);

  const currencyOptions = useMemo(() => ["", "ARS", "USD", "EUR"], []);
  const methodOptions = useMemo(() => ["Transfer", "Card", "Cash"], []);

  const loadReceipts = async () => {
    try {
      const data = await apiRequest("/api/treasury/receipts");
      setReceipts(data);
    } catch {
      showError("No se pudieron cargar los cobros.");
    }
  };

  const loadReservations = async () => {
    try {
      const data = await apiRequest("/api/reservations");
      setReservations(data);
    } catch {
      showError("No se pudieron cargar las reservas.");
    }
  };

  useEffect(() => {
    loadReceipts();
    loadReservations();
  }, []);

  const handleReceiptChange = (event) => {
    setReceiptForm((prev) => ({ ...prev, [event.target.name]: event.target.value }));
  };

  const handleApplicationChange = (event) => {
    setApplicationForm((prev) => ({ ...prev, [event.target.name]: event.target.value }));
  };

  const handleReceiptSubmit = async (event) => {
    event.preventDefault();
    try {
      await apiRequest("/api/treasury/receipts", {
        method: "POST",
        body: JSON.stringify({
          reference: receiptForm.reference,
          method: receiptForm.method,
          currency: receiptForm.currency || null,
          amount: Number(receiptForm.amount || 0),
          receivedAt: receiptForm.receivedAt ? `${receiptForm.receivedAt}T00:00:00Z` : null,
          notes: receiptForm.notes,
        }),
      });
      setReceiptForm(initialReceiptForm);
      loadReceipts();
      await showSuccess("Cobro registrado correctamente.");
    } catch {
      await showError("No se pudo registrar el cobro.");
    }
  };

  const handleApplicationSubmit = async (event) => {
    event.preventDefault();
    if (!applicationForm.receiptId) {
      await showError("Selecciona un cobro.");
      return;
    }

    try {
      await apiRequest(`/api/treasury/receipts/${applicationForm.receiptId}/applications`, {
        method: "POST",
        body: JSON.stringify({
          reservationId: Number(applicationForm.reservationId),
          amountApplied: Number(applicationForm.amountApplied || 0),
        }),
      });
      setApplicationForm((prev) => ({ ...initialApplicationForm, receiptId: prev.receiptId }));
      loadReceipts();
      await showSuccess("Aplicación registrada correctamente.");
    } catch {
      await showError("No se pudo registrar la aplicación.");
    }
  };

  const selectedReceipt = receipts.find((receipt) => String(receipt.id) === applicationForm.receiptId);

  return (
    <div className="space-y-8">
      <div>
        <h2 className="text-2xl font-semibold">Tesorería</h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          Registra cobros y aplica montos a las reservas activas.
        </p>
      </div>

      <div className="grid gap-6 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,0.9fr)]">
        <form
          className="grid gap-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60"
          onSubmit={handleReceiptSubmit}
        >
          <input
            name="reference"
            placeholder="Referencia del cobro"
            value={receiptForm.reference}
            onChange={handleReceiptChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            required
          />
          <div className="grid gap-4 md:grid-cols-2">
            <select
              name="method"
              value={receiptForm.method}
              onChange={handleReceiptChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            >
              {methodOptions.map((option) => (
                <option key={option} value={option}>
                  {option}
                </option>
              ))}
            </select>
            <select
              name="currency"
              value={receiptForm.currency}
              onChange={handleReceiptChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            >
              {currencyOptions.map((option) => (
                <option key={option} value={option}>
                  {option || "Sin moneda"}
                </option>
              ))}
            </select>
          </div>
          <div className="grid gap-4 md:grid-cols-2">
            <input
              name="amount"
              type="number"
              min="0"
              step="0.01"
              placeholder="Monto"
              value={receiptForm.amount}
              onChange={handleReceiptChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              required
            />
            <input
              name="receivedAt"
              type="date"
              value={receiptForm.receivedAt}
              onChange={handleReceiptChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            />
          </div>
          <input
            name="notes"
            placeholder="Notas"
            value={receiptForm.notes}
            onChange={handleReceiptChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
          />
          <button
            type="submit"
            className="rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500"
          >
            Registrar cobro
          </button>
        </form>

        <div className="space-y-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
          <div>
            <h3 className="text-lg font-semibold">Cobros recientes</h3>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Controla los saldos disponibles.
            </p>
          </div>
          <div className="space-y-3">
            {receipts.map((receipt) => (
              <button
                key={receipt.id}
                type="button"
                onClick={() => setApplicationForm((prev) => ({ ...prev, receiptId: String(receipt.id) }))}
                className={`w-full rounded-2xl border px-4 py-3 text-left text-sm shadow-sm transition ${
                  String(receipt.id) === applicationForm.receiptId
                    ? "border-indigo-500 bg-indigo-50 text-indigo-700 dark:border-indigo-400 dark:bg-indigo-500/10 dark:text-indigo-200"
                    : "border-slate-200 bg-white text-slate-700 hover:border-indigo-200 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-200"
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className="font-semibold">{receipt.reference}</span>
                  <span className="text-xs uppercase tracking-widest">{receipt.method}</span>
                </div>
                <div className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                  Disponible: {receipt.remainingAmount.toLocaleString("es-AR", {
                    style: "currency",
                    currency: receipt.currency || "USD",
                  })}
                </div>
              </button>
            ))}
            {receipts.length === 0 && (
              <p className="rounded-xl bg-slate-50 px-4 py-3 text-sm text-slate-500 dark:bg-slate-900/40 dark:text-slate-400">
                Aún no hay cobros registrados.
              </p>
            )}
          </div>
        </div>
      </div>

      <div className="grid gap-6 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]">
        <form
          className="grid gap-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60"
          onSubmit={handleApplicationSubmit}
        >
          <div>
            <h3 className="text-lg font-semibold">Aplicar cobro</h3>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Asigna un monto a una reserva específica.
            </p>
          </div>
          <select
            name="receiptId"
            value={applicationForm.receiptId}
            onChange={handleApplicationChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            required
          >
            <option value="">Selecciona cobro</option>
            {receipts.map((receipt) => (
              <option key={receipt.id} value={receipt.id}>
                {receipt.reference}
              </option>
            ))}
          </select>
          <select
            name="reservationId"
            value={applicationForm.reservationId}
            onChange={handleApplicationChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            required
          >
            <option value="">Selecciona reserva</option>
            {reservations.map((reservation) => (
              <option key={reservation.id} value={reservation.id}>
                {reservation.referenceCode}
              </option>
            ))}
          </select>
          <input
            name="amountApplied"
            type="number"
            min="0"
            step="0.01"
            placeholder="Monto a aplicar"
            value={applicationForm.amountApplied}
            onChange={handleApplicationChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            required
          />
          {selectedReceipt && (
            <p className="text-xs text-slate-500 dark:text-slate-400">
              Disponible: {selectedReceipt.remainingAmount.toLocaleString("es-AR", {
                style: "currency",
                currency: selectedReceipt.currency || "USD",
              })}
            </p>
          )}
          <button
            type="submit"
            className="rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500"
          >
            Aplicar cobro
          </button>
        </form>

        <div className="overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
          <table className="w-full text-left text-sm">
            <thead className="bg-slate-50 text-slate-500 dark:bg-slate-950 dark:text-slate-300">
              <tr>
                <th className="px-4 py-3">Reserva</th>
                <th className="px-4 py-3">Monto</th>
                <th className="px-4 py-3">Fecha</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-200 bg-white dark:divide-slate-800 dark:bg-slate-900/40">
              {selectedReceipt?.applications?.map((application) => (
                <tr key={application.id} className="hover:bg-slate-50 dark:hover:bg-slate-900/60">
                  <td className="px-4 py-3">#{application.reservationId}</td>
                  <td className="px-4 py-3">
                    {application.amountApplied.toLocaleString("es-AR", {
                      style: "currency",
                      currency: selectedReceipt.currency || "USD",
                    })}
                  </td>
                  <td className="px-4 py-3">
                    {new Date(application.appliedAt).toLocaleDateString()}
                  </td>
                </tr>
              ))}
              {!selectedReceipt && (
                <tr>
                  <td className="px-4 py-4 text-sm text-slate-500 dark:text-slate-400" colSpan={3}>
                    Selecciona un cobro para ver sus aplicaciones.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
