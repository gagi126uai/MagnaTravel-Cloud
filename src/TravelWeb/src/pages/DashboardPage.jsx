import { useEffect, useState } from "react";
import { apiRequest } from "../api";
import { showError } from "../alerts";
import { isAdmin } from "../auth";
import {
    Users,
    CalendarRange,
    CreditCard,
    BadgeDollarSign,
    Search,
    Activity
} from "lucide-react";
import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle
} from "../components/ui/card";
import { Input } from "../components/ui/input";
import { Button } from "../components/ui/button";

export default function DashboardPage() {
    const [summary, setSummary] = useState(null);
    const [searchQuery, setSearchQuery] = useState("");
    const [searchResults, setSearchResults] = useState(null);
    const adminUser = isAdmin();

    useEffect(() => {
        const endpoint = adminUser ? "/api/reports/summary" : "/api/reports/operations";
        apiRequest(endpoint)
            .then(setSummary)
            .catch(() => {
                showError("No se pudieron cargar los indicadores.");
            });
    }, [adminUser]);

    const handleSearch = async (event) => {
        event.preventDefault();
        if (!searchQuery.trim()) {
            setSearchResults(null);
            return;
        }

        try {
            const data = await apiRequest(`/api/search?query=${encodeURIComponent(searchQuery)}`);
            setSearchResults(data);
        } catch (error) {
            showError(error.message || "No se pudo completar la búsqueda.");
        }
    };

    return (
        <div className="space-y-6">
            <div className="flex flex-col gap-2 md:flex-row md:items-center md:justify-between">
                <div>
                    <h2 className="text-3xl font-bold tracking-tight">Dashboard</h2>
                    <p className="text-muted-foreground">
                        Resumen ejecutivo de la operación diaria.
                    </p>
                </div>
                <div className="flex items-center gap-2 rounded-full border bg-card px-4 py-1 text-xs font-semibold uppercase tracking-wider text-muted-foreground shadow-sm">
                    <Activity className="h-3 w-3 text-green-500 animate-pulse" />
                    Actualizado en tiempo real
                </div>
            </div>

            {summary && (
                <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
                    <Card>
                        <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                            <CardTitle className="text-sm font-medium">Clientes</CardTitle>
                            <Users className="h-4 w-4 text-muted-foreground" />
                        </CardHeader>
                        <CardContent>
                            <div className="text-2xl font-bold">{summary.totalCustomers}</div>
                            <p className="text-xs text-muted-foreground">Activos en plataforma</p>
                        </CardContent>
                    </Card>
                    <Card>
                        <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                            <CardTitle className="text-sm font-medium">Reservas</CardTitle>
                            <CalendarRange className="h-4 w-4 text-muted-foreground" />
                        </CardHeader>
                        <CardContent>
                            <div className="text-2xl font-bold">{summary.totalReservations}</div>
                            <p className="text-xs text-muted-foreground">Confirmadas este mes</p>
                        </CardContent>
                    </Card>
                    <Card>
                        <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                            <CardTitle className="text-sm font-medium">Pagos</CardTitle>
                            <CreditCard className="h-4 w-4 text-muted-foreground" />
                        </CardHeader>
                        <CardContent>
                            <div className="text-2xl font-bold">{summary.totalPayments}</div>
                            <p className="text-xs text-muted-foreground">Transacciones procesadas</p>
                        </CardContent>
                    </Card>
                    {adminUser ? (
                        <Card className="border-primary/20 bg-primary/5">
                            <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                                <CardTitle className="text-sm font-medium text-primary">Ingresos</CardTitle>
                                <BadgeDollarSign className="h-4 w-4 text-primary" />
                            </CardHeader>
                            <CardContent>
                                <div className="text-2xl font-bold text-primary">${summary.totalRevenue.toFixed(2)}</div>
                                <p className="text-xs text-primary/70">Total acumulado</p>
                            </CardContent>
                        </Card>
                    ) : (
                        <Card>
                            <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                                <CardTitle className="text-sm font-medium">Estado Operativo</CardTitle>
                                <Activity className="h-4 w-4 text-muted-foreground" />
                            </CardHeader>
                            <CardContent>
                                <div className="text-2xl font-bold text-green-600">En curso</div>
                                <p className="text-xs text-muted-foreground">Sistemas online</p>
                            </CardContent>
                        </Card>
                    )}
                </div>
            )}

            <Card>
                <CardHeader>
                    <CardTitle>Búsqueda Rápida</CardTitle>
                    <CardDescription>
                        Encuentra vouchers, clientes o pagos sin salir del dashboard.
                    </CardDescription>
                </CardHeader>
                <CardContent>
                    <form onSubmit={handleSearch} className="flex gap-4">
                        <div className="relative flex-1">
                            <Search className="absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" />
                            <Input
                                type="search"
                                placeholder="Buscar por nombre, voucher o método de pago..."
                                className="pl-9"
                                value={searchQuery}
                                onChange={(event) => setSearchQuery(event.target.value)}
                            />
                        </div>
                        <Button type="submit">Buscar</Button>
                    </form>

                    {searchResults && (
                        <div className="mt-6 grid gap-6 lg:grid-cols-3">
                            <div className="space-y-4">
                                <h4 className="text-sm font-medium text-muted-foreground">Clientes</h4>
                                {searchResults.customers.length === 0 ? (
                                    <p className="text-sm text-muted-foreground">Sin resultados.</p>
                                ) : (
                                    searchResults.customers.map((customer) => (
                                        <Card key={customer.id} className="p-4">
                                            <p className="font-semibold">{customer.fullName}</p>
                                            <p className="text-xs text-muted-foreground">{customer.email || "Sin email"}</p>
                                        </Card>
                                    ))
                                )}
                            </div>

                            <div className="space-y-4">
                                <h4 className="text-sm font-medium text-muted-foreground">Vouchers</h4>
                                {searchResults.vouchers.length === 0 ? (
                                    <p className="text-sm text-muted-foreground">Sin resultados.</p>
                                ) : (
                                    searchResults.vouchers.map((voucher) => (
                                        <Card key={voucher.id} className="p-4">
                                            <div className="flex items-center justify-between">
                                                <p className="font-semibold">{voucher.referenceCode}</p>
                                                <span className="text-[10px] uppercase tracking-wider text-muted-foreground border px-1.5 py-0.5 rounded">
                                                    {voucher.status}
                                                </span>
                                            </div>
                                            <p className="text-xs text-muted-foreground mt-1">
                                                {voucher.customerName}
                                            </p>
                                        </Card>
                                    ))
                                )}
                            </div>

                            <div className="space-y-4">
                                <h4 className="text-sm font-medium text-muted-foreground">Pagos</h4>
                                {searchResults.payments.length === 0 ? (
                                    <p className="text-sm text-muted-foreground">Sin resultados.</p>
                                ) : (
                                    searchResults.payments.map((payment) => (
                                        <Card key={payment.id} className="p-4">
                                            <p className="font-semibold text-green-600">
                                                ${Number(payment.amount).toFixed(2)}
                                            </p>
                                            <p className="text-xs text-muted-foreground">
                                                {payment.reservationCode} · {payment.method}
                                            </p>
                                        </Card>
                                    ))
                                )}
                            </div>
                        </div>
                    )}
                </CardContent>
            </Card>
        </div>
    );
}
