import { useEffect, useState } from "react";
import { apiRequest } from "../api";
import { showError } from "../alerts";
import { isAdmin } from "../auth";

export default function DashboardPage() {
  const [summary, setSummary] = useState(null);
  const [searchQuery, setSearchQuery] = useState("");
  const [searchResults, setSearchResults] = useState(null);
  const adminUser = isAdmin();

  useEffect(() => {
    const endpoint = adminUser ? "/api/reports/summary" : "/api/reports/operations";
    apiRequest(endpoint)
      .then(setSummary)
      .catch(() => {
        showError("No se pudieron cargar los indicadores.");
      });
  }, [adminUser]);

  const handleSearch = async (event) => {
    event.preventDefault();
    if (!searchQuery.trim()) {
      setSearchResults(null);
      return;
    }

    try {
      const data = await apiRequest(`/api/search?query=${encodeURIComponent(searchQuery)}`);
      setSearchResults(data);
    } catch (error) {
      showError(error.message || "No se pudo completar la búsqueda.");
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h2 className="text-2xl font-semibold">Dashboard</h2>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            Resumen ejecutivo de la operación diaria.
          </p>
        </div>
        <div className="rounded-full border border-slate-200 bg-white px-4 py-2 text-xs font-semibold uppercase tracking-[0.2em] text-slate-400 shadow-sm dark:border-slate-800 dark:bg-slate-950">
          Actualizado en tiempo real
        </div>
      </div>

      {summary && (
        <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
          <div className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/70">
            <p className="text-xs uppercase tracking-[0.2em] text-slate-400">Clientes</p>
            <p className="mt-2 text-2xl font-semibold">{summary.totalCustomers}</p>
          </div>
          <div className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/70">
            <p className="text-xs uppercase tracking-[0.2em] text-slate-400">Reservas</p>
            <p className="mt-2 text-2xl font-semibold">{summary.totalReservations}</p>
          </div>
          <div className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/70">
            <p className="text-xs uppercase tracking-[0.2em] text-slate-400">Pagos</p>
            <p className="mt-2 text-2xl font-semibold">{summary.totalPayments}</p>
          </div>
          {adminUser ? (
            <div className="rounded-2xl border border-indigo-200 bg-indigo-50 p-4 shadow-sm dark:border-indigo-500/30 dark:bg-indigo-500/10">
              <p className="text-xs uppercase tracking-[0.2em] text-indigo-500">Ingresos</p>
              <p className="mt-2 text-2xl font-semibold">${summary.totalRevenue.toFixed(2)}</p>
            </div>
          ) : (
            <div className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/70">
              <p className="text-xs uppercase tracking-[0.2em] text-slate-400">Operación</p>
              <p className="mt-2 text-2xl font-semibold">En curso</p>
            </div>
          )}
        </div>
      )}

      <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
        <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <h3 className="text-lg font-semibold">Búsqueda rápida</h3>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Encuentra vouchers, clientes o pagos sin salir del dashboard.
            </p>
          </div>
          <form onSubmit={handleSearch} className="flex w-full max-w-md gap-2">
            <input
              type="text"
              value={searchQuery}
              onChange={(event) => setSearchQuery(event.target.value)}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              placeholder="Buscar por nombre, voucher o método de pago"
            />
            <button
              type="submit"
              className="rounded-xl bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500"
            >
              Buscar
            </button>
          </form>
        </div>

        {searchResults && (
          <div className="mt-6 grid gap-4 lg:grid-cols-3">
            <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-950/50">
              <h4 className="text-sm font-semibold">Clientes</h4>
              <ul className="mt-3 space-y-2 text-sm text-slate-600 dark:text-slate-300">
                {searchResults.customers.length === 0 ? (
                  <li className="text-slate-500">Sin resultados.</li>
                ) : (
                  searchResults.customers.map((customer) => (
                    <li key={customer.id} className="rounded-xl bg-white p-3 shadow-sm dark:bg-slate-900/70">
                      <p className="font-medium">{customer.fullName}</p>
                      <p className="text-xs text-slate-500">{customer.email || "Sin email"}</p>
                    </li>
                  ))
                )}
              </ul>
            </div>

            <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-950/50">
              <h4 className="text-sm font-semibold">Vouchers</h4>
              <ul className="mt-3 space-y-2 text-sm text-slate-600 dark:text-slate-300">
                {searchResults.vouchers.length === 0 ? (
                  <li className="text-slate-500">Sin resultados.</li>
                ) : (
                  searchResults.vouchers.map((voucher) => (
                    <li key={voucher.id} className="rounded-xl bg-white p-3 shadow-sm dark:bg-slate-900/70">
                      <p className="font-medium">{voucher.referenceCode}</p>
                      <p className="text-xs text-slate-500">
                        {voucher.customerName} · {voucher.status}
                      </p>
                    </li>
                  ))
                )}
              </ul>
            </div>

            <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-950/50">
              <h4 className="text-sm font-semibold">Pagos</h4>
              <ul className="mt-3 space-y-2 text-sm text-slate-600 dark:text-slate-300">
                {searchResults.payments.length === 0 ? (
                  <li className="text-slate-500">Sin resultados.</li>
                ) : (
                  searchResults.payments.map((payment) => (
                    <li key={payment.id} className="rounded-xl bg-white p-3 shadow-sm dark:bg-slate-900/70">
                      <p className="font-medium">
                        ${Number(payment.amount).toFixed(2)}
                      </p>
                      <p className="text-xs text-slate-500">
                        {payment.reservationCode} · {payment.method}
                      </p>
                    </li>
                  ))
                )}
              </ul>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
