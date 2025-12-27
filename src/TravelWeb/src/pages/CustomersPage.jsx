import { useEffect, useState } from "react";
import { apiRequest } from "../api";

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
  const [error, setError] = useState("");

  const loadCustomers = () => {
    apiRequest("/api/customers")
      .then(setCustomers)
      .catch(() => setError("No se pudieron cargar los clientes."));
  };

  useEffect(() => {
    loadCustomers();
  }, []);

  const handleChange = (event) => {
    setForm((prev) => ({ ...prev, [event.target.name]: event.target.value }));
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    setError("");
    try {
      await apiRequest("/api/customers", {
        method: "POST",
        body: JSON.stringify(form),
      });
      setForm(initialForm);
      loadCustomers();
    } catch {
      setError("No se pudo guardar el cliente.");
    }
  };

  return (
    <div>
      <h2 className="text-2xl font-semibold">Clientes</h2>
      <p className="mt-1 text-sm text-slate-400">
        Registra y administra los datos de tus pasajeros.
      </p>

      <form className="mt-6 grid gap-4 rounded-xl border border-slate-800 bg-slate-900 p-4 md:grid-cols-2" onSubmit={handleSubmit}>
        <input
          name="fullName"
          placeholder="Nombre completo"
          value={form.fullName}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
          required
        />
        <input
          name="email"
          placeholder="Email"
          value={form.email}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
        />
        <input
          name="phone"
          placeholder="Teléfono"
          value={form.phone}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
        />
        <input
          name="documentNumber"
          placeholder="Documento"
          value={form.documentNumber}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
        />
        <input
          name="address"
          placeholder="Dirección"
          value={form.address}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
        />
        <input
          name="notes"
          placeholder="Notas"
          value={form.notes}
          onChange={handleChange}
          className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2"
        />
        {error && <p className="text-sm text-rose-400 md:col-span-2">{error}</p>}
        <button
          type="submit"
          className="rounded-lg bg-indigo-500 px-4 py-2 text-sm font-semibold text-white hover:bg-indigo-600 md:col-span-2"
        >
          Guardar cliente
        </button>
      </form>

      <div className="mt-6 overflow-hidden rounded-xl border border-slate-800">
        <table className="w-full text-left text-sm">
          <thead className="bg-slate-900 text-slate-300">
            <tr>
              <th className="px-4 py-3">Nombre</th>
              <th className="px-4 py-3">Email</th>
              <th className="px-4 py-3">Documento</th>
              <th className="px-4 py-3">Teléfono</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-800 bg-slate-950">
            {customers.map((customer) => (
              <tr key={customer.id} className="hover:bg-slate-900/50">
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
