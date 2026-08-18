import { AlertTriangle, Info, Trash2, HelpCircle, X } from "lucide-react";

export default function ConfirmModal({ 
    isOpen, 
    onClose, 
    onConfirm, 
    title = "Confirmar acción", 
    message = "¿Estás seguro de que querés realizar esta acción?",
    confirmText = "Confirmar",
    cancelText = "Cancelar",
    type = "warning", // 'warning', 'danger', 'info', 'question'
    isLoading = false
}) {
    if (!isOpen) return null;

    const icons = {
        warning: <AlertTriangle className="h-7 w-7 text-amber-500" />,
        danger: <Trash2 className="h-7 w-7 text-rose-500" />,
        info: <Info className="h-7 w-7 text-sky-500" />,
        question: <HelpCircle className="h-7 w-7 text-blue-500" />
    };

    const confirmColors = {
        warning: "bg-gradient-to-br from-amber-400 to-orange-600 text-white shadow-amber-200/50 hover:shadow-amber-300/50",
        danger: "bg-gradient-to-br from-rose-500 to-red-700 text-white shadow-rose-200/50 hover:shadow-rose-300/50",
        info: "bg-gradient-to-br from-sky-400 to-blue-600 text-white shadow-sky-200/50 hover:shadow-sky-300/50",
        question: "bg-gradient-to-br from-blue-500 to-blue-700 text-white shadow-blue-200/50 hover:shadow-blue-300/50"
    };

    const iconBg = {
        warning: "bg-amber-50 dark:bg-amber-900/20 ring-4 ring-amber-100 dark:ring-amber-900/30",
        danger: "bg-rose-50 dark:bg-rose-900/20 ring-4 ring-rose-100 dark:ring-rose-900/30",
        info: "bg-sky-50 dark:bg-sky-900/20 ring-4 ring-sky-100 dark:ring-sky-900/30",
        question: "bg-blue-50 dark:bg-blue-900/20 ring-4 ring-blue-100 dark:ring-blue-900/30"
    };

    return (
        <div 
            className="fixed inset-0 z-[100] flex items-center justify-center p-4 bg-slate-900/40 backdrop-blur-md animate-in fade-in duration-300"
        >
            <div 
                className="w-full max-w-sm bg-white/95 dark:bg-slate-950/95 rounded-[2.5rem] shadow-[0_25px_50px_-12px_rgba(0,0,0,0.25)] border border-white/40 dark:border-slate-800/50 overflow-hidden animate-in zoom-in-95 slide-in-from-bottom-4 duration-300 relative"
                role="dialog"
                aria-modal="true"
            >
                {/* Close Button */}
                {!isLoading && (
                    <button 
                        onClick={onClose}
                        className="absolute top-6 right-6 p-2 rounded-full hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 transition-all"
                    >
                        <X className="h-5 w-5" />
                    </button>
                )}

                <div className="p-10 text-center">
                    <div className={`mx-auto mb-6 flex h-20 w-20 items-center justify-center rounded-[2rem] transition-transform hover:rotate-12 duration-500 ${iconBg[type]}`}>
                        {icons[type]}
                    </div>
                    <h3 className="mb-3 text-2xl font-black text-slate-900 dark:text-white tracking-tight leading-tight">
                        {title}
                    </h3>
                    <p className="text-sm font-medium text-slate-500 dark:text-slate-400 leading-relaxed px-2">
                        {message}
                    </p>
                </div>
                
                <div className="flex flex-col gap-3 p-10 pt-0">
                    <button
                        onClick={onConfirm}
                        disabled={isLoading}
                        data-testid="confirm-modal-confirm"
                        className={`w-full rounded-[14px] px-6 py-4 text-sm font-extrabold shadow-xl transition-all active:scale-[0.98] disabled:opacity-50 flex items-center justify-center gap-3 ${confirmColors[type]}`}
                    >
                        {isLoading ? (
                            <div className="h-5 w-5 border-2 border-white/30 border-t-white rounded-full animate-spin" />
                        ) : null}
                        {isLoading ? "Procesando..." : confirmText}
                    </button>
                    <button
                        onClick={onClose}
                        disabled={isLoading}
                        className="w-full rounded-[14px] border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 px-6 py-4 text-sm font-bold text-slate-600 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800 hover:border-slate-300 dark:hover:border-slate-700 transition-all disabled:opacity-50"
                    >
                        {cancelText}
                    </button>
                </div>
            </div>
        </div>
    );
}
