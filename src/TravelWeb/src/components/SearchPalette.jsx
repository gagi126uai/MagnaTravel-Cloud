import { useState, useEffect, useRef, useCallback } from "react";
import { useNavigate } from "react-router-dom";
import { Search, FolderOpen, User, CreditCard, X, Loader2 } from "lucide-react";
import { api } from "../api";
import { formatCurrency } from "../lib/utils";

export default function SearchPalette({ isOpen, onClose }) {
    const [query, setQuery] = useState("");
    const [results, setResults] = useState(null);
    const [loading, setLoading] = useState(false);
    const inputRef = useRef(null);
    const navigate = useNavigate();
    const debounceRef = useRef(null);

    // Focus input when opened
    useEffect(() => {
        if (isOpen) {
            setQuery("");
            setResults(null);
            setTimeout(() => inputRef.current?.focus(), 100);
        }
    }, [isOpen]);

    // Keyboard shortcut: Ctrl+K / Cmd+K
    useEffect(() => {
        const handleKeyDown = (e) => {
            if ((e.ctrlKey || e.metaKey) && e.key === "k") {
                e.preventDefault();
                if (isOpen) onClose();
                else onClose(true); // signal to parent to open
            }
            if (e.key === "Escape" && isOpen) {
                onClose();
            }
        };
        window.addEventListener("keydown", handleKeyDown);
        return () => window.removeEventListener("keydown", handleKeyDown);
    }, [isOpen, onClose]);

    // Debounced search
    const handleSearch = useCallback((value) => {
        setQuery(value);
        if (debounceRef.current) clearTimeout(debounceRef.current);

        if (!value.trim()) {
            setResults(null);
            return;
        }

        debounceRef.current = setTimeout(async () => {
            setLoading(true);
            try {
                const data = await api.get(`/search?query=${encodeURIComponent(value.trim())}`);
                setResults(data);
            } catch (error) {
                console.error("Search error:", error);
            } finally {
                setLoading(false);
            }
        }, 300);
    }, []);

    const handleSelect = (type, id) => {
        onClose();
        switch (type) {
            case "file":
                navigate(`/files/${id}`);
                break;
            case "customer":
                navigate(`/customers/${id}/account`);
                break;
        }
    };

    if (!isOpen) return null;

    const hasResults = results && (results.files?.length > 0 || results.customers?.length > 0 || results.payments?.length > 0);
    const noResults = results && !hasResults && query.trim();

    return (
        <div className="fixed inset-0 z-[100]">
            {/* Backdrop */}
            <div
                className="absolute inset-0 bg-black/50 backdrop-blur-sm animate-in fade-in duration-150"
                onClick={onClose}
            />

            {/* Modal */}
            <div className="relative flex items-start justify-center pt-[15vh]">
                <div className="w-full max-w-lg mx-4 bg-white dark:bg-slate-900 rounded-2xl shadow-2xl border border-slate-200 dark:border-slate-800 overflow-hidden animate-in fade-in zoom-in-95 duration-200">
                    {/* Search Input */}
                    <div className="flex items-center gap-3 px-4 py-3 border-b border-slate-200 dark:border-slate-800">
                        <Search className="h-5 w-5 text-slate-400 shrink-0" />
                        <input
                            ref={inputRef}
                            type="text"
                            placeholder="Buscar expedientes, clientes, pagos..."
                            value={query}
                            onChange={(e) => handleSearch(e.target.value)}
                            className="flex-1 bg-transparent text-sm outline-none placeholder:text-slate-400 text-slate-900 dark:text-white"
                        />
                        {loading && <Loader2 className="h-4 w-4 animate-spin text-indigo-500 shrink-0" />}
                        <kbd className="hidden sm:inline-flex items-center gap-0.5 px-1.5 py-0.5 text-[10px] font-mono font-medium text-slate-400 bg-slate-100 dark:bg-slate-800 rounded border border-slate-200 dark:border-slate-700">
                            ESC
                        </kbd>
                    </div>

                    {/* Results */}
                    <div className="max-h-[50vh] overflow-y-auto">
                        {/* Files */}
                        {results?.files?.length > 0 && (
                            <div className="p-2">
                                <div className="px-3 py-1.5 text-[10px] font-semibold uppercase tracking-wider text-slate-400">
                                    Expedientes
                                </div>
                                {results.files.map((file) => (
                                    <button
                                        key={`file-${file.id}`}
                                        onClick={() => handleSelect("file", file.id)}
                                        className="w-full flex items-center gap-3 px-3 py-2.5 rounded-lg hover:bg-indigo-50 dark:hover:bg-indigo-900/20 transition-colors text-left group"
                                    >
                                        <div className="h-8 w-8 rounded-full bg-indigo-100 dark:bg-indigo-900/30 flex items-center justify-center shrink-0">
                                            <FolderOpen className="h-4 w-4 text-indigo-600 dark:text-indigo-400" />
                                        </div>
                                        <div className="flex-1 min-w-0">
                                            <div className="text-sm font-medium text-slate-900 dark:text-white truncate">{file.name}</div>
                                            <div className="flex items-center gap-2 text-xs text-slate-500">
                                                <span className="font-mono">{file.fileNumber}</span>
                                                {file.payerName && <span>· {file.payerName}</span>}
                                            </div>
                                        </div>
                                        <span className={`text-[10px] font-semibold px-2 py-0.5 rounded-full ${file.status === 'Operativo' ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400' :
                                                file.status === 'Reservado' ? 'bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400' :
                                                    'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400'
                                            }`}>
                                            {file.status}
                                        </span>
                                    </button>
                                ))}
                            </div>
                        )}

                        {/* Customers */}
                        {results?.customers?.length > 0 && (
                            <div className="p-2 border-t border-slate-100 dark:border-slate-800">
                                <div className="px-3 py-1.5 text-[10px] font-semibold uppercase tracking-wider text-slate-400">
                                    Clientes
                                </div>
                                {results.customers.map((cust) => (
                                    <button
                                        key={`cust-${cust.id}`}
                                        onClick={() => handleSelect("customer", cust.id)}
                                        className="w-full flex items-center gap-3 px-3 py-2.5 rounded-lg hover:bg-blue-50 dark:hover:bg-blue-900/20 transition-colors text-left"
                                    >
                                        <div className="h-8 w-8 rounded-full bg-blue-100 dark:bg-blue-900/30 flex items-center justify-center shrink-0">
                                            <User className="h-4 w-4 text-blue-600 dark:text-blue-400" />
                                        </div>
                                        <div className="flex-1 min-w-0">
                                            <div className="text-sm font-medium text-slate-900 dark:text-white truncate">{cust.fullName}</div>
                                            <div className="text-xs text-slate-500 truncate">
                                                {cust.email || cust.phone || "Sin contacto"}
                                            </div>
                                        </div>
                                    </button>
                                ))}
                            </div>
                        )}

                        {/* Payments */}
                        {results?.payments?.length > 0 && (
                            <div className="p-2 border-t border-slate-100 dark:border-slate-800">
                                <div className="px-3 py-1.5 text-[10px] font-semibold uppercase tracking-wider text-slate-400">
                                    Pagos
                                </div>
                                {results.payments.map((pay) => (
                                    <div
                                        key={`pay-${pay.id}`}
                                        className="flex items-center gap-3 px-3 py-2.5 rounded-lg hover:bg-slate-50 dark:hover:bg-slate-800/50 transition-colors"
                                    >
                                        <div className="h-8 w-8 rounded-full bg-emerald-100 dark:bg-emerald-900/30 flex items-center justify-center shrink-0">
                                            <CreditCard className="h-4 w-4 text-emerald-600 dark:text-emerald-400" />
                                        </div>
                                        <div className="flex-1 min-w-0">
                                            <div className="text-sm font-medium text-slate-900 dark:text-white">
                                                {formatCurrency(pay.amount)} — {pay.method}
                                            </div>
                                            <div className="text-xs text-slate-500">
                                                {pay.fileNumber || "Sin expediente"} · {pay.status}
                                            </div>
                                        </div>
                                    </div>
                                ))}
                            </div>
                        )}

                        {/* No results */}
                        {noResults && (
                            <div className="p-8 text-center">
                                <Search className="h-10 w-10 mx-auto text-slate-300 dark:text-slate-600 mb-3" />
                                <p className="text-sm text-slate-500">No se encontraron resultados para "{query}"</p>
                            </div>
                        )}

                        {/* Initial state hint */}
                        {!results && !loading && (
                            <div className="p-6 text-center">
                                <p className="text-xs text-slate-400">Escribí un nombre, número de file o cliente</p>
                            </div>
                        )}
                    </div>

                    {/* Footer */}
                    <div className="px-4 py-2 border-t border-slate-200 dark:border-slate-800 bg-slate-50 dark:bg-slate-950 flex items-center gap-4 text-[10px] text-slate-400">
                        <span className="flex items-center gap-1">
                            <kbd className="px-1 py-0.5 bg-white dark:bg-slate-800 rounded border border-slate-200 dark:border-slate-700 font-mono">↑↓</kbd>
                            navegar
                        </span>
                        <span className="flex items-center gap-1">
                            <kbd className="px-1 py-0.5 bg-white dark:bg-slate-800 rounded border border-slate-200 dark:border-slate-700 font-mono">↵</kbd>
                            abrir
                        </span>
                        <span className="flex items-center gap-1">
                            <kbd className="px-1 py-0.5 bg-white dark:bg-slate-800 rounded border border-slate-200 dark:border-slate-700 font-mono">esc</kbd>
                            cerrar
                        </span>
                    </div>
                </div>
            </div>
        </div>
    );
}
