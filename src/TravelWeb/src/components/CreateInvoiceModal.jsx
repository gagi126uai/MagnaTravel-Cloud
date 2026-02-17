import { useState, useEffect } from 'react';
import { X, Plus, Trash2, AlertCircle, Calculator } from 'lucide-react';
import { api } from '../api';
import { showSuccess, showError } from '../alerts';

const VAT_RATES = [
    { id: 3, label: '0%', value: 0 },
    { id: 4, label: '10.5%', value: 0.105 },
    { id: 5, label: '21%', value: 0.21 },
    { id: 6, label: '27%', value: 0.27 },
    { id: 8, label: '5%', value: 0.05 },
    { id: 9, label: '2.5%', value: 0.025 }
];

const TRIBUTE_TYPES = [
    { id: 99, label: 'Otras Percepciones' },
    { id: 1, label: 'Impuestos Nacionales' },
    { id: 2, label: 'Impuestos Provinciales' }, // IIBB usually here with desc
    { id: 3, label: 'Impuestos Municipales' },
    { id: 4, label: 'Impuestos Internos' }
];

export default function CreateInvoiceModal({ isOpen, onClose, onSuccess, fileId, initialAmount, clientName, clientCuit }) {
    const [loading, setLoading] = useState(false);
    const [items, setItems] = useState([]);
    const [tributes, setTributes] = useState([]);
    const [totalNet, setTotalNet] = useState(0);
    const [totalVat, setTotalVat] = useState(0);
    const [totalTributes, setTotalTributes] = useState(0);
    const [total, setTotal] = useState(0);

    // Initial setup
    useEffect(() => {
        if (isOpen) {
            // Default item
            const defaultNet = initialAmount ? (initialAmount / 1.21) : 0;
            setItems([{
                description: 'Servicios Turísticos',
                quantity: 1,
                unitPrice: Number(defaultNet.toFixed(2)),
                alicuotaIvaId: 5 // 21% default
            }]);
            setTributes([]);
        }
    }, [isOpen, initialAmount]);

    // Calculations
    useEffect(() => {
        let net = 0;
        let vat = 0;

        items.forEach(item => {
            const itemNet = (Number(item.quantity) || 0) * (Number(item.unitPrice) || 0);
            const rate = VAT_RATES.find(r => r.id === Number(item.alicuotaIvaId))?.value || 0;
            net += itemNet;
            vat += itemNet * rate;
        });

        let trib = 0;
        tributes.forEach(t => {
            trib += (Number(t.importe) || 0);
        });

        setTotalNet(net);
        setTotalVat(vat);
        setTotalTributes(trib);
        setTotal(net + vat + trib);
    }, [items, tributes]);

    const handleAddItem = () => {
        setItems([...items, { description: '', quantity: 1, unitPrice: 0, alicuotaIvaId: 5 }]);
    };

    const handleRemoveItem = (index) => {
        setItems(items.filter((_, i) => i !== index));
    };

    const handleItemChange = (index, field, value) => {
        const newItems = [...items];
        newItems[index] = { ...newItems[index], [field]: value };
        setItems(newItems);
    };

    const handleAddTribute = () => {
        setTributes([...tributes, { tributeId: 99, description: '', baseImponible: 0, alicuota: 0, importe: 0 }]);
    };

    const handleRemoveTribute = (index) => {
        setTributes(tributes.filter((_, i) => i !== index));
    };

    const handleTributeChange = (index, field, value) => {
        const newTributes = [...tributes];
        newTributes[index] = { ...newTributes[index], [field]: value };
        setTributes(newTributes);
    };

    const handleSubmit = async (e) => {
        e.preventDefault();
        if (items.length === 0) return showError("Debe agregar al menos un ítem");
        if (total <= 0) return showError("El total debe ser mayor a 0");

        setLoading(true);
        try {
            // Transform items to DTO structure
            const payload = {
                travelFileId: fileId,
                items: items.map(i => ({
                    description: i.description,
                    quantity: Number(i.quantity),
                    unitPrice: Number(i.unitPrice),
                    total: Number(i.quantity) * Number(i.unitPrice),
                    alicuotaIvaId: Number(i.alicuotaIvaId)
                })),
                tributes: tributes.map(t => ({
                    tributeId: Number(t.tributeId),
                    description: t.description,
                    baseImponible: Number(t.baseImponible),
                    alicuota: Number(t.alicuota),
                    importe: Number(t.importe)
                }))
            };

            await api.post('/invoices', payload);
            showSuccess("Factura creada exitosamente");
            onSuccess();
            onClose();
        } catch (error) {
            console.error(error);
            showError(error.response?.data?.message || "Error al crear factura");
        } finally {
            setLoading(false);
        }
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50 backdrop-blur-sm">
            <div className="bg-white dark:bg-slate-900 rounded-xl shadow-2xl w-full max-w-5xl max-h-[95vh] overflow-hidden flex flex-col border border-gray-200 dark:border-slate-700">
                {/* Header Premium */}
                <div className="px-8 py-6 bg-gradient-to-r from-gray-50 to-white dark:from-slate-800 dark:to-slate-900 border-b border-gray-200 dark:border-slate-700 flex justify-between items-start">
                    <div>
                        <h2 className="text-2xl font-bold text-gray-900 dark:text-white flex items-center gap-3">
                            <div className="p-2 bg-indigo-100 dark:bg-indigo-900/30 rounded-lg">
                                <Calculator className="w-6 h-6 text-indigo-600 dark:text-indigo-400" />
                            </div>
                            Nueva Factura
                        </h2>
                        <div className="mt-4 flex flex-col gap-1">
                            <span className="text-xs font-semibold text-gray-500 uppercase tracking-wider">Cliente</span>
                            <div className="text-lg font-medium text-gray-900 dark:text-white">{clientName || 'Consumidor Final'}</div>
                            <div className="text-sm text-gray-500 font-mono">{clientCuit ? `CUIT: ${clientCuit}` : 'Sin CUIT registrado'}</div>
                        </div>
                    </div>
                    <div className="flex flex-col items-end gap-3">
                        <button onClick={onClose} className="text-gray-400 hover:text-gray-600 dark:text-slate-500 dark:hover:text-slate-300 transition-colors">
                            <X className="w-6 h-6" />
                        </button>
                        <div className="text-right">
                            <span className="text-xs font-semibold text-gray-500 uppercase tracking-wider block">Fecha</span>
                            <span className="text-sm font-medium text-gray-900 dark:text-white">{new Date().toLocaleDateString()}</span>
                        </div>
                    </div>
                </div>

                {/* Body */}
                <div className="flex-1 overflow-y-auto p-6 space-y-8">
                    {/* Items Section */}
                    <div>
                        <div className="flex justify-between items-center mb-3">
                            <h3 className="text-sm font-medium text-gray-700 dark:text-slate-300 uppercase tracking-wider">Items / Servicios</h3>
                        </div>
                        <div className="space-y-3">
                            {items.map((item, index) => (
                                <div key={index} className="flex flex-col md:flex-row gap-3 items-end bg-gray-50 dark:bg-slate-800/50 p-3 rounded-lg border border-gray-100 dark:border-slate-700">
                                    <div className="flex-1">
                                        <label className="block text-xs font-medium text-gray-500 mb-1">Descripción</label>
                                        <input
                                            type="text"
                                            value={item.description}
                                            onChange={(e) => handleItemChange(index, 'description', e.target.value)}
                                            className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white"
                                            placeholder="Ej. Servicios Turísticos"
                                        />
                                    </div>
                                    <div className="w-24">
                                        <label className="block text-xs font-medium text-gray-500 mb-1">Cant.</label>
                                        <input
                                            type="number"
                                            step="0.01"
                                            value={item.quantity}
                                            onChange={(e) => handleItemChange(index, 'quantity', e.target.value)}
                                            className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white text-right"
                                        />
                                    </div>
                                    <div className="w-32">
                                        <label className="block text-xs font-medium text-gray-500 mb-1">Precio Unit.</label>
                                        <div className="relative">
                                            <span className="absolute left-2 top-1.5 text-gray-400 text-xs">$</span>
                                            <input
                                                type="number"
                                                step="0.01"
                                                value={item.unitPrice}
                                                onChange={(e) => handleItemChange(index, 'unitPrice', e.target.value)}
                                                className="w-full text-sm pl-6 rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white text-right"
                                            />
                                        </div>
                                    </div>
                                    <div className="w-32">
                                        <label className="block text-xs font-medium text-gray-500 mb-1">IVA</label>
                                        <select
                                            value={item.alicuotaIvaId}
                                            onChange={(e) => handleItemChange(index, 'alicuotaIvaId', e.target.value)}
                                            className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white"
                                        >
                                            {VAT_RATES.map(rate => (
                                                <option key={rate.id} value={rate.id}>{rate.label}</option>
                                            ))}
                                        </select>
                                    </div>
                                    <div className="w-32 text-right pb-2 font-medium text-gray-900 dark:text-white">
                                        ${((item.quantity || 0) * (item.unitPrice || 0)).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
                                    </div>
                                    <button
                                        onClick={() => handleRemoveItem(index)}
                                        className="p-2 text-red-500 hover:bg-red-50 dark:hover:bg-red-900/20 rounded-md transition-colors"
                                        title="Eliminar ítem"
                                    >
                                        <Trash2 className="w-4 h-4" />
                                    </button>
                                </div>
                            ))}
                            <button
                                onClick={handleAddItem}
                                type="button"
                                className="flex items-center gap-2 text-sm text-indigo-600 dark:text-indigo-400 hover:text-indigo-700 font-medium mt-2"
                            >
                                <Plus className="w-4 h-4" /> Agregar Item
                            </button>
                        </div>
                    </div>

                    {/* Tributes Section */}
                    <div className="pt-4 border-t border-gray-200 dark:border-slate-700">
                        <div className="flex justify-between items-center mb-3">
                            <h3 className="text-sm font-medium text-gray-700 dark:text-slate-300 uppercase tracking-wider">Tributos / Percepciones</h3>
                        </div>
                        <div className="space-y-3">
                            {tributes.map((tribute, index) => (
                                <div key={index} className="flex flex-col md:flex-row gap-3 items-end bg-orange-50 dark:bg-orange-900/10 p-3 rounded-lg border border-orange-100 dark:border-orange-900/30">
                                    <div className="w-48">
                                        <label className="block text-xs font-medium text-gray-500 mb-1">Tipo</label>
                                        <select
                                            value={tribute.tributeId}
                                            onChange={(e) => handleTributeChange(index, 'tributeId', e.target.value)}
                                            className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white"
                                        >
                                            {TRIBUTE_TYPES.map(t => (
                                                <option key={t.id} value={t.id}>{t.label}</option>
                                            ))}
                                        </select>
                                    </div>
                                    <div className="flex-1">
                                        <label className="block text-xs font-medium text-gray-500 mb-1">Descripción</label>
                                        <input
                                            type="text"
                                            value={tribute.description}
                                            onChange={(e) => handleTributeChange(index, 'description', e.target.value)}
                                            className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white"
                                            placeholder="Detalle del tributo"
                                        />
                                    </div>
                                    <div className="w-32">
                                        <label className="block text-xs font-medium text-gray-500 mb-1">Importe</label>
                                        <div className="relative">
                                            <span className="absolute left-2 top-1.5 text-gray-400 text-xs">$</span>
                                            <input
                                                type="number"
                                                step="0.01"
                                                value={tribute.importe}
                                                onChange={(e) => handleTributeChange(index, 'importe', e.target.value)}
                                                className="w-full text-sm pl-6 rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white text-right"
                                            />
                                        </div>
                                    </div>
                                    <button
                                        onClick={() => handleRemoveTribute(index)}
                                        className="p-2 text-red-500 hover:bg-red-50 dark:hover:bg-red-900/20 rounded-md transition-colors"
                                        title="Eliminar tributo"
                                    >
                                        <Trash2 className="w-4 h-4" />
                                    </button>
                                </div>
                            ))}
                            <button
                                onClick={handleAddTribute}
                                type="button"
                                className="flex items-center gap-2 text-sm text-orange-600 dark:text-orange-400 hover:text-orange-700 font-medium mt-2"
                            >
                                <Plus className="w-4 h-4" /> Agregar Tributo
                            </button>
                        </div>
                    </div>
                </div>

                {/* Footer / Totals */}
                <div className="bg-gray-50 dark:bg-slate-800 px-6 py-4 border-t border-gray-200 dark:border-slate-700">
                    <div className="flex flex-col md:flex-row justify-between items-center gap-4">
                        <div className="text-xs text-gray-500 max-w-md">
                            <p className="flex items-center gap-1"><AlertCircle className="w-3 h-3" /> Los montos se enviarán a AFIP para autorización.</p>
                        </div>
                        <div className="flex items-center gap-8 w-full md:w-auto">
                            <div className="text-right space-y-1">
                                <div className="text-sm text-gray-500">Neto: <span className="text-gray-900 dark:text-white font-medium">${totalNet.toLocaleString('es-AR', { minimumFractionDigits: 2 })}</span></div>
                                <div className="text-sm text-gray-500">IVA: <span className="text-gray-900 dark:text-white font-medium">${totalVat.toLocaleString('es-AR', { minimumFractionDigits: 2 })}</span></div>
                                {totalTributes > 0 && (
                                    <div className="text-sm text-orange-600">Tributos: <span className="font-medium">${totalTributes.toLocaleString('es-AR', { minimumFractionDigits: 2 })}</span></div>
                                )}
                            </div>
                            <div className="text-right">
                                <span className="block text-xs text-gray-500 uppercase font-semibold">Total Final</span>
                                <span className="text-2xl font-bold text-gray-900 dark:text-white">${total.toLocaleString('es-AR', { minimumFractionDigits: 2 })}</span>
                            </div>
                        </div>
                    </div>
                    <div className="mt-4 flex justify-end gap-3">
                        <button
                            onClick={onClose}
                            className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 dark:bg-slate-800 dark:text-slate-300 dark:border-slate-600 dark:hover:bg-slate-700 transition-colors"
                        >
                            Cancelar
                        </button>
                        <button
                            onClick={handleSubmit}
                            disabled={loading || total <= 0}
                            className="px-4 py-2 text-sm font-medium text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 focus:ring-4 focus:ring-indigo-300 dark:focus:ring-indigo-900 disabled:opacity-50 flex items-center gap-2"
                        >
                            {loading ? 'Emitiendo...' : 'Emitir Factura'}
                        </button>
                    </div>
                </div>
            </div>
        </div>
    );
}
