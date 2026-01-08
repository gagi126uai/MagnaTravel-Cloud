import { useEffect, useState } from "react";
import { apiRequest } from "../api";
import { showError, showSuccess } from "../alerts";

const initialForm = {
  referenceCode: "",
  status: "Draft",
  productType: "Flight",
  departureDate: "",
  returnDate: "",
  basePrice: "",
  commission: "",
  totalAmount: "",
  supplierName: "",
  customerId: "",
};

export default function ReservationsPage() {
  const [reservations, setReservations] = useState([]);
  const [customers, setCustomers] = useState([]);
  const [form, setForm] = useState(initialForm);

  const loadData = async () => {
    try {
      const [reservationsData, customersData] = await Promise.all([
        apiRequest("/api/reservations"),
        apiRequest("/api/customers"),
      ]);
      setReservations(reservationsData);
      setCustomers(customersData);
    } catch {
      showError("No se pudieron cargar las reservas.");
    }
  };

  useEffect(() => {
    loadData();
  }, []);

  const handleChange = (event) => {
    setForm((prev) => ({ ...prev, [event.target.name]: event.target.value }));
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    try {
      await apiRequest("/api/reservations", {
        method: "POST",
        body: JSON.stringify({
          ...form,
          basePrice: Number(form.basePrice || 0),
          commission: Number(form.commission || 0),
          totalAmount: Number(form.totalAmount || 0),
          customerId: Number(form.customerId),
          departureDate: form.departureDate,
          returnDate: form.returnDate || null,
        }),
      });
      setForm(initialForm);
      loadData();
      await showSuccess("Reserva guardada correctamente.");
    } catch {
      await showError("No se pudo guardar la reserva.");
    }
  };

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-2xl font-semibold">Reservas</h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          Registra ventas de vuelos, hoteles o paquetes.
        </p>
      </div>

      <form
        className="grid gap-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60 md:grid-cols-3"
        onSubmit={handleSubmit}
      >
        <input
          name="referenceCode"
          placeholder="Código de reserva"
          value={form.referenceCode}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
          required
        />
        <select
          name="status"
          value={form.status}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        >
          <option value="Draft">Borrador</option>
          <option value="Confirmed">Confirmada</option>
          <option value="Cancelled">Cancelada</option>
        </select>
        <select
          name="productType"
          value={form.productType}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        >
          <option value="Flight">Vuelo</option>
          <option value="Hotel">Hotel</option>
          <option value="Package">Paquete</option>
          <option value="Insurance">Seguro</option>
        </select>
        <input
          name="departureDate"
          type="date"
          value={form.departureDate}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
          required
        />
        <input
          name="returnDate"
          type="date"
          value={form.returnDate}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        />
        <input
          name="supplierName"
          placeholder="Proveedor"
          value={form.supplierName}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        />
        <input
          name="basePrice"
          placeholder="Precio base"
          value={form.basePrice}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        />
        <input
          name="commission"
          placeholder="Comisión"
          value={form.commission}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        />
        <input
          name="totalAmount"
          placeholder="Total a cobrar"
          value={form.totalAmount}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        />
        <select
          name="customerId"
          value={form.customerId}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30 md:col-span-2"
          required
        >
          <option value="">Selecciona cliente</option>
          {customers.map((customer) => (
            <option key={customer.id} value={customer.id}>
              {customer.fullName}
            </option>
          ))}
        </select>
        <button
          type="submit"
          className="rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500 md:col-span-3"
        >
          Guardar reserva
        </button>
      </form>

      <div className="overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
        <table className="w-full text-left text-sm">
          <thead className="bg-slate-50 text-slate-500 dark:bg-slate-950 dark:text-slate-300">
            <tr>
              <th className="px-4 py-3">Código</th>
              <th className="px-4 py-3">Cliente</th>
              <th className="px-4 py-3">Tipo</th>
              <th className="px-4 py-3">Total</th>
              <th className="px-4 py-3">Estado</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-200 bg-white dark:divide-slate-800 dark:bg-slate-900/40">
            {reservations.map((reservation) => (
              <tr key={reservation.id} className="hover:bg-slate-50 dark:hover:bg-slate-900/60">
                <td className="px-4 py-3">{reservation.referenceCode}</td>
                <td className="px-4 py-3">{reservation.customer?.fullName}</td>
                <td className="px-4 py-3">{reservation.productType}</td>
                <td className="px-4 py-3">${reservation.totalAmount}</td>
                <td className="px-4 py-3">{reservation.status}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
