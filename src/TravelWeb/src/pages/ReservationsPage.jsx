import { useEffect, useState } from "react";
import { apiRequest } from "../api";
import { showError, showSuccess } from "../alerts";

const initialForm = {
  referenceCode: "",
  productType: "Flight",
  departureDate: "",
  returnDate: "",
  basePrice: "",
  commission: "",
  totalAmount: "",
  supplierName: "",
  customerId: "",
};

const initialStatusForm = {
  reservationId: "",
  status: "",
};

export default function ReservationsPage() {
  const [reservations, setReservations] = useState([]);
  const [customers, setCustomers] = useState([]);
  const [form, setForm] = useState(initialForm);
  const [statusForm, setStatusForm] = useState(initialStatusForm);

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

  const handleStatusChange = (event) => {
    setStatusForm((prev) => ({ ...prev, [event.target.name]: event.target.value }));
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

  const handleStatusSubmit = async (event) => {
    event.preventDefault();
    try {
      await apiRequest(`/api/reservations/${statusForm.reservationId}/status`, {
        method: "PUT",
        body: JSON.stringify({
          status: statusForm.status,
        }),
      });
      setStatusForm(initialStatusForm);
      loadData();
      await showSuccess("Estado actualizado correctamente.");
    } catch {
      await showError("No se pudo actualizar el estado.");
    }
  };

  const currentReservation = reservations.find(
    (reservation) => String(reservation.id) === statusForm.reservationId
  );

  const statusOptions = () => {
    if (!currentReservation) return [];
    if (currentReservation.status === "Draft") {
      return ["Draft", "Confirmed"];
    }
    if (currentReservation.status === "Confirmed") {
      return ["Confirmed", "Cancelled"];
    }
    return ["Cancelled"];
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

      <form
        className="grid gap-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60 md:grid-cols-3"
        onSubmit={handleStatusSubmit}
      >
        <select
          name="reservationId"
          value={statusForm.reservationId}
          onChange={(event) => {
            handleStatusChange(event);
            setStatusForm((prev) => ({ ...prev, status: "" }));
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
        <select
          name="status"
          value={statusForm.status}
          onChange={handleStatusChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
          required
        >
          <option value="" disabled>
            Selecciona estado
          </option>
          {statusOptions().map((status) => (
            <option key={status} value={status}>
              {status}
            </option>
          ))}
        </select>
        <button
          type="submit"
          className="rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500"
        >
          Actualizar estado
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
