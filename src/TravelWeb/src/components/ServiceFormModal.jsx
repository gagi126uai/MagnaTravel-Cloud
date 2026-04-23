import { useState, useEffect, useCallback, useMemo } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { X, Plane, Hotel, Bus, Package, Search, Calculator, DollarSign, AlertCircle, RefreshCw } from "lucide-react";
import { getPublicId } from "../lib/publicIds";

const SERVICE_TYPES = [
    { value: "Aereo", label: "Aéreo", icon: Plane, color: "sky" },
    { value: "Hotel", label: "Hotel", icon: Hotel, color: "amber" },
    { value: "Traslado", label: "Traslado", icon: Bus, color: "emerald" },
    { value: "Paquete", label: "Paquete", icon: Package, color: "violet" }
];

const inputClass = "w-full rounded-xl border border-slate-200 bg-slate-50 p-2.5 text-sm text-slate-900 focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-600 dark:bg-slate-700 dark:text-white dark:focus:border-indigo-400";
const labelClass = "block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1";

const calculateNights = (checkIn, checkOut) => {
    if (!checkIn || !checkOut) return 0;
    const start = new Date(checkIn);
    const end = new Date(checkOut);
    const diff = Math.ceil((end - start) / (1000 * 60 * 60 * 24));
    return diff > 0 ? diff : 0;
};

// ================== BUSCADOR DE TARIFAS ==================
function RateSelector({ serviceType, supplierId, onSelect, disabled }) {
    const [rates, setRates] = useState([]);
    const [search, setSearch] = useState("");
    const [loading, setLoading] = useState(false);
    const [showDropdown, setShowDropdown] = useState(false);

    const searchRates = useCallback(async () => {
        if (!supplierId) {
            setRates([]);
            return;
        }
        setLoading(true);
        try {
            const params = new URLSearchParams();
            // Normalize service type for backend (remove accents if any, though we'll pass values from SERVICE_TYPES)
            params.append("serviceType", serviceType);
            params.append("supplierId", supplierId);
            if (search) params.append("query", search);
            const data = await api.get(`/rates/search?${params}`);
            setRates(data || []);
        } catch {
            setRates([]);
        } finally {
            setLoading(false);
        }
    }, [serviceType, supplierId, search]);

    useEffect(() => {
        if (showDropdown && supplierId) searchRates();
    }, [showDropdown, supplierId, searchRates]);

    return (
        <div className="relative">
            <label className={labelClass}>Vincular Tarifario</label>
            <div className="relative">
                <Search className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                <input
                    type="text"
                    className={`${inputClass} pl-10 border-indigo-200 dark:border-indigo-900/50 bg-indigo-50/20`}
                    placeholder={`Buscar en tarifario de ${serviceType}...`}
                    value={search}
                    onChange={(e) => {
                        setSearch(e.target.value);
                        setShowDropdown(true);
                    }}
                    onFocus={() => setShowDropdown(true)}
                    disabled={disabled || !supplierId}
                />
                {loading && (
                    <div className="absolute right-3 top-2.5">
                        <RefreshCw className="h-4 w-4 text-indigo-500 animate-spin" />
                    </div>
                )}
            </div>

            {showDropdown && supplierId && (
                <div className="absolute z-30 w-full mt-1 bg-white dark:bg-slate-800 border border-slate-200 dark:border-slate-600 rounded-xl shadow-lg max-h-68 overflow-y-auto overflow-x-hidden animate-in fade-in slide-in-from-top-2 duration-200">
                    {rates.length === 0 ? (
                        <div className="p-4 text-center text-sm text-slate-500">
                            {loading ? "Buscando tarifas..." : "No se encontraron tarifas para este proveedor."}
                        </div>
                    ) : (
                        rates.map((rate) => {
                            const isExpired = rate.validTo && new Date(rate.validTo) < new Date();
                            return (
                                <button
                                    key={rate.publicId}
                                    type="button"
                                    onClick={() => {
                                        onSelect(rate);
                                        setSearch(rate.productName || rate.hotelName || "");
                                        setShowDropdown(false);
                                    }}
                                    className={`w-full text-left px-4 py-3 border-b border-slate-100 dark:border-slate-700 last:border-0 transition-colors
                                        ${isExpired ? "opacity-60 cursor-not-allowed bg-slate-50 dark:bg-slate-900/50" : "hover:bg-indigo-50 dark:hover:bg-indigo-900/30 cursor-pointer"}`}
                                    disabled={isExpired}
                                >
                                    <div className="flex justify-between items-start mb-1">
                                        <div className="font-bold text-slate-900 dark:text-white truncate">
                                            {rate.serviceType === "Hotel" ? rate.hotelName : rate.productName}
                                        </div>
                                        {isExpired && (
                                            <span className="ml-2 px-1.5 py-0.5 rounded text-[10px] font-bold bg-amber-100 text-amber-700">VENCIDO</span>
                                        )}
                                    </div>
                                    <div className="flex flex-wrap gap-x-3 gap-y-1 text-[11px] text-slate-500">
                                        {rate.city && <span>📍 {rate.city}</span>}
                                        {rate.roomType && <span>🛏️ {rate.roomType}</span>}
                                        {rate.airline && <span>✈️ {rate.airline}</span>}
                                        <div className="flex items-center gap-2 font-mono">
                                            <span className="text-emerald-600 font-bold">NET: ${rate.netCost}</span>
                                            <span className="text-indigo-600 font-bold">VTA: ${rate.salePrice}</span>
                                        </div>
                                    </div>
                                </button>
                            );
                        })
                    )}
                </div>
            )}
        </div>
    );
}

// ... Forms for Flight, Hotel, Transfer, Package remain mostly same but with disabled prop
function FlightForm({ form, setForm, suppliers, onRateSelect, disabled }) {
    return (
        <div className="space-y-4">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required disabled={disabled}>
                        <option value="">Seleccionar proveedor...</option>
                        {suppliers.map(s => <option key={s.id || s.publicId || s.PublicId} value={s.publicId || s.PublicId}>{s.name} {!s.isActive && '(Inactivo)'}</option>)}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Estado *</label>
                    <select className={inputClass} value={form.workflowStatus || "Solicitado"} onChange={e => setForm({ ...form, workflowStatus: e.target.value })} required disabled={disabled}>
                        <option value="Solicitado">Solicitado</option>
                        <option value="Confirmado">Confirmado</option>
                        <option value="Cancelado">Cancelado</option>
                    </select>
                </div>
            </div>
            <RateSelector serviceType={form.serviceType || "Aereo"} supplierId={form.supplierId} onSelect={onRateSelect} disabled={disabled} />
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
                <div>
                    <label className={labelClass}>Origen</label>
                    <input className={inputClass} placeholder="BUE" value={form.origin || ""} onChange={e => setForm({ ...form, origin: e.target.value.toUpperCase() })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Destino</label>
                    <input className={inputClass} placeholder="MIA" value={form.destination || ""} onChange={e => setForm({ ...form, destination: e.target.value.toUpperCase() })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Salida</label>
                    <input type="date" className={inputClass} value={form.departureDate || ""} onChange={e => setForm({ ...form, departureDate: e.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Regreso</label>
                    <input type="date" className={inputClass} value={form.arrivalDate || ""} onChange={e => setForm({ ...form, arrivalDate: e.target.value })} disabled={disabled} />
                </div>
            </div>
        </div>
    );
}

function HotelForm({ form, setForm, suppliers, onRateSelect, disabled }) {
    return (
        <div className="space-y-4">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required disabled={disabled}>
                        <option value="">Seleccionar proveedor...</option>
                        {suppliers.map(s => <option key={s.id || s.publicId || s.PublicId} value={s.publicId || s.PublicId}>{s.name} {!s.isActive && '(Inactivo)'}</option>)}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Estado *</label>
                    <select className={inputClass} value={form.workflowStatus || "Solicitado"} onChange={e => setForm({ ...form, workflowStatus: e.target.value })} required disabled={disabled}>
                        <option value="Solicitado">Solicitado</option>
                        <option value="Confirmado">Confirmado</option>
                        <option value="Cancelado">Cancelado</option>
                    </select>
                </div>
            </div>
            <RateSelector serviceType={form.serviceType || "Hotel"} supplierId={form.supplierId} onSelect={onRateSelect} disabled={disabled} />
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Tipo Habitación *</label>
                    <select className={inputClass} value={form.roomType || ""} onChange={e => setForm({ ...form, roomType: e.target.value })} required disabled={disabled}>
                        <option value="">Seleccionar...</option>
                        <option value="Single">Single</option>
                        <option value="Doble">Doble</option>
                        <option value="Triple">Triple</option>
                        <option value="Cuádruple">Cuádruple</option>
                        <option value="Suite">Suite</option>
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Régimen *</label>
                    <select className={inputClass} value={form.mealPlan || ""} onChange={e => setForm({ ...form, mealPlan: e.target.value })} required disabled={disabled}>
                        <option value="">Seleccionar...</option>
                        <option value="Solo Alojamiento">Solo Alojamiento</option>
                        <option value="Desayuno">Desayuno</option>
                        <option value="Media Pensión">Media Pensión</option>
                        <option value="Pensión Completa">Pensión Completa</option>
                        <option value="All Inclusive">All Inclusive</option>
                    </select>
                </div>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Nombre Hotel</label>
                    <input className={inputClass} value={form.hotelName || ""} onChange={e => setForm({ ...form, hotelName: e.target.value })} disabled={disabled} />
                </div>
                <div className="grid grid-cols-2 gap-2">
                    <div>
                        <label className={labelClass}>Check-In</label>
                        <input type="date" className={inputClass} value={form.checkIn || ""} onChange={e => setForm({ ...form, checkIn: e.target.value })} disabled={disabled} />
                    </div>
                    <div>
                        <label className={labelClass}>Check-Out</label>
                        <input type="date" className={inputClass} value={form.checkOut || ""} onChange={e => setForm({ ...form, checkOut: e.target.value })} disabled={disabled} />
                    </div>
                </div>
            </div>
            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className={labelClass}>Habitaciones</label>
                    <input type="number" className={inputClass} value={form.rooms || 1} onChange={e => setForm({ ...form, rooms: parseInt(e.target.value) })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Adultos</label>
                    <input type="number" className={inputClass} value={form.adults || 2} onChange={e => setForm({ ...form, adults: parseInt(e.target.value) })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Menores</label>
                    <input type="number" className={inputClass} value={form.children || 0} onChange={e => setForm({ ...form, children: parseInt(e.target.value) })} disabled={disabled} />
                </div>
            </div>
        </div>
    );
}

function TransferForm({ form, setForm, suppliers, onRateSelect, disabled }) {
    return (
        <div className="space-y-4">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required disabled={disabled}>
                        <option value="">Seleccionar proveedor...</option>
                        {suppliers.map(s => <option key={s.id || s.publicId || s.PublicId} value={s.publicId || s.PublicId}>{s.name} {!s.isActive && '(Inactivo)'}</option>)}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Estado *</label>
                    <select className={inputClass} value={form.workflowStatus || "Solicitado"} onChange={e => setForm({ ...form, workflowStatus: e.target.value })} required disabled={disabled}>
                        <option value="Solicitado">Solicitado</option>
                        <option value="Confirmado">Confirmado</option>
                        <option value="Cancelado">Cancelado</option>
                    </select>
                </div>
            </div>
            <RateSelector serviceType={form.serviceType || "Traslado"} supplierId={form.supplierId} onSelect={onRateSelect} disabled={disabled} />
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Pick-up</label>
                    <input className={inputClass} value={form.pickupLocation || ""} onChange={e => setForm({ ...form, pickupLocation: e.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Drop-off</label>
                    <input className={inputClass} value={form.dropoffLocation || ""} onChange={e => setForm({ ...form, dropoffLocation: e.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Fecha</label>
                    <input type="date" className={inputClass} value={form.pickupDate || ""} onChange={e => setForm({ ...form, pickupDate: e.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Hora</label>
                    <input type="time" className={inputClass} value={form.pickupTime || ""} onChange={e => setForm({ ...form, pickupTime: e.target.value })} disabled={disabled} />
                </div>
            </div>
        </div>
    );
}

function PackageForm({ form, setForm, suppliers, onRateSelect, disabled }) {
    return (
        <div className="space-y-4">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required disabled={disabled}>
                        <option value="">Seleccionar proveedor...</option>
                        {suppliers.map(s => <option key={s.id || s.publicId || s.PublicId} value={s.publicId || s.PublicId}>{s.name} {!s.isActive && '(Inactivo)'}</option>)}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Estado *</label>
                    <select className={inputClass} value={form.workflowStatus || "Solicitado"} onChange={e => setForm({ ...form, workflowStatus: e.target.value })} required disabled={disabled}>
                        <option value="Solicitado">Solicitado</option>
                        <option value="Confirmado">Confirmado</option>
                        <option value="Cancelado">Cancelado</option>
                    </select>
                </div>
            </div>
            <RateSelector serviceType={form.serviceType || "Paquete"} supplierId={form.supplierId} onSelect={onRateSelect} disabled={disabled} />
            <div className="grid grid-cols-1 gap-4">
                <div>
                    <label className={labelClass}>Nombre del Paquete</label>
                    <input className={inputClass} value={form.packageName || ""} onChange={e => setForm({ ...form, packageName: e.target.value })} disabled={disabled} />
                </div>
                <div className="grid grid-cols-2 gap-4">
                    <div>
                        <label className={labelClass}>Inicio</label>
                        <input type="date" className={inputClass} value={form.startDate || ""} onChange={e => setForm({ ...form, startDate: e.target.value })} disabled={disabled} />
                    </div>
                    <div>
                        <label className={labelClass}>Fin</label>
                        <input type="date" className={inputClass} value={form.endDate || ""} onChange={e => setForm({ ...form, endDate: e.target.value })} disabled={disabled} />
                    </div>
                </div>
            </div>
        </div>
    );
}

// ================== FORMULARIO DE PRECIOS ==================
function PricingForm({ form, setForm, commissionPercent, onRecalculate, disabled }) {
    const margin = (form.salePrice || 0) - (form.netCost || 0);

    return (
        <div className="rounded-xl bg-gradient-to-r from-slate-50 to-slate-100 dark:from-slate-800 dark:to-slate-800/50 p-4 border border-slate-200 dark:border-slate-700 space-y-4">
            <div className="flex items-center justify-between">
                <h4 className="text-xs font-bold text-slate-500 dark:text-slate-400 uppercase flex items-center gap-2">
                    <DollarSign className="h-4 w-4" /> Valores Económicos
                </h4>
                {commissionPercent > 0 && !disabled && (
                    <button type="button" onClick={onRecalculate}
                        className="text-xs px-3 py-1.5 rounded-lg bg-indigo-100 text-indigo-700 dark:bg-indigo-900/50 dark:text-indigo-300 hover:bg-indigo-200 dark:hover:bg-indigo-900 flex items-center gap-1 transition-colors">
                        <Calculator className="h-3 w-3" /> Aplicar {commissionPercent}%
                    </button>
                )}
            </div>

            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className={labelClass}>Costo Neto *</label>
                    <div className="relative">
                        <span className="absolute left-3 top-2.5 text-slate-500">$</span>
                        <input type="number" step="0.01" className={`${inputClass} pl-6`} value={form.netCost || 0} onChange={e => setForm({ ...form, netCost: parseFloat(e.target.value) || 0 })} required disabled={disabled} />
                    </div>
                </div>
                <div>
                    <label className={labelClass}>Precio Venta *</label>
                    <div className="relative">
                        <span className="absolute left-3 top-2.5 text-slate-500">$</span>
                        <input type="number" step="0.01" className={`${inputClass} pl-6`} value={form.salePrice || 0} onChange={e => setForm({ ...form, salePrice: parseFloat(e.target.value) || 0 })} required disabled={disabled} />
                    </div>
                </div>
                <div>
                    <label className={labelClass}>Ganancia</label>
                    <div className={`p-2.5 rounded-xl text-center font-bold ${margin >= 0 ? 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400' : 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400'}`}>
                        ${margin.toFixed(2)}
                    </div>
                </div>
            </div>
        </div>
    );
}

// ================== MODAL PRINCIPAL ==================
export default function ServiceFormModal({ isOpen, onClose, reservaId, reservaStatus, suppliers, onSuccess, initialServiceType, serviceToEdit }) {
    const [serviceType, setServiceType] = useState(initialServiceType || "Aereo");
    const [form, setForm] = useState({ 
        supplierId: "", 
        netCost: 0, 
        salePrice: 0, 
        unitNetCost: 0, 
        unitSalePrice: 0, 
        rooms: 1, 
        adults: 2, 
        children: 0, 
        roomType: "Doble", 
        mealPlan: "Desayuno", 
        checkIn: "", 
        checkOut: "",
        workflowStatus: "Solicitado"
    });
    const [selectedRate, setSelectedRate] = useState(null); 
    const [currentRateInSystem, setCurrentRateInSystem] = useState(null);
    const [loading, setLoading] = useState(false);
    const [commissionPercent, setCommissionPercent] = useState(10);

    const isLocked = reservaStatus === "Operativo" || reservaStatus === "Cerrado";

    const sortedSuppliers = useMemo(() => {
        return [...suppliers].sort((a, b) => {
            if (a.isActive === b.isActive) return a.name.localeCompare(b.name);
            return a.isActive ? -1 : 1;
        });
    }, [suppliers]);

    const fetchCommission = useCallback(async () => {
        try {
            const params = new URLSearchParams();
            if (form.supplierId) params.append("supplierId", form.supplierId);
            params.append("serviceType", serviceType);
            const result = await api.get(`/commissions/calculate?${params}`);
            setCommissionPercent(result.commissionPercent || 10);
        } catch {
            setCommissionPercent(10);
        }
    }, [form.supplierId, serviceType]);

    useEffect(() => {
        if (form.supplierId) fetchCommission();
    }, [fetchCommission, form.supplierId]);

    useEffect(() => {
        if (serviceType === "Hotel") {
            const nights = calculateNights(form.checkIn, form.checkOut);
            const qty = (nights || 1) * (form.rooms || 1);
            if (form.unitNetCost > 0 || form.unitSalePrice > 0) {
                setForm(prev => ({
                    ...prev,
                    netCost: Math.round(prev.unitNetCost * qty * 100) / 100,
                    salePrice: Math.round(prev.unitSalePrice * qty * 100) / 100
                }));
            }
        }
    }, [form.checkIn, form.checkOut, form.rooms, form.unitNetCost, form.unitSalePrice, serviceType]);

    useEffect(() => {
        const checkPriceSync = async (rateId) => {
            try {
                const rate = await api.get(`/rates/${rateId}`);
                setCurrentRateInSystem(rate);
            } catch (err) {
                console.error("Error fetching rate for sync check", err);
            }
        };

        if (isOpen) {
            if (serviceToEdit) {
                setServiceType(serviceToEdit._type || serviceType);
                const formattedForm = {
                    ...serviceToEdit,
                    supplierId: serviceToEdit.supplierPublicId?.toString() || serviceToEdit.supplierId?.toString() || "",
                    departureDate: serviceToEdit.departureTime?.split('T')[0],
                    arrivalDate: serviceToEdit.arrivalTime?.split('T')[0],
                    checkIn: serviceToEdit.checkIn?.split('T')[0],
                    checkOut: serviceToEdit.checkOut?.split('T')[0],
                    startDate: serviceToEdit.startDate?.split('T')[0],
                    endDate: serviceToEdit.endDate?.split('T')[0],
                    pickupDate: serviceToEdit.pickupDateTime?.split('T')[0],
                    pickupTime: serviceToEdit.pickupDateTime ? new Date(serviceToEdit.pickupDateTime).toLocaleTimeString('en-GB').slice(0, 5) : "",
                    returnDate: serviceToEdit.returnDateTime?.split('T')[0],
                    returnTime: serviceToEdit.returnDateTime ? new Date(serviceToEdit.returnDateTime).toLocaleTimeString('en-GB').slice(0, 5) : "",
                    workflowStatus: serviceToEdit.workflowStatus || "Solicitado"
                };
                setForm(formattedForm);
                if (serviceToEdit.ratePublicId) checkPriceSync(serviceToEdit.ratePublicId);
            } else {
                setServiceType(initialServiceType || "Aereo");
                setForm({ 
                    supplierId: "", 
                    netCost: 0, 
                    salePrice: 0, 
                    unitNetCost: 0, 
                    unitSalePrice: 0, 
                    rooms: 1, 
                    adults: 2, 
                    children: 0, 
                    roomType: "Doble", 
                    mealPlan: "Desayuno", 
                    workflowStatus: "Solicitado" 
                });
                setCurrentRateInSystem(null);
            }
            setSelectedRate(null);
        }
    }, [isOpen, initialServiceType, serviceToEdit]);

    const isPriceDesynced = useMemo(() => {
        if (!currentRateInSystem || !serviceToEdit) return false;
        return currentRateInSystem.salePrice !== serviceToEdit.salePrice || 
               currentRateInSystem.netCost !== serviceToEdit.netCost;
    }, [currentRateInSystem, serviceToEdit]);

    const handleUpdateToLatestRate = () => {
        if (!currentRateInSystem) return;
        let multiplier = 1;
        if (serviceType === "Hotel") {
            const nights = calculateNights(form.checkIn, form.checkOut);
            const rooms = form.rooms || 1;
            if (nights > 0) multiplier = nights * rooms;
        }
        setForm(prev => ({
            ...prev,
            unitNetCost: currentRateInSystem.netCost,
            unitSalePrice: currentRateInSystem.salePrice,
            // netCost and salePrice will be updated by the useEffect
        }));
        showSuccess("Precios actualizados según tarifario.");
    };

    const handleRateSelect = (rate) => {
        setSelectedRate(rate);
        setForm(prev => {
            const newForm = { 
                ...prev, 
                rateId: rate.publicId?.toString() || "",
                unitNetCost: rate.netCost,
                unitSalePrice: rate.salePrice,
                description: rate.description || prev.description || ""
            };

            // If it's NOT a hotel, we don't use unit prices (yet), so we just set the total
            if (serviceType !== "Hotel") {
                newForm.netCost = rate.netCost;
                newForm.salePrice = rate.salePrice;
            }

            // Auto-populate based on service type
            if (serviceType === "Hotel") {
                newForm.hotelName = rate.hotelName || rate.productName;
                newForm.city = rate.city || prev.city;
                if (rate.roomType) newForm.roomType = rate.roomType;
                if (rate.mealPlan) newForm.mealPlan = rate.mealPlan;
            } else if (serviceType === "Paquete") {
                newForm.packageName = rate.productName;
            } else if (serviceType === "Aereo") {
                newForm.origin = rate.origin || prev.origin;
                newForm.destination = rate.destination || prev.destination;
                newForm.airline = rate.airline || prev.airline;
            } else if (serviceType === "Traslado") {
                newForm.pickupLocation = rate.pickupLocation || prev.pickupLocation;
                newForm.dropoffLocation = rate.dropoffLocation || prev.dropoffLocation;
            }

            return newForm;
        });
    };

    const applyCommission = () => {
        const cost = form.netCost || 0;
        const margin = cost * (commissionPercent / 100);
        setForm(prev => ({ ...prev, salePrice: Math.round((cost + margin) * 100) / 100 }));
    };

    const handleSubmit = async (e) => {
        e.preventDefault();
        setLoading(true);
        try {
            let endpoint = "";
            let method = "post";
            if (serviceType === "Aereo") endpoint = `/reservas/${reservaId}/flights`;
            else if (serviceType === "Hotel") endpoint = `/reservas/${reservaId}/hotels`;
            else if (serviceType === "Traslado") endpoint = `/reservas/${reservaId}/transfers`;
            else if (serviceType === "Paquete") endpoint = `/reservas/${reservaId}/packages`;

            if (serviceToEdit) {
                endpoint += `/${getPublicId(serviceToEdit)}`;
                method = "put";
            }

            const payload = { ...form };
            if (serviceType === "Aereo") {
                payload.departureTime = new Date(form.departureDate).toISOString();
                payload.arrivalTime = new Date(form.arrivalDate).toISOString();
            } else if (serviceType === "Hotel") {
                payload.checkIn = new Date(form.checkIn).toISOString();
                payload.checkOut = new Date(form.checkOut).toISOString();
            } else if (serviceType === "Traslado") {
                const pickupDT = form.pickupTime ? `${form.pickupDate}T${form.pickupTime}` : form.pickupDate;
                payload.pickupDateTime = new Date(pickupDT).toISOString();
            } else if (serviceType === "Paquete") {
                payload.startDate = new Date(form.startDate).toISOString();
                payload.endDate = new Date(form.endDate).toISOString();
            }

            const savedService = method === "put"
                ? await api.put(endpoint, payload)
                : await api.post(endpoint, payload);

            showSuccess("Servicio guardado");
            try {
                await onSuccess?.({ service: savedService, serviceType, action: method, showLoading: false });
            } catch (refreshError) {
                console.error("Error refreshing reserva after saving service", refreshError);
                showError("Servicio guardado, pero no se pudo actualizar la lista.");
            }
            onClose();
        } catch (error) {
            showError(error.message || "Error al guardar");
        } finally {
            setLoading(false);
        }
    };

    if (!isOpen) return null;
    const currentType = SERVICE_TYPES.find(t => t.value === serviceType);

    return (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4 backdrop-blur-sm">
            <div className="bg-white dark:bg-slate-900 rounded-2xl shadow-2xl max-w-2xl w-full max-h-[90vh] overflow-hidden">
                <div className="flex items-center justify-between p-4 border-b border-slate-200 dark:border-slate-700 bg-gradient-to-r from-indigo-500 to-purple-600">
                    <h2 className="text-lg font-semibold text-white flex items-center gap-2">
                        {currentType && <currentType.icon className="h-5 w-5" />}
                        {serviceToEdit ? "Editar Servicio" : "Agregar Servicio"}
                    </h2>
                    <button onClick={onClose} className="p-2 hover:bg-white/20 rounded-lg text-white/80 hover:text-white transition-colors">
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <div className="flex border-b border-slate-200 dark:border-slate-700 bg-slate-50 dark:bg-slate-800/50">
                    {SERVICE_TYPES.map(({ value, label, icon: Icon, color }) => (
                        <button key={value} type="button" 
                            onClick={() => {
                                if (!serviceToEdit) {
                                    setServiceType(value);
                                    setForm(prev => ({ ...prev, serviceType: value }));
                                }
                            }} 
                            disabled={!!serviceToEdit}
                            className={`flex-1 flex items-center justify-center gap-2 py-3.5 text-sm font-medium transition-all ${serviceType === value ? `border-b-3 border-${color}-500 text-${color}-600 bg-white dark:bg-slate-900` : "text-slate-500"}`}>
                            <Icon className="h-4 w-4" /> <span className="hidden sm:inline">{label}</span>
                        </button>
                    ))}
                </div>

                <form onSubmit={handleSubmit} className="p-4 space-y-4 overflow-y-auto max-h-[60vh]">
                    {isPriceDesynced && !isLocked && (
                        <div className="p-3 mb-4 rounded-xl bg-amber-50 border border-amber-200 dark:bg-amber-900/20 dark:border-amber-700/50 flex items-center justify-between">
                            <div className="flex items-center gap-3 text-amber-800 dark:text-amber-300 text-sm">
                                <AlertCircle className="h-5 w-5 flex-shrink-0" />
                                <div>
                                    <p className="font-bold">Tarifas Desactualizadas</p>
                                    <p className="opacity-80">El precio en el tarifario ha cambiado.</p>
                                </div>
                            </div>
                            <button type="button" onClick={handleUpdateToLatestRate} className="px-3 py-1.5 bg-amber-600 text-white rounded-lg text-xs font-bold hover:bg-amber-700 flex items-center gap-1">
                                <RefreshCw className="h-3.5 w-3.5" /> Actualizar
                            </button>
                        </div>
                    )}

                    {isLocked && (
                        <div className="p-3 mb-4 rounded-xl bg-slate-100 border border-slate-200 dark:bg-slate-800 dark:border-slate-700 flex items-center gap-3 text-slate-600 dark:text-slate-400 text-sm">
                            <AlertCircle className="h-5 w-5" />
                            <p>Reserva en estado <b>{reservaStatus}</b>. La edición de precios está bloqueada.</p>
                        </div>
                    )}

                    {serviceType === "Aereo" && <FlightForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} />}
                    {serviceType === "Hotel" && <HotelForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} />}
                    {serviceType === "Traslado" && <TransferForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} />}
                    {serviceType === "Paquete" && <PackageForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} />}

                    <PricingForm form={form} setForm={setForm} commissionPercent={commissionPercent} onRecalculate={applyCommission} disabled={isLocked} />

                    <div className="flex justify-end gap-3 pt-4 border-t border-slate-200 dark:border-slate-700">
                        <button type="button" onClick={onClose} className="px-5 py-2.5 text-sm rounded-xl hover:bg-slate-100 text-slate-600">Cancelar</button>
                        <button type="submit" disabled={loading} className="px-5 py-2.5 text-sm bg-indigo-600 text-white rounded-xl disabled:opacity-50">
                            {loading ? "Guardando..." : "Guardar Servicio"}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
