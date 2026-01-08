import { useEffect, useMemo, useState } from "react";
import { apiRequest } from "../api";
import { showError, showSuccess } from "../alerts";

const initialTariffForm = {
  name: "",
  description: "",
  productType: "General",
  currency: "USD",
  defaultPrice: "",
  isActive: true,
};

const initialValidityForm = {
  tariffId: "",
  startDate: "",
  endDate: "",
  price: "",
  isActive: true,
  notes: "",
};

export default function TariffsPage() {
  const [tariffs, setTariffs] = useState([]);
  const [tariffForm, setTariffForm] = useState(initialTariffForm);
  const [validityForm, setValidityForm] = useState(initialValidityForm);
  const [validities, setValidities] = useState([]);
  const [selectedTariffId, setSelectedTariffId] = useState("");

  const currencyOptions = useMemo(() => ["ARS", "USD", "EUR"], []);
  const productOptions = useMemo(() => ["General", "Flight", "Hotel", "Package", "Insurance"], []);

  const loadTariffs = () => {
    apiRequest("/api/tariffs")
      .then((data) => {
        setTariffs(data);
        if (data.length > 0 && !selectedTariffId) {
          setSelectedTariffId(String(data[0].id));
          setValidityForm((prev) => ({ ...prev, tariffId: String(data[0].id) }));
        }
      })
      .catch(() => {
        showError("No se pudieron cargar los tarifarios.");
      });
  };

  const loadValidities = (tariffId) => {
    if (!tariffId) {
      setValidities([]);
      return;
    }

    apiRequest(`/api/tariffs/${tariffId}/validities`)
      .then(setValidities)
      .catch(() => {
        showError("No se pudieron cargar las vigencias.");
      });
  };

  useEffect(() => {
    loadTariffs();
  }, []);

  useEffect(() => {
    if (selectedTariffId) {
      loadValidities(selectedTariffId);
    }
  }, [selectedTariffId]);

  const handleTariffChange = (event) => {
    const { name, value, type, checked } = event.target;
    setTariffForm((prev) => ({ ...prev, [name]: type === "checkbox" ? checked : value }));
  };

  const handleValidityChange = (event) => {
    const { name, value, type, checked } = event.target;
    setValidityForm((prev) => ({ ...prev, [name]: type === "checkbox" ? checked : value }));
  };

  const handleTariffSubmit = async (event) => {
    event.preventDefault();
    try {
      await apiRequest("/api/tariffs", {
        method: "POST",
        body: JSON.stringify({
          ...tariffForm,
          currency: tariffForm.currency || null,
          defaultPrice: Number(tariffForm.defaultPrice),
        }),
      });
      setTariffForm(initialTariffForm);
      loadTariffs();
      await showSuccess("Tarifario guardado correctamente.");
    } catch {
      await showError("No se pudo guardar el tarifario.");
    }
  };

  const handleValiditySubmit = async (event) => {
    event.preventDefault();
    if (!validityForm.tariffId) {
      await showError("Selecciona un tarifario para agregar una vigencia.");
      return;
    }

    try {
      await apiRequest(`/api/tariffs/${validityForm.tariffId}/validities`, {
        method: "POST",
        body: JSON.stringify({
          startDate: `${validityForm.startDate}T00:00:00Z`,
          endDate: `${validityForm.endDate}T00:00:00Z`,
          price: Number(validityForm.price),
          isActive: validityForm.isActive,
          notes: validityForm.notes,
        }),
      });
      setValidityForm((prev) => ({ ...initialValidityForm, tariffId: prev.tariffId }));
      loadValidities(validityForm.tariffId);
      await showSuccess("Vigencia registrada correctamente.");
    } catch {
      await showError("No se pudo guardar la vigencia.");
    }
  };

  return (
    <div className="space-y-8">
      <div>
        <h2 className="text-2xl font-semibold">Tarifarios</h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          Gestiona los tarifarios y sus vigencias por moneda.
        </p>
      </div>

      <div className="grid gap-6 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,0.9fr)]">
        <form
          className="grid gap-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60"
          onSubmit={handleTariffSubmit}
        >
          <input
            name="name"
            placeholder="Nombre del tarifario"
            value={tariffForm.name}
            onChange={handleTariffChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            required
          />
          <input
            name="description"
            placeholder="Descripción"
            value={tariffForm.description}
            onChange={handleTariffChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
          />
          <select
            name="productType"
            value={tariffForm.productType}
            onChange={handleTariffChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
          >
            {productOptions.map((option) => (
              <option key={option} value={option}>
                {option}
              </option>
            ))}
          </select>
          <div className="grid gap-4 md:grid-cols-2">
            <select
              name="currency"
              value={tariffForm.currency}
              onChange={handleTariffChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            >
              <option value="">Sin moneda</option>
              {currencyOptions.map((option) => (
                <option key={option} value={option}>
                  {option}
                </option>
              ))}
            </select>
            <input
              name="defaultPrice"
              type="number"
              min="0"
              step="0.01"
              placeholder="Precio base"
              value={tariffForm.defaultPrice}
              onChange={handleTariffChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              required
            />
          </div>
          <label className="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-300">
            <input
              type="checkbox"
              name="isActive"
              checked={tariffForm.isActive}
              onChange={handleTariffChange}
              className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
            />
            Tarifario activo
          </label>
          <button
            type="submit"
            className="rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500"
          >
            Guardar tarifario
          </button>
        </form>

        <div className="space-y-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
          <div>
            <h3 className="text-lg font-semibold">Listado de tarifarios</h3>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Selecciona uno para ver sus vigencias.
            </p>
          </div>
          <div className="space-y-3">
            {tariffs.map((tariff) => (
              <button
                key={tariff.id}
                type="button"
                onClick={() => {
                  setSelectedTariffId(String(tariff.id));
                  setValidityForm((prev) => ({ ...prev, tariffId: String(tariff.id) }));
                }}
                className={`w-full rounded-2xl border px-4 py-3 text-left text-sm shadow-sm transition ${
                  String(tariff.id) === selectedTariffId
                    ? "border-indigo-500 bg-indigo-50 text-indigo-700 dark:border-indigo-400 dark:bg-indigo-500/10 dark:text-indigo-200"
                    : "border-slate-200 bg-white text-slate-700 hover:border-indigo-200 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-200"
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className="font-semibold">{tariff.name}</span>
                  <span className="text-xs uppercase tracking-widest">{tariff.currency || "Sin moneda"}</span>
                </div>
                <div className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                  Base: {tariff.defaultPrice.toLocaleString("es-AR", {
                    style: "currency",
                    currency: tariff.currency || "USD",
                  })}
                </div>
                <div className="mt-1 text-xs text-slate-400">{tariff.productType}</div>
              </button>
            ))}
            {tariffs.length === 0 && (
              <p className="rounded-xl bg-slate-50 px-4 py-3 text-sm text-slate-500 dark:bg-slate-900/40 dark:text-slate-400">
                Aún no hay tarifarios registrados.
              </p>
            )}
          </div>
        </div>
      </div>

      <div className="grid gap-6 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]">
        <form
          className="grid gap-4 rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60"
          onSubmit={handleValiditySubmit}
        >
          <div>
            <h3 className="text-lg font-semibold">Registrar vigencia</h3>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Define el rango de fechas y el precio asociado.
            </p>
          </div>
          <select
            name="tariffId"
            value={validityForm.tariffId}
            onChange={(event) => {
              handleValidityChange(event);
              setSelectedTariffId(event.target.value);
            }}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            required
          >
            <option value="" disabled>
              Selecciona tarifario
            </option>
            {tariffs.map((tariff) => (
              <option key={tariff.id} value={tariff.id}>
                {tariff.name}
              </option>
            ))}
          </select>
          <div className="grid gap-4 md:grid-cols-2">
            <input
              type="date"
              name="startDate"
              value={validityForm.startDate}
              onChange={handleValidityChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              required
            />
            <input
              type="date"
              name="endDate"
              value={validityForm.endDate}
              onChange={handleValidityChange}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              required
            />
          </div>
          <input
            type="number"
            name="price"
            min="0"
            step="0.01"
            placeholder="Precio"
            value={validityForm.price}
            onChange={handleValidityChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
            required
          />
          <input
            name="notes"
            placeholder="Notas"
            value={validityForm.notes}
            onChange={handleValidityChange}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
          />
          <label className="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-300">
            <input
              type="checkbox"
              name="isActive"
              checked={validityForm.isActive}
              onChange={handleValidityChange}
              className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
            />
            Vigencia activa
          </label>
          <button
            type="submit"
            className="rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 transition hover:bg-indigo-500"
          >
            Guardar vigencia
          </button>
        </form>

        <div className="overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
          <table className="w-full text-left text-sm">
            <thead className="bg-slate-50 text-slate-500 dark:bg-slate-950 dark:text-slate-300">
              <tr>
                <th className="px-4 py-3">Rango</th>
                <th className="px-4 py-3">Precio</th>
                <th className="px-4 py-3">Estado</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-200 bg-white dark:divide-slate-800 dark:bg-slate-900/40">
              {validities.map((validity) => (
                <tr key={validity.id} className="hover:bg-slate-50 dark:hover:bg-slate-900/60">
                  <td className="px-4 py-3">
                    {new Date(validity.startDate).toLocaleDateString()} -
                    {" "}
                    {new Date(validity.endDate).toLocaleDateString()}
                  </td>
                  <td className="px-4 py-3">
                    {validity.price.toLocaleString("es-AR", {
                      style: "currency",
                      currency:
                        tariffs.find((tariff) => String(tariff.id) === selectedTariffId)?.currency ||
                        "USD",
                    })}
                  </td>
                  <td className="px-4 py-3">
                    <span
                      className={`rounded-full px-3 py-1 text-xs font-semibold ${
                        validity.isActive
                          ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-200"
                          : "bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-300"
                      }`}
                    >
                      {validity.isActive ? "Activa" : "Inactiva"}
                    </span>
                  </td>
                </tr>
              ))}
              {validities.length === 0 && (
                <tr>
                  <td className="px-4 py-4 text-sm text-slate-500 dark:text-slate-400" colSpan={3}>
                    No hay vigencias para el tarifario seleccionado.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
