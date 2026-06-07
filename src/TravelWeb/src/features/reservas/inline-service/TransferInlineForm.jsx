/**
 * Formulario de Traslado dentro de la ficha de carga en línea (ServiceInlineCard).
 *
 * Campos a la vista SIEMPRE (sin revelado progresivo — guía UX ronda 1):
 *   Buscador de trayecto · Operador · Fecha · Llegada o salida · Pasajeros
 *   Privado o compartido · Costo · Venta · Moneda
 *
 * Más detalles (plegado):
 *   Número de vuelo asociado · Horario de búsqueda · Confirmación del operador
 *
 * Permiso `cobranzas.see_cost`:
 *   - Con permiso: ve el campo Costo + ganancia en el footer.
 *   - Sin permiso: no ve costo ni ganancia (jamás "$0").
 *
 * Cálculo del total: precio total de venta directo (traslado privado = precio cerrado;
 * compartido = normalmente precio por persona, pero el vendedor ingresa el total).
 */

import { useState } from "react";
import { Car, ChevronDown, ChevronUp, Calendar, Users } from "lucide-react";
import { hasPermission } from "../../../auth";
import { ProductSearchField } from "./ProductSearchField";
import { redondearDinero, formatearPrecio } from "./HotelInlineForm";

// ─── Clases CSS ───────────────────────────────────────────────────────────────
const INPUT_BASE = "w-full py-2 px-3 text-sm border rounded-lg bg-white focus:outline-none focus:ring-1 focus:border-blue-500 focus:ring-blue-500 disabled:bg-slate-50 disabled:text-slate-400";
const INPUT_NORMAL = `${INPUT_BASE} border-slate-200`;
const INPUT_SUGERIDO = `${INPUT_BASE} border-yellow-400 bg-yellow-50`;
const LABEL_BASE = "block text-xs font-semibold text-slate-600 mb-1";

// ─── Recuadro violeta para trayecto nuevo ────────────────────────────────────

/**
 * Recuadro que aparece al crear un trayecto de traslado nuevo.
 * Campo mínimo: nombre del trayecto (ej: "EZE → Sheraton Pilar") + operador.
 */
function NewTransferBox({ newProduct, onChange, suppliers }) {
    return (
        <div className="border border-dashed border-violet-400 bg-violet-50 rounded-xl p-4 mb-4">
            <div className="flex items-center gap-2 mb-3">
                <Car className="w-4 h-4 text-violet-600" />
                <span className="text-sm font-semibold text-violet-700">
                    Trayecto nuevo — se guarda en tu tarifario al confirmar
                </span>
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-violet-200 text-violet-700">
                    Creado en venta
                </span>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                    <label className={LABEL_BASE}>Trayecto *</label>
                    <input
                        type="text"
                        className={INPUT_NORMAL}
                        value={newProduct.name || ""}
                        onChange={(event) => onChange({ ...newProduct, name: event.target.value })}
                        placeholder="Ej: EZE → Sheraton Pilar"
                        required
                        data-testid="new-transfer-name"
                        aria-label="Nombre del trayecto nuevo"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE}>Operador *</label>
                    <select
                        className={INPUT_NORMAL}
                        value={newProduct.supplierPublicId || ""}
                        onChange={(event) => onChange({ ...newProduct, supplierPublicId: event.target.value })}
                        required
                        data-testid="new-transfer-supplier"
                        aria-label="Operador del traslado nuevo"
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

// ─── Componente principal TransferInlineForm ──────────────────────────────────

export function TransferInlineForm({ form, setForm, suppliers, isEditing }) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    const ventaTotal = redondearDinero(Number(form.salePrice) || 0);
    const costoTotal = canSeeCost ? redondearDinero(Number(form.netCost) || 0) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;

    // "Más detalles" se abre automáticamente al editar si ya hay datos en esos campos.
    // vehicleType también se considera: si viene persistido del backend, expandimos la sección.
    const tieneDetallesExistentes = Boolean(
        form.associatedFlightNumber || form.pickupTime || form.confirmationNumber || form.vehicleType
    );
    const [mostrarDetalles, setMostrarDetalles] = useState(tieneDetallesExistentes || isEditing);

    const [camposSugeridos, setCamposSugeridos] = useState({
        supplierId: false,
        netCost: false,
        salePrice: false,
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

            {/* === BUSCADOR (trayecto) === */}
            <ProductSearchField
                serviceType="Traslado"
                value={form.routeName || ""}
                onChange={handleSearchChange}
                onSelectExisting={handleSelectExisting}
                onCreateNew={handleCreateNew}
                disabled={isEditing}
                label="Trayecto"
                placeholder="Ej: EZE → hotel, Aeropuerto → ciudad..."
            />

            {/* === RECUADRO PRODUCTO NUEVO === */}
            {form.newCatalogProduct && (
                <NewTransferBox
                    newProduct={form.newCatalogProduct}
                    onChange={(newProduct) => setForm((prev) => ({ ...prev, newCatalogProduct: newProduct }))}
                    suppliers={suppliers}
                />
            )}

            {/* === OPERADOR === */}
            {!form.newCatalogProduct && (
                <div>
                    <label className={LABEL_BASE} htmlFor="transfer-operador">Operador</label>
                    <select
                        id="transfer-operador"
                        className={camposSugeridos.supplierId ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.supplierId || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, supplierId: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, supplierId: false }));
                        }}
                        data-testid="transfer-supplier"
                        aria-label="Operador del traslado"
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

            {/* === FECHA + LLEGADA O SALIDA + PASAJEROS + TIPO === */}
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <div>
                    <label className={LABEL_BASE} htmlFor="transfer-fecha">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Fecha
                    </label>
                    <input
                        id="transfer-fecha"
                        type="date"
                        className={INPUT_NORMAL}
                        value={form.pickupDate || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, pickupDate: event.target.value }))}
                        data-testid="transfer-fecha"
                        aria-label="Fecha del traslado"
                    />
                </div>
                <div>
                    {/*
                     * "Llegada o salida": mapea a direction "in"/"out" del backend
                     * (TransferBookingDto.Direction). El value del option ES el valor backend.
                     */}
                    <label className={LABEL_BASE} htmlFor="transfer-tipo-movimiento">Llegada o salida</label>
                    <select
                        id="transfer-tipo-movimiento"
                        className={INPUT_NORMAL}
                        value={form.movementType || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, movementType: event.target.value }))}
                        data-testid="transfer-tipo-movimiento"
                        aria-label="Tipo de movimiento: llegada o salida"
                    >
                        <option value="">Sin especificar</option>
                        <option value="in">Llegada</option>
                        <option value="out">Salida</option>
                    </select>
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="transfer-pasajeros">
                        <Users className="inline w-3 h-3 mr-1" />
                        Pasajeros
                    </label>
                    <input
                        id="transfer-pasajeros"
                        type="number"
                        min={1}
                        className={INPUT_NORMAL}
                        value={form.passengers || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, passengers: event.target.value }))}
                        placeholder="1"
                        data-testid="transfer-pasajeros"
                        aria-label="Cantidad de pasajeros"
                    />
                </div>
                <div>
                    {/*
                     * Privado/Compartido: mapea a serviceMode "private"/"shared" del backend
                     * (TransferBookingDto.ServiceMode). El value del option ES el valor backend.
                     */}
                    <label className={LABEL_BASE} htmlFor="transfer-modalidad">Modalidad</label>
                    <select
                        id="transfer-modalidad"
                        className={INPUT_NORMAL}
                        value={form.transferType || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, transferType: event.target.value }))}
                        data-testid="transfer-modalidad"
                        aria-label="Privado o compartido"
                    >
                        <option value="">Sin especificar</option>
                        <option value="private">Privado</option>
                        <option value="shared">Compartido</option>
                    </select>
                </div>
            </div>

            {/* === PRECIOS + MONEDA === */}
            <div className={`grid gap-3 ${canSeeCost ? "grid-cols-2 sm:grid-cols-3" : "grid-cols-2"}`}>
                {canSeeCost && (
                    <div>
                        <label className={LABEL_BASE} htmlFor="transfer-costo">Costo</label>
                        <input
                            id="transfer-costo"
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
                            data-testid="transfer-costo"
                            aria-label="Costo del traslado"
                        />
                    </div>
                )}
                <div>
                    <label className={LABEL_BASE} htmlFor="transfer-venta">Venta</label>
                    <input
                        id="transfer-venta"
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
                        data-testid="transfer-venta"
                        aria-label="Precio de venta del traslado"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="transfer-moneda">Moneda</label>
                    <select
                        id="transfer-moneda"
                        className={camposSugeridos.currency ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.currency || "ARS"}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, currency: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, currency: false }));
                        }}
                        data-testid="transfer-moneda"
                        aria-label="Moneda"
                    >
                        <option value="ARS">ARS (pesos)</option>
                        <option value="USD">USD (dólares)</option>
                    </select>
                </div>
            </div>

            {/* === MÁS DETALLES: Nº vuelo asociado · Horario de búsqueda · Confirmación === */}
            <div>
                <button
                    type="button"
                    onClick={() => setMostrarDetalles((prev) => !prev)}
                    className="flex items-center gap-1 text-sm font-semibold text-blue-600 hover:text-blue-800 transition-colors"
                    data-testid="transfer-mas-detalles-toggle"
                    aria-expanded={mostrarDetalles}
                >
                    {mostrarDetalles ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                    {mostrarDetalles ? "Menos detalles" : "+ Más detalles"}
                </button>

                {mostrarDetalles && (
                    <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-3">
                        <div>
                            <label className={LABEL_BASE} htmlFor="transfer-vuelo-asociado">Número de vuelo asociado</label>
                            <input
                                id="transfer-vuelo-asociado"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.associatedFlightNumber || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, associatedFlightNumber: event.target.value.toUpperCase() }))}
                                placeholder="Ej: AR1234"
                                data-testid="transfer-vuelo-asociado"
                                aria-label="Número de vuelo asociado al traslado"
                            />
                        </div>
                        <div>
                            <label className={LABEL_BASE} htmlFor="transfer-horario">Horario de búsqueda</label>
                            <input
                                id="transfer-horario"
                                type="time"
                                className={INPUT_NORMAL}
                                value={form.pickupTime || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, pickupTime: event.target.value }))}
                                data-testid="transfer-horario"
                                aria-label="Horario de búsqueda del traslado"
                            />
                        </div>
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE} htmlFor="transfer-confirmacion">Confirmación del operador</label>
                            <input
                                id="transfer-confirmacion"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.confirmationNumber || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, confirmationNumber: event.target.value }))}
                                placeholder="Número o código de confirmación"
                                data-testid="transfer-confirmacion"
                                aria-label="Número de confirmación del operador"
                            />
                        </div>
                        <div>
                            {/*
                             * Tipo de vehículo: texto libre, igual que el modal viejo (ServiceFormModal:1089-1096).
                             * Ejemplos: "Van", "Sedan", "Micro". Opcional, no afecta el precio.
                             */}
                            <label className={LABEL_BASE} htmlFor="transfer-tipo-vehiculo">Tipo de vehículo</label>
                            <input
                                id="transfer-tipo-vehiculo"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.vehicleType || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, vehicleType: event.target.value }))}
                                placeholder="Van, sedan, microbus..."
                                data-testid="inline-transfer-vehicle-type"
                                aria-label="Tipo de vehículo del traslado"
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
 * Calcula los totales del traslado para mostrar en el footer.
 * El traslado usa precio total directo (privado = precio cerrado; compartido = total también).
 */
export function calcularTotalesTraslado({ salePrice, netCost, canSeeCost }) {
    const ventaTotal = redondearDinero(Number(salePrice) || 0);
    const costoTotal = canSeeCost ? redondearDinero(Number(netCost) || 0) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
    return { ventaTotal, costoTotal, ganancia };
}
