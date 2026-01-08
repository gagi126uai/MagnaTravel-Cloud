import { useEffect, useState } from "react";
import { apiRequest } from "../api";
import { showError, showSuccess } from "../alerts";

const emptySupplier = {
  name: "",
  email: "",
  phone: "",
  notes: "",
};

export default function SuppliersPage() {
  const [suppliers, setSuppliers] = useState([]);
  const [formData, setFormData] = useState(emptySupplier);
  const [editingId, setEditingId] = useState(null);

  const loadSuppliers = async () => {
    try {
      const data = await apiRequest("/api/suppliers");
      setSuppliers(data);
    } catch (error) {
      showError(error.message || "No se pudieron cargar los proveedores.");
    }
  };

  useEffect(() => {
    loadSuppliers();
  }, []);

  const handleChange = (field, value) => {
    setFormData((prev) => ({ ...prev, [field]: value }));
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    const payload = {
      name: formData.name.trim(),
      email: formData.email.trim() || null,
      phone: formData.phone.trim() || null,
      notes: formData.notes.trim() || null,
    };

    try {
      if (editingId) {
        await apiRequest(`/api/suppliers/${editingId}`, {
          method: "PUT",
          body: JSON.stringify(payload),
        });
        showSuccess("Proveedor actualizado.");
      } else {
        await apiRequest("/api/suppliers", {
          method: "POST",
          body: JSON.stringify(payload),
        });
        showSuccess("Proveedor creado.");
      }
      setFormData(emptySupplier);
      setEditingId(null);
      await loadSuppliers();
    } catch (error) {
      showError(error.message || "No se pudo guardar el proveedor.");
    }
  };

  const handleEdit = (supplier) => {
    setEditingId(supplier.id);
    setFormData({
      name: supplier.name ?? "",
      email: supplier.email ?? "",
      phone: supplier.phone ?? "",
      notes: supplier.notes ?? "",
    });
  };

  const handleDelete = async (id) => {
    try {
      await apiRequest(`/api/suppliers/${id}`, { method: "DELETE" });
      showSuccess("Proveedor eliminado.");
      await loadSuppliers();
    } catch (error) {
      showError(error.message || "No se pudo eliminar el proveedor.");
    }
  };

  return (
    <div className="space-y-6">
      <header>
        <h1 className="text-2xl font-semibold">Proveedores</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Carga y gestiona proveedores turísticos.
        </p>
      </header>

      <div className="grid gap-6 lg:grid-cols-[1.3fr_0.7fr]">
        <section className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
          <div className="flex items-center justify-between">
            <h2 className="text-lg font-semibold">Listado</h2>
            <span className="rounded-full bg-slate-100 px-3 py-1 text-xs text-slate-500 dark:bg-slate-800 dark:text-slate-300">
              {suppliers.length} proveedores
            </span>
          </div>
          <div className="mt-4 overflow-x-auto">
            <table className="min-w-full text-left text-sm text-slate-200">
              <thead className="text-xs uppercase text-slate-500 dark:text-slate-400">
                <tr>
                  <th className="px-3 py-2">Nombre</th>
                  <th className="px-3 py-2">Email</th>
                  <th className="px-3 py-2">Teléfono</th>
                  <th className="px-3 py-2 text-right">Acciones</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-200 text-slate-700 dark:divide-slate-800 dark:text-slate-200">
                {suppliers.length === 0 ? (
                  <tr>
                    <td className="px-3 py-4 text-slate-400" colSpan="4">
                      No hay proveedores cargados.
                    </td>
                  </tr>
                ) : (
                  suppliers.map((supplier) => (
                    <tr key={supplier.id}>
                      <td className="px-3 py-3">{supplier.name}</td>
                      <td className="px-3 py-3">{supplier.email || "-"}</td>
                      <td className="px-3 py-3">{supplier.phone || "-"}</td>
                      <td className="px-3 py-3 text-right">
                        <div className="flex justify-end gap-2">
                          <button
                            type="button"
                            onClick={() => handleEdit(supplier)}
                            className="rounded-lg border border-slate-200 px-3 py-1 text-xs text-slate-500 hover:bg-slate-100 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
                          >
                            Editar
                          </button>
                          <button
                            type="button"
                            onClick={() => handleDelete(supplier.id)}
                            className="rounded-lg border border-rose-500/40 px-3 py-1 text-xs text-rose-200 hover:bg-rose-500/10"
                          >
                            Eliminar
                          </button>
                        </div>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </section>

        <section className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
          <h2 className="text-lg font-semibold">
            {editingId ? "Editar proveedor" : "Nuevo proveedor"}
          </h2>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Gestiona contactos clave para reservas.
          </p>
          <form onSubmit={handleSubmit} className="mt-4 space-y-3">
            <input
              type="text"
              value={formData.name}
              onChange={(event) => handleChange("name", event.target.value)}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              placeholder="Nombre del proveedor"
              required
            />
            <input
              type="email"
              value={formData.email}
              onChange={(event) => handleChange("email", event.target.value)}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              placeholder="Email"
            />
            <input
              type="text"
              value={formData.phone}
              onChange={(event) => handleChange("phone", event.target.value)}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              placeholder="Teléfono"
            />
            <textarea
              value={formData.notes}
              onChange={(event) => handleChange("notes", event.target.value)}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              placeholder="Notas"
              rows="3"
            />
            <button
              type="submit"
              className="w-full rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500"
            >
              {editingId ? "Guardar cambios" : "Crear proveedor"}
            </button>
          </form>
        </section>
      </div>
    </div>
  );
}
