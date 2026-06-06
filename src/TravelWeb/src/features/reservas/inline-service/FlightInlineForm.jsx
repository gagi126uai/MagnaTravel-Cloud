/**
 * Formulario de Aéreo dentro de la ficha de carga en línea (ServiceInlineCard).
 *
 * Campos a la vista SIEMPRE (sin revelado progresivo — guía UX ronda 1):
 *   Buscador de ruta/aerolínea · Operador/consolidador · Ida · Vuelta · Pasajeros
 *   Costo · Venta · Moneda · Fecha límite de emisión (a la vista, guía UX)
 *
 * Más detalles (plegado):
 *   PNR · Números de ticket · Horarios y escalas · Equipaje
 *
 * Permiso `cobranzas.see_cost`:
 *   - Con permiso: ve el campo Costo + ganancia en el footer.
 *   - Sin permiso: no ve costo ni ganancia (jamás "$0").
 *
 * Cálculo del total: precio total × pasajeros (si se ingresa precio unitario).
 * NOTA: El aéreo puede venderse como precio cerrado (consolidado) o por pasajero;
 * usamos precio total de venta directo (como el modal viejo) para no asumir unitarización.
 */

import { useState } from "react";
import { Plane, ChevronDown, ChevronUp, Calendar, Users } from "lucide-react";
import { hasPermission } from "../../../auth";
import { ProductSearchField } from "./ProductSearchField";
import { redondearDinero, formatearPrecio } from "./HotelInlineForm";

// ─── Clases CSS (mismas que HotelInlineForm para coherencia visual) ──────────
const INPUT_BASE = "w-full py-2 px-3 text-sm border rounded-lg bg-white focus:outline-none focus:ring-1 focus:border-blue-500 focus:ring-blue-500 disabled:bg-slate-50 disabled:text-slate-400";
const INPUT_NORMAL = `${INPUT_BASE} border-slate-200`;
const INPUT_SUGERIDO = `${INPUT_BASE} border-yellow-400 bg-yellow-50`;
const INPUT_CALCULADO = `${INPUT_BASE} border-slate-200 border-dashed bg-slate-50 text-slate-600 font-semibold cursor-default`;
const LABEL_BASE = "block text-xs font-semibold text-slate-600 mb-1";

// ─── Recuadro violeta para vuelo nuevo ───────────────────────────────────────

/**
 * Recuadro que aparece cuando el usuario crea una ruta/aerolínea nueva.
 * Campos mínimos: nombre/identificador (ej: "AEP-MDQ LATAM") + operador.
 */
function NewFlightBox({ newProduct, onChange, suppliers }) {
    return (
        <div className="border border-dashed border-violet-400 bg-violet-50 rounded-xl p-4 mb-4">
            <div className="flex items-center gap-2 mb-3">
                <Plane className="w-4 h-4 text-violet-600" />
                <span className="text-sm font-semibold text-violet-700">
                    Ruta nueva — se guarda en tu tarifario al confirmar
                </span>
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-violet-200 text-violet-700">
                    Creado en venta
                </span>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                    <label className={LABEL_BASE}>Ruta / aerolínea *</label>
                    <input
                        type="text"
                        className={INPUT_NORMAL}
                        value={newProduct.name || ""}
                        onChange={(event) => onChange({ ...newProduct, name: event.target.value })}
                        placeholder="Ej: AEP–IGR LATAM"
                        required
                        data-testid="new-flight-name"
                        aria-label="Ruta o aerolínea nueva"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE}>Operador / consolidador *</label>
                    <select
                        className={INPUT_NORMAL}
                        value={newProduct.supplierPublicId || ""}
                        onChange={(event) => onChange({ ...newProduct, supplierPublicId: event.target.value })}
                        required
                        data-testid="new-flight-supplier"
                        aria-label="Operador o consolidador del vuelo nuevo"
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

// ─── Componente principal FlightInlineForm ────────────────────────────────────

export function FlightInlineForm({ form, setForm, suppliers, isEditing }) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    // Cálculo de ganancia: precio de venta - costo (solo si tiene permiso de ver costos)
    const ventaTotal = redondearDinero(Number(form.salePrice) || 0);
    const costoTotal = canSeeCost ? redondearDinero(Number(form.netCost) || 0) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;

    // "Más detalles" se abre automáticamente al editar si ya hay datos en esos campos
    const tieneDetallesExistentes = Boolean(
        form.pnr || form.ticketNumber || form.baggage || form.scheduleNotes
    );
    const [mostrarDetalles, setMostrarDetalles] = useState(tieneDetallesExistentes || isEditing);

    // Campos que vinieron sugeridos del buscador (se pintan en amarillo)
    const [camposSugeridos, setCamposSugeridos] = useState({
        supplierId: false,
        netCost: false,
        salePrice: false,
        currency: false,
    });

    // C5: si el operador sugerido no está en la lista de operadores de la reserva,
    // lo agregamos dinámicamente para que el <select> no quede con nada seleccionado
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
        const salePrice = sale.salePrice != null ? String(sale.salePrice) : "";
        const netCost = canSeeCost && sale.netCost != null ? String(sale.netCost) : form.netCost;
        const currency = sale.currency || "ARS";

        setForm((prev) => ({
            ...prev,
            routeName: catalogResult.name || prev.routeName,
            rateId: catalogResult.ratePublicId,
            newCatalogProduct: null,
            supplierId: supplierPublicId,
            supplierName: sale.supplierName || null,
            salePrice,
            netCost: canSeeCost ? netCost : prev.netCost,
            currency,
        }));

        setCamposSugeridos({
            supplierId: Boolean(supplierPublicId),
            netCost: canSeeCost && sale.netCost != null,
            salePrice: Boolean(sale.salePrice),
            currency: Boolean(sale.currency),
        });
    };

    const handleCreateNew = (searchText) => {
        setForm((prev) => ({
            ...prev,
            routeName: searchText,
            rateId: null,
            newCatalogProduct: { name: searchText, supplierPublicId: "" },
            supplierId: "",
            netCost: "",
            salePrice: "",
            currency: "ARS",
        }));
        setCamposSugeridos({ supplierId: false, netCost: false, salePrice: false, currency: false });
    };

    const handleSearchChange = (texto) => {
        setForm((prev) => ({
            ...prev,
            routeName: texto,
            rateId: null,
            newCatalogProduct: texto ? prev.newCatalogProduct : null,
        }));
        if (!texto) {
            setCamposSugeridos({ supplierId: false, netCost: false, salePrice: false, currency: false });
        }
    };

    return (
        <div className="space-y-4">

            {/* === BUSCADOR (ruta o aerolínea) === */}
            <ProductSearchField
                serviceType="Aereo"
                value={form.routeName || ""}
                onChange={handleSearchChange}
                onSelectExisting={handleSelectExisting}
                onCreateNew={handleCreateNew}
                disabled={isEditing}
                label="Ruta / aerolínea"
                placeholder="Ej: AEP–IGR, LATAM, Aerolíneas..."
            />

            {/* === RECUADRO PRODUCTO NUEVO === */}
            {form.newCatalogProduct && (
                <NewFlightBox
                    newProduct={form.newCatalogProduct}
                    onChange={(newProduct) => setForm((prev) => ({ ...prev, newCatalogProduct: newProduct }))}
                    suppliers={suppliers}
                />
            )}

            {/* === OPERADOR / CONSOLIDADOR (solo si no es producto nuevo) === */}
            {!form.newCatalogProduct && (
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-operador">Operador / consolidador</label>
                    <select
                        id="flight-operador"
                        className={camposSugeridos.supplierId ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.supplierId || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, supplierId: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, supplierId: false }));
                        }}
                        data-testid="flight-supplier"
                        aria-label="Operador o consolidador"
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

            {/* === FECHAS IDA · VUELTA + PASAJEROS === */}
            <div className="grid grid-cols-2 sm:grid-cols-3 gap-3">
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-ida">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Ida
                    </label>
                    <input
                        id="flight-ida"
                        type="date"
                        className={INPUT_NORMAL}
                        value={form.departureDate || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, departureDate: event.target.value }))}
                        data-testid="flight-ida"
                        aria-label="Fecha de ida"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-vuelta">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Vuelta
                    </label>
                    <input
                        id="flight-vuelta"
                        type="date"
                        className={INPUT_NORMAL}
                        value={form.returnDate || ""}
                        min={form.departureDate || undefined}
                        onChange={(event) => setForm((prev) => ({ ...prev, returnDate: event.target.value }))}
                        data-testid="flight-vuelta"
                        aria-label="Fecha de vuelta (vacío si solo hay ida)"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-pasajeros">
                        <Users className="inline w-3 h-3 mr-1" />
                        Pasajeros
                    </label>
                    <input
                        id="flight-pasajeros"
                        type="number"
                        min={1}
                        className={INPUT_NORMAL}
                        value={form.passengers || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, passengers: event.target.value }))}
                        placeholder="1"
                        data-testid="flight-pasajeros"
                        aria-label="Cantidad de pasajeros"
                    />
                </div>
            </div>

            {/* === PRECIOS + MONEDA + FECHA LÍMITE DE EMISIÓN ===
                La fecha de emisión va A LA VISTA (no en "Más detalles") — decisión guía UX. */}
            <div className={`grid gap-3 ${canSeeCost ? "grid-cols-2 sm:grid-cols-4" : "grid-cols-2 sm:grid-cols-3"}`}>
                {canSeeCost && (
                    <div>
                        <label className={LABEL_BASE} htmlFor="flight-costo">Costo</label>
                        <input
                            id="flight-costo"
                            type="number"
                            min={0}
                            step="0.01"
                            className={camposSugeridos.netCost ? INPUT_SUGERIDO : INPUT_NORMAL}
                            value={form.netCost || ""}
                            onChange={(event) => {
                                setForm((prev) => ({ ...prev, netCost: event.target.value }));
                                setCamposSugeridos((prev) => ({ ...prev, netCost: false }));
                            }}
                            placeholder="0,00"
                            data-testid="flight-costo"
                            aria-label="Costo total del vuelo"
                        />
                    </div>
                )}
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-venta">Venta</label>
                    <input
                        id="flight-venta"
                        type="number"
                        min={0}
                        step="0.01"
                        className={camposSugeridos.salePrice ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.salePrice || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, salePrice: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, salePrice: false }));
                        }}
                        placeholder="0,00"
                        required
                        data-testid="flight-venta"
                        aria-label="Precio de venta total"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-moneda">Moneda</label>
                    <select
                        id="flight-moneda"
                        className={camposSugeridos.currency ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.currency || "ARS"}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, currency: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, currency: false }));
                        }}
                        data-testid="flight-moneda"
                        aria-label="Moneda"
                    >
                        <option value="ARS">ARS (pesos)</option>
                        <option value="USD">USD (dólares)</option>
                    </select>
                </div>
                <div>
                    {/* Fecha límite de emisión: A LA VISTA (guía UX) — el sistema avisa cuando se acerca */}
                    <label className={LABEL_BASE} htmlFor="flight-fecha-emision">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Límite de emisión
                    </label>
                    <input
                        id="flight-fecha-emision"
                        type="date"
                        className={INPUT_NORMAL}
                        value={form.emissionDeadline || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, emissionDeadline: event.target.value }))}
                        data-testid="flight-fecha-emision"
                        aria-label="Fecha límite de emisión del vuelo"
                    />
                </div>
            </div>

            {/* === MÁS DETALLES: PNR · Nº ticket · Horarios/escalas · Equipaje === */}
            <div>
                <button
                    type="button"
                    onClick={() => setMostrarDetalles((prev) => !prev)}
                    className="flex items-center gap-1 text-sm font-semibold text-blue-600 hover:text-blue-800 transition-colors"
                    data-testid="flight-mas-detalles-toggle"
                    aria-expanded={mostrarDetalles}
                >
                    {mostrarDetalles ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                    {mostrarDetalles ? "Menos detalles" : "+ Más detalles"}
                </button>

                {mostrarDetalles && (
                    <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-3">
                        <div>
                            <label className={LABEL_BASE} htmlFor="flight-pnr">Código de reserva (PNR)</label>
                            <input
                                id="flight-pnr"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.pnr || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, pnr: event.target.value.toUpperCase() }))}
                                placeholder="ABC123"
                                data-testid="flight-pnr"
                                aria-label="Código de reserva PNR"
                            />
                        </div>
                        <div>
                            <label className={LABEL_BASE} htmlFor="flight-ticket">Números de ticket</label>
                            <input
                                id="flight-ticket"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.ticketNumber || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, ticketNumber: event.target.value }))}
                                placeholder="0741234567890"
                                data-testid="flight-ticket"
                                aria-label="Números de ticket"
                            />
                        </div>
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE} htmlFor="flight-horarios">Horarios y escalas</label>
                            <input
                                id="flight-horarios"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.scheduleNotes || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, scheduleNotes: event.target.value }))}
                                placeholder="Ej: Sale 10:30 AEP · Escala 1h MDZ · Llega 15:20 IGR"
                                data-testid="flight-horarios"
                                aria-label="Horarios y escalas"
                            />
                        </div>
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE} htmlFor="flight-equipaje">Equipaje</label>
                            <input
                                id="flight-equipaje"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.baggage || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, baggage: event.target.value }))}
                                placeholder="Ej: 1 pieza 23kg + 1 de mano"
                                data-testid="flight-equipaje"
                                aria-label="Equipaje incluido"
                            />
                        </div>
                    </div>
                )}
            </div>

            {/* Los totales se exportan hacia el footer de ServiceInlineCard */}
        </div>
    );
}

// ─── Cálculo de totales exportado para el footer de ServiceInlineCard ─────────

/**
 * Calcula los totales del vuelo para mostrar en el footer.
 * El aéreo usa precio total (no se multiplica por días/noches).
 */
export function calcularTotalesVuelo({ salePrice, netCost, canSeeCost }) {
    const ventaTotal = redondearDinero(Number(salePrice) || 0);
    const costoTotal = canSeeCost ? redondearDinero(Number(netCost) || 0) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
    return { ventaTotal, costoTotal, ganancia };
}
