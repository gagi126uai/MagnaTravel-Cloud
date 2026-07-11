/**
 * "Agregar otro cargo de este operador" — acción SECUNDARIA y escondida de la ficha de
 * una anulada (ADR-044 T4, spec `docs/ux/2026-07-10-t4-multas-pantallas.md` sección 1).
 *
 * Cuándo se usa: el caso simple de siempre (una multa, un cargo administrativo
 * automático) NO cambia y no muestra esto. Pero a veces el MISMO operador aplica dos
 * cosas a la vez sobre la misma anulación (caso real confirmado por el contador: un
 * cargo administrativo Y una retención fiscal). Para ese caso puntual, este componente
 * agrega un SEGUNDO cargo — arranca como un link discreto debajo del cargo ya
 * confirmado; quien no lo necesita, ni lo ve como una pregunta.
 *
 * Solo aparece cuando el operador YA tiene un cargo confirmado (el link se gatea desde
 * afuera, en OperatorPenaltyStepPanel — este componente asume que ya corresponde
 * mostrarse). Va EN LÍNEA, nunca en una ventana flotante (regla dura 2026-06-09).
 */

import { useState } from "react";
import { ChevronRight, Loader2, Plus, X } from "lucide-react";
import { cancellationsApi } from "../api/cancellationsApi";
import { showSuccess, showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import {
  TIPOS_CARGO,
  MONEDA_OTRO_CARGO_DEFAULT,
  requiereDocumentoDelOperador,
  requiereMontoDeGestion,
  puedeAgregarOtroCargo,
  construirPayloadOtroCargo,
  fechaLocalInput,
} from "../lib/otroCargoOperador";
import { resolverMonedaFacturaDestino, debeMostrarRecuadroTipoCambio } from "../lib/facturaDestinoLogic";
import { FacturaDestinoSelect } from "./FacturaDestinoSelect";

const MONEDAS = [
  { value: "USD", label: "Dólares (USD)" },
  { value: "ARS", label: "Pesos (ARS)" },
];

const FUENTES_TC = [
  { value: "Manual", label: "Manual" },
  { value: "BNA_VendedorDivisa", label: "BNA vendedor divisa" },
  { value: "BCRA_A3500", label: "BCRA mayorista A3500" },
];

const fechaHoy = () => fechaLocalInput();

/**
 * Props:
 *   - reservaPublicId: GUID de la reserva (para buscar la cancelación vigente al abrir).
 *   - reservaNumero: número de negocio, para el header de la ficha.
 *   - supplierPublicId: GUID del operador al que corresponde este cargo (obligatorio
 *     cuando hay más de un operador en la cancelación — mismo criterio que el resto de
 *     los paneles de multa; en el caso mono-operador de siempre no hace daño mandarlo).
 *   - monedaSugerida: moneda con la que arranca el selector (la de la multa ya confirmada).
 *   - onAgregado: callback tras agregar el cargo con éxito (el padre refresca la reserva).
 */
export function AgregarOtroCargoOperadorInline({
  reservaPublicId,
  reservaNumero,
  supplierPublicId,
  monedaSugerida,
  onAgregado,
}) {
  const [expandido, setExpandido] = useState(false);
  const [cargandoCancelacion, setCargandoCancelacion] = useState(false);
  const [cancellationPublicId, setCancellationPublicId] = useState(null);
  const [saleInvoices, setSaleInvoices] = useState([]);
  const [errorCarga, setErrorCarga] = useState(null);
  // P5 (2+ facturas activas): a qué factura del cliente corresponde este cargo nuevo.
  // Con 1 sola factura activa, queda "" y ni se muestra el desplegable (autocompletado).
  const [targetInvoicePublicId, setTargetInvoicePublicId] = useState("");

  const [kind, setKind] = useState(TIPOS_CARGO[0].value);
  const [montoStr, setMontoStr] = useState("");
  const [moneda, setMoneda] = useState(monedaSugerida ?? MONEDA_OTRO_CARGO_DEFAULT);
  const [masDetallesAbierto, setMasDetallesAbierto] = useState(false);
  const [collectionMode, setCollectionMode] = useState("Retenida");
  const [documentRef, setDocumentRef] = useState("");
  const [clientTransferMode, setClientTransferMode] = useState("AsIs");
  const [managementFeeAmountStr, setManagementFeeAmountStr] = useState("");
  const [tipoCambioStr, setTipoCambioStr] = useState("");
  const [fuenteTipoCambio, setFuenteTipoCambio] = useState("Manual");
  const [fechaTipoCambio, setFechaTipoCambio] = useState(fechaHoy());
  const [justificacionTipoCambio, setJustificacionTipoCambio] = useState("");

  const [submitting, setSubmitting] = useState(false);
  const [errorEnvio, setErrorEnvio] = useState(null);

  const resetearFormulario = () => {
    setKind(TIPOS_CARGO[0].value);
    setMontoStr("");
    setMoneda(monedaSugerida ?? MONEDA_OTRO_CARGO_DEFAULT);
    setMasDetallesAbierto(false);
    setCollectionMode("Retenida");
    setDocumentRef("");
    setClientTransferMode("AsIs");
    setManagementFeeAmountStr("");
    setTipoCambioStr("");
    setFuenteTipoCambio("Manual");
    setFechaTipoCambio(fechaHoy());
    setJustificacionTipoCambio("");
    setTargetInvoicePublicId("");
    setErrorEnvio(null);
  };

  // Al abrir la ficha, buscamos la cancelación vigente (necesitamos su GUID para el
  // endpoint POST /cancellations/{id}/operator-charges) y de paso traemos las facturas
  // de venta activas — se usan solo para decidir el recuadro de tipo de cambio y el
  // aviso de "factura ambigua" (ver otroCargoOperador.js).
  const handleAbrir = async () => {
    setExpandido(true);
    setErrorCarga(null);
    setCargandoCancelacion(true);
    try {
      const cancelacion = await cancellationsApi.getByReserva(reservaPublicId);
      if (!cancelacion?.publicId) {
        setErrorCarga("No se encontró la cancelación de esta reserva. Actualizá la página y volvé a intentar.");
        return;
      }
      setCancellationPublicId(cancelacion.publicId);
      setSaleInvoices(Array.isArray(cancelacion.saleInvoices) ? cancelacion.saleInvoices : []);
    } catch (error) {
      setErrorCarga(getApiErrorMessage(error, "No se pudo cargar los datos de la cancelación. Intentá de nuevo."));
    } finally {
      setCargandoCancelacion(false);
    }
  };

  const handleCerrar = () => {
    setExpandido(false);
    setCancellationPublicId(null);
    setSaleInvoices([]);
    setErrorCarga(null);
    resetearFormulario();
  };

  // Con 1 sola factura activa se autocompleta sola; con 2+, el recuadro de TC espera a
  // que el usuario elija la factura destino (recién ahí sabemos si cruza de moneda).
  const monedaFacturaDestino = resolverMonedaFacturaDestino(saleInvoices, targetInvoicePublicId);
  const mostrarRecuadroTipoCambio = debeMostrarRecuadroTipoCambio(moneda, monedaFacturaDestino);

  const canSubmit = puedeAgregarOtroCargo({
    montoStr,
    collectionMode,
    documentRef,
    clientTransferMode,
    managementFeeAmountStr,
    mostrarRecuadroTipoCambio,
    tipoCambioStr,
    fuenteTipoCambio,
    fechaTipoCambio,
    justificacionTipoCambio,
    saleInvoices,
    targetInvoicePublicId,
    submitting,
  });

  const handleAgregar = async () => {
    if (!canSubmit || !cancellationPublicId) return;
    setSubmitting(true);
    setErrorEnvio(null);
    try {
      const payload = construirPayloadOtroCargo({
        kind,
        montoStr,
        moneda,
        collectionMode,
        documentRef,
        notes: "",
        clientTransferMode,
        managementFeeAmountStr,
        mostrarRecuadroTipoCambio,
        tipoCambioStr,
        fuenteTipoCambio,
        fechaTipoCambio,
        justificacionTipoCambio,
        saleInvoices,
        targetInvoicePublicId,
      });
      await cancellationsApi.addOperatorCharge(cancellationPublicId, payload, supplierPublicId);
      showSuccess("Listo. Se agregó el cargo de este operador.", "Cargo agregado");
      handleCerrar();
      onAgregado && onAgregado();
    } catch (error) {
      // Mismo criterio de mensajes que ConfirmarMultaOperadorInline: 409 con invariantCode
      // ya viene en español desde el backend (getApiErrorMessage lo muestra tal cual).
      setErrorEnvio(getApiErrorMessage(error, "No se pudo agregar el cargo. Intentá de nuevo."));
      setSubmitting(false);
    }
  };

  if (!expandido) {
    return (
      <button
        type="button"
        onClick={handleAbrir}
        data-testid="link-agregar-otro-cargo"
        className="inline-flex items-center gap-1 text-xs font-medium text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200 transition-colors"
      >
        <Plus className="h-3 w-3" aria-hidden="true" />
        Agregar otro cargo de este operador
      </button>
    );
  }

  return (
    <div
      className="mt-2 rounded-xl border border-slate-200 bg-white dark:border-slate-700 dark:bg-slate-900 p-4 space-y-3"
      data-testid="ficha-agregar-otro-cargo"
    >
      <div className="flex items-center justify-between">
        <h5 className="text-sm font-bold text-slate-900 dark:text-white">
          Otro cargo de este operador — Reserva #{reservaNumero}
        </h5>
        <button
          type="button"
          onClick={handleCerrar}
          disabled={submitting}
          className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 rounded p-1 disabled:opacity-40"
          aria-label="Cerrar sin agregar el cargo"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      {cargandoCancelacion ? (
        <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 py-2">
          <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
          Cargando…
        </div>
      ) : errorCarga ? (
        <div className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200" role="alert">
          {errorCarga}
        </div>
      ) : (
        <>
          {errorEnvio && (
            <div
              role="alert"
              className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200"
              data-testid="otro-cargo-error"
            >
              {errorEnvio}
            </div>
          )}

          {/* Tipo de cargo — a diferencia del camino simple, ACÁ sí se pregunta (P2=A). */}
          <div>
            <label className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5" htmlFor="otro-cargo-tipo">
              Tipo de cargo <span className="text-rose-500" aria-hidden="true">*</span>
            </label>
            <select
              id="otro-cargo-tipo"
              value={kind}
              onChange={(e) => setKind(e.target.value)}
              disabled={submitting}
              data-testid="otro-cargo-tipo-select"
              className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-400"
            >
              {TIPOS_CARGO.map((opcion) => (
                <option key={opcion.value} value={opcion.value}>{opcion.label}</option>
              ))}
            </select>
          </div>

          {/* Monto + Moneda */}
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5" htmlFor="otro-cargo-monto">
                Monto <span className="text-rose-500" aria-hidden="true">*</span>
              </label>
              <input
                id="otro-cargo-monto"
                type="number"
                min="0.01"
                step="0.01"
                value={montoStr}
                onChange={(e) => setMontoStr(e.target.value)}
                placeholder="0.00"
                disabled={submitting}
                data-testid="otro-cargo-monto-input"
                className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-400"
              />
            </div>
            <div>
              <label className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5" htmlFor="otro-cargo-moneda">
                Moneda <span className="text-rose-500" aria-hidden="true">*</span>
              </label>
              <select
                id="otro-cargo-moneda"
                value={moneda}
                onChange={(e) => setMoneda(e.target.value)}
                disabled={submitting}
                data-testid="otro-cargo-moneda-select"
                className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-400"
              >
                {MONEDAS.map((opcion) => (
                  <option key={opcion.value} value={opcion.value}>{opcion.label}</option>
                ))}
              </select>
            </div>
          </div>

          {/* Desplegable "¿A qué factura corresponde?" — SOLO con 2+ facturas activas
              (P5). Con 1 sola factura, FacturaDestinoSelect no renderiza nada: el
              sistema la usa sola sin preguntar. */}
          <FacturaDestinoSelect
            saleInvoices={saleInvoices}
            value={targetInvoicePublicId}
            onChange={setTargetInvoicePublicId}
            disabled={submitting}
            testId="otro-cargo-factura-destino-select"
          />

          {/* "Más detalles" — cerrado por defecto (P3=A, P4=A). */}
          <div className="border-t border-slate-100 dark:border-slate-800 pt-2">
            <button
              type="button"
              onClick={() => setMasDetallesAbierto((v) => !v)}
              disabled={submitting}
              data-testid="otro-cargo-mas-detalles-toggle"
              className="inline-flex items-center gap-1 text-xs font-semibold text-indigo-600 hover:text-indigo-700 dark:text-indigo-400"
            >
              <ChevronRight
                className={`h-3.5 w-3.5 transition-transform ${masDetallesAbierto ? "rotate-90" : ""}`}
                aria-hidden="true"
              />
              Más detalles
              <span className="font-normal text-slate-400">(cómo lo cobra · traslado al cliente · documento)</span>
            </button>

            {masDetallesAbierto && (
              <div className="mt-3 space-y-4" data-testid="otro-cargo-mas-detalles-panel">
                {/* Cómo cobra el operador */}
                <fieldset>
                  <legend className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5">
                    ¿Cómo lo cobra el operador?
                  </legend>
                  <div className="space-y-1.5">
                    <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                      <input
                        type="radio"
                        name="otro-cargo-collection-mode"
                        checked={collectionMode === "Retenida"}
                        onChange={() => setCollectionMode("Retenida")}
                        disabled={submitting}
                      />
                      Lo descuenta de lo que te va a devolver
                    </label>
                    <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                      <input
                        type="radio"
                        name="otro-cargo-collection-mode"
                        checked={collectionMode === "FacturadaAparte"}
                        onChange={() => setCollectionMode("FacturadaAparte")}
                        disabled={submitting}
                        data-testid="otro-cargo-facturada-aparte-radio"
                      />
                      Te lo factura aparte
                    </label>
                  </div>
                  {requiereDocumentoDelOperador(collectionMode) && (
                    <div className="mt-2">
                      <label className="block text-xs font-semibold text-slate-500 mb-1" htmlFor="otro-cargo-documento">
                        Documento del operador <span className="text-rose-500" aria-hidden="true">*</span>
                      </label>
                      <input
                        id="otro-cargo-documento"
                        type="text"
                        value={documentRef}
                        onChange={(e) => setDocumentRef(e.target.value)}
                        maxLength={200}
                        disabled={submitting}
                        placeholder="Nº de nota / adjunto..."
                        data-testid="otro-cargo-documento-input"
                        className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-400"
                      />
                    </div>
                  )}
                </fieldset>

                {/* Qué pasa con el cliente */}
                <fieldset>
                  <legend className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5">
                    ¿Qué pasa con el cliente?
                  </legend>
                  <div className="space-y-1.5">
                    <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                      <input
                        type="radio"
                        name="otro-cargo-client-transfer"
                        checked={clientTransferMode === "AsIs"}
                        onChange={() => setClientTransferMode("AsIs")}
                        disabled={submitting}
                      />
                      Se le traslada tal cual
                    </label>
                    <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                      <input
                        type="radio"
                        name="otro-cargo-client-transfer"
                        checked={clientTransferMode === "WithManagementFee"}
                        onChange={() => setClientTransferMode("WithManagementFee")}
                        disabled={submitting}
                        data-testid="otro-cargo-management-fee-radio"
                      />
                      + un cargo de gestión
                    </label>
                    {requiereMontoDeGestion(clientTransferMode) && (
                      <input
                        type="number"
                        min="0.01"
                        step="0.01"
                        value={managementFeeAmountStr}
                        onChange={(e) => setManagementFeeAmountStr(e.target.value)}
                        placeholder="Monto del cargo de gestión"
                        disabled={submitting}
                        data-testid="otro-cargo-management-fee-input"
                        className="ml-6 w-40 rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-2 py-1.5 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-400"
                      />
                    )}
                    <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                      <input
                        type="radio"
                        name="otro-cargo-client-transfer"
                        checked={clientTransferMode === "Absorbed"}
                        onChange={() => setClientTransferMode("Absorbed")}
                        disabled={submitting}
                      />
                      Lo absorbe la agencia
                    </label>
                  </div>
                </fieldset>
              </div>
            )}
          </div>

          {/* Recuadro de tipo de cambio — SOLO si el cargo cruza de moneda (regla dura
              multimoneda: nunca aparece la frase "diferencia de cambio"). El TC que manda
              es el del día en que el operador cobró la multa (decisión de negocio). */}
          {mostrarRecuadroTipoCambio && (
            <div
              className="rounded-lg border-2 border-dashed border-indigo-300 bg-indigo-50/50 dark:border-indigo-900/50 dark:bg-indigo-950/20 p-3.5 space-y-3"
              data-testid="otro-cargo-recuadro-tc"
            >
              <p className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">
                Tipo de cambio del día que el operador cobró
              </p>
              <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                <div>
                  <label className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">1 US$ = $ ___</label>
                  <input
                    type="number"
                    step="0.01"
                    min="0.01"
                    value={tipoCambioStr}
                    onChange={(e) => setTipoCambioStr(e.target.value)}
                    disabled={submitting}
                    data-testid="otro-cargo-tc-input"
                    className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-indigo-800 dark:bg-slate-800 dark:text-white"
                  />
                </div>
                <div>
                  <label className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">Fuente</label>
                  <select
                    value={fuenteTipoCambio}
                    onChange={(e) => setFuenteTipoCambio(e.target.value)}
                    disabled={submitting}
                    data-testid="otro-cargo-tc-fuente-select"
                    className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-indigo-800 dark:bg-slate-800 dark:text-white"
                  >
                    {FUENTES_TC.map((f) => (
                      <option key={f.value} value={f.value}>{f.label}</option>
                    ))}
                  </select>
                </div>
                <div>
                  <label className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">Fecha</label>
                  <input
                    type="date"
                    value={fechaTipoCambio}
                    onChange={(e) => setFechaTipoCambio(e.target.value)}
                    disabled={submitting}
                    data-testid="otro-cargo-tc-fecha-input"
                    className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-indigo-800 dark:bg-slate-800 dark:text-white"
                  />
                </div>
              </div>
              {fuenteTipoCambio === "Manual" && (
                <div>
                  <label className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">
                    ¿De dónde salió este tipo de cambio? <span className="text-rose-500" aria-hidden="true">*</span>
                  </label>
                  <input
                    type="text"
                    value={justificacionTipoCambio}
                    onChange={(e) => setJustificacionTipoCambio(e.target.value)}
                    maxLength={500}
                    disabled={submitting}
                    placeholder="Ej.: cotización informada por el operador en su liquidación..."
                    data-testid="otro-cargo-tc-justificacion-input"
                    className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-indigo-800 dark:bg-slate-800 dark:text-white"
                  />
                </div>
              )}
            </div>
          )}

          {/* Acciones */}
          <div className="flex justify-end gap-3 pt-1">
            <button
              type="button"
              onClick={handleCerrar}
              disabled={submitting}
              className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 disabled:opacity-50"
            >
              Volver
            </button>
            <button
              type="button"
              onClick={handleAgregar}
              disabled={!canSubmit}
              data-testid="otro-cargo-agregar-btn"
              className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-bold text-white hover:bg-indigo-700 disabled:opacity-50 flex items-center gap-2"
            >
              {submitting && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
              {submitting ? "Agregando…" : "Agregar el cargo"}
            </button>
          </div>
        </>
      )}
    </div>
  );
}
