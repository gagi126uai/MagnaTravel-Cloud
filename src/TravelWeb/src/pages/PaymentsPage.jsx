import { useEffect, useState } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import {
  Search,
  CreditCard,
  Banknote,
  ArrowUpRight,
  ArrowDownLeft,
  Filter
} from "lucide-react";
import { Button } from "../components/ui/button";

export default function PaymentsPage() {
  const [payments, setPayments] = useState([]);
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState("");
  const [filterType, setFilterType] = useState("All");

  useEffect(() => {
    loadPayments();
  }, []);

  const loadPayments = async () => {
    setLoading(true);
    try {
      // Assuming a general endpoint for all payments (In / out)
      // If not exists, we might need to create one or assume this is only 'receipts' (Cobros)
      // For now, I'll use the existing /treasury/receipts or similar, but let's stick to what we know exists or mock it
      // The user complained about "Reservations" dropdown.
      // Let's try to get all payments. 
      const data = await api.get("/payments");
      setPayments(data.reverse()); // Show newest first
    } catch (error) {
      console.log("Error loading payments");
    } finally {
      setLoading(false);
    }
  };

  const filteredPayments = payments.filter(p => {
    const matchSearch = p.reservation?.customer?.fullName?.toLowerCase().includes(searchTerm.toLowerCase()) ||
      p.id?.toString().includes(searchTerm);
    const matchFilter = filterType === 'All' || p.method === filterType;
    return matchSearch && matchFilter;
  });

  const totalAmount = filteredPayments.reduce((acc, curr) => acc + (curr.amount || 0), 0);

  return (
    <div className="space-y-4 md:space-y-6">
      <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-3">
        <div>
          <h2 className="text-xl md:text-2xl font-bold tracking-tight">Caja Administrativa</h2>
          <p className="text-sm text-muted-foreground">Control de ingresos y egresos monetarios.</p>
        </div>
        <div className="flex items-center gap-2 bg-emerald-50 text-emerald-700 px-3 py-2 rounded-xl border border-emerald-100 dark:bg-emerald-900/20 dark:text-emerald-300 dark:border-emerald-800 w-full sm:w-auto justify-between sm:justify-start">
          <div className="text-xs sm:text-sm font-medium">Total:</div>
          <div className="text-lg sm:text-xl font-bold">${totalAmount.toLocaleString()}</div>
        </div>
      </div>

      {/* Toolbar */}
      <div className="flex flex-col sm:flex-row gap-4 items-center justify-between bg-white dark:bg-slate-900/50 p-2 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm">
        <div className="flex items-center gap-2 w-full sm:w-auto overflow-x-auto">
          <Button variant={filterType === 'All' ? 'default' : 'ghost'} size="sm" onClick={() => setFilterType('All')}>Todos</Button>
          <Button variant={filterType === 'Cash' ? 'default' : 'ghost'} size="sm" onClick={() => setFilterType('Cash')}>Efectivo</Button>
          <Button variant={filterType === 'Transfer' ? 'default' : 'ghost'} size="sm" onClick={() => setFilterType('Transfer')}>Transferencia</Button>
          <Button variant={filterType === 'Card' ? 'default' : 'ghost'} size="sm" onClick={() => setFilterType('Card')}>Tarjeta</Button>
        </div>

        <div className="relative w-full sm:max-w-xs">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
          <input
            className="w-full bg-transparent pl-9 pr-4 py-2 text-sm border-none focus:outline-none placeholder:text-muted-foreground/70"
            placeholder="Buscar movimiento..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
          />
        </div>
      </div>

      {/* Data Table */}
      <div className="rounded-xl border border-slate-200 bg-white shadow-sm overflow-hidden dark:bg-slate-900 dark:border-slate-800">
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm">
            <thead className="bg-slate-50 border-b border-slate-200 dark:bg-slate-950 dark:border-slate-800">
              <tr>
                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400">ID</th>
                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400">Fecha</th>
                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400">Concepto / Cliente</th>
                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400">Método</th>
                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400 text-right">Monto</th>
                <th className="px-6 py-3 font-medium text-slate-500 dark:text-slate-400 text-center">Estado</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
              {filteredPayments.map((payment) => (
                <tr key={payment.id} className="hover:bg-slate-50 dark:hover:bg-slate-800/50 transition-colors">
                  <td className="px-6 py-4 font-mono text-xs text-slate-500">#{payment.id}</td>
                  <td className="px-6 py-4">
                    {new Date(payment.paidAt).toLocaleDateString()}
                    <div className="text-xs text-slate-400">{new Date(payment.paidAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</div>
                  </td>
                  <td className="px-6 py-4">
                    <div className="font-medium">
                      {payment.travelFile?.name || payment.reservation?.customer?.fullName || "Sin expediente"}
                    </div>
                    <div className="text-xs text-slate-500">
                      {payment.travelFile ? `File ${payment.travelFile.fileNumber}` : `Reserva #${payment.reservationId || "-"}`}
                      {payment.notes && ` • ${payment.notes}`}
                    </div>
                  </td>
                  <td className="px-6 py-4">
                    <div className="flex items-center gap-2">
                      {payment.method === 'Cash' && <Banknote className="h-4 w-4 text-emerald-500" />}
                      {payment.method === 'Card' && <CreditCard className="h-4 w-4 text-indigo-500" />}
                      {payment.method === 'Transfer' && <ArrowUpRight className="h-4 w-4 text-blue-500" />}
                      <span>{payment.method === 'Cash' ? 'Efectivo' : payment.method === 'Card' ? 'Tarjeta' : 'Transf.'}</span>
                    </div>
                  </td>
                  <td className="px-6 py-4 text-right font-mono font-bold text-slate-700 dark:text-slate-200">
                    ${payment.amount?.toLocaleString()}
                  </td>
                  <td className="px-6 py-4 text-center">
                    <span className={`px-2 py-0.5 rounded-full text-xs font-medium ${payment.status === 'Paid' ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400' :
                      payment.status === 'Pending' ? 'bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400' :
                        'bg-slate-100 text-slate-600'
                      }`}>
                      {payment.status === 'Paid' ? 'Completado' : payment.status === 'Pending' ? 'Pendiente' : payment.status}
                    </span>
                  </td>
                </tr>
              ))}
              {filteredPayments.length === 0 && (
                <tr>
                  <td colSpan={6} className="px-6 py-12 text-center text-muted-foreground">
                    No hay movimientos registrados.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
