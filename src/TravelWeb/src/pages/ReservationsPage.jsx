import { useEffect, useState } from "react";
import { apiRequest } from "../api";

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
  const [error, setError] = useState("");

  const loadData = async () => {
    try {
      const [reservationsData, customersData] = await Promise.all([
        apiRequest("/api/reservations"),
        apiRequest("/api/customers"),
      ]);
      setReservations(reservationsData);
      setCustomers(customersData);
    } catch {
      setError("No se pudieron cargar las reservas.");
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
    setError("");
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
    } catch {
      setError("No se pudo guardar la reserva.");
    }
  };

  return (
    <div>
      <h2 className="text-2xl font-semibold">Reservas</h2>
      <p className="mt-1 text-sm text-slate-400">
        Registra ventas de vuelos, hoteles o paquetes.
      </p>

      <form
        className="mt-6 grid gap-4 rounded-xl border border-slate-800 bg-slate-900 p-4 md:grid-cols-3"
        onSubmit={handleSubmit}
      >
        <input
          name="referenceCode"
          placeholder="Código de reserva"
          value={form.referenceCode}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
          required
        />
        <select
          name="status"
          value={form.status}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
        >
          <option value="Draft">Borrador</option>
          <option value="Confirmed">Confirmada</option>
          <option value="Cancelled">Cancelada</option>
        </select>
        <select
          name="productType"
          value={form.productType}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
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
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
          required
        />
        <input
          name="returnDate"
          type="date"
          value={form.returnDate}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
        />
        <input
          name="supplierName"
          placeholder="Proveedor"
          value={form.supplierName}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
        />
        <input
          name="basePrice"
          placeholder="Precio base"
          value={form.basePrice}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
        />
        <input
          name="commission"
          placeholder="Comisión"
          value={form.commission}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
        />
        <input
          name="totalAmount"
          placeholder="Total a cobrar"
          value={form.totalAmount}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
        />
        <select
          name="customerId"
          value={form.customerId}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2 md:col-span-2"
          required
        >
          <option value="">Selecciona cliente</option>
          {customers.map((customer) => (
            <option key={customer.id} value={customer.id}>
              {customer.fullName}
            </option>
          ))}
        </select>
        {error && <p className="text-sm text-rose-400 md:col-span-3">{error}</p>}
        <button
          type="submit"
          className="rounded-lg bg-indigo-500 px-4 py-2 text-sm font-semibold text-white hover:bg-indigo-600 md:col-span-3"
        >
          Guardar reserva
        </button>
      </form>

      <div className="mt-6 overflow-hidden rounded-xl border border-slate-800">
        <table className="w-full text-left text-sm">
          <thead className="bg-slate-900 text-slate-300">
            <tr>
              <th className="px-4 py-3">Código</th>
              <th className="px-4 py-3">Cliente</th>
              <th className="px-4 py-3">Tipo</th>
              <th className="px-4 py-3">Total</th>
              <th className="px-4 py-3">Estado</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-800 bg-slate-950">
            {reservations.map((reservation) => (
              <tr key={reservation.id} className="hover:bg-slate-900/50">
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
