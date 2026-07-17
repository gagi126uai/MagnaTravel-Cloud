/**
 * Solapa "Datos" de la ficha del cliente — edición EN LÍNEA de identidad y condición
 * fiscal, sin ventana flotante (espejo exacto de `SupplierInlineEditForm` en
 * `SupplierAccountPage.jsx`, spec `docs/ux/2026-07-17-ficha-cliente-solapa-datos.md`).
 *
 * Reemplaza al modal del listado (`CustomerFormModal`) como lugar para editar los datos
 * DESDE LA FICHA del cliente; el modal del listado sigue existiendo para el alta.
 *
 * Esta solapa hace su PROPIO `GET /customers/{id}` (el overview de la cuenta no trae
 * `taxConditionId`/`documentNumber`/`isActive` — esos campos solo vienen del endpoint de
 * detalle del cliente).
 */
import { useCallback, useEffect, useState } from "react";
import { Loader2, RefreshCw, Search, XCircle } from "lucide-react";
import { api } from "../../../api";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import {
  TAX_CONDITION_OPTIONS,
  construirEstadoInicialDatosCliente,
  construirPayloadDatosCliente,
  debeDeshabilitarCuit,
  puedeGuardarDatosCliente,
} from "../lib/datosClienteLogic";

const inputClass =
  "w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 disabled:cursor-not-allowed disabled:bg-slate-50 disabled:text-slate-400 dark:bg-slate-950 dark:border-slate-800 dark:text-white dark:disabled:bg-slate-900";
const labelClass = "text-sm font-medium text-slate-700 dark:text-slate-300";

/**
 * Props:
 *   - customerPublicId: string — publicId del cliente (para GET/PUT).
 *   - taxIdLocked: boolean — veredicto del backend (overview.taxIdLocked, spec §3).
 *   - canEdit: boolean — hasPermission("clientes.edit"); sin esto, todo queda deshabilitado
 *     y el botón "Guardar cambios" no se muestra (spec §5, fila "Sin permiso de editar").
 *   - onGuardado: callback tras un guardado exitoso — el padre recarga el overview para
 *     que el banner ámbar y el encabezado reflejen los datos nuevos (spec §7).
 */
export function DatosClienteTab({ customerPublicId, taxIdLocked, canEdit, onGuardado }) {
  // ── Carga del detalle del cliente (fuente de los campos editables) ─────────
  const [loading, setLoading] = useState(true);
  const [errorCarga, setErrorCarga] = useState(null);
  const [formData, setFormData] = useState(construirEstadoInicialDatosCliente(null));
  // `notes` no se muestra en esta solapa, pero el PUT lo pisa completo si no viaja
  // (ver docstring de construirPayloadDatosCliente) — se guarda aparte para reinyectarlo.
  const [notasOriginales, setNotasOriginales] = useState(null);

  // ── Guardado ─────────────────────────────────────────────────────────────
  const [saving, setSaving] = useState(false);
  const [errorGuardado, setErrorGuardado] = useState(null);

  // ── Búsqueda AFIP del CUIT (mismo comportamiento que CustomerFormModal) ────
  const [afipResults, setAfipResults] = useState([]);
  const [loadingAfip, setLoadingAfip] = useState(false);

  const cargarCliente = useCallback(async () => {
    setLoading(true);
    setErrorCarga(null);
    try {
      const detalle = await api.get(`/customers/${customerPublicId}`);
      setFormData(construirEstadoInicialDatosCliente(detalle));
      setNotasOriginales(detalle?.notes ?? null);
    } catch (error) {
      setErrorCarga(getApiErrorMessage(error, "No se pudieron cargar los datos del cliente."));
    } finally {
      setLoading(false);
    }
  }, [customerPublicId]);

  // Se dispara al montar y cada vez que cambia el cliente activo (navegación entre fichas).
  useEffect(() => {
    cargarCliente();
  }, [cargarCliente]);

  const handleChange = (campo) => (event) => {
    setFormData((anterior) => ({ ...anterior, [campo]: event.target.value }));
  };

  const handleAfipSearch = async () => {
    const query = (formData.taxId || "").trim();
    if (query.length < 3) return;
    setLoadingAfip(true);
    try {
      const data = await api.get(`/fiscal/search?q=${encodeURIComponent(query)}`);
      setAfipResults(Array.isArray(data) ? data : []);
    } catch {
      // Servicio externo (padrón AFIP): una falla acá no debe tumbar la ficha del
      // cliente. El usuario simplemente sigue completando los datos a mano.
      setAfipResults([]);
    } finally {
      setLoadingAfip(false);
    }
  };

  const handleAfipSelect = (persona) => {
    setFormData((anterior) => ({
      ...anterior,
      fullName: persona.razonSocial || `${persona.apellido || ""} ${persona.nombre || ""}`.trim() || anterior.fullName,
      taxId: persona.id || anterior.taxId,
      taxConditionId: persona.taxConditionId || anterior.taxConditionId,
    }));
    setAfipResults([]);
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    // Doble candado: el botón ya está disabled sin permiso/campos obligatorios, pero un
    // segundo chequeo acá evita un envío doble por Enter en algún campo del formulario.
    if (!canEdit || saving || !puedeGuardarDatosCliente(formData)) return;

    setSaving(true);
    setErrorGuardado(null);
    try {
      await api.put(`/customers/${customerPublicId}`, construirPayloadDatosCliente(formData, notasOriginales));
      showSuccess("Datos del cliente guardados correctamente.");
      if (onGuardado) await onGuardado();
    } catch (error) {
      // La ficha queda abierta con todo lo cargado intacto (no se resetea formData):
      // el usuario reintenta desde el mismo botón (guía Ronda 2, 2026-06-06).
      setErrorGuardado(getApiErrorMessage(error, "No se pudo guardar. Revisá la conexión y probá de nuevo."));
    } finally {
      setSaving(false);
    }
  };

  const cuitDeshabilitado = !canEdit || debeDeshabilitarCuit(taxIdLocked);
  const camposDeshabilitados = !canEdit;

  // ── Estado: cargando el detalle del cliente ─────────────────────────────
  if (loading) {
    return (
      <div className="flex items-center justify-center gap-2 py-10 text-sm text-slate-400 dark:text-slate-500" data-testid="datos-cliente-loading">
        <Loader2 className="h-4 w-4 animate-spin" />
        Cargando datos del cliente…
      </div>
    );
  }

  // ── Estado: no se pudo cargar el detalle (toda la solapa depende de él) ──
  if (errorCarga) {
    return (
      <div className="flex flex-col items-center gap-3 py-10 text-center" data-testid="datos-cliente-load-error">
        <p className="text-sm text-rose-600 dark:text-rose-400">{errorCarga}</p>
        <button
          type="button"
          onClick={cargarCliente}
          className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
          data-testid="datos-cliente-load-retry"
        >
          <RefreshCw className="h-3.5 w-3.5" />
          Reintentar
        </button>
      </div>
    );
  }

  return (
    <form onSubmit={handleSubmit} className="max-w-2xl space-y-5">
      <div className="grid gap-4 sm:grid-cols-2">
        {/* Nombre completo: único campo de identidad obligatorio (spec §2) */}
        <div className="space-y-2 sm:col-span-2">
          <label className={labelClass}>Nombre completo *</label>
          <input
            type="text"
            required
            value={formData.fullName}
            onChange={handleChange("fullName")}
            disabled={camposDeshabilitados}
            placeholder="Ej: Fam. García"
            className={inputClass}
            data-testid="customer-datos-fullName"
          />
        </div>

        <div className="space-y-2">
          <label className={labelClass}>Documento / Pasaporte</label>
          <input
            type="text"
            value={formData.documentNumber}
            onChange={handleChange("documentNumber")}
            disabled={camposDeshabilitados}
            className={inputClass}
            data-testid="customer-datos-documentNumber"
          />
        </div>

        {/* CUIT / DNI: candado independiente del resto del formulario (spec §3) */}
        <div className="space-y-2">
          <label className={labelClass}>CUIT / DNI</label>
          <div className="relative">
            <input
              type="text"
              value={formData.taxId}
              onChange={(event) => {
                setFormData((anterior) => ({ ...anterior, taxId: event.target.value }));
                setAfipResults([]);
              }}
              disabled={cuitDeshabilitado}
              placeholder="20-30111222-3"
              className={`${inputClass} pr-10 font-mono`}
              data-testid="customer-datos-taxId"
            />
            <button
              type="button"
              onClick={handleAfipSearch}
              disabled={cuitDeshabilitado}
              title="Buscar en AFIP"
              className="absolute right-2 top-2 p-1 text-slate-400 hover:text-indigo-600 disabled:cursor-not-allowed disabled:hover:text-slate-400 transition-colors"
              data-testid="customer-datos-taxId-search"
            >
              {loadingAfip ? <Loader2 className="h-4 w-4 animate-spin text-indigo-500" /> : <Search className="h-4 w-4" />}
            </button>

            {afipResults.length > 0 && (
              <div className="absolute left-0 right-0 z-[100] mt-1 w-full overflow-hidden rounded-lg border border-slate-200 bg-white shadow-xl dark:border-slate-800 dark:bg-slate-900">
                <div className="flex items-center justify-between border-b border-slate-100 bg-slate-50 px-3 py-2 dark:border-slate-700 dark:bg-slate-800">
                  <span className="text-[10px] font-bold uppercase text-slate-500">Resultados AFIP</span>
                  <button type="button" onClick={() => setAfipResults([])} className="text-slate-400 hover:text-slate-600">
                    <XCircle className="h-3 w-3" />
                  </button>
                </div>
                <div className="max-h-48 overflow-y-auto">
                  {afipResults.map((persona, indice) => (
                    <button
                      key={indice}
                      type="button"
                      onClick={() => handleAfipSelect(persona)}
                      className="group w-full border-b border-slate-50 px-4 py-2 text-left transition-colors last:border-0 hover:bg-indigo-50 dark:border-slate-800 dark:hover:bg-indigo-900/30"
                    >
                      <div className="truncate text-sm font-medium text-slate-900 group-hover:text-indigo-600 dark:text-white">
                        {persona.razonSocial || `${persona.apellido || ""} ${persona.nombre || ""}`}
                      </div>
                      <div className="text-[10px] text-slate-500">{persona.id} • {persona.taxCondition}</div>
                    </button>
                  ))}
                </div>
              </div>
            )}
          </div>

          {/* Línea explicativa del candado — texto EXACTO de la spec §3, sin jerga ni
              derivación a "administración" (regla 2026-07-08). */}
          {cuitDeshabilitado && canEdit && (
            <p className="text-xs text-amber-700 dark:text-amber-400" data-testid="customer-datos-taxid-locked-note">
              🔒 El CUIT no se puede cambiar acá (los comprobantes ya salieron con ese CUIT); si el titular
              cambió de CUIT, registrá un cliente nuevo.
            </p>
          )}
        </div>

        {/* Condición fiscal: SIEMPRE editable, aunque el CUIT esté bloqueado (spec §3) */}
        <div className="space-y-2 sm:col-span-2">
          <label className={labelClass}>Condición fiscal (AFIP) *</label>
          <select
            value={formData.taxConditionId}
            onChange={(event) => setFormData((anterior) => ({ ...anterior, taxConditionId: Number(event.target.value) }))}
            disabled={camposDeshabilitados}
            className={inputClass}
            data-testid="customer-datos-taxConditionId"
          >
            {TAX_CONDITION_OPTIONS.map((opcion) => (
              <option key={opcion.value} value={opcion.value}>
                {opcion.label}
              </option>
            ))}
          </select>
        </div>

        <div className="space-y-2">
          <label className={labelClass}>Email</label>
          <input
            type="email"
            value={formData.email}
            onChange={handleChange("email")}
            disabled={camposDeshabilitados}
            placeholder="cliente@mail.com"
            className={inputClass}
            data-testid="customer-datos-email"
          />
        </div>

        <div className="space-y-2">
          <label className={labelClass}>Teléfono</label>
          <input
            type="text"
            value={formData.phone}
            onChange={handleChange("phone")}
            disabled={camposDeshabilitados}
            placeholder="11-4444-5555"
            className={inputClass}
            data-testid="customer-datos-phone"
          />
        </div>

        <div className="space-y-2 sm:col-span-2">
          <label className={labelClass}>Dirección</label>
          <input
            type="text"
            value={formData.address}
            onChange={handleChange("address")}
            disabled={camposDeshabilitados}
            placeholder="Calle y número, ciudad"
            className={inputClass}
            data-testid="customer-datos-address"
          />
        </div>

        {/* Toggle activo/inactivo: inactivo = no aparece en buscadores, mantiene historial */}
        <div className="flex items-center gap-3 rounded-lg border border-slate-100 bg-slate-50 p-3 dark:border-slate-800 dark:bg-slate-900/30 sm:col-span-2">
          <input
            type="checkbox"
            id="customer-datos-isActive"
            checked={formData.isActive}
            onChange={(event) => setFormData((anterior) => ({ ...anterior, isActive: event.target.checked }))}
            disabled={camposDeshabilitados}
            className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500 disabled:cursor-not-allowed"
            data-testid="customer-datos-isActive"
          />
          <label htmlFor="customer-datos-isActive" className={`${labelClass} ${camposDeshabilitados ? "" : "cursor-pointer"}`}>
            Cliente activo
          </label>
          <span className="text-xs text-muted-foreground">
            {formData.isActive
              ? "Aparece en buscadores; puede tener nuevas reservas."
              : "Inactivo — no aparece en buscadores, pero mantiene su historial."}
          </span>
        </div>
      </div>

      {/* Cartel rojo de error de guardado, arriba del botón (guía Ronda 2, 2026-06-06):
          todo lo cargado en el formulario queda intacto, el usuario reintenta acá mismo. */}
      {errorGuardado && (
        <p role="alert" className="text-sm text-rose-600 dark:text-rose-400" data-testid="customer-datos-error">
          {errorGuardado}
        </p>
      )}

      {/* Sin permiso de editar (clientes.edit falso): no se ofrece el botón Guardar
          (spec §5) — todos los campos ya quedaron deshabilitados arriba. */}
      {canEdit && (
        <div className="flex items-center gap-3 border-t border-slate-100 pt-4 dark:border-slate-800">
          <button
            type="submit"
            disabled={saving || !puedeGuardarDatosCliente(formData)}
            className="rounded-lg bg-indigo-600 px-5 py-2.5 text-sm font-medium text-white shadow-lg shadow-indigo-500/25 transition-all hover:bg-indigo-700 disabled:opacity-50"
            data-testid="customer-datos-submit"
          >
            {saving ? "Guardando…" : "Guardar cambios"}
          </button>
        </div>
      )}
    </form>
  );
}
