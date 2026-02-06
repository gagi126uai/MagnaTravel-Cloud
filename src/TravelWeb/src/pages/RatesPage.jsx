import { useEffect, useState, useCallback } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { Plus, Pencil, Trash2, Search, X, DollarSign, Calculator } from "lucide-react";
import Swal from "sweetalert2";

const serviceTypes = [
    { value: "Aereo", label: "Aéreo" },
    { value: "Hotel", label: "Hotel" },
    { value: "Traslado", label: "Traslado" },
    { value: "Asistencia", label: "Asistencia" },
    { value: "Excursion", label: "Excursión" },
    { value: "Paquete", label: "Paquete" },
    { value: "Otro", label: "Otro" },
];

const Modal = ({ isOpen, onClose, title, children }) => {
    if (!isOpen) return null;
    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm">
            <div className="w-full max-w-lg overflow-hidden rounded-2xl bg-white shadow-2xl dark:bg-slate-900">
                <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4 dark:border-slate-800">
                    <h3 className="text-lg font-semibold text-slate-900 dark:text-white">{title}</h3>
                    <button onClick={onClose} className="rounded-full p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-700 dark:hover:bg-slate-800">
                        <X className="h-5 w-5" />
                    </button>
                </div>
                <div className="p-6">{children}</div>
            </div>
        </div>
    );
};

export default function RatesPage() {
    const [rates, setRates] = useState([]);
    const [suppliers, setSuppliers] = useState([]);
    const [loading, setLoading] = useState(true);
    const [showModal, setShowModal] = useState(false);
    const [searchTerm, setSearchTerm] = useState("");
    const [filterType, setFilterType] = useState("");

    const [form, setForm] = useState({
        id: null,
        supplierId: "",
        serviceType: "Aereo",
        productName: "",
        description: "",
        netCost: 0,
        salePrice: 0,
        currency: "USD",
        validFrom: "",
        validTo: ""
    });

    // Commission integration state
    const [commissionPercent, setCommissionPercent] = useState(10);
    const [isCalculating, setIsCalculating] = useState(false);

    useEffect(() => {
        loadRates();
        loadSuppliers();
    }, []);

    const loadRates = async () => {
        setLoading(true);
        try {
            const data = await api.get("/rates");
            setRates(Array.isArray(data) ? data : []);
        } catch (error) {
            console.error("Error loading rates:", error);
            setRates([]);
        } finally {
            setLoading(false);
        }
    };

    const loadSuppliers = async () => {
        try {
            const data = await api.get("/suppliers");
            setSuppliers(data || []);
        } catch { }
    };

    // Fetch commission for current supplier/service combination
    const fetchCommission = useCallback(async (supplierId, serviceType) => {
        try {
            setIsCalculating(true);
            const params = new URLSearchParams();
            if (supplierId) params.append("supplierId", supplierId);
            if (serviceType) params.append("serviceType", serviceType);
            const result = await api.get(`/commissions/calculate?${params}`);
            setCommissionPercent(result.commissionPercent || 10);
            return result.commissionPercent || 10;
        } catch {
            return 10;
        } finally {
            setIsCalculating(false);
        }
    }, []);

    // Calculate suggested sale price based on cost + commission
    const calculateSalePrice = (netCost, commission) => {
        const cost = parseFloat(netCost) || 0;
        const commissionAmount = cost * (commission / 100);
        return Math.round((cost + commissionAmount) * 100) / 100;
    };

    // Handle supplier change - recalculate commission
    const handleSupplierChange = async (e) => {
        const supplierId = e.target.value;
        setForm(prev => ({ ...prev, supplierId }));
        const newCommission = await fetchCommission(supplierId, form.serviceType);
        if (form.netCost > 0) {
            setForm(prev => ({ ...prev, supplierId, salePrice: calculateSalePrice(prev.netCost, newCommission) }));
        }
    };

    // Handle service type change - recalculate commission
    const handleServiceTypeChange = async (e) => {
        const serviceType = e.target.value;
        setForm(prev => ({ ...prev, serviceType }));
        const newCommission = await fetchCommission(form.supplierId, serviceType);
        if (form.netCost > 0) {
            setForm(prev => ({ ...prev, serviceType, salePrice: calculateSalePrice(prev.netCost, newCommission) }));
        }
    };

    // Handle net cost change - recalculate sale price
    const handleNetCostChange = (e) => {
        const netCost = e.target.value;
        const suggestedSale = calculateSalePrice(netCost, commissionPercent);
        setForm(prev => ({ ...prev, netCost, salePrice: suggestedSale }));
    };

    // Apply commission button
    const applyCommission = () => {
        setForm(prev => ({ ...prev, salePrice: calculateSalePrice(prev.netCost, commissionPercent) }));
    };

    const saveRate = async (e) => {
        e.preventDefault();
        try {
            const payload = {
                ...form,
                supplierId: form.supplierId ? parseInt(form.supplierId) : null,
                netCost: parseFloat(form.netCost),
                salePrice: parseFloat(form.salePrice),
                validFrom: form.validFrom ? new Date(form.validFrom).toISOString() : null,
                validTo: form.validTo ? new Date(form.validTo).toISOString() : null
            };

            if (form.id) {
                await api.put(`/rates/${form.id}`, payload);
                showSuccess("Tarifa actualizada");
            } else {
                await api.post("/rates", payload);
                showSuccess("Tarifa creada");
            }

            setShowModal(false);
            resetForm();
            loadRates();
        } catch (error) {
            showError(error.message || "Error al guardar tarifa");
        }
    };

    const deleteRate = async (rateId) => {
        const result = await Swal.fire({
            title: "¿Eliminar tarifa?",
            text: "Esta acción no se puede deshacer",
            icon: "warning",
            showCancelButton: true,
            confirmButtonColor: "#ef4444",
            confirmButtonText: "Sí, eliminar",
            cancelButtonText: "Cancelar"
        });
        if (result.isConfirmed) {
            try {
                await api.delete(`/rates/${rateId}`);
                showSuccess("Tarifa eliminada");
                loadRates();
            } catch (error) {
                showError("Error al eliminar tarifa");
            }
        }
    };

    const editRate = (rate) => {
        setForm({
            id: rate.id,
            supplierId: rate.supplierId?.toString() || "",
            serviceType: rate.serviceType,
            productName: rate.productName,
            description: rate.description || "",
            netCost: rate.netCost,
            salePrice: rate.salePrice,
            currency: rate.currency || "USD",
            validFrom: rate.validFrom ? rate.validFrom.split("T")[0] : "",
            validTo: rate.validTo ? rate.validTo.split("T")[0] : ""
        });
        setShowModal(true);
    };

    const resetForm = () => {
        setForm({ id: null, supplierId: "", serviceType: "Aereo", productName: "", description: "", netCost: 0, salePrice: 0, currency: "USD", validFrom: "", validTo: "" });
    };

    const openNewModal = async () => {
        resetForm();
        setShowModal(true);
        await fetchCommission("", "Aereo"); // Load default commission
    };

    // Filtrado
    const filteredRates = rates.filter(rate => {
        const matchSearch = !searchTerm ||
            rate.productName?.toLowerCase().includes(searchTerm.toLowerCase()) ||
            rate.supplierName?.toLowerCase().includes(searchTerm.toLowerCase());
        const matchType = !filterType || rate.serviceType === filterType;
        return matchSearch && matchType;
    });

    return (
        <div className="space-y-6">
            {/* Header */}
            <header className="flex items-center justify-between">
                <div>
                    <h1 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white flex items-center gap-3">
                        <div className="rounded-xl bg-gradient-to-br from-emerald-500 to-teal-600 p-2.5 text-white shadow-lg shadow-emerald-500/20">
                            <DollarSign className="h-6 w-6" />
                        </div>
                        Tarifario
                    </h1>
                    <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
                        Gestione las tarifas y precios de sus proveedores
                    </p>
                </div>
                <button
                    onClick={openNewModal}
                    className="flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-medium text-white shadow-sm shadow-indigo-500/20 hover:bg-indigo-500 transition-colors"
                >
                    <Plus className="h-4 w-4" />
                    Nueva Tarifa
                </button>
            </header>

            {/* Filters */}
            <div className="flex flex-col sm:flex-row gap-4">
                <div className="relative flex-1 max-w-md">
                    <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                    <input
                        type="text"
                        placeholder="Buscar por producto o proveedor..."
                        className="w-full rounded-xl border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm focus:border-indigo-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                        value={searchTerm}
                        onChange={(e) => setSearchTerm(e.target.value)}
                    />
                </div>
                <select
                    className="rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm focus:border-indigo-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                    value={filterType}
                    onChange={(e) => setFilterType(e.target.value)}
                >
                    <option value="">Todos los tipos</option>
                    {serviceTypes.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
                </select>
            </div>

            {/* Stats */}
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                <div className="rounded-xl border bg-white p-4 dark:border-slate-800 dark:bg-slate-900/50">
                    <div className="text-xs text-slate-500 uppercase font-bold">Total Tarifas</div>
                    <div className="text-2xl font-bold mt-1">{rates.length}</div>
                </div>
                <div className="rounded-xl border bg-white p-4 dark:border-slate-800 dark:bg-slate-900/50">
                    <div className="text-xs text-slate-500 uppercase font-bold">Aéreos</div>
                    <div className="text-2xl font-bold mt-1 text-sky-600">{rates.filter(r => r.serviceType === "Aereo").length}</div>
                </div>
                <div className="rounded-xl border bg-white p-4 dark:border-slate-800 dark:bg-slate-900/50">
                    <div className="text-xs text-slate-500 uppercase font-bold">Hoteles</div>
                    <div className="text-2xl font-bold mt-1 text-amber-600">{rates.filter(r => r.serviceType === "Hotel").length}</div>
                </div>
                <div className="rounded-xl border bg-white p-4 dark:border-slate-800 dark:bg-slate-900/50">
                    <div className="text-xs text-slate-500 uppercase font-bold">Paquetes</div>
                    <div className="text-2xl font-bold mt-1 text-violet-600">{rates.filter(r => r.serviceType === "Paquete").length}</div>
                </div>
            </div>

            {/* Table */}
            <div className="rounded-2xl border border-slate-200 bg-white overflow-hidden dark:border-slate-800 dark:bg-slate-900/50">
                <table className="w-full text-sm">
                    <thead className="bg-slate-50 dark:bg-slate-800/50">
                        <tr>
                            <th className="px-4 py-3 text-left font-medium text-slate-600 dark:text-slate-300">Producto</th>
                            <th className="px-4 py-3 text-left font-medium text-slate-600 dark:text-slate-300">Tipo</th>
                            <th className="px-4 py-3 text-left font-medium text-slate-600 dark:text-slate-300">Proveedor</th>
                            <th className="px-4 py-3 text-right font-medium text-slate-600 dark:text-slate-300">Costo</th>
                            <th className="px-4 py-3 text-right font-medium text-slate-600 dark:text-slate-300">Venta</th>
                            <th className="px-4 py-3 text-right font-medium text-slate-600 dark:text-slate-300">Margen</th>
                            <th className="px-4 py-3 text-left font-medium text-slate-600 dark:text-slate-300">Vigencia</th>
                            <th className="px-4 py-3 text-center font-medium text-slate-600 dark:text-slate-300">Acciones</th>
                        </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                        {loading ? (
                            <tr><td colSpan="8" className="px-4 py-8 text-center text-slate-500">Cargando...</td></tr>
                        ) : filteredRates.length === 0 ? (
                            <tr><td colSpan="8" className="px-4 py-8 text-center text-slate-500">
                                {searchTerm || filterType ? "No se encontraron tarifas con los filtros aplicados" : "No hay tarifas cargadas. Cree una nueva tarifa para comenzar."}
                            </td></tr>
                        ) : filteredRates.map(rate => (
                            <tr key={rate.id} className="hover:bg-slate-50 dark:hover:bg-slate-800/30">
                                <td className="px-4 py-3">
                                    <div className="font-medium">{rate.productName}</div>
                                    {rate.description && <div className="text-xs text-slate-500">{rate.description}</div>}
                                </td>
                                <td className="px-4 py-3">
                                    <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${rate.serviceType === "Aereo" ? "bg-sky-100 text-sky-700" :
                                        rate.serviceType === "Hotel" ? "bg-amber-100 text-amber-700" :
                                            rate.serviceType === "Paquete" ? "bg-violet-100 text-violet-700" :
                                                "bg-slate-100 text-slate-700"
                                        }`}>
                                        {rate.serviceType}
                                    </span>
                                </td>
                                <td className="px-4 py-3">{rate.supplierName || <span className="text-slate-400">-</span>}</td>
                                <td className="px-4 py-3 text-right font-mono">${rate.netCost?.toLocaleString()}</td>
                                <td className="px-4 py-3 text-right font-mono font-medium text-emerald-600">${rate.salePrice?.toLocaleString()}</td>
                                <td className="px-4 py-3 text-right">
                                    <span className={`font-medium ${(rate.salePrice - rate.netCost) >= 0 ? 'text-emerald-600' : 'text-rose-600'}`}>
                                        ${(rate.salePrice - rate.netCost).toLocaleString()}
                                    </span>
                                </td>
                                <td className="px-4 py-3 text-xs text-slate-500">
                                    {rate.validFrom ? new Date(rate.validFrom).toLocaleDateString() : "-"} al {rate.validTo ? new Date(rate.validTo).toLocaleDateString() : "-"}
                                </td>
                                <td className="px-4 py-3 text-center">
                                    <button onClick={() => editRate(rate)} className="p-1.5 text-slate-500 hover:text-indigo-600 hover:bg-indigo-50 rounded-lg transition-colors"><Pencil className="h-4 w-4" /></button>
                                    <button onClick={() => deleteRate(rate.id)} className="p-1.5 text-slate-500 hover:text-rose-600 hover:bg-rose-50 rounded-lg transition-colors"><Trash2 className="h-4 w-4" /></button>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>

            {/* Modal */}
            <Modal isOpen={showModal} onClose={() => setShowModal(false)} title={form.id ? "Editar Tarifa" : "Nueva Tarifa"}>
                <form onSubmit={saveRate} className="space-y-4">
                    <div>
                        <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Producto *</label>
                        <input type="text" required className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                            value={form.productName} onChange={e => setForm({ ...form, productName: e.target.value })} placeholder="Vuelo Miami-Buenos Aires" />
                    </div>
                    <div className="grid grid-cols-2 gap-4">
                        <div>
                            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Tipo Servicio *</label>
                            <select className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                value={form.serviceType} onChange={handleServiceTypeChange}>
                                {serviceTypes.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
                            </select>
                        </div>
                        <div>
                            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Proveedor</label>
                            <select className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                value={form.supplierId} onChange={handleSupplierChange}>
                                <option value="">Sin proveedor específico</option>
                                {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                            </select>
                        </div>
                    </div>
                    {/* Commission Badge */}
                    <div className="flex items-center gap-2 p-3 rounded-xl bg-indigo-50 dark:bg-indigo-900/20 border border-indigo-100 dark:border-indigo-800">
                        <Calculator className="h-4 w-4 text-indigo-600" />
                        <span className="text-sm text-indigo-700 dark:text-indigo-300">
                            Comisión aplicable: <strong>{commissionPercent}%</strong>
                            {isCalculating && <span className="ml-2 text-xs opacity-70">(calculando...)</span>}
                        </span>
                        <button type="button" onClick={applyCommission} className="ml-auto text-xs px-2 py-1 rounded-lg bg-indigo-600 text-white hover:bg-indigo-500">
                            Aplicar
                        </button>
                    </div>
                    <div>
                        <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Descripción</label>
                        <input type="text" className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                            value={form.description} onChange={e => setForm({ ...form, description: e.target.value })} placeholder="Temporada alta, Economy" />
                    </div>
                    <div className="grid grid-cols-3 gap-4">
                        <div>
                            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Costo Neto *</label>
                            <div className="relative mt-1">
                                <span className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-400">$</span>
                                <input type="number" step="0.01" required className="block w-full rounded-xl border border-slate-200 bg-slate-50 pl-7 pr-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                    value={form.netCost} onChange={handleNetCostChange} />
                            </div>
                            <p className="text-xs text-slate-500 mt-1">Al ingresar costo, se calcula venta automáticamente</p>
                        </div>
                        <div>
                            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Precio Venta *</label>
                            <div className="relative mt-1">
                                <span className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-400">$</span>
                                <input type="number" step="0.01" required className="block w-full rounded-xl border border-slate-200 bg-slate-50 pl-7 pr-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                    value={form.salePrice} onChange={e => setForm({ ...form, salePrice: e.target.value })} />
                            </div>
                        </div>
                        <div>
                            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Moneda</label>
                            <select className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                value={form.currency} onChange={e => setForm({ ...form, currency: e.target.value })}>
                                <option value="USD">USD</option>
                                <option value="ARS">ARS</option>
                                <option value="EUR">EUR</option>
                            </select>
                        </div>
                    </div>
                    <div className="grid grid-cols-2 gap-4">
                        <div>
                            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Vigencia Desde</label>
                            <input type="date" className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                value={form.validFrom} onChange={e => setForm({ ...form, validFrom: e.target.value })} />
                        </div>
                        <div>
                            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Vigencia Hasta</label>
                            <input type="date" className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                value={form.validTo} onChange={e => setForm({ ...form, validTo: e.target.value })} />
                        </div>
                    </div>
                    <div className="flex justify-end gap-3 pt-4">
                        <button type="button" onClick={() => setShowModal(false)} className="rounded-xl px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800">Cancelar</button>
                        <button type="submit" className="rounded-xl bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500">{form.id ? "Guardar Cambios" : "Crear Tarifa"}</button>
                    </div>
                </form>
            </Modal>
        </div>
    );
}
