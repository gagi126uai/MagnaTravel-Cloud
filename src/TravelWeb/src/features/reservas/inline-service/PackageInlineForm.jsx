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
 */

import { useState } from "react";
import { Package, ChevronDown, ChevronUp, Calendar, Users } from "lucide-react";
import { hasPermission } from "../../../auth";
import { ProductSearchField } from "./ProductSearchField";
import { redondearDinero, formatearPrecio } from "./HotelInlineForm";

// ─── Clases CSS ───────────────────────────────────────────────────────────────
const INPUT_BASE = "w-full py-2 px-3 text-sm border rounded-lg bg-white focus:outline-none focus:ring-1 focus:border-blue-500 focus:ring-blue-500 disabled:bg-slate-50 disabled:text-slate-400";
const INPUT_NORMAL = `${INPUT_BASE} border-slate-200`;
const INPUT_SUGERIDO = `${INPUT_BASE} border-yellow-400 bg-yellow-50`;
const INPUT_CALCULADO = `${INPUT_BASE} border-slate-200 border-dashed bg-slate-50 text-slate-600 font-semibold cursor-default`;
const LABEL_BASE = "block text-xs font-semibold text-slate-600 mb-1";

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

export function PackageInlineForm({ form, setForm, suppliers, isEditing }) {
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
                  name: form.supplierName || `Operador sugerido (${String(form.supplierId).slice(0, 8)}…)`,
              },
              ...suppliers,
          ]
        : suppliers;

    const handleSelectExisting = (catalogResult) => {
        const sale = catalogResult.lastSale || catalogResult.rateFallback || {};
        const supplierPublicId = sale.supplierPublicId || "";
        const unitSalePrice = sale.salePrice != null ? String(sale.salePrice) : "";
        const unitNetCost = canSeeCost && sale.netCost != null ? String(sale.netCost) : form.unitNetCost;
        const currency = sale.currency || "ARS";

        setForm((prev) => ({
            ...prev,
            packageName: catalogResult.name || prev.packageName,
            rateId: catalogResult.ratePublicId,
            newCatalogProduct: null,
            supplierId: supplierPublicId,
            supplierName: sale.supplierName || null,
            unitSalePrice,
            unitNetCost: canSeeCost ? unitNetCost : prev.unitNetCost,
            currency,
        }));

        setCamposSugeridos({
            supplierId: Boolean(supplierPublicId),
            unitNetCost: canSeeCost && sale.netCost != null,
            unitSalePrice: Boolean(sale.salePrice),
            currency: Boolean(sale.currency),
        });
    };

    const handleCreateNew = (searchText) => {
        setForm((prev) => ({
            ...prev,
            packageName: searchText,
            rateId: null,
            newCatalogProduct: { name: searchText, supplierPublicId: "" },
            supplierId: "",
            unitNetCost: "",
            unitSalePrice: "",
            currency: "ARS",
        }));
        setCamposSugeridos({ supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false });
    };

    const handleSearchChange = (texto) => {
        setForm((prev) => ({
            ...prev,
            packageName: texto,
            rateId: null,
            newCatalogProduct: texto ? prev.newCatalogProduct : null,
        }));
        if (!texto) {
            setCamposSugeridos({ supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false });
        }
    };

    return (
        <div className="space-y-4">

            {/* === BUSCADOR (nombre del paquete) === */}
            <ProductSearchField
                serviceType="Paquete"
                value={form.packageName || ""}
                onChange={handleSearchChange}
                onSelectExisting={handleSelectExisting}
                onCreateNew={handleCreateNew}
                disabled={isEditing}
                label="Paquete"
                placeholder="Ej: Iguazú 7 noches, Cancún todo incluido..."
            />

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
                        className={INPUT_NORMAL}
                        value={form.startDate || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, startDate: event.target.value }))}
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
                        className={INPUT_NORMAL}
                        value={form.endDate || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, endDate: event.target.value }))}
                        data-testid="package-end-date"
                        aria-label="Fecha de fin del paquete (opcional)"
                    />
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
                            }}
                            placeholder="0,00"
                            data-testid="package-costo-persona"
                            aria-label="Costo por persona"
                        />
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
                </div>
                <div>
                    {/* Total calculado automáticamente: precio/persona × pasajeros */}
                    <label className={LABEL_BASE}>Total venta</label>
                    <input
                        type="text"
                        className={INPUT_CALCULADO}
                        value={ventaTotal > 0 ? formatearPrecio(ventaTotal) : "—"}
                        readOnly
                        tabIndex={-1}
                        aria-label={`Venta total: ${formatearPrecio(ventaTotal)}`}
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
