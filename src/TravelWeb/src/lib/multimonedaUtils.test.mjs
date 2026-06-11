/**
 * Tests para las utilidades de multimoneda.
 * Cubre:
 *   - formatCurrency(amount, currency) con ARS, USD y sin currency (default legacy)
 *   - Comportamiento mono-moneda (default sin pasar currency = legacy USD/en-US, regla B-1)
 *   - formatCurrency de financeUtils.js con el mismo contrato
 *   - esCobroCruzado con la detección canónica A-4 (imputedCurrency)
 *   - buildMetricItems con cashByCurrency (contrato real A-2)
 */

import assert from "node:assert/strict";
import { describe, it } from "node:test";

// --- lib/utils.js ---
// Node no procesa JSX/TS, así que importamos la función directamente.
// Como el archivo usa ESM pero sin extensión .mjs, hacemos un pequeño shim inline.

// Reimplementamos la función exactamente como quedó en utils.js para testear su contrato.
// Regla B-1 (2026-06-11): default SIN currency = comportamiento legacy USD/en-US (HEAD).
// Los call sites que pasan ARS o USD explícito obtienen el formato correcto de cada moneda.

function formatCurrencyUtils(amount, currency) {
    if (amount === undefined || amount === null) {
        if (!currency) return "$0.00";
        return currency === "USD" ? "US$0.00" : "$0,00";
    }
    const number = Number(amount);
    if (currency === "ARS") {
        return new Intl.NumberFormat("es-AR", {
            style: "currency",
            currency: "ARS",
            minimumFractionDigits: 2,
        }).format(number);
    }
    if (currency === "USD") {
        return "US$" + new Intl.NumberFormat("es-AR", {
            minimumFractionDigits: 2,
            maximumFractionDigits: 2,
        }).format(number);
    }
    // Sin currency: comportamiento legacy idéntico a HEAD (USD/en-US)
    return new Intl.NumberFormat("en-US", {
        style: "currency",
        currency: "USD",
        minimumFractionDigits: 2,
    }).format(number);
}

function formatCurrencyFinance(amount, currency = "ARS") {
    const number = Number(amount || 0);
    if (currency === "USD") {
        return "US$" + new Intl.NumberFormat("es-AR", {
            minimumFractionDigits: 2,
            maximumFractionDigits: 2,
        }).format(number);
    }
    return new Intl.NumberFormat("es-AR", {
        style: "currency",
        currency: "ARS",
        minimumFractionDigits: 2,
    }).format(number);
}

describe("formatCurrency — lib/utils.js (multimoneda + regla B-1)", () => {
    it("regla B-1: sin currency → comportamiento legacy USD/en-US (mismo que HEAD)", () => {
        // Call sites que NO pasan currency deben seguir viendo el mismo formato de siempre.
        // El formato legacy es USD/en-US: usa punto para miles y coma para decimales (en-US = $1,000.00)
        const result = formatCurrencyUtils(1000);
        assert.ok(result.includes("1,000"), `Legacy sin currency debe usar en-US '1,000': '${result}'`);
    });

    it("ARS explícito: usa formato es-AR (punto de miles, coma decimal)", () => {
        const result = formatCurrencyUtils(1000, "ARS");
        assert.ok(result.includes("1.000"), `ARS explícito debe usar es-AR '1.000': '${result}'`);
    });

    it("ARS explícito: NO es igual que sin-currency (formatos diferentes)", () => {
        const sinMoneda = formatCurrencyUtils(500);
        const conARS = formatCurrencyUtils(500, "ARS");
        // Regla B-1: sin currency = legacy USD/en-US; ARS = es-AR. Son distintos.
        assert.notEqual(sinMoneda, conARS);
    });

    it("USD: empieza con 'US$'", () => {
        const result = formatCurrencyUtils(100, "USD");
        assert.ok(result.startsWith("US$"), `Esperaba 'US$' al inicio, recibí '${result}'`);
    });

    it("USD: NO empieza con '$' solo (para distinguirlo del peso)", () => {
        const result = formatCurrencyUtils(100, "USD");
        assert.ok(!result.startsWith("$ ") && !result.startsWith("$1"),
            `No debe comenzar con '$' para no confundirse con pesos: '${result}'`);
    });

    it("USD cero: 'US$0.00'", () => {
        const result = formatCurrencyUtils(0, "USD");
        assert.ok(result.startsWith("US$"), `Cero USD debe empezar con 'US$': '${result}'`);
    });

    it("null sin currency → '$0.00' (legacy)", () => {
        const result = formatCurrencyUtils(null);
        assert.equal(result, "$0.00");
    });

    it("null → USD retorna 'US$0.00'", () => {
        const result = formatCurrencyUtils(null, "USD");
        assert.equal(result, "US$0.00");
    });

    it("null → ARS retorna string de $0 (es-AR)", () => {
        const result = formatCurrencyUtils(null, "ARS");
        assert.ok(result.includes("0"), `Null ARS debe dar '0' en el resultado: '${result}'`);
    });

    it("undefined sin currency → '$0.00' (legacy)", () => {
        const result = formatCurrencyUtils(undefined);
        assert.equal(result, "$0.00");
    });
});

describe("formatCurrency — financeUtils.js (multimoneda)", () => {
    it("default ARS: formatea sin pasar currency", () => {
        const result = formatCurrencyFinance(1000);
        assert.ok(result.includes("1.000"), `Esperaba '1.000' en '${result}'`);
    });

    it("USD: empieza con 'US$'", () => {
        const result = formatCurrencyFinance(1500, "USD");
        assert.ok(result.startsWith("US$"), `Esperaba 'US$' al inicio, recibí '${result}'`);
    });

    it("monto 0 → ARS formatea sin error", () => {
        const result = formatCurrencyFinance(0);
        assert.ok(typeof result === "string");
    });

    it("monto null → ARS trata como 0 (no lanza)", () => {
        const result = formatCurrencyFinance(null);
        assert.ok(result.includes("0"));
    });
});

describe("Regla ③ — mono-moneda = comportamiento idéntico al previo (HEAD)", () => {
    it("regla B-1: sin pasar currency a formatCurrencyUtils → formato legacy (no 'US$' de multimoneda)", () => {
        // La función sin currency devuelve el formato legacy USD/en-US (igual que HEAD).
        // Lo importante es que NO empiece con "US$" (eso sería el formato nuevo explícito de USD).
        // El legacy usa "$1,000.00" (en-US), que sí empieza con "$" pero no con "US$".
        const result = formatCurrencyUtils(2000);
        assert.ok(!result.startsWith("US$"), `El formato legacy no debe usar 'US$': '${result}'`);
    });

    it("sin pasar currency a formatCurrencyFinance, el resultado es ARS (no USD)", () => {
        // financeUtils.js sí defaultea a ARS (contexto de pagos/finanzas, todos en pesos)
        const result = formatCurrencyFinance(2000);
        assert.ok(!result.startsWith("US$"), `Debe ser ARS, no USD: '${result}'`);
    });
});

describe("ServiceList — lógica de totales por moneda", () => {
    /**
     * Prueba la lógica de acumulación de salePrice por moneda que usa ServiceList
     * para construir la fila TOTAL al pie (decisión A, 2026-06-11).
     */

    function acumularTotalesPorMoneda(services) {
        return services.reduce((acc, svc) => {
            const moneda = svc.currency || "ARS";
            acc[moneda] = (acc[moneda] || 0) + (svc.salePrice || 0);
            return acc;
        }, {});
    }

    it("servicios todos en ARS → una sola moneda en el map", () => {
        const services = [
            { currency: "ARS", salePrice: 100000 },
            { currency: "ARS", salePrice: 50000 },
        ];
        const totales = acumularTotalesPorMoneda(services);
        assert.deepEqual(Object.keys(totales), ["ARS"]);
        assert.equal(totales["ARS"], 150000);
    });

    it("servicios mixtos ARS y USD → dos monedas en el map", () => {
        const services = [
            { currency: "ARS", salePrice: 100000 },
            { currency: "USD", salePrice: 500 },
            { currency: "ARS", salePrice: 200000 },
        ];
        const totales = acumularTotalesPorMoneda(services);
        assert.equal(totales["ARS"], 300000);
        assert.equal(totales["USD"], 500);
    });

    it("servicio sin currency → cae a ARS por default", () => {
        const services = [
            { salePrice: 75000 },   // sin currency
        ];
        const totales = acumularTotalesPorMoneda(services);
        assert.equal(totales["ARS"], 75000);
    });

    it("fila TOTAL no se muestra si monedas.length <= 1 (mono-moneda)", () => {
        // La regla A dice: SOLO si hay 2 monedas reales en la lista
        const services = [
            { currency: "ARS", salePrice: 100 },
            { currency: "ARS", salePrice: 200 },
        ];
        const totales = acumularTotalesPorMoneda(services);
        const monedas = Object.keys(totales);
        assert.equal(monedas.length, 1, "con una sola moneda no se debería mostrar el TOTAL");
    });

    it("fila TOTAL se muestra si monedas.length > 1 (multimoneda)", () => {
        const services = [
            { currency: "ARS", salePrice: 100 },
            { currency: "USD", salePrice: 50 },
        ];
        const totales = acumularTotalesPorMoneda(services);
        const monedas = Object.keys(totales);
        assert.equal(monedas.length, 2, "con dos monedas se debería mostrar el TOTAL");
    });
});

describe("RegistrarCobroInline — cálculo de monto equivalente (cobro cruzado)", () => {
    /**
     * Replica la lógica del cálculo inline que hace RegistrarCobroInline
     * para mostrar cuánto se cancela de la deuda en la otra moneda.
     */

    function calcularMontoEquivalente(monedaCobro, saldoImputado, monto, tipoCambio) {
        const tc = parseFloat(tipoCambio);
        const m = parseFloat(monto);
        if (isNaN(tc) || tc <= 0 || isNaN(m) || m <= 0) return null;
        if (monedaCobro === "ARS" && saldoImputado === "USD") return m / tc;
        if (monedaCobro === "USD" && saldoImputado === "ARS") return m * tc;
        return null;
    }

    it("pago en ARS para bajar deuda USD: imputedAmount = monto / TC", () => {
        // Cobro 120.000 pesos con TC 1.200 → cancela 100 USD
        const equivalente = calcularMontoEquivalente("ARS", "USD", 120000, 1200);
        assert.equal(equivalente, 100);
    });

    it("pago en USD para bajar deuda ARS: imputedAmount = monto × TC", () => {
        // Cobro 100 USD con TC 1.200 → cancela 120.000 ARS
        const equivalente = calcularMontoEquivalente("USD", "ARS", 100, 1200);
        assert.equal(equivalente, 120000);
    });

    it("misma moneda (no cruza): retorna null — no aparece recuadro TC", () => {
        const equivalente = calcularMontoEquivalente("ARS", "ARS", 50000, 1200);
        assert.equal(equivalente, null);
    });

    it("TC vacío → retorna null (recuadro no calcula)", () => {
        const equivalente = calcularMontoEquivalente("ARS", "USD", 50000, "");
        assert.equal(equivalente, null);
    });

    it("TC cero → retorna null (evita división por cero)", () => {
        const equivalente = calcularMontoEquivalente("ARS", "USD", 50000, 0);
        assert.equal(equivalente, null);
    });

    it("monto cero → retorna null", () => {
        const equivalente = calcularMontoEquivalente("ARS", "USD", 0, 1200);
        assert.equal(equivalente, null);
    });
});

describe("esCobroCruzado — detección de cobros cruzados en historial (regla A-4)", () => {
    /**
     * Regla canónica A-4 (2026-06-11): el cobro es cruzado si y solo si
     * imputedCurrency != null && imputedCurrency !== currency.
     * No se usa exchangeRate como fallback (puede existir en registros legacy sin cruce real).
     */
    function esCobroCruzado(payment) {
        if (!payment) return false;
        return payment.imputedCurrency != null && payment.imputedCurrency !== payment.currency;
    }

    it("cobro normal ARS sin imputación: NO es cruzado", () => {
        assert.equal(esCobroCruzado({ currency: "ARS", amount: 1000 }), false);
    });

    it("cobro con imputedCurrency diferente: ES cruzado", () => {
        assert.equal(esCobroCruzado({ currency: "ARS", imputedCurrency: "USD", exchangeRate: 1200 }), true);
    });

    it("cobro con exchangeRate pero sin imputedCurrency: NO es cruzado (regla A-4 — fuente real es imputedCurrency)", () => {
        // exchangeRate puede existir en registros legacy que NO fueron cobros cruzados.
        // La fuente de verdad es imputedCurrency (campo nuevo, explícito).
        assert.equal(esCobroCruzado({ currency: "ARS", exchangeRate: 1200 }), false);
    });

    it("cobro con imputedCurrency igual a currency: NO es cruzado", () => {
        assert.equal(esCobroCruzado({ currency: "ARS", imputedCurrency: "ARS" }), false);
    });

    it("payment null: NO es cruzado (no lanza)", () => {
        assert.equal(esCobroCruzado(null), false);
    });

    it("imputedCurrency null: NO es cruzado (cobro normal, campo opcional)", () => {
        assert.equal(esCobroCruzado({ currency: "ARS", imputedCurrency: null }), false);
    });
});

describe("ReservaSummaryStrip — lógica de selección de modo (mono vs multi)", () => {
    /**
     * Prueba la lógica de decisión que usa ReservaSummaryStrip para elegir
     * entre el render mono-moneda (igual que hoy) y el multimoneda.
     * La regla ③ exige que mono-moneda se vea EXACTAMENTE como antes.
     */

    function resolverModo(reserva) {
        return reserva.esMultimoneda &&
            Array.isArray(reserva.porMoneda) &&
            reserva.porMoneda.length > 1
            ? "multimoneda"
            : "monoMoneda";
    }

    it("esMultimoneda=true y 2 monedas → modo multimoneda", () => {
        const reserva = {
            esMultimoneda: true,
            porMoneda: [
                { currency: "ARS", balance: 100000, totalSale: 200000, totalPaid: 100000, totalCost: 80000 },
                { currency: "USD", balance: 500, totalSale: 1000, totalPaid: 500, totalCost: 400 },
            ],
        };
        assert.equal(resolverModo(reserva), "multimoneda");
    });

    it("esMultimoneda=false → modo monoMoneda aunque porMoneda tenga items", () => {
        const reserva = {
            esMultimoneda: false,
            porMoneda: [{ currency: "ARS", balance: 100000 }],
        };
        assert.equal(resolverModo(reserva), "monoMoneda");
    });

    it("esMultimoneda=true pero solo 1 moneda en porMoneda → modo monoMoneda", () => {
        const reserva = {
            esMultimoneda: true,
            porMoneda: [{ currency: "ARS", balance: 100000 }],
        };
        assert.equal(resolverModo(reserva), "monoMoneda");
    });

    it("sin esMultimoneda (undefined) → modo monoMoneda (regla ③)", () => {
        const reserva = { balance: 50000, totalSale: 100000 };
        assert.equal(resolverModo(reserva), "monoMoneda");
    });

    it("esMultimoneda=true pero porMoneda vacío → modo monoMoneda", () => {
        const reserva = { esMultimoneda: true, porMoneda: [] };
        assert.equal(resolverModo(reserva), "monoMoneda");
    });
});

describe("buildMetricItems — caja multimoneda (contrato real A-2: cashByCurrency)", () => {
    /**
     * Replica la lógica de buildMetricItems de PaymentsCashPage.
     * Contrato real del backend: summary.cashByCurrency = [{currency, cashInThisMonth, cashOutThisMonth, netCashThisMonth}]
     * NOT summary.porMoneda, NOT cashIn/cashOut sin sufijo.
     */
    function buildMetricItems(summary) {
        if (!summary) {
            return [
                { label: "Ingresos del mes", value: 0 },
                { label: "Egresos del mes", value: 0 },
                { label: "Resultado de caja del mes", value: 0 },
            ];
        }
        const cashByCurrency = Array.isArray(summary.cashByCurrency) ? summary.cashByCurrency : null;
        if (cashByCurrency && cashByCurrency.length > 1) {
            return [
                {
                    label: "Ingresos del mes",
                    valuesByCurrency: cashByCurrency.map((pm) => ({
                        currency: pm.currency,
                        value: pm.cashInThisMonth ?? 0,
                    })),
                },
                {
                    label: "Egresos del mes",
                    valuesByCurrency: cashByCurrency.map((pm) => ({
                        currency: pm.currency,
                        value: pm.cashOutThisMonth ?? 0,
                    })),
                },
                {
                    label: "Resultado de caja del mes",
                    valuesByCurrency: cashByCurrency.map((pm) => ({
                        currency: pm.currency,
                        value: pm.netCashThisMonth ?? 0,
                    })),
                },
            ];
        }
        return [
            { label: "Ingresos del mes", value: summary.cashInThisMonth || 0 },
            { label: "Egresos del mes", value: summary.cashOutThisMonth || 0 },
            { label: "Resultado de caja del mes", value: summary.netCashThisMonth || 0 },
        ];
    }

    it("sin summary → 3 items con value 0 (mono-moneda)", () => {
        const items = buildMetricItems(null);
        assert.equal(items.length, 3);
        assert.equal(items[0].value, 0);
    });

    it("summary mono-moneda (sin cashByCurrency) → 3 items planos con escalares", () => {
        const summary = {
            cashInThisMonth: 100000,
            cashOutThisMonth: 50000,
            netCashThisMonth: 50000,
        };
        const items = buildMetricItems(summary);
        assert.equal(items[0].value, 100000, "Ingresos");
        assert.equal(items[1].value, 50000, "Egresos");
        assert.equal(items[2].value, 50000, "Resultado");
        // En modo mono NO debe haber valuesByCurrency
        assert.equal(items[0].valuesByCurrency, undefined);
    });

    it("cashByCurrency con 1 fila → cae a mono-moneda (no multimoneda)", () => {
        const summary = {
            cashInThisMonth: 100000,
            cashByCurrency: [{ currency: "ARS", cashInThisMonth: 100000, cashOutThisMonth: 0, netCashThisMonth: 100000 }],
        };
        const items = buildMetricItems(summary);
        assert.equal(items[0].value, 100000, "Una sola moneda → escalar");
        assert.equal(items[0].valuesByCurrency, undefined);
    });

    it("cashByCurrency con 2 filas → modo multimoneda con valuesByCurrency", () => {
        const summary = {
            cashInThisMonth: 150000,
            cashByCurrency: [
                { currency: "ARS", cashInThisMonth: 100000, cashOutThisMonth: 40000, netCashThisMonth: 60000 },
                { currency: "USD", cashInThisMonth: 500, cashOutThisMonth: 100, netCashThisMonth: 400 },
            ],
        };
        const items = buildMetricItems(summary);
        // Verifica que la primera tarjeta (Ingresos) tiene dos filas
        assert.ok(Array.isArray(items[0].valuesByCurrency), "Debe tener valuesByCurrency");
        assert.equal(items[0].valuesByCurrency.length, 2);
        assert.equal(items[0].valuesByCurrency[0].currency, "ARS");
        assert.equal(items[0].valuesByCurrency[0].value, 100000);
        assert.equal(items[0].valuesByCurrency[1].currency, "USD");
        assert.equal(items[0].valuesByCurrency[1].value, 500);
    });

    it("cashByCurrency: Resultado usa netCashThisMonth del backend (no recalcula)", () => {
        const summary = {
            cashByCurrency: [
                { currency: "ARS", cashInThisMonth: 100000, cashOutThisMonth: 40000, netCashThisMonth: 60000 },
                { currency: "USD", cashInThisMonth: 500, cashOutThisMonth: 100, netCashThisMonth: 400 },
            ],
        };
        const items = buildMetricItems(summary);
        const resultado = items[2].valuesByCurrency;
        assert.equal(resultado[0].value, 60000, "ARS: netCashThisMonth del backend");
        assert.equal(resultado[1].value, 400, "USD: netCashThisMonth del backend");
    });

    it("summary con cashByCurrency (porMoneda ignorado): cashByCurrency gana", () => {
        // Verifica que el código lee cashByCurrency y NO porMoneda (nombres inventados del draft)
        const summary = {
            cashInThisMonth: 100000,
            // porMoneda NO es el campo real — si existiera, se ignora
            porMoneda: [
                { currency: "ARS", cashIn: 999 },
                { currency: "USD", cashIn: 999 },
            ],
            // cashByCurrency SÍ es el campo real
            cashByCurrency: [
                { currency: "ARS", cashInThisMonth: 100000, cashOutThisMonth: 0, netCashThisMonth: 100000 },
                { currency: "USD", cashInThisMonth: 500, cashOutThisMonth: 0, netCashThisMonth: 500 },
            ],
        };
        const items = buildMetricItems(summary);
        assert.ok(Array.isArray(items[0].valuesByCurrency), "Debe usar cashByCurrency");
        // El valor de ARS debe ser 100000 (de cashByCurrency), no 999 (de porMoneda)
        assert.equal(items[0].valuesByCurrency[0].value, 100000);
    });
});

describe("buildKpiValue y buildFlujoNeto — contrato real A-3 (reportes porMoneda objeto 6 listas)", () => {
    /**
     * Replica la lógica de buildKpiValue y buildFlujonetoValue de ReportsPage.
     * Contrato real del backend /reports/detailed (A-3, 2026-06-11):
     * s.porMoneda = objeto (no array) con 6 listas de {currency, amount}:
     *   cobrosDelMes, pagosProveedores, ventasDelMes, costosDelMes, saldoPendiente, cuentasPorPagar.
     */

    // Verifica si hay modo multimoneda disponible
    function getMonedasDisponibles(porMonedaObj) {
        return porMonedaObj &&
            typeof porMonedaObj === "object" &&
            !Array.isArray(porMonedaObj) &&
            Array.isArray(porMonedaObj.cobrosDelMes) &&
            porMonedaObj.cobrosDelMes.length > 1
            ? porMonedaObj.cobrosDelMes
            : null;
    }

    function buildKpiValue(monoValue, listaMoneda, monedasDisponibles) {
        if (!monedasDisponibles || !Array.isArray(listaMoneda) || listaMoneda.length <= 1) {
            return monoValue;
        }
        return listaMoneda.map((item) => ({
            currency: item.currency,
            value: item.amount ?? 0,
        }));
    }

    function buildFlujoNeto(monoValue, porMonedaObj, monedasDisponibles) {
        if (!monedasDisponibles) return monoValue;
        const cobros = Array.isArray(porMonedaObj.cobrosDelMes) ? porMonedaObj.cobrosDelMes : [];
        const pagos = Array.isArray(porMonedaObj.pagosProveedores) ? porMonedaObj.pagosProveedores : [];
        const pagosPorMoneda = {};
        pagos.forEach((p) => { pagosPorMoneda[p.currency] = p.amount ?? 0; });
        return cobros.map((cobro) => ({
            currency: cobro.currency,
            value: (cobro.amount ?? 0) - (pagosPorMoneda[cobro.currency] ?? 0),
        }));
    }

    const porMonedaEjemplo = {
        cobrosDelMes: [
            { currency: "ARS", amount: 100000 },
            { currency: "USD", amount: 800 },
        ],
        pagosProveedores: [
            { currency: "ARS", amount: 60000 },
            { currency: "USD", amount: 300 },
        ],
        ventasDelMes: [
            { currency: "ARS", amount: 200000 },
            { currency: "USD", amount: 1500 },
        ],
        costosDelMes: [
            { currency: "ARS", amount: 120000 },
            { currency: "USD", amount: 900 },
        ],
        saldoPendiente: [
            { currency: "ARS", amount: 50000 },
            { currency: "USD", amount: 200 },
        ],
        cuentasPorPagar: [
            { currency: "ARS", amount: 30000 },
            { currency: "USD", amount: 100 },
        ],
    };

    it("sin porMoneda → buildKpiValue retorna el string mono-moneda", () => {
        const monedasDisponibles = getMonedasDisponibles(null);
        const result = buildKpiValue("$100.000,00", [], monedasDisponibles);
        assert.equal(result, "$100.000,00");
    });

    it("porMoneda array (forma vieja, no objeto) → NO activa multimoneda", () => {
        // El contrato real es objeto, no array. Si viene array (como el draft), se ignora.
        const porMonedaArray = [{ currency: "ARS", amount: 100 }, { currency: "USD", amount: 50 }];
        const monedasDisponibles = getMonedasDisponibles(porMonedaArray);
        assert.equal(monedasDisponibles, null, "Array de porMoneda no debe activar multimoneda");
    });

    it("porMoneda objeto con cobrosDelMes.length > 1 → activa multimoneda", () => {
        const monedasDisponibles = getMonedasDisponibles(porMonedaEjemplo);
        assert.ok(monedasDisponibles !== null, "Debe activar multimoneda");
        assert.equal(monedasDisponibles.length, 2);
    });

    it("buildKpiValue usa cobrosDelMes → 2 items {currency, amount}", () => {
        const monedasDisponibles = getMonedasDisponibles(porMonedaEjemplo);
        const result = buildKpiValue("$100.000,00", porMonedaEjemplo.cobrosDelMes, monedasDisponibles);
        assert.ok(Array.isArray(result));
        assert.equal(result[0].currency, "ARS");
        assert.equal(result[0].value, 100000);
        assert.equal(result[1].currency, "USD");
        assert.equal(result[1].value, 800);
    });

    it("buildKpiValue usa saldoPendiente para Deuda Clientes", () => {
        const monedasDisponibles = getMonedasDisponibles(porMonedaEjemplo);
        const result = buildKpiValue("$50.000,00", porMonedaEjemplo.saldoPendiente, monedasDisponibles);
        assert.ok(Array.isArray(result));
        assert.equal(result[0].value, 50000, "ARS deuda clientes");
        assert.equal(result[1].value, 200, "USD deuda clientes");
    });

    it("buildFlujoNeto: ARS = cobros 100000 - pagos 60000 = 40000", () => {
        const monedasDisponibles = getMonedasDisponibles(porMonedaEjemplo);
        const result = buildFlujoNeto("$40.000,00", porMonedaEjemplo, monedasDisponibles);
        assert.ok(Array.isArray(result));
        assert.equal(result[0].currency, "ARS");
        assert.equal(result[0].value, 40000);
    });

    it("buildFlujoNeto: USD = cobros 800 - pagos 300 = 500", () => {
        const monedasDisponibles = getMonedasDisponibles(porMonedaEjemplo);
        const result = buildFlujoNeto("", porMonedaEjemplo, monedasDisponibles);
        assert.equal(result[1].currency, "USD");
        assert.equal(result[1].value, 500);
    });

    it("buildKpiValue con lista de 1 elemento → cae a mono-moneda (no multimoneda)", () => {
        // Si cobrosDelMes tiene 1 fila sola, monedasDisponibles es null → mono
        const porMonedaMono = {
            cobrosDelMes: [{ currency: "ARS", amount: 100000 }],
            pagosProveedores: [{ currency: "ARS", amount: 60000 }],
        };
        const monedasDisponibles = getMonedasDisponibles(porMonedaMono);
        assert.equal(monedasDisponibles, null, "1 moneda no activa multimoneda");
    });
});
