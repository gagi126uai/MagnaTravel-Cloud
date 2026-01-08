import { useEffect, useState } from "react";
import { apiRequest } from "../api";
import { showError, showSuccess } from "../alerts";

const initialForm = {
  reservationId: "",
  amount: "",
  method: "Card",
  status: "Pending",
};

export default function PaymentsPage() {
  const [reservations, setReservations] = useState([]);
  const [payments, setPayments] = useState([]);
  const [form, setForm] = useState(initialForm);

  const loadReservations = async () => {
    try {
      const data = await apiRequest("/api/reservations");
      setReservations(data);
    } catch {
      showError("No se pudieron cargar las reservas.");
    }
  };

  useEffect(() => {
    loadReservations();
  }, []);

  const handleChange = (event) => {
    setForm((prev) => ({ ...prev, [event.target.name]: event.target.value }));
  };

  const loadPayments = async (reservationId) => {
    if (!reservationId) return;
    try {
      const data = await apiRequest(`/api/payments/reservation/${reservationId}`);
      setPayments(data);
    } catch {
      showError("No se pudieron cargar los pagos.");
    }
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    try {
      await apiRequest("/api/payments", {
        method: "POST",
        body: JSON.stringify({
          reservationId: Number(form.reservationId),
          amount: Number(form.amount || 0),
          method: form.method,
          status: form.status,
        }),
      });
      setForm(initialForm);
      loadPayments(form.reservationId);
      await showSuccess("Pago registrado correctamente.");
    } catch {
      await showError("No se pudo registrar el pago.");
    }
  };

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-2xl font-semibold">Pagos</h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          Registra pagos parciales y controla saldos pendientes.
        </p>
      </div>

      <div className="grid gap-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60 md:grid-cols-2">
        <select
          name="reservationId"
          value={form.reservationId}
          onChange={(event) => {
            handleChange(event);
            loadPayments(event.target.value);
          }}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
          required
        >
          <option value="">Selecciona reserva</option>
          {reservations.map((reservation) => (
            <option key={reservation.id} value={reservation.id}>
              {reservation.referenceCode} - {reservation.customer?.fullName}
            </option>
          ))}
        </select>
        <input
          name="amount"
          placeholder="Monto"
          value={form.amount}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
          required
        />
        <select
          name="method"
          value={form.method}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        >
          <option value="Card">Tarjeta</option>
          <option value="Transfer">Transferencia</option>
          <option value="Cash">Efectivo</option>
        </select>
        <select
          name="status"
          value={form.status}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        >
          <option value="Pending">Pendiente</option>
          <option value="Partial">Parcial</option>
          <option value="Paid">Pagado</option>
        </select>
        <button
          type="button"
          onClick={handleSubmit}
          className="rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500 md:col-span-2"
        >
          Registrar pago
        </button>
      </div>

      <div className="overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
        <table className="w-full text-left text-sm">
          <thead className="bg-slate-50 text-slate-500 dark:bg-slate-950 dark:text-slate-300">
            <tr>
              <th className="px-4 py-3">Fecha</th>
              <th className="px-4 py-3">Monto</th>
              <th className="px-4 py-3">Método</th>
              <th className="px-4 py-3">Estado</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-200 bg-white dark:divide-slate-800 dark:bg-slate-900/40">
            {payments.map((payment) => (
              <tr key={payment.id} className="hover:bg-slate-50 dark:hover:bg-slate-900/60">
                <td className="px-4 py-3">
                  {new Date(payment.paidAt).toLocaleDateString()}
                </td>
                <td className="px-4 py-3">${payment.amount}</td>
                <td className="px-4 py-3">{payment.method}</td>
                <td className="px-4 py-3">{payment.status}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
