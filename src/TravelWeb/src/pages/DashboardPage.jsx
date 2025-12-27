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
    <div>
      <h2 className="text-2xl font-semibold">Dashboard</h2>
      <p className="mt-1 text-sm text-slate-400">
        Resumen rápido de tu operación.
      </p>

      {summary && (
        <div className="mt-6 grid gap-4 md:grid-cols-2 lg:grid-cols-4">
          <div className="rounded-xl border border-slate-800 bg-slate-900/80 p-4 shadow-lg shadow-slate-950/30">
            <p className="text-xs text-slate-400">Clientes</p>
            <p className="text-2xl font-semibold">{summary.totalCustomers}</p>
          </div>
          <div className="rounded-xl border border-slate-800 bg-slate-900/80 p-4 shadow-lg shadow-slate-950/30">
            <p className="text-xs text-slate-400">Reservas</p>
            <p className="text-2xl font-semibold">{summary.totalReservations}</p>
          </div>
          <div className="rounded-xl border border-slate-800 bg-slate-900/80 p-4 shadow-lg shadow-slate-950/30">
            <p className="text-xs text-slate-400">Pagos</p>
            <p className="text-2xl font-semibold">{summary.totalPayments}</p>
          </div>
          {adminUser ? (
            <div className="rounded-xl border border-slate-800 bg-slate-900/80 p-4 shadow-lg shadow-slate-950/30">
              <p className="text-xs text-slate-400">Ingresos</p>
              <p className="text-2xl font-semibold">${summary.totalRevenue.toFixed(2)}</p>
            </div>
          ) : (
            <div className="rounded-xl border border-slate-800 bg-slate-900/80 p-4 shadow-lg shadow-slate-950/30">
              <p className="text-xs text-slate-400">Operación</p>
              <p className="text-2xl font-semibold">En curso</p>
            </div>
          )}
        </div>
      )}

      <div className="mt-8 rounded-2xl border border-slate-800 bg-slate-950/70 p-6">
        <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <h3 className="text-lg font-semibold text-white">Búsqueda rápida</h3>
            <p className="text-sm text-slate-400">
              Encuentra vouchers, clientes o pagos sin salir del dashboard.
            </p>
          </div>
          <form onSubmit={handleSearch} className="flex w-full max-w-md gap-2">
            <input
              type="text"
              value={searchQuery}
              onChange={(event) => setSearchQuery(event.target.value)}
              className="w-full rounded-lg border border-slate-800 bg-slate-900/70 px-3 py-2 text-sm text-white"
              placeholder="Buscar por nombre, voucher o método de pago"
            />
            <button
              type="submit"
              className="rounded-lg bg-indigo-500 px-4 py-2 text-sm font-semibold text-white hover:bg-indigo-400"
            >
              Buscar
            </button>
          </form>
        </div>

        {searchResults && (
          <div className="mt-6 grid gap-4 lg:grid-cols-3">
            <div className="rounded-xl border border-slate-800 bg-slate-900/70 p-4">
              <h4 className="text-sm font-semibold text-white">Clientes</h4>
              <ul className="mt-3 space-y-2 text-sm text-slate-300">
                {searchResults.customers.length === 0 ? (
                  <li className="text-slate-500">Sin resultados.</li>
                ) : (
                  searchResults.customers.map((customer) => (
                    <li key={customer.id} className="rounded-lg bg-slate-950/60 p-2">
                      <p className="font-medium text-white">{customer.fullName}</p>
                      <p className="text-xs text-slate-400">{customer.email || "Sin email"}</p>
                    </li>
                  ))
                )}
              </ul>
            </div>

            <div className="rounded-xl border border-slate-800 bg-slate-900/70 p-4">
              <h4 className="text-sm font-semibold text-white">Vouchers</h4>
              <ul className="mt-3 space-y-2 text-sm text-slate-300">
                {searchResults.vouchers.length === 0 ? (
                  <li className="text-slate-500">Sin resultados.</li>
                ) : (
                  searchResults.vouchers.map((voucher) => (
                    <li key={voucher.id} className="rounded-lg bg-slate-950/60 p-2">
                      <p className="font-medium text-white">{voucher.referenceCode}</p>
                      <p className="text-xs text-slate-400">
                        {voucher.customerName} · {voucher.status}
                      </p>
                    </li>
                  ))
                )}
              </ul>
            </div>

            <div className="rounded-xl border border-slate-800 bg-slate-900/70 p-4">
              <h4 className="text-sm font-semibold text-white">Pagos</h4>
              <ul className="mt-3 space-y-2 text-sm text-slate-300">
                {searchResults.payments.length === 0 ? (
                  <li className="text-slate-500">Sin resultados.</li>
                ) : (
                  searchResults.payments.map((payment) => (
                    <li key={payment.id} className="rounded-lg bg-slate-950/60 p-2">
                      <p className="font-medium text-white">
                        ${Number(payment.amount).toFixed(2)}
                      </p>
                      <p className="text-xs text-slate-400">
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
