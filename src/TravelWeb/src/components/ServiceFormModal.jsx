import { useState, useEffect, useCallback } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { X, Plane, Hotel, Bus, Package, Search, Calculator, DollarSign, AlertCircle } from "lucide-react";

const SERVICE_TYPES = [
    { value: "Aereo", label: "Aéreo", icon: Plane, color: "sky" },
    { value: "Hotel", label: "Hotel", icon: Hotel, color: "amber" },
    { value: "Traslado", label: "Traslado", icon: Bus, color: "emerald" },
    { value: "Paquete", label: "Paquete", icon: Package, color: "violet" }
];

// Clases CSS con dark mode
const inputClass = "w-full rounded-xl border border-slate-200 bg-slate-50 p-2.5 text-sm text-slate-900 focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-600 dark:bg-slate-700 dark:text-white dark:focus:border-indigo-400";
const labelClass = "block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1";

// Calcular noches entre dos fechas
const calculateNights = (checkIn, checkOut) => {
    if (!checkIn || !checkOut) return 0;
    const start = new Date(checkIn);
    const end = new Date(checkOut);
    const diff = Math.ceil((end - start) / (1000 * 60 * 60 * 24));
    return diff > 0 ? diff : 0;
};

// ================== BUSCADOR DE TARIFAS ==================
function RateSelector({ serviceType, supplierId, onSelect, suppliers }) {
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
        if (supplierId) {
            const timer = setTimeout(searchRates, 300);
            return () => clearTimeout(timer);
        }
    }, [searchRates, supplierId]);

    if (!supplierId) {
        return (
            <div className="p-3 rounded-xl bg-amber-50 dark:bg-amber-900/20 border border-amber-200 dark:border-amber-800 flex items-center gap-2 text-amber-700 dark:text-amber-400 text-sm">
                <AlertCircle className="h-4 w-4" />
                Selecciona primero un proveedor para buscar en el tarifario
            </div>
        );
    }

    const supplierName = suppliers?.find(s => s.id.toString() === supplierId)?.name || "proveedor";

    return (
        <div className="relative">
            <label className={labelClass}>
                <Search className="inline h-4 w-4 mr-1" />
                Buscar tarifa de {supplierName}
            </label>
            <input
                type="text"
                className={inputClass}
                placeholder={`Buscar ${serviceType.toLowerCase()}...`}
                value={search}
                onChange={e => setSearch(e.target.value)}
                onFocus={() => setShowDropdown(true)}
            />
            {showDropdown && rates.length > 0 && (
                <div className="absolute z-30 w-full mt-1 bg-white dark:bg-slate-800 border border-slate-200 dark:border-slate-600 rounded-xl shadow-lg max-h-60 overflow-y-auto">
                    {rates.map(rate => {
                        const isExpired = rate.validTo && new Date(rate.validTo) < new Date();
                        return (
                            <button
                                key={rate.id}
                                type="button"
                                disabled={isExpired}
                                onClick={() => {
                                    if (isExpired) return;
                                    onSelect(rate);
                                    setShowDropdown(false);
                                    setSearch(rate.productName || "");
                                }}
                                className={`w-full text-left px-4 py-3 border-b border-slate-100 dark:border-slate-700 last:border-0 transition-colors
                                    ${isExpired
                                        ? "bg-red-50 dark:bg-red-900/10 cursor-not-allowed opacity-60"
                                        : "hover:bg-indigo-50 dark:hover:bg-indigo-900/30 cursor-pointer"
                                    }`}
                            >
                                <div className="flex justify-between items-start">
                                    <div className="font-medium text-slate-900 dark:text-white">{rate.productName}</div>
                                    {isExpired && (
                                        <span className="ml-2 px-2 py-0.5 rounded text-xs font-bold bg-red-100 text-red-600 dark:bg-red-900/30 dark:text-red-400 ring-1 ring-red-500/20">
                                            VENCIDA
                                        </span>
                                    )}
                                </div>
                                <div className="text-xs text-slate-500 dark:text-slate-400 flex flex-col gap-1 mt-1">
                                    <div className="flex items-center gap-3">
                                        <span className="text-emerald-600 dark:text-emerald-400 font-medium">Costo: ${rate.netCost}</span>
                                        <span className="text-indigo-600 dark:text-indigo-400 font-medium">Venta: ${rate.salePrice}</span>
                                    </div>
                                    {rate.serviceType === "Hotel" && (
                                        <div className="text-slate-600 dark:text-slate-300">
                                            {rate.roomType} {rate.roomCategory} {rate.roomFeatures && `(${rate.roomFeatures})`}
                                        </div>
                                    )}
                                </div>
                            </button>
                        );
                    })}
                </div>
            )}
            {showDropdown && rates.length === 0 && !loading && search && (
                <div className="absolute z-30 w-full mt-1 bg-white dark:bg-slate-800 border border-slate-200 dark:border-slate-600 rounded-xl shadow-lg p-4 text-center text-sm text-slate-500">
                    No se encontraron tarifas
                </div>
            )}
            {loading && <div className="text-xs text-slate-500 mt-1">Buscando...</div>}
        </div>
    );
}

// ================== FORMULARIO VUELOS (simplificado) ==================
function FlightForm({ form, setForm, suppliers, onRateSelect }) {
    return (
        <div className="space-y-4">
            {/* Proveedor PRIMERO */}
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required>
                        <option value="">Seleccionar proveedor...</option>
                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>PNR/Localizador</label>
                    <input className={inputClass} placeholder="ABC123" value={form.pnr || ""} onChange={e => setForm({ ...form, pnr: e.target.value })} />
                </div>
            </div>

            {/* Buscador de tarifas - DESPUÉS del proveedor */}
            <RateSelector serviceType="Aereo" supplierId={form.supplierId} onSelect={onRateSelect} suppliers={suppliers} />

            <div className="grid grid-cols-4 gap-4">
                <div>
                    <label className={labelClass}>Aerolínea *</label>
                    <input className={inputClass} placeholder="AA" maxLength={3} value={form.airlineCode || ""} onChange={e => setForm({ ...form, airlineCode: e.target.value.toUpperCase() })} required />
                </div>
                <div>
                    <label className={labelClass}>Vuelo *</label>
                    <input className={inputClass} placeholder="900" value={form.flightNumber || ""} onChange={e => setForm({ ...form, flightNumber: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Origen *</label>
                    <input className={inputClass} placeholder="MIA" maxLength={3} value={form.origin || ""} onChange={e => setForm({ ...form, origin: e.target.value.toUpperCase() })} required />
                </div>
                <div>
                    <label className={labelClass}>Destino *</label>
                    <input className={inputClass} placeholder="EZE" maxLength={3} value={form.destination || ""} onChange={e => setForm({ ...form, destination: e.target.value.toUpperCase() })} required />
                </div>
            </div>

            {/* Fechas SIMPLES - sin hora */}
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Fecha Salida *</label>
                    <input type="date" className={inputClass} value={form.departureDate || ""} onChange={e => setForm({ ...form, departureDate: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Fecha Llegada *</label>
                    <input type="date" className={inputClass} value={form.arrivalDate || ""} onChange={e => setForm({ ...form, arrivalDate: e.target.value })} required />
                </div>
            </div>

            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Clase</label>
                    <select className={inputClass} value={form.cabinClass || "Economy"} onChange={e => setForm({ ...form, cabinClass: e.target.value })}>
                        <option value="Economy">Economy</option>
                        <option value="Premium Economy">Premium Economy</option>
                        <option value="Business">Business</option>
                        <option value="First">First</option>
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Equipaje</label>
                    <input className={inputClass} placeholder="23kg" value={form.baggage || ""} onChange={e => setForm({ ...form, baggage: e.target.value })} />
                </div>
            </div>
        </div>
    );
}

// ================== FORMULARIO HOTEL ==================
function HotelForm({ form, setForm, suppliers, onRateSelect }) {
    const nights = calculateNights(form.checkIn, form.checkOut);

    return (
        <div className="space-y-4">
            {/* Proveedor PRIMERO */}
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required>
                        <option value="">Seleccionar proveedor...</option>
                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Código Confirmación</label>
                    <input className={inputClass} placeholder="CONF123" value={form.confirmationNumber || ""} onChange={e => setForm({ ...form, confirmationNumber: e.target.value })} />
                </div>
            </div>

            {/* Buscador de tarifas */}
            <RateSelector serviceType="Hotel" supplierId={form.supplierId} onSelect={onRateSelect} suppliers={suppliers} />

            {/* Nombre del hotel (viene del tarifario o se puede escribir) */}
            <div>
                <label className={labelClass}>Nombre Hotel *</label>
                <input className={inputClass} placeholder="Se completa al buscar tarifa..." value={form.hotelName || ""} onChange={e => setForm({ ...form, hotelName: e.target.value })} required />
            </div>

            {/* Fechas y noches */}
            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className={labelClass}>Check-In *</label>
                    <input type="date" className={inputClass} value={form.checkIn || ""} onChange={e => setForm({ ...form, checkIn: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Check-Out *</label>
                    <input type="date" className={inputClass} value={form.checkOut || ""} onChange={e => setForm({ ...form, checkOut: e.target.value })} required />
                </div>
                <div className="flex flex-col justify-end">
                    <div className="p-2.5 rounded-xl bg-indigo-50 dark:bg-indigo-900/30 text-center">
                        <span className="text-2xl font-bold text-indigo-600 dark:text-indigo-400">{nights}</span>
                        <span className="text-xs text-indigo-600 dark:text-indigo-400 block">noches</span>
                    </div>
                </div>
            </div>

            {/* Ocupación */}
            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className={labelClass}>Adultos</label>
                    <input type="number" min="1" className={inputClass} value={form.adults || 2} onChange={e => setForm({ ...form, adults: parseInt(e.target.value) })} />
                </div>
                <div>
                    <label className={labelClass}>Niños</label>
                    <input type="number" min="0" className={inputClass} value={form.children || 0} onChange={e => setForm({ ...form, children: parseInt(e.target.value) })} />
                </div>
                <div>
                    <label className={labelClass}>Habitaciones</label>
                    <input type="number" min="1" className={inputClass} value={form.rooms || 1} onChange={e => setForm({ ...form, rooms: parseInt(e.target.value) })} />
                </div>
            </div>
        </div>
    );
}

// ================== FORMULARIO TRASLADOS ==================
function TransferForm({ form, setForm, suppliers, onRateSelect }) {
    return (
        <div className="space-y-4">
            {/* Proveedor PRIMERO */}
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required>
                        <option value="">Seleccionar proveedor...</option>
                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Código Confirmación</label>
                    <input className={inputClass} placeholder="TRF123" value={form.confirmationNumber || ""} onChange={e => setForm({ ...form, confirmationNumber: e.target.value })} />
                </div>
            </div>

            {/* Buscador de tarifas */}
            <RateSelector serviceType="Traslado" supplierId={form.supplierId} onSelect={onRateSelect} suppliers={suppliers} />

            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Punto Recogida *</label>
                    <input className={inputClass} placeholder="Aeropuerto Miami (MIA)" value={form.pickupLocation || ""} onChange={e => setForm({ ...form, pickupLocation: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Punto Destino *</label>
                    <input className={inputClass} placeholder="Hotel Riu Palace" value={form.dropoffLocation || ""} onChange={e => setForm({ ...form, dropoffLocation: e.target.value })} required />
                </div>
            </div>

            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Fecha Recogida *</label>
                    <input type="date" className={inputClass} value={form.pickupDate || ""} onChange={e => setForm({ ...form, pickupDate: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Hora Recogida</label>
                    <input type="time" className={inputClass} value={form.pickupTime || ""} onChange={e => setForm({ ...form, pickupTime: e.target.value })} />
                </div>
            </div>

            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className={labelClass}>Tipo Vehículo</label>
                    <select className={inputClass} value={form.vehicleType || "Sedan"} onChange={e => setForm({ ...form, vehicleType: e.target.value })}>
                        <option value="Sedan">Sedán</option>
                        <option value="Van">Van</option>
                        <option value="Minibus">Minibus</option>
                        <option value="Bus">Bus</option>
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Pasajeros</label>
                    <input type="number" min="1" className={inputClass} value={form.passengers || 1} onChange={e => setForm({ ...form, passengers: parseInt(e.target.value) })} />
                </div>
                <div className="flex items-end pb-1">
                    <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
                        <input type="checkbox" checked={form.isRoundTrip || false} onChange={e => setForm({ ...form, isRoundTrip: e.target.checked })} className="rounded" />
                        Ida y Vuelta
                    </label>
                </div>
            </div>

            {form.isRoundTrip && (
                <div className="grid grid-cols-2 gap-4 p-3 rounded-xl bg-slate-50 dark:bg-slate-800/50">
                    <div>
                        <label className={labelClass}>Fecha Regreso *</label>
                        <input type="date" className={inputClass} value={form.returnDate || ""} onChange={e => setForm({ ...form, returnDate: e.target.value })} />
                    </div>
                    <div>
                        <label className={labelClass}>Hora Regreso</label>
                        <input type="time" className={inputClass} value={form.returnTime || ""} onChange={e => setForm({ ...form, returnTime: e.target.value })} />
                    </div>
                </div>
            )}
        </div>
    );
}

// ================== FORMULARIO PAQUETES ==================
function PackageForm({ form, setForm, suppliers, onRateSelect }) {
    return (
        <div className="space-y-4">
            {/* Proveedor PRIMERO */}
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required>
                        <option value="">Seleccionar proveedor...</option>
                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Código Confirmación</label>
                    <input className={inputClass} placeholder="PKG123" value={form.confirmationNumber || ""} onChange={e => setForm({ ...form, confirmationNumber: e.target.value })} />
                </div>
            </div>

            {/* Buscador de tarifas */}
            <RateSelector serviceType="Paquete" supplierId={form.supplierId} onSelect={onRateSelect} suppliers={suppliers} />

            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Nombre Paquete *</label>
                    <input className={inputClass} placeholder="Cancún All Inclusive 7 noches" value={form.packageName || ""} onChange={e => setForm({ ...form, packageName: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Destino *</label>
                    <input className={inputClass} placeholder="Cancún, México" value={form.destination || ""} onChange={e => setForm({ ...form, destination: e.target.value })} required />
                </div>
            </div>

            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Fecha Inicio *</label>
                    <input type="date" className={inputClass} value={form.startDate || ""} onChange={e => setForm({ ...form, startDate: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Fecha Fin *</label>
                    <input type="date" className={inputClass} value={form.endDate || ""} onChange={e => setForm({ ...form, endDate: e.target.value })} required />
                </div>
            </div>

            <div>
                <label className={labelClass}>¿Qué incluye?</label>
                <div className="grid grid-cols-3 gap-2 mt-2">
                    {[
                        { key: "includesHotel", label: "Hotel" },
                        { key: "includesFlight", label: "Vuelo" },
                        { key: "includesTransfer", label: "Traslados" },
                        { key: "includesExcursions", label: "Excursiones" },
                        { key: "includesMeals", label: "Comidas" },
                    ].map(item => (
                        <label key={item.key} className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
                            <input type="checkbox" checked={form[item.key] || false} onChange={e => setForm({ ...form, [item.key]: e.target.checked })} className="rounded" />
                            {item.label}
                        </label>
                    ))}
                </div>
            </div>

            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Adultos</label>
                    <input type="number" min="1" className={inputClass} value={form.adults || 2} onChange={e => setForm({ ...form, adults: parseInt(e.target.value) })} />
                </div>
                <div>
                    <label className={labelClass}>Niños</label>
                    <input type="number" min="0" className={inputClass} value={form.children || 0} onChange={e => setForm({ ...form, children: parseInt(e.target.value) })} />
                </div>
            </div>
        </div>
    );
}

// ================== FORMULARIO DE PRECIOS ==================
function PricingForm({ form, setForm, commissionPercent, onRecalculate }) {
    const margin = (form.salePrice || 0) - (form.netCost || 0);

    return (
        <div className="rounded-xl bg-gradient-to-r from-slate-50 to-slate-100 dark:from-slate-800 dark:to-slate-800/50 p-4 border border-slate-200 dark:border-slate-700 space-y-4">
            <div className="flex items-center justify-between">
                <h4 className="text-xs font-bold text-slate-500 dark:text-slate-400 uppercase flex items-center gap-2">
                    <DollarSign className="h-4 w-4" /> Valores Económicos
                </h4>
                {commissionPercent > 0 && (
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
                        <input type="number" step="0.01" className={`${inputClass} pl-6`} value={form.netCost || 0} onChange={e => setForm({ ...form, netCost: parseFloat(e.target.value) || 0 })} required />
                    </div>
                </div>
                <div>
                    <label className={labelClass}>Precio Venta *</label>
                    <div className="relative">
                        <span className="absolute left-3 top-2.5 text-slate-500">$</span>
                        <input type="number" step="0.01" className={`${inputClass} pl-6`} value={form.salePrice || 0} onChange={e => setForm({ ...form, salePrice: parseFloat(e.target.value) || 0 })} required />
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
export default function ServiceFormModal({ isOpen, onClose, fileId, suppliers, onSuccess, initialServiceType, serviceToEdit }) {
    const [serviceType, setServiceType] = useState(initialServiceType || "Aereo");
    const [form, setForm] = useState({ supplierId: "", netCost: 0, salePrice: 0, rooms: 1, checkIn: "", checkOut: "" });
    const [selectedRate, setSelectedRate] = useState(null); // Validar selectedRate para recálculos
    const [loading, setLoading] = useState(false);
    const [commissionPercent, setCommissionPercent] = useState(10);

    // Reset form cuando cambia el tipo de servicio
    const handleTypeChange = (newType) => {
        if (serviceToEdit) return; // Prevent type change when editing
        setServiceType(newType);
        setSelectedRate(null);
        setForm({ supplierId: form.supplierId, netCost: 0, salePrice: 0, rooms: 1 }); // Mantener proveedor
    };

    // Obtener comisión aplicable
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
        if (isOpen) {
            if (serviceToEdit) {
                // POPULATE FORM FOR EDITING
                setServiceType(serviceToEdit._type || serviceType);

                // Map fields based on type
                const formattedForm = {
                    ...serviceToEdit,
                    supplierId: serviceToEdit.supplierId?.toString() || "",
                    // Ensure dates are strings for inputs
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
                };
                setForm(formattedForm);
            } else {
                setServiceType(initialServiceType || "Aereo");
                setForm({ supplierId: "", netCost: 0, salePrice: 0, rooms: 1 });
            }
            setSelectedRate(null);
        }
    }, [isOpen, initialServiceType, serviceToEdit]);

    useEffect(() => {
        if (form.supplierId) fetchCommission();
    }, [fetchCommission, form.supplierId]);

    // Recalcular totales cuando cambian fechas/habitaciones y hay tarifa seleccionada
    useEffect(() => {
        if (serviceType === "Hotel" && selectedRate && form.checkIn && form.checkOut) {
            const nights = calculateNights(form.checkIn, form.checkOut);

            // Si la tarifa es "por noche" (asumido por defecto en hoteles si no es paquete)
            // Multiplicamos Costo * Noches * Habitaciones
            const rateCost = selectedRate.netCost || 0;
            const ratePrice = selectedRate.salePrice || 0;
            const rooms = form.rooms || 1;

            if (nights > 0) {
                setForm(prev => ({
                    ...prev,
                    netCost: rateCost * nights * rooms,
                    salePrice: ratePrice * nights * rooms
                }));
            }
        }
    }, [form.checkIn, form.checkOut, form.rooms, selectedRate, serviceType]);

    // Helper para fecha
    const calculateNights = (start, end) => {
        if (!start || !end) return 0;
        const d1 = new Date(start);
        const d2 = new Date(end);
        const diff = d2.getTime() - d1.getTime();
        return Math.max(0, Math.ceil(diff / (1000 * 3600 * 24)));
    };

    // Aplicar comisión al costo
    const applyCommission = () => {
        const cost = form.netCost || 0;
        const margin = cost * (commissionPercent / 100);
        setForm(prev => ({ ...prev, salePrice: Math.round((cost + margin) * 100) / 100 }));
    };

    // Al seleccionar una tarifa del buscador
    const handleRateSelect = (rate) => {
        console.log("Rate selected:", rate);
        setSelectedRate(rate); // Guardar para recálculos

        let multiplier = 1;
        if (serviceType === "Hotel") {
            const nights = calculateNights(form.checkIn, form.checkOut);
            const rooms = form.rooms || 1;
            if (nights > 0) multiplier = nights * rooms;
        }

        setForm(prev => ({
            ...prev,
            netCost: (rate.netCost || prev.netCost) * multiplier,
            salePrice: (rate.salePrice || prev.salePrice) * multiplier,
            // Campos según tipo
            ...(serviceType === "Aereo" && {
                origin: rate.origin || prev.origin,
                destination: rate.destination || prev.destination,
                cabinClass: rate.cabinClass || prev.cabinClass,
                airlineCode: rate.airlineCode || prev.airlineCode
            }),
            ...(serviceType === "Hotel" && {
                hotelName: rate.hotelName || rate.productName || prev.hotelName,
                city: rate.city || prev.city,
                starRating: rate.starRating?.toString() || prev.starRating,
                roomType: rate.roomType || prev.roomType,
                mealPlan: rate.mealPlan || prev.mealPlan
            }),
            ...(serviceType === "Traslado" && {
                pickupLocation: rate.pickupLocation || prev.pickupLocation,
                dropoffLocation: rate.dropoffLocation || prev.dropoffLocation,
                vehicleType: rate.vehicleType || prev.vehicleType
            }),
            ...(serviceType === "Paquete" && {
                packageName: rate.productName || prev.packageName,
                destination: rate.destination || prev.destination
            })
        }));
    };

    const handleSubmit = async (e) => {
        e.preventDefault();
        setLoading(true);
        try {
            let endpoint = "";
            let method = "post";

            if (serviceType === "Aereo") endpoint = `/files/${fileId}/flights`;
            else if (serviceType === "Hotel") endpoint = `/files/${fileId}/hotels`;
            else if (serviceType === "Traslado") endpoint = `/files/${fileId}/transfers`;
            else if (serviceType === "Paquete") endpoint = `/files/${fileId}/packages`;

            // If editing, append ID and change method to PUT
            if (serviceToEdit) {
                endpoint += `/${serviceToEdit.id}`;
                method = "put";
            }

            const payload = { ...form, supplierId: parseInt(form.supplierId) };

            // Ajustar fechas según tipo
            if (serviceType === "Aereo") {
                payload.departureTime = new Date(form.departureDate).toISOString();
                payload.arrivalTime = new Date(form.arrivalDate).toISOString();
            } else if (serviceType === "Hotel") {
                payload.checkIn = new Date(form.checkIn).toISOString();
                payload.checkOut = new Date(form.checkOut).toISOString();
            } else if (serviceType === "Traslado") {
                const pickupDT = form.pickupTime ? `${form.pickupDate}T${form.pickupTime}` : form.pickupDate;
                payload.pickupDateTime = new Date(pickupDT).toISOString();
                if (form.isRoundTrip && form.returnDate) {
                    const returnDT = form.returnTime ? `${form.returnDate}T${form.returnTime}` : form.returnDate;
                    payload.returnDateTime = new Date(returnDT).toISOString();
                }
            } else if (serviceType === "Paquete") {
                payload.startDate = new Date(form.startDate).toISOString();
                payload.endDate = new Date(form.endDate).toISOString();
            }

            if (method === "put") {
                await api.put(endpoint, payload);
                showSuccess("Servicio actualizado correctamente");
            } else {
                await api.post(endpoint, payload);
                showSuccess("Servicio agregado correctamente");
            }
            onSuccess();
            onClose();
        } catch (error) {
            showError(error.message || "Error al guardar servicio");
        } finally {
            setLoading(false);
        }
    };

    if (!isOpen) return null;

    const currentType = SERVICE_TYPES.find(t => t.value === serviceType);

    return (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4 backdrop-blur-sm">
            <div className="bg-white dark:bg-slate-900 rounded-2xl shadow-2xl max-w-2xl w-full max-h-[90vh] overflow-hidden" onClick={e => e.stopPropagation()}>
                {/* Header */}
                <div className="flex items-center justify-between p-4 border-b border-slate-200 dark:border-slate-700 bg-gradient-to-r from-indigo-500 to-purple-600">
                    <h2 className="text-lg font-semibold text-white flex items-center gap-2">
                        {currentType && <currentType.icon className="h-5 w-5" />}
                        {serviceToEdit ? "Editar Servicio" : "Agregar Servicio"}
                    </h2>
                    <button onClick={onClose} className="p-2 hover:bg-white/20 rounded-lg text-white/80 hover:text-white transition-colors">
                        <X className="h-5 w-5" />
                    </button>
                </div>

                {/* Tabs de tipo - FUNCIONALES */}
                <div className="flex border-b border-slate-200 dark:border-slate-700 bg-slate-50 dark:bg-slate-800/50">
                    {SERVICE_TYPES.map(({ value, label, icon: Icon, color }) => (
                        <button
                            key={value}
                            type="button"
                            onClick={() => handleTypeChange(value)}
                            className={`flex-1 flex items-center justify-center gap-2 py-3.5 text-sm font-medium transition-all duration-200 ${serviceType === value
                                ? `border-b-3 border-${color}-500 text-${color}-600 dark:text-${color}-400 bg-white dark:bg-slate-900`
                                : "text-slate-500 dark:text-slate-400 hover:text-slate-700 dark:hover:text-slate-300 hover:bg-white/50 dark:hover:bg-slate-800"
                                }`}
                        >
                            <Icon className="h-4 w-4" />
                            <span className="hidden sm:inline">{label}</span>
                        </button>
                    ))}
                </div>

                {/* Formulario */}
                <form onSubmit={handleSubmit} className="p-4 space-y-4 overflow-y-auto max-h-[60vh]">
                    {serviceType === "Aereo" && <FlightForm form={form} setForm={setForm} suppliers={suppliers} onRateSelect={handleRateSelect} />}
                    {serviceType === "Hotel" && <HotelForm form={form} setForm={setForm} suppliers={suppliers} onRateSelect={handleRateSelect} />}
                    {serviceType === "Traslado" && <TransferForm form={form} setForm={setForm} suppliers={suppliers} onRateSelect={handleRateSelect} />}
                    {serviceType === "Paquete" && <PackageForm form={form} setForm={setForm} suppliers={suppliers} onRateSelect={handleRateSelect} />}

                    <PricingForm form={form} setForm={setForm} commissionPercent={commissionPercent} onRecalculate={applyCommission} />

                    {/* Botones */}
                    <div className="flex justify-end gap-3 pt-4 border-t border-slate-200 dark:border-slate-700">
                        <button type="button" onClick={onClose} className="px-5 py-2.5 text-sm rounded-xl hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-600 dark:text-slate-300 transition-colors">
                            Cancelar
                        </button>
                        <button type="submit" disabled={loading} className="px-5 py-2.5 text-sm bg-gradient-to-r from-indigo-600 to-purple-600 text-white rounded-xl hover:from-indigo-500 hover:to-purple-500 disabled:opacity-50 font-medium shadow-lg shadow-indigo-500/25 transition-all">
                            {loading ? "Guardando..." : "Guardar Servicio"}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
