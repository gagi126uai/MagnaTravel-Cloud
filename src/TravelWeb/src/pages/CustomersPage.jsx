import { useEffect, useState } from "react";
import { apiRequest } from "../api";
import { showError, showSuccess } from "../alerts";

const initialForm = {
  fullName: "",
  email: "",
  phone: "",
  documentNumber: "",
  address: "",
  notes: "",
};

export default function CustomersPage() {
  const [customers, setCustomers] = useState([]);
  const [form, setForm] = useState(initialForm);

  const loadCustomers = () => {
    apiRequest("/api/customers")
      .then(setCustomers)
      .catch(() => {
        showError("No se pudieron cargar los clientes.");
      });
  };

  useEffect(() => {
    loadCustomers();
  }, []);

  const handleChange = (event) => {
    setForm((prev) => ({ ...prev, [event.target.name]: event.target.value }));
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    try {
      await apiRequest("/api/customers", {
        method: "POST",
        body: JSON.stringify(form),
      });
      setForm(initialForm);
      loadCustomers();
      await showSuccess("Cliente guardado correctamente.");
    } catch {
      await showError("No se pudo guardar el cliente.");
    }
  };

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-2xl font-semibold">Clientes</h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          Registra y administra los datos de tus pasajeros.
        </p>
      </div>

      <form
        className="grid gap-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60 md:grid-cols-2"
        onSubmit={handleSubmit}
      >
        <input
          name="fullName"
          placeholder="Nombre completo"
          value={form.fullName}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
          required
        />
        <input
          name="email"
          placeholder="Email"
          value={form.email}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        />
        <input
          name="phone"
          placeholder="Teléfono"
          value={form.phone}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        />
        <input
          name="documentNumber"
          placeholder="Documento"
          value={form.documentNumber}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        />
        <input
          name="address"
          placeholder="Dirección"
          value={form.address}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        />
        <input
          name="notes"
          placeholder="Notas"
          value={form.notes}
          onChange={handleChange}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
        />
        <button
          type="submit"
          className="rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500 md:col-span-2"
        >
          Guardar cliente
        </button>
      </form>

      <div className="overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
        <table className="w-full text-left text-sm">
          <thead className="bg-slate-50 text-slate-500 dark:bg-slate-950 dark:text-slate-300">
            <tr>
              <th className="px-4 py-3">Nombre</th>
              <th className="px-4 py-3">Email</th>
              <th className="px-4 py-3">Documento</th>
              <th className="px-4 py-3">Teléfono</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-200 bg-white dark:divide-slate-800 dark:bg-slate-900/40">
            {customers.map((customer) => (
              <tr key={customer.id} className="hover:bg-slate-50 dark:hover:bg-slate-900/60">
                <td className="px-4 py-3">{customer.fullName}</td>
                <td className="px-4 py-3">{customer.email}</td>
                <td className="px-4 py-3">{customer.documentNumber}</td>
                <td className="px-4 py-3">{customer.phone}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
