import { useEffect, useMemo, useState } from "react";
import { apiRequest } from "../api";
import { showError, showSuccess } from "../alerts";

const initialQuoteForm = {
  referenceCode: "",
  customerId: "",
  status: "Draft",
  productType: "Flight",
  currency: "USD",
  totalAmount: "",
  validUntil: "",
  notes: "",
};

const initialVersionForm = {
  quoteId: "",
  productType: "Flight",
  currency: "USD",
  totalAmount: "",
  validUntil: "",
  notes: "",
};

export default function QuotesPage() {
  const [quotes, setQuotes] = useState([]);
  const [customers, setCustomers] = useState([]);
  const [quoteForm, setQuoteForm] = useState(initialQuoteForm);
  const [versionForm, setVersionForm] = useState(initialVersionForm);
  const [selectedQuote, setSelectedQuote] = useState(null);

  const currencyOptions = useMemo(() => ["", "ARS", "USD", "EUR"], []);
  const productOptions = useMemo(() => ["Flight", "Hotel", "Package", "Insurance", "General"], []);
  const statusOptions = useMemo(() => ["Draft", "Sent", "Approved", "Rejected"], []);

  const loadQuotes = async () => {
    try {
      const data = await apiRequest("/api/quotes");
      setQuotes(data);
    } catch {
      showError("No se pudieron cargar las cotizaciones.");
    }
  };

  const loadCustomers = async () => {
    try {
      const data = await apiRequest("/api/customers");
      setCustomers(data);
    } catch {
      showError("No se pudieron cargar los clientes.");
    }
  };

  const loadQuoteDetail = async (quoteId) => {
    if (!quoteId) {
      setSelectedQuote(null);
      return;
    }

    try {
      const detail = await apiRequest(`/api/quotes/${quoteId}`);
      setSelectedQuote(detail);
    } catch {
      showError("No se pudo cargar el detalle de la cotización.");
    }
  };

  useEffect(() => {
    loadQuotes();
    loadCustomers();
  }, []);

  const handleQuoteChange = (event) => {
    setQuoteForm((prev) => ({ ...prev, [event.target.name]: event.target.value }));
  };

  const handleVersionChange = (event) => {
    setVersionForm((prev) => ({ ...prev, [event.target.name]: event.target.value }));
  };

  const handleQuoteSubmit = async (event) => {
    event.preventDefault();
    try {
      await apiRequest("/api/quotes", {
        method: "POST",
        body: JSON.stringify({
          referenceCode: quoteForm.referenceCode,
          customerId: Number(quoteForm.customerId),
          status: quoteForm.status,
          version: {
            productType: quoteForm.productType,
            currency: quoteForm.currency || null,
            totalAmount: Number(quoteForm.totalAmount || 0),
            validUntil: quoteForm.validUntil ? `${quoteForm.validUntil}T00:00:00Z` : null,
            notes: quoteForm.notes,
          },
        }),
      });
      setQuoteForm(initialQuoteForm);
      loadQuotes();
      await showSuccess("Cotización registrada correctamente.");
    } catch {
      await showError("No se pudo registrar la cotización.");
    }
  };

  const handleVersionSubmit = async (event) => {
    event.preventDefault();
    if (!versionForm.quoteId) {
      await showError("Selecciona una cotización.");
      return;
    }

    try {
      await apiRequest(`/api/quotes/${versionForm.quoteId}/versions`, {
        method: "POST",
        body: JSON.stringify({
          productType: versionForm.productType,
          currency: versionForm.currency || null,
          totalAmount: Number(versionForm.totalAmount || 0),
          validUntil: versionForm.validUntil ? `${versionForm.validUntil}T00:00:00Z` : null,
          notes: versionForm.notes,
        }),
      });
      setVersionForm((prev) => ({ ...initialVersionForm, quoteId: prev.quoteId }));
      loadQuotes();
      loadQuoteDetail(versionForm.quoteId);
      await showSuccess("Versión creada correctamente.");
    } catch {
      await showError("No se pudo crear la versión.");
    }
  };

  return (
    <div className="space-y-8">
      <div>
        <h2 className="text-2xl font-semibold">Cotizaciones</h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          Registra versiones de cotizaciones y comparte propuestas con los clientes.
        </p>
      </div>

      <div className="grid gap-6 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,0.9fr)]">
        <form
          className="grid gap-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60"
          onSubmit={handleQuoteSubmit}
        >
          <input
            name="referenceCode"
            placeholder="Código de cotización"
            value={quoteForm.referenceCode}
            onChange={handleQuoteChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            required
          />
          <select
            name="customerId"
            value={quoteForm.customerId}
            onChange={handleQuoteChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            required
          >
            <option value="">Selecciona cliente</option>
            {customers.map((customer) => (
              <option key={customer.id} value={customer.id}>
                {customer.fullName}
              </option>
            ))}
          </select>
          <div className="grid gap-4 md:grid-cols-3">
            <select
              name="status"
              value={quoteForm.status}
              onChange={handleQuoteChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            >
              {statusOptions.map((option) => (
                <option key={option} value={option}>
                  {option}
                </option>
              ))}
            </select>
            <select
              name="productType"
              value={quoteForm.productType}
              onChange={handleQuoteChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            >
              {productOptions.map((option) => (
                <option key={option} value={option}>
                  {option}
                </option>
              ))}
            </select>
            <select
              name="currency"
              value={quoteForm.currency}
              onChange={handleQuoteChange}
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
              name="totalAmount"
              type="number"
              min="0"
              step="0.01"
              placeholder="Total cotizado"
              value={quoteForm.totalAmount}
              onChange={handleQuoteChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              required
            />
            <input
              name="validUntil"
              type="date"
              value={quoteForm.validUntil}
              onChange={handleQuoteChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            />
          </div>
          <input
            name="notes"
            placeholder="Notas"
            value={quoteForm.notes}
            onChange={handleQuoteChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
          />
          <button
            type="submit"
            className="rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500"
          >
            Crear cotización
          </button>
        </form>

        <div className="space-y-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
          <div>
            <h3 className="text-lg font-semibold">Cotizaciones recientes</h3>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Selecciona una cotización para ver sus versiones.
            </p>
          </div>
          <div className="space-y-3">
            {quotes.map((quote) => (
              <button
                key={quote.id}
                type="button"
                onClick={() => {
                  setVersionForm((prev) => ({ ...prev, quoteId: String(quote.id) }));
                  loadQuoteDetail(quote.id);
                }}
                className={`w-full rounded-2xl border px-4 py-3 text-left text-sm shadow-sm transition ${
                  String(quote.id) === String(versionForm.quoteId)
                    ? "border-indigo-500 bg-indigo-50 text-indigo-700 dark:border-indigo-400 dark:bg-indigo-500/10 dark:text-indigo-200"
                    : "border-slate-200 bg-white text-slate-700 hover:border-indigo-200 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-200"
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className="font-semibold">{quote.referenceCode}</span>
                  <span className="text-xs uppercase tracking-widest">v{quote.latestVersion}</span>
                </div>
                <div className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                  {quote.customerName} · {quote.productType}
                </div>
                <div className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                  {quote.totalAmount.toLocaleString("es-AR", {
                    style: "currency",
                    currency: quote.currency || "USD",
                  })}
                </div>
              </button>
            ))}
            {quotes.length === 0 && (
              <p className="rounded-xl bg-slate-50 px-4 py-3 text-sm text-slate-500 dark:bg-slate-900/40 dark:text-slate-400">
                Aún no hay cotizaciones registradas.
              </p>
            )}
          </div>
        </div>
      </div>

      <div className="grid gap-6 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]">
        <form
          className="grid gap-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60"
          onSubmit={handleVersionSubmit}
        >
          <div>
            <h3 className="text-lg font-semibold">Nueva versión</h3>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Agrega cambios sobre la cotización seleccionada.
            </p>
          </div>
          <select
            name="quoteId"
            value={versionForm.quoteId}
            onChange={(event) => {
              handleVersionChange(event);
              loadQuoteDetail(event.target.value);
            }}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            required
          >
            <option value="">Selecciona cotización</option>
            {quotes.map((quote) => (
              <option key={quote.id} value={quote.id}>
                {quote.referenceCode}
              </option>
            ))}
          </select>
          <div className="grid gap-4 md:grid-cols-2">
            <select
              name="productType"
              value={versionForm.productType}
              onChange={handleVersionChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            >
              {productOptions.map((option) => (
                <option key={option} value={option}>
                  {option}
                </option>
              ))}
            </select>
            <select
              name="currency"
              value={versionForm.currency}
              onChange={handleVersionChange}
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
              name="totalAmount"
              type="number"
              min="0"
              step="0.01"
              placeholder="Total"
              value={versionForm.totalAmount}
              onChange={handleVersionChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              required
            />
            <input
              name="validUntil"
              type="date"
              value={versionForm.validUntil}
              onChange={handleVersionChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            />
          </div>
          <input
            name="notes"
            placeholder="Notas"
            value={versionForm.notes}
            onChange={handleVersionChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
          />
          <button
            type="submit"
            className="rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500"
          >
            Guardar versión
          </button>
        </form>

        <div className="overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
          <table className="w-full text-left text-sm">
            <thead className="bg-slate-50 text-slate-500 dark:bg-slate-950 dark:text-slate-300">
              <tr>
                <th className="px-4 py-3">Versión</th>
                <th className="px-4 py-3">Producto</th>
                <th className="px-4 py-3">Total</th>
                <th className="px-4 py-3">Vigencia</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-200 bg-white dark:divide-slate-800 dark:bg-slate-900/40">
              {selectedQuote?.versions?.map((version) => (
                <tr key={version.id} className="hover:bg-slate-50 dark:hover:bg-slate-900/60">
                  <td className="px-4 py-3">v{version.versionNumber}</td>
                  <td className="px-4 py-3">{version.productType}</td>
                  <td className="px-4 py-3">
                    {version.totalAmount.toLocaleString("es-AR", {
                      style: "currency",
                      currency: version.currency || "USD",
                    })}
                  </td>
                  <td className="px-4 py-3">
                    {version.validUntil ? new Date(version.validUntil).toLocaleDateString() : "-"}
                  </td>
                </tr>
              ))}
              {!selectedQuote && (
                <tr>
                  <td className="px-4 py-4 text-sm text-slate-500 dark:text-slate-400" colSpan={4}>
                    Selecciona una cotización para ver sus versiones.
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
