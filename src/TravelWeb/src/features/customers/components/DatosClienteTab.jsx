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
 *
 * Documento (Obra 3, firma de Gastón 2026-07-27): esta solapa usa el MISMO casillero
 * único que el alta (`CustomerFormModal` + `customerDocumentLogic.js`) — un solo
 * desplegable de tipo (CUIT/CUIL/DNI/Pasaporte/Otro) + número, en vez de los dos campos
 * sueltos que había antes ("Documento/Pasaporte" y "CUIT/DNI"). Si el cliente tiene el
 * OTRO documento guardado aparte (por ejemplo, CUIT y un DNI viejo a la vez), se muestra
 * debajo como dato de SOLO LECTURA — nunca se esconde.
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
import {
  DOCUMENT_TYPE_OPTIONS,
  aplicarResultadoAfip,
  construirEstadoInicialDocumento,
  construirPayloadDocumento,
  describirDocumentoAlternativo,
  esTipoDocumentoFiscal,
  obtenerDocumentoAlternativo,
  tipoDocumentoTieneBusquedaAfip,
} from "../lib/customerDocumentLogic";

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
  const [formData, setFormData] = useState(() => ({
    ...construirEstadoInicialDatosCliente(null),
    ...construirEstadoInicialDocumento(null),
  }));
  // `notes` no se muestra en esta solapa, pero el PUT lo pisa completo si no viaja
  // (ver docstring de construirPayloadDatosCliente) — se guarda aparte para reinyectarlo.
  const [notasOriginales, setNotasOriginales] = useState(null);
  // Foto de cómo arrancó el casillero de documento (mismo criterio que CustomerFormModal,
  // P-21): sirve para saber si el usuario lo TOCÓ antes de guardar, sin necesitar un flag
  // manual en cada onChange — ver handleSubmit.
  const [documentoInicial, setDocumentoInicial] = useState(() => construirEstadoInicialDocumento(null));
  // Cliente TAL CUAL lo devolvió el motor (sin mezclar con el formulario editable):
  // hace falta crudo para el round-trip de construirPayloadDocumento (P-21, no pisar un
  // documento que no se ve en pantalla) y para saber si hay un OTRO documento guardado
  // que el casillero no está mostrando (obtenerDocumentoAlternativo).
  const [clienteOriginal, setClienteOriginal] = useState(null);

  // ── Guardado ─────────────────────────────────────────────────────────────
  const [saving, setSaving] = useState(false);
  const [errorGuardado, setErrorGuardado] = useState(null);

  // ── Búsqueda AFIP del documento (mismo comportamiento que CustomerFormModal) ────
  const [afipResults, setAfipResults] = useState([]);
  const [loadingAfip, setLoadingAfip] = useState(false);

  const cargarCliente = useCallback(async () => {
    setLoading(true);
    setErrorCarga(null);
    try {
      const detalle = await api.get(`/customers/${customerPublicId}`);
      setFormData({
        ...construirEstadoInicialDatosCliente(detalle),
        ...construirEstadoInicialDocumento(detalle),
      });
      setDocumentoInicial(construirEstadoInicialDocumento(detalle));
      setClienteOriginal(detalle);
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

  // Lupita AFIP: solo tiene sentido para los tipos que están en el padrón (CUIT/CUIL/DNI
  // — ver tipoDocumentoTieneBusquedaAfip). El botón ya queda oculto para Pasaporte/Otro,
  // este chequeo es un segundo candado por si se dispara desde otro lado.
  const handleAfipSearch = async () => {
    if (!tipoDocumentoTieneBusquedaAfip(formData.tipoDocumento)) return;
    const query = (formData.numeroDocumento || "").trim();
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
      // B1 (mismo criterio que CustomerFormModal): lo que devuelve el padrón SIEMPRE es
      // un CUIT/CUIL de 11 dígitos — aplicarResultadoAfip sube el tipo a CUIT si el
      // casillero estaba en un tipo no fiscal.
      ...aplicarResultadoAfip({ tipoDocumento: anterior.tipoDocumento, numeroDocumento: anterior.numeroDocumento }, persona),
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
      // documentNumber/taxId acá son los que arma construirEstadoInicialDatosCliente al
      // cargar (viejos, sin tipo) — se descartan a propósito: el documento real del
      // casillero único viaja aparte, calculado abajo con construirPayloadDocumento.
      const { tipoDocumento, numeroDocumento, documentNumber, taxId, ...resto } = formData;
      // P-21 (mismo criterio que CustomerFormModal, hallazgo B2): "tocado" se calcula
      // comparando contra la FOTO de cómo arrancó el casillero (documentoInicial), nunca
      // con un flag manual — así no hay forma de pisar en silencio un documento que el
      // casillero no está mostrando (por ejemplo, un DNI viejo guardado junto al CUIT).
      const documentoFueTocado =
        tipoDocumento !== documentoInicial.tipoDocumento || numeroDocumento !== documentoInicial.numeroDocumento;
      const payload = {
        ...construirPayloadDatosCliente(resto, notasOriginales),
        ...construirPayloadDocumento({ tipoDocumento, numeroDocumento, documentoFueTocado, clienteOriginal }),
      };
      await api.put(`/customers/${customerPublicId}`, payload);
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

  // El candado ahora cubre el casillero ENTERO (tipo + número + lupita), no solo un
  // campo suelto. Fix del reviewer (2026-07-27) a este comentario: la razón NO es que
  // cambiar el tipo "esquive" el candado del CUIT — el taxId viaja SIEMPRE preservado
  // igual (regla 4 de construirPayloadDocumento, ver customerDocumentLogic.js), aunque
  // el usuario pase el casillero a un tipo no fiscal. El riesgo real es otro: si solo el
  // número quedara trabado y el tipo fuera libre, el usuario podría cambiar el tipo a
  // DNI/Pasaporte/Otro y PISAR sin darse cuenta el documento no fiscal que el cliente ya
  // tenía guardado (documentType/documentNumber) — el casillero solo puede mostrar UNO
  // de los dos a la vez, así que ese dato viejo queda invisible mientras se edita. Este
  // candado ampliado queda como decisión del orquestador (pendiente de firma de
  // Gastón) — no es un veredicto nuevo del backend, sigue siendo el mismo taxIdLocked.
  const documentoDeshabilitado = !canEdit || debeDeshabilitarCuit(taxIdLocked);
  const camposDeshabilitados = !canEdit;
  // Documento guardado que el casillero no muestra (Obra 3): se calcula sobre el cliente
  // CRUDO (clienteOriginal), nunca sobre formData — así sigue reflejando lo guardado
  // aunque el usuario esté a mitad de editar el casillero en pantalla.
  const documentoAlternativo = obtenerDocumentoAlternativo(clienteOriginal);

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

        {/* Documento: casillero único (Obra 3, firma 2026-07-27) — mismo componente que el
            alta (CustomerFormModal): desplegable de tipo + número + lupita AFIP condicional.
            Antes había DOS campos sueltos acá ("Documento/Pasaporte" y "CUIT/DNI") que
            podían pisarse entre sí sin que el form supiera nunca qué TIPO era cada uno. */}
        <div className="space-y-2 sm:col-span-2">
          <label htmlFor="customer-datos-document-type" className={labelClass}>Documento</label>
          <div className="grid grid-cols-[auto_1fr] gap-2">
            <select
              id="customer-datos-document-type"
              value={formData.tipoDocumento}
              onChange={(event) => {
                const tipoDocumento = event.target.value;
                setFormData((anterior) => ({ ...anterior, tipoDocumento }));
                if (!tipoDocumentoTieneBusquedaAfip(tipoDocumento)) setAfipResults([]);
              }}
              disabled={documentoDeshabilitado}
              className={`${inputClass} w-auto`}
              data-testid="customer-datos-document-type"
            >
              {DOCUMENT_TYPE_OPTIONS.map((opcion) => (
                <option key={opcion.value} value={opcion.value}>{opcion.label}</option>
              ))}
            </select>

            <div className="relative">
              <input
                type="text"
                aria-label="Número de documento"
                value={formData.numeroDocumento}
                onChange={(event) => {
                  setFormData((anterior) => ({ ...anterior, numeroDocumento: event.target.value }));
                  setAfipResults([]);
                }}
                disabled={documentoDeshabilitado}
                placeholder={esTipoDocumentoFiscal(formData.tipoDocumento) ? "20-30111222-0" : "Número de documento"}
                className={`${inputClass} pr-10 font-mono`}
                data-testid="customer-datos-document-number"
              />
              {/* Lupita AFIP: SOLO para los tipos que están en el padrón (CUIT/CUIL/DNI) */}
              {tipoDocumentoTieneBusquedaAfip(formData.tipoDocumento) && (
                <button
                  type="button"
                  onClick={handleAfipSearch}
                  disabled={documentoDeshabilitado}
                  title="Buscar en AFIP"
                  className="absolute right-2 top-2 p-1 text-slate-400 hover:text-indigo-600 disabled:cursor-not-allowed disabled:hover:text-slate-400 transition-colors"
                  data-testid="customer-datos-document-search"
                >
                  {loadingAfip ? <Loader2 className="h-4 w-4 animate-spin text-indigo-500" /> : <Search className="h-4 w-4" />}
                </button>
              )}

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
          </div>

          {/* Línea explicativa del candado. Fix del reviewer (2026-07-27): el texto viejo
              hablaba SOLO del CUIT ("El CUIT no se puede cambiar acá"), pero ahora el
              candado apaga el casillero ENTERO (tipo + número) — el texto nuevo aclara
              que es "el documento" el que queda trabado, sin perder el motivo original
              (comprobantes ya emitidos con ese CUIT). */}
          {documentoDeshabilitado && canEdit && (
            <p className="text-xs text-amber-700 dark:text-amber-400" data-testid="customer-datos-document-locked-note">
              🔒 El documento no se puede cambiar acá: los comprobantes ya salieron con este CUIT. Si el
              titular cambió de CUIT, registrá un cliente nuevo.
            </p>
          )}

          {/* Otro documento guardado que el casillero no muestra (Obra 3, firma 2026-07-27:
              "hay 5 casos reales" de clientes con CUIT y DNI a la vez) — se ve siempre,
              nunca se esconde ningún documento cargado. Solo lectura: para cambiarlo hay
              que tocar el casillero de arriba. describirDocumentoAlternativo arma la frase
              legible (fix del reviewer: "Otro" como tipo se muestra "otro documento", no
              "Otro" con mayúscula suelta como si fuera un nombre de documento). */}
          {documentoAlternativo && (
            <p className="text-xs text-slate-500 dark:text-slate-400" data-testid="customer-datos-document-alternativo">
              También tiene {describirDocumentoAlternativo(documentoAlternativo)}.
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
