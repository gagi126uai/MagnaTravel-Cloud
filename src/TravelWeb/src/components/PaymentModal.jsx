import { useState, useEffect } from "react";
import { api } from "../api";
import { DollarSign, X } from "lucide-react";
import { showError, showSuccess } from "../alerts";

export default function PaymentModal({ isOpen, onClose, onSuccess, reservaId, maxAmount }) {
    const [amount, setAmount] = useState("");
    const [method, setMethod] = useState("Transferencia");
    const [notes, setNotes] = useState("");
    const [loading, setLoading] = useState(false);

    useEffect(() => {
        if (isOpen) {
            setAmount("");
            setMethod("Transferencia");
            setNotes("");
            setLoading(false);
        }
    }, [isOpen]);

    const handleSubmit = async (e) => {
        e.preventDefault();
        try {
            setLoading(true);

            if (parseFloat(amount) <= 0) {
                showError("El monto debe ser mayor a 0");
                return;
            }

            if (maxAmount && parseFloat(amount) > maxAmount) {
                showError(`El monto excede el saldo pendiente ($${maxAmount})`);
                return;
            }

            await api.post(`/reservas/${reservaId}/payments`, {
                amount: parseFloat(amount),
                method,
                notes
            });

            showSuccess("Pago registrado correctamente");
            onSuccess();
            onClose();
        } catch (error) {
            console.error(error);
            showError(error.message || "Error al registrar pago");
        } finally {
            setLoading(false);
        }
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50 backdrop-blur-sm">
            <div className="bg-white dark:bg-slate-800 rounded-xl shadow-xl w-full max-w-md border border-gray-200 dark:border-slate-700">
                <div className="flex items-center justify-between p-4 border-b border-gray-200 dark:border-slate-700">
                    <h3 className="text-lg font-medium text-gray-900 dark:text-white flex items-center gap-2">
                        <div className="p-2 bg-green-100 dark:bg-green-900/30 rounded-lg text-green-600 dark:text-green-400">
                            <DollarSign className="w-5 h-5" />
                        </div>
                        Registrar Pago
                    </h3>
                    <button onClick={onClose} className="text-gray-400 hover:text-gray-600 dark:text-slate-500 dark:hover:text-slate-300">
                        <X className="w-5 h-5" />
                    </button>
                </div>

                <form onSubmit={handleSubmit} className="p-6 space-y-4">
                    <div>
                        <label className="block text-sm font-medium text-gray-700 dark:text-slate-300 mb-1">Monto a Cobrar</label>
                        <div className="relative">
                            <div className="pointer-events-none absolute inset-y-0 left-0 flex items-center pl-3">
                                <span className="text-gray-500 dark:text-slate-400 sm:text-sm">$</span>
                            </div>
                            <input
                                type="number"
                                step="0.01"
                                required
                                className="block w-full rounded-lg border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white pl-7 focus:border-green-500 focus:ring-green-500 sm:text-sm py-2"
                                placeholder="0.00"
                                value={amount}
                                onChange={e => setAmount(e.target.value)}
                                max={maxAmount}
                            />
                        </div>
                        {maxAmount !== undefined && (
                            <p className="text-xs text-gray-500 mt-1">Saldo pendiente: ${maxAmount.toLocaleString()}</p>
                        )}
                    </div>

                    <div>
                        <label className="block text-sm font-medium text-gray-700 dark:text-slate-300 mb-1">Método de Pago</label>
                        <select
                            className="block w-full rounded-lg border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white focus:border-green-500 focus:ring-green-500 sm:text-sm py-2"
                            value={method}
                            onChange={e => setMethod(e.target.value)}
                        >
                            <option>Transferencia</option>
                            <option>Efectivo</option>
                            <option>Tarjeta Crédito</option>
                            <option>Tarjeta Débito</option>
                            <option>Cheque</option>
                        </select>
                    </div>

                    <div>
                        <label className="block text-sm font-medium text-gray-700 dark:text-slate-300 mb-1">Notas / Referencia</label>
                        <input
                            type="text"
                            className="block w-full rounded-lg border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white focus:border-green-500 focus:ring-green-500 sm:text-sm py-2"
                            placeholder="Ej: Comprobante #1234"
                            value={notes}
                            onChange={e => setNotes(e.target.value)}
                        />
                    </div>

                    <div className="pt-2 flex justify-end gap-3">
                        <button
                            type="button"
                            onClick={onClose}
                            className="px-4 py-2 text-sm font-medium text-gray-700 dark:text-gray-200 bg-white dark:bg-slate-700 border border-gray-300 dark:border-slate-600 rounded-lg hover:bg-gray-50 dark:hover:bg-slate-600"
                        >
                            Cancelar
                        </button>
                        <button
                            type="submit"
                            disabled={loading || !amount}
                            className="px-4 py-2 text-sm font-medium text-white bg-green-600 rounded-lg hover:bg-green-700 focus:ring-4 focus:ring-green-300 dark:focus:ring-green-800 shadow-sm flex items-center gap-2 disabled:opacity-50"
                        >
                            {loading ? "Procesando..." : "Confirmar Pago"}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
