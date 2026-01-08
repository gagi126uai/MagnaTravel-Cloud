import { useEffect, useMemo, useState } from "react";
import { apiRequest } from "../api";
import { showError, showSuccess } from "../alerts";

const initialCupoForm = {
  name: "",
  productType: "Flight",
  travelDate: "",
  capacity: "",
  overbookingLimit: "0",
};

const initialAssignForm = {
  cupoId: "",
  quantity: "",
  reservationId: "",
};

export default function CuposPage() {
  const [cupos, setCupos] = useState([]);
  const [cupoForm, setCupoForm] = useState(initialCupoForm);
  const [assignForm, setAssignForm] = useState(initialAssignForm);

  const productTypes = useMemo(() => ["Flight", "Hotel", "Tour"], []);

  const loadCupos = () => {
    apiRequest("/api/cupos")
      .then((data) => {
        setCupos(data);
        if (data.length > 0 && !assignForm.cupoId) {
          setAssignForm((prev) => ({ ...prev, cupoId: String(data[0].id) }));
        }
      })
      .catch(() => {
        showError("No se pudieron cargar los cupos.");
      });
  };

  useEffect(() => {
    loadCupos();
  }, []);

  const handleCupoChange = (event) => {
    const { name, value } = event.target;
    setCupoForm((prev) => ({ ...prev, [name]: value }));
  };

  const handleAssignChange = (event) => {
    const { name, value } = event.target;
    setAssignForm((prev) => ({ ...prev, [name]: value }));
  };

  const handleCupoSubmit = async (event) => {
    event.preventDefault();
    try {
      await apiRequest("/api/cupos", {
        method: "POST",
        body: JSON.stringify({
          ...cupoForm,
          capacity: Number(cupoForm.capacity),
          overbookingLimit: Number(cupoForm.overbookingLimit),
          travelDate: `${cupoForm.travelDate}T00:00:00Z`,
        }),
      });
      setCupoForm(initialCupoForm);
      loadCupos();
      await showSuccess("Cupo creado correctamente.");
    } catch {
      await showError("No se pudo crear el cupo.");
    }
  };

  const handleAssignSubmit = async (event) => {
    event.preventDefault();
    if (!assignForm.cupoId) {
      await showError("Selecciona un cupo para asignar.");
      return;
    }

    try {
      await apiRequest(`/api/cupos/${assignForm.cupoId}/assign`, {
        method: "POST",
        body: JSON.stringify({
          quantity: Number(assignForm.quantity),
          reservationId: assignForm.reservationId ? Number(assignForm.reservationId) : null,
        }),
      });
      setAssignForm((prev) => ({ ...prev, quantity: "", reservationId: "" }));
      loadCupos();
      await showSuccess("Cupo asignado correctamente.");
    } catch (error) {
      await showError(error.message || "No se pudo asignar el cupo.");
    }
  };

  return (
    <div className="space-y-8">
      <div>
        <h2 className="text-2xl font-semibold">Cupos</h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          Administra la disponibilidad y sobreventa por operación.
        </p>
      </div>

      <div className="grid gap-6 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,0.9fr)]">
        <form
          className="grid gap-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60"
          onSubmit={handleCupoSubmit}
        >
          <input
            name="name"
            placeholder="Nombre del cupo"
            value={cupoForm.name}
            onChange={handleCupoChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            required
          />
          <div className="grid gap-4 md:grid-cols-2">
            <select
              name="productType"
              value={cupoForm.productType}
              onChange={handleCupoChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            >
              {productTypes.map((type) => (
                <option key={type} value={type}>
                  {type}
                </option>
              ))}
            </select>
            <input
              name="travelDate"
              type="date"
              value={cupoForm.travelDate}
              onChange={handleCupoChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              required
            />
          </div>
          <div className="grid gap-4 md:grid-cols-2">
            <input
              name="capacity"
              type="number"
              min="0"
              placeholder="Capacidad"
              value={cupoForm.capacity}
              onChange={handleCupoChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              required
            />
            <input
              name="overbookingLimit"
              type="number"
              min="0"
              placeholder="Sobreventa"
              value={cupoForm.overbookingLimit}
              onChange={handleCupoChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              required
            />
          </div>
          <button
            type="submit"
            className="rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500"
          >
            Guardar cupo
          </button>
        </form>

        <form
          className="grid gap-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60"
          onSubmit={handleAssignSubmit}
        >
          <div>
            <h3 className="text-lg font-semibold">Asignar cupos</h3>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Registra confirmaciones con control de sobreventa.
            </p>
          </div>
          <select
            name="cupoId"
            value={assignForm.cupoId}
            onChange={handleAssignChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            required
          >
            <option value="" disabled>
              Selecciona cupo
            </option>
            {cupos.map((cupo) => (
              <option key={cupo.id} value={cupo.id}>
                {cupo.name} · {new Date(cupo.travelDate).toLocaleDateString()}
              </option>
            ))}
          </select>
          <div className="grid gap-4 md:grid-cols-2">
            <input
              name="quantity"
              type="number"
              min="1"
              placeholder="Cantidad"
              value={assignForm.quantity}
              onChange={handleAssignChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              required
            />
            <input
              name="reservationId"
              type="number"
              min="1"
              placeholder="Reserva (opcional)"
              value={assignForm.reservationId}
              onChange={handleAssignChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            />
          </div>
          <button
            type="submit"
            className="rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500"
          >
            Confirmar cupo
          </button>
        </form>
      </div>

      <div className="overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
        <table className="w-full text-left text-sm">
          <thead className="bg-slate-50 text-slate-500 dark:bg-slate-950 dark:text-slate-300">
            <tr>
              <th className="px-4 py-3">Cupo</th>
              <th className="px-4 py-3">Fecha</th>
              <th className="px-4 py-3">Capacidad</th>
              <th className="px-4 py-3">Reservado</th>
              <th className="px-4 py-3">Sobreventa</th>
              <th className="px-4 py-3">Disponible</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-200 bg-white dark:divide-slate-800 dark:bg-slate-900/40">
            {cupos.map((cupo) => (
              <tr key={cupo.id} className="hover:bg-slate-50 dark:hover:bg-slate-900/60">
                <td className="px-4 py-3">
                  <p className="font-semibold">{cupo.name}</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400">{cupo.productType}</p>
                </td>
                <td className="px-4 py-3">{new Date(cupo.travelDate).toLocaleDateString()}</td>
                <td className="px-4 py-3">{cupo.capacity}</td>
                <td className="px-4 py-3">{cupo.reserved}</td>
                <td className="px-4 py-3">{cupo.overbookingLimit}</td>
                <td className="px-4 py-3">
                  <span
                    className={`rounded-full px-3 py-1 text-xs font-semibold ${
                      cupo.available > 0
                        ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-200"
                        : "bg-rose-100 text-rose-700 dark:bg-rose-500/20 dark:text-rose-200"
                    }`}
                  >
                    {cupo.available}
                  </span>
                </td>
              </tr>
            ))}
            {cupos.length === 0 && (
              <tr>
                <td className="px-4 py-4 text-sm text-slate-500 dark:text-slate-400" colSpan={6}>
                  No hay cupos registrados.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
