import { useEffect, useState } from "react";
import { api } from "../api";
import { showError } from "../alerts";
import { formatCurrency, formatDate } from "../lib/utils";
import { CurrencyBadge } from "../components/ui/CurrencyBadge";
import { useNavigate } from "react-router-dom";
import {
  TrendingUp,
  DollarSign,
  ArrowDownRight,
  ArrowUpRight,
  Percent,
  Building2,
  Users,
  Calendar,
  FileText,
  Download,
  BarChart3,
  Wallet,
  CreditCard,
  AlertCircle,
  Loader2,
  PieChart,
  X
} from "lucide-react";
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  Legend,
  AreaChart,
  Area
} from "recharts";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle
} from "../components/ui/card";
import { ReportsSkeleton } from "../components/ui/skeleton";
import { Button } from "../components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "../components/ui/tabs";
import Swal from "sweetalert2";
import AnalyticsPage from "./AnalyticsPage";
import { getPublicId } from "../lib/publicIds";

export default function ReportsPage() {
  const [report, setReport] = useState(null);
  const [receivables, setReceivables] = useState([]);
  const [loading, setLoading] = useState(true);
  const [isExporting, setIsExporting] = useState(false);
  const navigate = useNavigate();

  // Date range — default: first day of current month to today
  const today = new Date();
  const firstOfMonth = new Date(today.getFullYear(), today.getMonth(), 1);
  const [dateFrom, setDateFrom] = useState(firstOfMonth.toISOString().split("T")[0]);
  const [dateTo, setDateTo] = useState(today.toISOString().split("T")[0]);

  // Quick presets
  const [activePreset, setActivePreset] = useState("month");

  const applyPreset = (preset) => {
    setActivePreset(preset);
    const now = new Date();
    let from, to;

    switch (preset) {
      case "month":
        from = new Date(now.getFullYear(), now.getMonth(), 1);
        to = now;
        break;
      case "lastMonth": {
        from = new Date(now.getFullYear(), now.getMonth() - 1, 1);
        to = new Date(now.getFullYear(), now.getMonth(), 0);
        break;
      }
      case "quarter":
        from = new Date(now.getFullYear(), Math.floor(now.getMonth() / 3) * 3, 1);
        to = now;
        break;
      case "year":
        from = new Date(now.getFullYear(), 0, 1);
        to = now;
        break;
      case "all":
        from = new Date(2020, 0, 1);
        to = now;
        break;
      default:
        return;
    }

    setDateFrom(from.toISOString().split("T")[0]);
    setDateTo(to.toISOString().split("T")[0]);
  };

  useEffect(() => {
    loadReport();
  }, [dateFrom, dateTo]);

  const loadReport = async () => {
    try {
      setLoading(true);
      const [reportData, receivablesData] = await Promise.all([
        api.get(`/reports/detailed?from=${dateFrom}&to=${dateTo}`),
        api.get('/reports/detailed-receivables')
      ]);
      setReport(reportData);
      setReceivables(receivablesData);
    } catch (error) {
      console.error("Error loading report:", error);
      showError("No se pudieron cargar los reportes.");
    } finally {
      setLoading(false);
    }
  };

  const openExportModal = () => {
    Swal.fire({
      title: 'Exportar Reporte',
      html: `
        <div class="text-left space-y-4">
          <div>
            <label class="block text-sm font-medium text-gray-700 mb-1">Rango de Fechas</label>
            <div class="flex gap-2">
              <input type="date" id="exportFrom" class="swal2-input m-0 w-full text-sm" value="${dateFrom}">
              <input type="date" id="exportTo" class="swal2-input m-0 w-full text-sm" value="${dateTo}">
            </div>
          </div>
          <div>
            <label class="block text-sm font-medium text-gray-700 mb-2">Incluir Información</label>
            <div class="space-y-2">
              <label class="flex items-center gap-2 cursor-pointer">
                <input type="checkbox" id="incSales" checked class="w-4 h-4 text-indigo-600 rounded border-gray-300 focus:ring-indigo-500">
                <span class="text-sm text-gray-900">Ventas y Margen</span>
              </label>
              <label class="flex items-center gap-2 cursor-pointer">
                <input type="checkbox" id="incReceivables" checked class="w-4 h-4 text-indigo-600 rounded border-gray-300 focus:ring-indigo-500">
                <span class="text-sm text-gray-900">Cuentas por Cobrar (Deudores)</span>
              </label>
              <label class="flex items-center gap-2 cursor-pointer">
                <input type="checkbox" id="incPayables" checked class="w-4 h-4 text-indigo-600 rounded border-gray-300 focus:ring-indigo-500">
                <span class="text-sm text-gray-900">Cuentas por Pagar (Acreedores)</span>
              </label>
            </div>
          </div>
        </div>
      `,
      showCancelButton: true,
      confirmButtonText: 'Descargar Excel',
      cancelButtonText: 'Cancelar',
      confirmButtonColor: '#4f46e5',
      preConfirm: () => {
        return {
          from: document.getElementById('exportFrom').value,
          to: document.getElementById('exportTo').value,
          includeSales: document.getElementById('incSales').checked,
          includeReceivables: document.getElementById('incReceivables').checked,
          includePayables: document.getElementById('incPayables').checked,
        }
      }
    }).then((result) => {
      if (result.isConfirmed) {
        handleExport(result.value);
      }
    });
  };

  const handleExport = async (config) => {
    try {
      if (!config.includeSales && !config.includeReceivables && !config.includePayables) {
        showError("Debes seleccionar al menos un tipo de reporte.");
        return;
      }

      setIsExporting(true);
      const query = new URLSearchParams({
        from: config.from,
        to: config.to,
        includeSales: config.includeSales,
        includeReceivables: config.includeReceivables,
        includePayables: config.includePayables
      }).toString();

      const response = await api.get(`/reports/export?${query}`, {
        responseType: 'blob'
      });

      const url = window.URL.createObjectURL(new Blob([response]));
      const link = document.createElement('a');
      link.href = url;
      link.setAttribute('download', `Reporte_MagnaTravel_${new Date().toISOString().slice(0, 10)}.xlsx`);
      document.body.appendChild(link);
      link.click();
      link.parentNode.removeChild(link);
    } catch (error) {
      showError("Error al exportar Excel.");
    } finally {
      setIsExporting(false);
    }
  };

  if (loading && !report) {
    return <ReportsSkeleton />;
  }

  if (!report) {
    return (
      <div className="text-center py-12">
        <p className="text-muted-foreground">No se pudieron cargar los reportes.</p>
      </div>
    );
  }

  const s = report.summary;

  // cashFlow mono-moneda (mantiene compatibilidad con KpiCard de flujo neto cuando no hay porMoneda)
  const cashFlow = s.customerPayments - s.supplierPayments;

  /**
   * Contrato real del backend /reports/detailed (A-3, 2026-06-11):
   * s.porMoneda = objeto con 6 listas, cada una [{currency, amount}]:
   *   cobrosDelMes, pagosProveedores, ventasDelMes, costosDelMes, saldoPendiente, cuentasPorPagar.
   *
   * Para multimoneda: necesitamos saber qué monedas hay (de cobrosDelMes, que es la lista
   * más completa). Si porMoneda existe y cobrosDelMes tiene 2+ filas → modo multimoneda.
   */
  const porMonedaObj = s.porMoneda && typeof s.porMoneda === "object" && !Array.isArray(s.porMoneda)
    ? s.porMoneda
    : null;

  // Lista de monedas disponibles: extraída de cobrosDelMes (la más representativa)
  const monedasDisponibles = porMonedaObj &&
    Array.isArray(porMonedaObj.cobrosDelMes) &&
    porMonedaObj.cobrosDelMes.length > 1
    ? porMonedaObj.cobrosDelMes
    : null;

  /**
   * Construye el "value" de un KpiCard a partir de una de las 6 listas de porMoneda.
   *
   * @param {string} monoValue - valor formateado para modo mono-moneda (string)
   * @param {Array<{currency,amount}>} listaMoneda - una de las sub-listas de s.porMoneda
   */
  function buildKpiValue(monoValue, listaMoneda) {
    if (!monedasDisponibles || !Array.isArray(listaMoneda) || listaMoneda.length <= 1) {
      return monoValue;
    }
    return listaMoneda.map((item) => ({
      currency: item.currency,
      value: item.amount ?? 0,
    }));
  }

  /**
   * Construye el valor del Flujo Neto por moneda: cobros - pagos para cada moneda.
   * Las dos listas (cobrosDelMes, pagosProveedores) se cruzan por currency.
   */
  function buildFlujonetoValue() {
    if (!monedasDisponibles) return formatCurrency(cashFlow);

    const cobros = Array.isArray(porMonedaObj.cobrosDelMes) ? porMonedaObj.cobrosDelMes : [];
    const pagos = Array.isArray(porMonedaObj.pagosProveedores) ? porMonedaObj.pagosProveedores : [];

    // Mapas por moneda para lookup O(1).
    const cobrosPorMoneda = {};
    cobros.forEach((c) => { cobrosPorMoneda[c.currency] = c.amount ?? 0; });
    const pagosPorMoneda = {};
    pagos.forEach((p) => { pagosPorMoneda[p.currency] = p.amount ?? 0; });

    // Union de monedas presentes en cobros O pagos: una moneda con pagos pero sin
    // cobros (o viceversa) NO debe perder su fila de flujo neto.
    const monedas = [...new Set([...Object.keys(cobrosPorMoneda), ...Object.keys(pagosPorMoneda)])];

    return monedas.map((currency) => ({
      currency,
      // Flujo neto = cobros − pagos en esa moneda. Regla ①: nunca mezclar monedas.
      value: (cobrosPorMoneda[currency] ?? 0) - (pagosPorMoneda[currency] ?? 0),
    }));
  }

  return (
    <div className="space-y-6 animate-in fade-in duration-500">
      {/* Header */}
      <div className="flex flex-col md:flex-row justify-between items-start md:items-center gap-4">
        <div>
          <h2 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white flex items-center gap-2">
            <BarChart3 className="h-6 w-6 text-indigo-600" />
            Reportes
          </h2>
          <p className="text-sm text-muted-foreground mt-1">
            Análisis financiero y operativo de tu agencia
          </p>
        </div>
        <Button
          variant="outline"
          onClick={openExportModal}
          disabled={isExporting}
          className="flex items-center gap-2"
        >
          {isExporting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
          Exportar Excel
        </Button>
      </div>

      {/* Date Filter Bar */}
      <div className="flex flex-col sm:flex-row gap-4 items-start sm:items-center justify-between bg-white dark:bg-slate-900/50 p-4 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm">
        <div className="flex flex-wrap gap-1.5">
          {[
            { key: "month", label: "Este Mes" },
            { key: "lastMonth", label: "Mes Anterior" },
            { key: "quarter", label: "Trimestre" },
            { key: "year", label: "Año" },
            { key: "all", label: "Todo" },
          ].map((p) => (
            <button
              key={p.key}
              onClick={() => applyPreset(p.key)}
              className={`px-3 py-1.5 text-xs font-medium rounded-lg transition-all ${activePreset === p.key
                ? "bg-indigo-600 text-white shadow-sm"
                : "bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700"
                }`}
            >
              {p.label}
            </button>
          ))}
        </div>
        <div className="flex items-center gap-2">
          <Calendar className="h-4 w-4 text-slate-400" />
          <input
            type="date"
            value={dateFrom}
            onChange={(e) => { setDateFrom(e.target.value); setActivePreset(""); }}
            className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-800 dark:border-slate-700 dark:text-white"
          />
          <span className="text-slate-400 text-sm">a</span>
          <input
            type="date"
            value={dateTo}
            onChange={(e) => { setDateTo(e.target.value); setActivePreset(""); }}
            className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-800 dark:border-slate-700 dark:text-white"
          />
          {loading && <Loader2 className="h-4 w-4 animate-spin text-indigo-500" />}
        </div>
      </div>

      <Tabs defaultValue="sales" className="space-y-6">
        <TabsList className="bg-slate-100 dark:bg-slate-800 p-1 rounded-xl">
          <TabsTrigger value="sales" className="rounded-lg px-4 py-2 text-sm font-medium data-[state=active]:bg-white dark:data-[state=active]:bg-slate-950 data-[state=active]:text-indigo-600 data-[state=active]:shadow-sm">Ventas y Margen</TabsTrigger>
          <TabsTrigger value="finance" className="rounded-lg px-4 py-2 text-sm font-medium data-[state=active]:bg-white dark:data-[state=active]:bg-slate-950 data-[state=active]:text-indigo-600 data-[state=active]:shadow-sm">Finanzas y Deudas</TabsTrigger>
          <TabsTrigger value="intelligence" className="rounded-lg px-4 py-2 text-sm font-medium data-[state=active]:bg-white dark:data-[state=active]:bg-slate-950 data-[state=active]:text-indigo-600 data-[state=active]:shadow-sm">Inteligencia Analítica</TabsTrigger>
        </TabsList>

        <TabsContent value="intelligence" className="space-y-6">
           <AnalyticsPage />
        </TabsContent>

        <TabsContent value="sales" className="space-y-6">
          {/* KPI Cards */}
          <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
            <KpiCard
              title="Ventas Totales"
              value={formatCurrency(s.totalSales)}
              subtitle={`${s.filesCount} expedientes`}
              icon={TrendingUp}
              color="indigo"
            />
            <KpiCard
              title="Margen Bruto"
              value={formatCurrency(s.grossMargin)}
              subtitle={`${s.marginPercent}% ganancia`}
              icon={Percent}
              color="emerald"
            />
            <KpiCard
              title="Promedio Venta"
              value={s.filesCount > 0 ? formatCurrency(s.totalSales / s.filesCount) : '$0'}
              subtitle="por expediente"
              icon={BarChart3}
              color="blue"
            />
            <KpiCard
              title="Promedio Costo"
              value={s.filesCount > 0 ? formatCurrency(s.totalCosts / s.filesCount) : '$0'}
              subtitle="por expediente"
              icon={CreditCard}
              color="rose"
            />
          </div>

          {/* Monthly Chart */}
          <Card className="shadow-sm">
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <BarChart3 className="h-5 w-5 text-slate-500" />
                Evolución de Ventas
              </CardTitle>
              <CardDescription>Comparativa mensual de ingresos vs egresos</CardDescription>
            </CardHeader>
            <CardContent className="pl-0">
              <div className="h-[350px] w-full">
                <ResponsiveContainer width="100%" height="100%">
                  <AreaChart data={report.monthlyBreakdown} margin={{ top: 10, right: 30, left: 0, bottom: 0 }}>
                    <defs>
                      <linearGradient id="colorSales" x1="0" y1="0" x2="0" y2="1">
                        <stop offset="5%" stopColor="#6366f1" stopOpacity={0.1} />
                        <stop offset="95%" stopColor="#6366f1" stopOpacity={0} />
                      </linearGradient>
                    </defs>
                    <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="#e2e8f0" />
                    <XAxis dataKey="month" stroke="#64748b" fontSize={12} tickLine={false} axisLine={false} />
                    <YAxis stroke="#64748b" fontSize={12} tickLine={false} axisLine={false}
                      tickFormatter={(v) => v >= 1000 ? `$${(v / 1000).toFixed(0)}k` : `$${v}`} />
                    <Tooltip
                      contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }}
                      formatter={(value) => [formatCurrency(value), undefined]}
                    />
                    <Legend wrapperStyle={{ paddingTop: '20px' }} />
                    <Area type="monotone" dataKey="sales" name="Ventas" stroke="#6366f1" fillOpacity={1} fill="url(#colorSales)" strokeWidth={2} />
                    <Area type="monotone" dataKey="costs" name="Costos" stroke="#94a3b8" fill="transparent" strokeDasharray="5 5" strokeWidth={2} />
                  </AreaChart>
                </ResponsiveContainer>
              </div>
            </CardContent>
          </Card>

          {/* Top Customers & Recent Files */}
          <div className="grid gap-6 md:grid-cols-2">
            <Card className="shadow-sm">
              <CardHeader>
                <CardTitle className="flex items-center gap-2 text-indigo-600 dark:text-indigo-400">
                  <Users className="h-5 w-5" />
                  Top Clientes
                </CardTitle>
                <CardDescription>Clientes con mayor volumen de compra</CardDescription>
              </CardHeader>
              <CardContent>
                {report.topCustomers?.length > 0 ? (
                  <div className="space-y-3 max-h-[350px] overflow-y-auto pr-1">
                    {report.topCustomers.map((cust, i) => (
                      <div
                        key={i}
                        onClick={() => navigate(`/customers/${getPublicId(cust)}/account`)}
                        className="flex items-center justify-between p-3 rounded-lg bg-slate-50 dark:bg-slate-800/50 border border-slate-100 dark:border-slate-800 hover:border-slate-300 dark:hover:border-slate-600 hover:bg-slate-100 dark:hover:bg-slate-800 cursor-pointer transition-all"
                      >
                        <div className="flex items-center gap-3">
                          <div className={`h-8 w-8 rounded-full flex items-center justify-center text-sm font-bold shadow-sm ${i === 0 ? 'bg-amber-100 text-amber-700 border border-amber-200' :
                            i === 1 ? 'bg-slate-200 text-slate-600 border border-slate-300' :
                              i === 2 ? 'bg-orange-100 text-orange-700 border border-orange-200' :
                                'bg-white text-slate-500 border border-slate-200'
                            }`}>
                            {i + 1}
                          </div>
                          <div>
                            <span className="font-semibold text-sm text-slate-900 dark:text-slate-200 hover:underline">{cust.name}</span>
                            <div className="text-xs text-slate-500">{cust.fileCount} viajes</div>
                          </div>
                        </div>
                        <div className="text-right">
                          <div className="font-bold text-sm text-indigo-600 dark:text-indigo-400">
                            {formatCurrency(cust.totalSale)}
                          </div>
                        </div>
                      </div>
                    ))}
                  </div>
                ) : (
                  <EmptyState message="Sin datos de clientes en este período" />
                )}
              </CardContent>
            </Card>

            {/* Additional chart or list could go here */}
          </div>
        </TabsContent>

        <TabsContent value="finance" className="space-y-6">
          {/* Las 4 tarjetas de la solapa Finanzas: con soporte multimoneda.
              Contrato A-3 (2026-06-11): s.porMoneda.cobrosDelMes/pagosProveedores/saldoPendiente
              son las listas reales. Sin porMoneda: comportamiento idéntico al anterior. */}
          <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
            <KpiCard
              title="Cobros (Entradas)"
              value={buildKpiValue(
                formatCurrency(s.customerPayments),
                porMonedaObj?.cobrosDelMes
              )}
              subtitle="En este período"
              icon={Wallet}
              color="emerald"
            />
            <KpiCard
              title="Pagos (Salidas)"
              value={buildKpiValue(
                formatCurrency(s.supplierPayments),
                porMonedaObj?.pagosProveedores
              )}
              subtitle="En este período"
              icon={CreditCard}
              color="rose"
            />
            <KpiCard
              title="Flujo Neto"
              value={buildFlujonetoValue()}
              subtitle="Resultado de caja"
              icon={cashFlow >= 0 ? ArrowUpRight : ArrowDownRight}
              color={cashFlow >= 0 ? "blue" : "orange"}
            />
            <KpiCard
              title="Deuda Clientes"
              value={buildKpiValue(
                // Total simple cuando no hay multimoneda: suma todos los receivables
                formatCurrency(receivables.reduce((acc, curr) => acc + (curr.currentBalance || 0), 0)),
                // Multimoneda: usar saldoPendiente del backend (ya viene segmentado por moneda)
                porMonedaObj?.saldoPendiente
              )}
              subtitle="Total por cobrar"
              icon={AlertCircle}
              color="purple"
            />
          </div>

          <div className="grid gap-6 lg:grid-cols-2">
            {/* Accounts Receivable (Deuda Clientes) */}
            <Card className="shadow-sm border-l-4 border-l-purple-500">
              <CardHeader>
                <CardTitle className="flex items-center gap-2 text-purple-700 dark:text-purple-400">
                  <Users className="h-5 w-5" />
                  Cuentas por Cobrar
                </CardTitle>
                <CardDescription>Clientes que deben dinero a la agencia</CardDescription>
              </CardHeader>
              <CardContent>
                {receivables?.length > 0 ? (
                  <div className="space-y-2 max-h-[400px] overflow-y-auto pr-1">
                    {receivables.map((debtor) => {
                      const monedaDebtor = debtor.currency || "ARS";
                      return (
                        <div
                          key={getPublicId(debtor)}
                          onClick={() => navigate(`/customers/${getPublicId(debtor)}/account`)}
                          className="flex items-center justify-between p-3 rounded-lg bg-slate-50 dark:bg-slate-800/50 border border-slate-100 dark:border-slate-800 hover:border-purple-200 dark:hover:border-purple-800 hover:bg-purple-50/50 dark:hover:bg-purple-900/10 cursor-pointer transition-all"
                        >
                          <div className="flex flex-col">
                            <span className="font-medium text-sm text-slate-700 dark:text-slate-300 hover:text-purple-700 dark:hover:text-purple-400 transition-colors">{debtor.fullName}</span>
                            <span className="text-[10px] text-slate-500">Ult. mov: {debtor.lastMovementDate ? formatDate(debtor.lastMovementDate) : '-'}</span>
                          </div>
                          {/* Cartelito de moneda pegado al monto — la moneda es una dimensión de la fila */}
                          <span className="font-mono font-bold text-purple-600 dark:text-purple-400 text-sm inline-flex items-center gap-1">
                            <CurrencyBadge currency={monedaDebtor} />
                            {formatCurrency(debtor.currentBalance, monedaDebtor)}
                          </span>
                        </div>
                      );
                    })}
                    {/* Total a Cobrar al pie: por moneda si hay más de una, o simple si es mono-moneda */}
                    {(() => {
                      const totalesPorMoneda = receivables.reduce((acc, r) => {
                        const m = r.currency || "ARS";
                        acc[m] = (acc[m] || 0) + r.currentBalance;
                        return acc;
                      }, {});
                      const monedas = Object.keys(totalesPorMoneda);
                      return (
                        <div className="pt-3 mt-2 border-t border-slate-200 dark:border-slate-700 flex justify-between items-center px-2">
                          <span className="text-sm font-bold text-slate-700 dark:text-slate-300">Total a Cobrar</span>
                          <span className="font-bold text-purple-700 dark:text-purple-400 flex items-center gap-2 flex-wrap justify-end">
                            {monedas.map((m, idx) => (
                              <span key={m} className="inline-flex items-center gap-1">
                                <CurrencyBadge currency={m} />
                                {formatCurrency(totalesPorMoneda[m], m)}
                                {idx < monedas.length - 1 && <span className="text-slate-400 mx-1">·</span>}
                              </span>
                            ))}
                          </span>
                        </div>
                      );
                    })()}
                  </div>
                ) : (
                  <EmptyState message="¡Excelente! No hay clientes con deuda." />
                )}
              </CardContent>
            </Card>

            {/* Accounts Payable (Deuda Proveedores) */}
            <Card className="shadow-sm border-l-4 border-l-rose-500">
              <CardHeader>
                <CardTitle className="flex items-center gap-2 text-rose-600 dark:text-rose-400">
                  <Building2 className="h-5 w-5" />
                  Cuentas por Pagar
                </CardTitle>
                <CardDescription>Saldo a favor de proveedores</CardDescription>
              </CardHeader>
              <CardContent>
                {report.supplierDebts?.length > 0 ? (
                  <div className="space-y-2 max-h-[400px] overflow-y-auto pr-1">
                    {report.supplierDebts.map((sup) => {
                      const monedaSupplier = sup.currency || "ARS";
                      return (
                        <div
                          key={getPublicId(sup)}
                          onClick={() => navigate(`/suppliers/${getPublicId(sup)}/account`)}
                          className="flex items-center justify-between p-3 rounded-lg bg-slate-50 dark:bg-slate-800/50 border border-slate-100 dark:border-slate-800 hover:border-rose-200 dark:hover:border-rose-800 hover:bg-rose-50/50 dark:hover:bg-rose-900/10 cursor-pointer transition-all"
                        >
                          <div className="flex items-center gap-3">
                            <div className="h-8 w-8 rounded-full bg-rose-100 dark:bg-rose-900/30 flex items-center justify-center">
                              <Building2 className="h-4 w-4 text-rose-600 dark:text-rose-400" />
                            </div>
                            <span className="font-medium text-sm text-slate-700 dark:text-slate-300 hover:text-rose-700 dark:hover:text-rose-400 transition-colors">{sup.name}</span>
                          </div>
                          {/* Cartelito de moneda pegado al monto — la moneda es una dimensión de la fila */}
                          <span className="font-mono font-bold text-rose-600 dark:text-rose-400 text-sm inline-flex items-center gap-1">
                            <CurrencyBadge currency={monedaSupplier} />
                            {formatCurrency(sup.currentBalance, monedaSupplier)}
                          </span>
                        </div>
                      );
                    })}
                    {/* Total a Pagar al pie: por moneda si hay más de una */}
                    {(() => {
                      const totalesPorMoneda = report.supplierDebts.reduce((acc, sd) => {
                        const m = sd.currency || "ARS";
                        acc[m] = (acc[m] || 0) + sd.currentBalance;
                        return acc;
                      }, {});
                      const monedas = Object.keys(totalesPorMoneda);
                      return (
                        <div className="pt-3 mt-2 border-t border-slate-200 dark:border-slate-700 flex justify-between items-center px-2">
                          <span className="text-sm font-bold text-slate-700 dark:text-slate-300">Total a Pagar</span>
                          <span className="font-bold text-rose-700 dark:text-rose-400 flex items-center gap-2 flex-wrap justify-end">
                            {monedas.map((m, idx) => (
                              <span key={m} className="inline-flex items-center gap-1">
                                <CurrencyBadge currency={m} />
                                {formatCurrency(totalesPorMoneda[m], m)}
                                {idx < monedas.length - 1 && <span className="text-slate-400 mx-1">·</span>}
                              </span>
                            ))}
                          </span>
                        </div>
                      );
                    })()}
                  </div>
                ) : (
                  <EmptyState message="Sin deuda con proveedores" />
                )}
              </CardContent>
            </Card>
          </div>
        </TabsContent>
      </Tabs>
    </div>
  );
}

/**
 * Tarjeta KPI del tablero de Reportes.
 *
 * Multimoneda: si `value` es un array de { currency, value } (en vez de un string formateado),
 * se muestran dos líneas, una por moneda (pesos arriba con cartelito $, dólares abajo con US$).
 * Regla ③: si `value` es un string (mono-moneda), la tarjeta se ve igual que siempre.
 */
function KpiCard({ title, value, subtitle, icon: Icon, color }) {
  const colorMap = {
    indigo: { bg: 'bg-indigo-50 dark:bg-indigo-900/10', text: 'text-indigo-600 dark:text-indigo-400' },
    emerald: { bg: 'bg-emerald-50 dark:bg-emerald-900/10', text: 'text-emerald-600 dark:text-emerald-400' },
    blue: { bg: 'bg-blue-50 dark:bg-blue-900/10', text: 'text-blue-600 dark:text-blue-400' },
    rose: { bg: 'bg-rose-50 dark:bg-rose-900/10', text: 'text-rose-600 dark:text-rose-400' },
    purple: { bg: 'bg-purple-50 dark:bg-purple-900/10', text: 'text-purple-600 dark:text-purple-400' },
    orange: { bg: 'bg-orange-50 dark:bg-orange-900/10', text: 'text-orange-600 dark:text-orange-400' },
  };
  const c = colorMap[color] || colorMap.indigo;

  // Decidir si el value es multi-moneda (array) o mono-moneda (string/número)
  const esMultimoneda = Array.isArray(value);

  return (
    <Card className={`border-none shadow-sm ${c.bg} transition-all hover:scale-[1.02]`}>
      <CardContent className="p-5">
        <div className="flex items-center justify-between">
          <p className={`text-xs font-medium ${c.text} opacity-80 uppercase tracking-wider`}>{title}</p>
          <Icon className={`h-4 w-4 ${c.text}`} />
        </div>
        <div className="mt-2">
          {esMultimoneda ? (
            // Modo multimoneda: dos líneas, una por moneda
            <div className="space-y-1">
              {value.map((pm) => (
                <div key={pm.currency} className="flex items-center gap-1.5">
                  <CurrencyBadge currency={pm.currency} size="sm" />
                  <span className={`text-xl font-bold ${c.text}`}>
                    {formatCurrency(pm.value, pm.currency)}
                  </span>
                </div>
              ))}
            </div>
          ) : (
            // Modo mono-moneda: igual que siempre
            <span className={`text-2xl font-bold ${c.text}`}>{value}</span>
          )}
        </div>
        {subtitle && (
          <p className={`text-xs ${c.text} mt-1 opacity-70`}>{subtitle}</p>
        )}
      </CardContent>
    </Card>
  );
}

function EmptyState({ message }) {
  return (
    <div className="text-center py-8 text-muted-foreground">
      <p className="text-sm">{message}</p>
    </div>
  );
}
