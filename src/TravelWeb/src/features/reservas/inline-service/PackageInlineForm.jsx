/**
 * Formulario de Paquete dentro de la ficha de carga en línea (ServiceInlineCard).
 *
 * Campos a la vista SIEMPRE (sin revelado progresivo — guía UX ronda 1):
 *   Buscador del paquete · Operador · Salida · Fecha de fin · Pasajeros y base (doble/triple)
 *   Precio por persona · Costo · Venta · Moneda
 *
 * Más detalles (plegado):
 *   Qué incluye (texto libre) · Número de file del operador
 *
 * Permiso `cobranzas.see_cost`:
 *   - Con permiso: ve el campo Costo + ganancia en el footer.
 *   - Sin permiso: no ve costo ni ganancia (jamás "$0").
 *
 * Cálculo del total: precio por persona × pasajeros.
 * La venta total y el costo total se calculan separados (el vendedor puede ingresar
 * un precio por persona de venta y uno de costo independientes).
 *
 * Fecha de fin: campo opcional. Si se carga, no puede ser anterior a la salida
 * (la validación vive en ServiceInlineCard.validarForm).
 *
 * LÍNEA INTELIGENTE (spec 2026-08-07, §3): mientras el vendedor escribe la frase entera
 * en el buscador del paquete, `useServiceLineInterpretationForForm` va precargando en
 * amarillo lo que el motor entendió (operador, fechas, costo por persona). El paquete NO
 * tiene variante natural (V2=todos, con esa única excepción documentada en la spec).
 */

import { useState } from "react";
import { Package, ChevronDown, ChevronUp, Calendar, Users } from "lucide-react";
import { hasPermission } from "../../../auth";
import { ProductSearchField } from "./ProductSearchField";
import { redondearDinero, formatearPrecio } from "./HotelInlineForm";
import { resolverCamposALimpiarAlCrearNuevo } from "./inlineServiceFormHelpers";
import { buildLastSaleHintText } from "./lastSaleHintLogic";
import { LastSaleHint } from "./LastSaleHint";
import { useServiceLineInterpretationForForm } from "./useServiceLineInterpretationForForm";
import { ServiceLineDoubtQuestion } from "./ServiceLineDoubtQuestion";
import { ResolvedProductRow } from "./ResolvedProductRow";
import { DOUBT_FIELD, construirPatchDeSeleccionManual } from "./serviceLineInterpretationLogic";

// ─── Clases CSS ───────────────────────────────────────────────────────────────
const INPUT_BASE = "w-full py-2 px-3 text-sm border rounded-lg bg-white focus:outline-none focus:ring-1 focus:border-blue-500 focus:ring-blue-500 disabled:bg-slate-50 disabled:text-slate-400";
const INPUT_NORMAL = `${INPUT_BASE} border-slate-200`;
const INPUT_SUGERIDO = `${INPUT_BASE} border-yellow-400 bg-yellow-50`;
const INPUT_CALCULADO = `${INPUT_BASE} border-slate-200 border-dashed bg-slate-50 text-slate-600 font-semibold cursor-default`;
const LABEL_BASE = "block text-xs font-semibold text-slate-600 mb-1";

// Ids de los campos que puede señalar una duda grande de la línea inteligente (§4).
const IDS_DUDA_LINEA_INTELIGENTE = {
    supplierId: "package-operador",
    unitNetCost: "package-costo-persona",
    startDate: "package-salida",
    endDate: "package-fin",
};

// ─── Recuadro violeta para paquete nuevo ─────────────────────────────────────

/**
 * Recuadro que aparece al crear un paquete nuevo.
 * Campo mínimo: nombre del paquete + operador.
 */
function NewPackageBox({ newProduct, onChange, suppliers }) {
    return (
        <div className="border border-dashed border-violet-400 bg-violet-50 rounded-xl p-4 mb-4">
            <div className="flex items-center gap-2 mb-3">
                <Package className="w-4 h-4 text-violet-600" />
                <span className="text-sm font-semibold text-violet-700">
                    Paquete nuevo — se guarda en tu tarifario al confirmar
                </span>
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-violet-200 text-violet-700">
                    Creado en venta
                </span>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                    <label className={LABEL_BASE}>Nombre del paquete *</label>
                    <input
                        type="text"
                        className={INPUT_NORMAL}
                        value={newProduct.name || ""}
                        onChange={(event) => onChange({ ...newProduct, name: event.target.value })}
                        placeholder="Ej: Iguazú 7 noches todo incluido"
                        required
                        data-testid="new-package-name"
                        aria-label="Nombre del paquete nuevo"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE}>Operador *</label>
                    <select
                        className={INPUT_NORMAL}
                        value={newProduct.supplierPublicId || ""}
                        onChange={(event) => onChange({ ...newProduct, supplierPublicId: event.target.value })}
                        required
                        data-testid="new-package-supplier"
                        aria-label="Operador del paquete nuevo"
                    >
                        <option value="">Seleccioná...</option>
                        {suppliers.map((supplier) => (
                            <option
                                key={supplier.publicId || supplier.PublicId}
                                value={supplier.publicId || supplier.PublicId}
                            >
                                {supplier.name}
                            </option>
                        ))}
                    </select>
                </div>
            </div>
        </div>
    );
}

// ─── Componente principal PackageInlineForm ───────────────────────────────────

export function PackageInlineForm({ reservaId, form, setForm, suppliers, isEditing }) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    // Cantidad de pasajeros (mínimo 1 para el cálculo)
    const pasajeros = Math.max(Number(form.passengers) || 1, 1);

    // Total = precio por persona × pasajeros
    const ventaTotal = redondearDinero((Number(form.unitSalePrice) || 0) * pasajeros);
    const costoTotal = canSeeCost
        ? redondearDinero((Number(form.unitNetCost) || 0) * pasajeros)
        : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;

    // "Más detalles" se abre automáticamente al editar si ya hay datos
    const tieneDetallesExistentes = Boolean(form.itinerary || form.fileNumber);
    const [mostrarDetalles, setMostrarDetalles] = useState(tieneDetallesExistentes || isEditing);

    const [camposSugeridos, setCamposSugeridos] = useState({
        supplierId: false,
        unitNetCost: false,
        unitSalePrice: false,
        currency: false,
    });

    // Renglón gris "Último precio" (spec 2026-08-06, §3.2, P9=A) — ver HotelInlineForm.
    const [ultimoPrecioSugerido, setUltimoPrecioSugerido] = useState(null);
    const textoUltimoPrecio = buildLastSaleHintText(ultimoPrecioSugerido, { canSeeCost });

    // Texto de la caja de arriba, separado de packageName (mockup firmado §3.3, ver
    // HotelInlineForm para la explicación completa del porqué).
    const [textoBuscador, setTextoBuscador] = useState(() => form.packageName || "");

    // ─── LA LÍNEA INTELIGENTE (spec 2026-08-07, §3) ───────────────────────────────────
    // El paquete no usa la sugerencia POR VARIANTE (no tiene variante, V2=todos): por eso
    // acá no hay `precioTocadoPorElUsuario`/`monedaTocadaPorElUsuario` como en Hotel — se
    // marcan tocados el costo y la moneda directamente en sus onChange, más abajo (por
    // eso tampoco necesita `alPrecargarPrecioDeLaFrase`: no hay una segunda feature a la
    // que avisarle).
    const {
        isThinking: pensandoLineaInteligente,
        duda: dudaLineaInteligente,
        onRespuestaDuda,
        aiOverride,
        productoResueltoPorLineaInteligente,
        limpiarResolucionIA,
        marcarTocado,
        camposTocados,
    } = useServiceLineInterpretationForForm({
        reservaId,
        serviceType: "Paquete",
        isEditing,
        canSeeCost,
        form,
        setForm,
        setCamposSugeridos,
        precioTocadoPorElUsuario: false,
        monedaTocadaPorElUsuario: false,
        idsDeCampoParaEnfocar: IDS_DUDA_LINEA_INTELIGENTE,
    });

    // C5: operador sugerido que no está en la lista de la reserva
    const supplierListaIds = new Set(suppliers.map((s) => s.publicId || s.PublicId));
    const supplierSugeridoFuera =
        camposSugeridos.supplierId &&
        form.supplierId &&
        !supplierListaIds.has(form.supplierId);
    const suppliersFull = supplierSugeridoFuera
        ? [
              {
                  publicId: form.supplierId,
                  // Sin supplierName no mostramos el ID interno recortado (eso es un dato
                  // técnico que un usuario no programador no puede leer) — mejor un texto
                  // genérico que igual identifica que hay un operador sugerido (2026-08-03).
                  name: form.supplierName || "Operador sugerido",
              },
              ...suppliers,
          ]
        : suppliers;

    const handleSelectExisting = (catalogResult, meta) => {
        const sale = catalogResult.lastSale || catalogResult.rateFallback || {};

        // Bug bloqueante B3: ver el comentario completo en HotelInlineForm.
        const { patch: patchVentaCatalogo, camposSugeridos: sugeridosVentaCatalogo } = meta?.fromAiOverride
            ? construirPatchDeSeleccionManual({
                  serviceType: "Paquete", sale, canSeeCost,
                  camposActualmenteSugeridos: camposSugeridos, camposTocados,
              })
            : {
                  patch: {
                      supplierId: sale.supplierPublicId || "",
                      supplierName: sale.supplierName || null,
                      unitSalePrice: sale.salePrice != null ? String(sale.salePrice) : "",
                      unitNetCost: canSeeCost && sale.netCost != null ? String(sale.netCost) : form.unitNetCost,
                      currency: sale.currency || "ARS",
                  },
                  camposSugeridos: {
                      supplierId: Boolean(sale.supplierPublicId),
                      unitNetCost: canSeeCost && sale.netCost != null,
                      unitSalePrice: Boolean(sale.salePrice),
                      currency: Boolean(sale.currency),
                  },
              };

        setForm((prev) => ({
            ...prev,
            packageName: catalogResult.name || prev.packageName,
            rateId: catalogResult.ratePublicId,
            newCatalogProduct: null,
            ...patchVentaCatalogo,
        }));
        setTextoBuscador(catalogResult.name || form.packageName || "");

        setCamposSugeridos((prev) => ({ ...prev, ...sugeridosVentaCatalogo }));
        setUltimoPrecioSugerido(catalogResult);
        limpiarResolucionIA();
    };

    const handleCreateNew = (searchText) => {
        // Bug #28 (Tanda 4, 2026-07-24): antes esto borraba operador/costo/venta/moneda
        // SIEMPRE, aunque el usuario los hubiera tipeado a mano. Ahora solo se limpian los
        // campos que TODAVÍA son sugerencia sin tocar (ver resolverCamposALimpiarAlCrearNuevo).
        const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
            { supplierId: form.supplierId, unitNetCost: form.unitNetCost, unitSalePrice: form.unitSalePrice, currency: form.currency },
            camposSugeridos,
            { supplierId: "", unitNetCost: "", unitSalePrice: "", currency: "ARS" }
        );
        setForm((prev) => ({
            ...prev,
            packageName: searchText,
            rateId: null,
            newCatalogProduct: { name: searchText, supplierPublicId: "" },
            ...camposLimpios,
        }));
        setTextoBuscador(searchText);
        setCamposSugeridos({ supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false });
        setUltimoPrecioSugerido(null);
        limpiarResolucionIA();
    };

    const handleSearchChange = (texto) => {
        setTextoBuscador(texto);
        setForm((prev) => ({
            ...prev,
            packageName: texto,
            rateId: null,
            newCatalogProduct: texto ? prev.newCatalogProduct : null,
        }));
        limpiarResolucionIA();
        if (!texto) {
            setCamposSugeridos({ supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false });
        }
        setUltimoPrecioSugerido(null);
    };

    return (
        <div className="space-y-4">

            {/* === BUSCADOR (nombre del paquete) === */}
            <ProductSearchField
                serviceType="Paquete"
                value={textoBuscador}
                onChange={handleSearchChange}
                onSelectExisting={handleSelectExisting}
                onCreateNew={handleCreateNew}
                disabled={isEditing}
                label="Escribilo como te salga"
                placeholder="Ej: Iguazú 7 noches todo incluido julia tours 900 usd por persona 1 al 8/10"
                aiCandidates={aiOverride?.candidates ?? null}
                aiCreateText={aiOverride?.createText ?? null}
                externalThinking={pensandoLineaInteligente}
            />

            {/* === RENGLÓN "Producto *" (Momento 3, §3.3 — mockup firmado) === */}
            {productoResueltoPorLineaInteligente && form.rateId && !form.newCatalogProduct && (
                <ResolvedProductRow
                    id="package-producto-resuelto"
                    label="Paquete *"
                    value={form.packageName}
                    dataTestId="package-producto-resuelto"
                />
            )}

            {/* === RECUADRO PRODUCTO NUEVO === */}
            {form.newCatalogProduct && (
                <NewPackageBox
                    newProduct={form.newCatalogProduct}
                    onChange={(newProduct) => setForm((prev) => ({ ...prev, newCatalogProduct: newProduct }))}
                    suppliers={suppliers}
                />
            )}

            {/* === OPERADOR === */}
            {!form.newCatalogProduct && (
                <div>
                    <label className={LABEL_BASE} htmlFor="package-operador">Operador</label>
                    <select
                        id="package-operador"
                        className={camposSugeridos.supplierId ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.supplierId || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, supplierId: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, supplierId: false }));
                            marcarTocado("supplierId");
                        }}
                        data-testid="package-supplier"
                        aria-label="Operador del paquete"
                    >
                        <option value="">Seleccioná un operador...</option>
                        {suppliersFull.map((supplier) => (
                            <option
                                key={supplier.publicId || supplier.PublicId}
                                value={supplier.publicId || supplier.PublicId}
                            >
                                {supplier.name}
                            </option>
                        ))}
                    </select>
                    {dudaLineaInteligente?.field === DOUBT_FIELD.SUPPLIER && (
                        <ServiceLineDoubtQuestion doubt={dudaLineaInteligente} onRespuesta={onRespuestaDuda} />
                    )}
                </div>
            )}

            {/* === SALIDA + FECHA DE FIN + PASAJEROS + BASE === */}
            {/*
             * grid-cols-2 en mobile (Salida + Fin en primera fila, Pasajeros + Base en segunda).
             * sm:grid-cols-4 en pantallas más anchas: los 4 campos en una sola fila.
             */}
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <div>
                    <label className={LABEL_BASE} htmlFor="package-salida">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Salida
                    </label>
                    <input
                        id="package-salida"
                        type="date"
                        className={camposSugeridos.startDate ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.startDate || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, startDate: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, startDate: false }));
                            marcarTocado("startDate");
                        }}
                        data-testid="package-salida"
                        aria-label="Fecha de salida del paquete"
                    />
                </div>
                <div>
                    {/*
                     * Fecha de fin: opcional. El backend la acepta null (PackageBookingDto.EndDate
                     * es nullable desde ADR-018). Si el usuario no la carga, el backend coalesce
                     * EndDate a StartDate para calcular noches (Nights = 0).
                     * La validación fin < salida vive en ServiceInlineCard.validarForm.
                     */}
                    <label className={LABEL_BASE} htmlFor="package-fin">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Fecha de fin
                    </label>
                    <input
                        id="package-fin"
                        type="date"
                        className={camposSugeridos.endDate ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.endDate || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, endDate: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, endDate: false }));
                            marcarTocado("endDate");
                        }}
                        data-testid="package-end-date"
                        aria-label="Fecha de fin del paquete"
                    />
                    {dudaLineaInteligente?.field === DOUBT_FIELD.DATES && (
                        <ServiceLineDoubtQuestion doubt={dudaLineaInteligente} onRespuesta={onRespuestaDuda} />
                    )}
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="package-pasajeros">
                        <Users className="inline w-3 h-3 mr-1" />
                        Pasajeros
                    </label>
                    <input
                        id="package-pasajeros"
                        type="number"
                        min={1}
                        className={INPUT_NORMAL}
                        value={form.passengers || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, passengers: event.target.value }))}
                        placeholder="1"
                        data-testid="package-pasajeros"
                        aria-label="Cantidad de pasajeros"
                    />
                </div>
                <div>
                    {/*
                     * Base de habitación: mapea a occupancyBase del backend
                     * (PackageBookingDto.OccupancyBase). El value del option ES el valor backend.
                     */}
                    <label className={LABEL_BASE} htmlFor="package-base">Base</label>
                    <select
                        id="package-base"
                        className={INPUT_NORMAL}
                        value={form.roomBase || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, roomBase: event.target.value }))}
                        data-testid="package-base"
                        aria-label="Base de la habitación (doble, triple, etc.)"
                    >
                        <option value="">Sin especificar</option>
                        <option value="double">Doble</option>
                        <option value="triple">Triple</option>
                        <option value="quadruple">Cuádruple</option>
                        <option value="single">Simple</option>
                    </select>
                </div>
            </div>

            {/* === PRECIO POR PERSONA + COSTO POR PERSONA + TOTAL (calculado) + MONEDA === */}
            <div className={`grid gap-3 ${canSeeCost ? "grid-cols-2 sm:grid-cols-4" : "grid-cols-2 sm:grid-cols-3"}`}>
                {canSeeCost && (
                    <div>
                        <label className={LABEL_BASE} htmlFor="package-costo-persona">Costo por persona</label>
                        <input
                            id="package-costo-persona"
                            type="number"
                            min={0}
                            step="0.01"
                            className={camposSugeridos.unitNetCost ? INPUT_SUGERIDO : INPUT_NORMAL}
                            value={form.unitNetCost || ""}
                            onChange={(event) => {
                                setForm((prev) => ({ ...prev, unitNetCost: event.target.value }));
                                setCamposSugeridos((prev) => ({ ...prev, unitNetCost: false }));
                                marcarTocado("unitNetCost");
                            }}
                            placeholder="0,00"
                            data-testid="package-costo-persona"
                            aria-label="Costo por persona"
                        />
                        <LastSaleHint text={textoUltimoPrecio} />
                        {dudaLineaInteligente?.field === DOUBT_FIELD.PRICE && (
                            <ServiceLineDoubtQuestion doubt={dudaLineaInteligente} onRespuesta={onRespuestaDuda} />
                        )}
                    </div>
                )}
                <div>
                    <label className={LABEL_BASE} htmlFor="package-venta-persona">Venta por persona</label>
                    <input
                        id="package-venta-persona"
                        type="number"
                        min={0}
                        step="0.01"
                        className={camposSugeridos.unitSalePrice ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.unitSalePrice || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, unitSalePrice: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, unitSalePrice: false }));
                        }}
                        placeholder="0,00"
                        required
                        data-testid="package-venta-persona"
                        aria-label="Precio de venta por persona"
                    />
                    {!canSeeCost && <LastSaleHint text={textoUltimoPrecio} />}
                </div>
                <div>
                    {/* Total calculado automáticamente: precio/persona × pasajeros */}
                    <label className={LABEL_BASE}>Total venta</label>
                    <input
                        type="text"
                        className={INPUT_CALCULADO}
                        value={ventaTotal > 0 ? formatearPrecio(ventaTotal, form.currency || "ARS") : "—"}
                        readOnly
                        tabIndex={-1}
                        aria-label={`Venta total: ${formatearPrecio(ventaTotal, form.currency || "ARS")}`}
                        data-testid="package-venta-total"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="package-moneda">Moneda</label>
                    <select
                        id="package-moneda"
                        className={camposSugeridos.currency ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.currency || "ARS"}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, currency: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, currency: false }));
                            marcarTocado("currency");
                        }}
                        data-testid="package-moneda"
                        aria-label="Moneda"
                    >
                        <option value="ARS">ARS (pesos)</option>
                        <option value="USD">USD (dólares)</option>
                    </select>
                </div>
            </div>

            {/* Campo "Fecha límite de seña" eliminado en F2 (Próximos Inicios).
                El aviso de la campanita se calcula desde firstStartDate (backend).
                operatorPaymentDeadline NO se mantiene en el estado ni en el payload. */}

            {/* === MÁS DETALLES: Qué incluye · Nº de file === */}
            <div>
                <button
                    type="button"
                    onClick={() => setMostrarDetalles((prev) => !prev)}
                    className="flex items-center gap-1 text-sm font-semibold text-blue-600 hover:text-blue-800 transition-colors"
                    data-testid="package-mas-detalles-toggle"
                    aria-expanded={mostrarDetalles}
                >
                    {mostrarDetalles ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                    {mostrarDetalles ? "Menos detalles" : "+ Más detalles"}
                </button>

                {mostrarDetalles && (
                    <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-3">
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE} htmlFor="package-incluye">Qué incluye</label>
                            <textarea
                                id="package-incluye"
                                className={`${INPUT_NORMAL} resize-none`}
                                rows={3}
                                value={form.itinerary || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, itinerary: event.target.value }))}
                                placeholder="Ej: Hotel 4* · Traslados aeropuerto · Excursión cataratas · Seguro de viaje"
                                data-testid="package-incluye"
                                aria-label="Qué incluye el paquete"
                            />
                        </div>
                        <div>
                            <label className={LABEL_BASE} htmlFor="package-file">Número de file del operador</label>
                            <input
                                id="package-file"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.fileNumber || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, fileNumber: event.target.value }))}
                                placeholder="Ej: PKG-2026-0482"
                                data-testid="package-file"
                                aria-label="Número de file del operador"
                            />
                        </div>
                    </div>
                )}
            </div>
        </div>
    );
}

// ─── Cálculo de totales exportado para el footer de ServiceInlineCard ─────────

/**
 * Calcula los totales del paquete para mostrar en el footer.
 * Total = precio por persona × pasajeros.
 */
export function calcularTotalesPaquete({ unitSalePrice, unitNetCost, passengers, canSeeCost }) {
    const pasajeros = Math.max(Number(passengers) || 1, 1);
    const ventaTotal = redondearDinero((Number(unitSalePrice) || 0) * pasajeros);
    const costoTotal = canSeeCost ? redondearDinero((Number(unitNetCost) || 0) * pasajeros) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
    return { pasajeros, ventaTotal, costoTotal, ganancia };
}
