import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
    resolverMonedaPrincipalProveedor,
    calcularEquivalenteProveedor,
    construirPayloadPagoProveedor,
    ordenarBloquesPesosPrimero,
    debeMostrarseEnGrisNeutro,
    aplanarReembolsosPendientesPorMoneda,
    validarFormularioReembolsoRecibido,
    construirTextoCuentaReembolso,
    filtrarServiciosPorMonedaDePago,
    hayServiciosDelProveedorEnReserva,
    armarFilasDeudaPorReserva,
    agruparServiciosPorReserva,
    construirDetalleFilaDeuda,
    construirMensajeExitoPago,
} from "./supplierPageLogic.js";
import { formatCurrency } from "../../../lib/utils.js";

// ─── resolverMonedaPrincipalProveedor ────────────────────────────────────────

describe("resolverMonedaPrincipalProveedor", () => {
    it("retorna ARS cuando el array esta vacio", () => {
        assert.equal(resolverMonedaPrincipalProveedor([]), "ARS");
    });

    it("retorna ARS cuando el argumento es null", () => {
        assert.equal(resolverMonedaPrincipalProveedor(null), "ARS");
    });

    it("retorna ARS cuando el argumento es undefined", () => {
        assert.equal(resolverMonedaPrincipalProveedor(undefined), "ARS");
    });

    it("retorna la primera moneda con saldo positivo", () => {
        const balances = [
            { currency: "USD", balance: 500 },
            { currency: "ARS", balance: 10000 },
        ];
        assert.equal(resolverMonedaPrincipalProveedor(balances), "USD");
    });

    it("retorna ARS primero si tiene deuda aunque USD tambien tenga", () => {
        const balances = [
            { currency: "ARS", balance: 5000 },
            { currency: "USD", balance: 300 },
        ];
        assert.equal(resolverMonedaPrincipalProveedor(balances), "ARS");
    });

    it("retorna la primera de la lista cuando todos estan en cero", () => {
        const balances = [
            { currency: "ARS", balance: 0 },
            { currency: "USD", balance: 0 },
        ];
        assert.equal(resolverMonedaPrincipalProveedor(balances), "ARS");
    });

    it("retorna la primera de la lista cuando todos estan a favor (balance negativo)", () => {
        const balances = [
            { currency: "ARS", balance: -100 },
            { currency: "USD", balance: -200 },
        ];
        assert.equal(resolverMonedaPrincipalProveedor(balances), "ARS");
    });

    it("funciona con una sola moneda", () => {
        assert.equal(
            resolverMonedaPrincipalProveedor([{ currency: "USD", balance: 300 }]),
            "USD"
        );
    });

    it("ignora monedas con balance undefined o null, prefiere las con deuda real", () => {
        const balances = [
            { currency: "ARS", balance: null },
            { currency: "USD", balance: 500 },
        ];
        // null no es > 0, así que USD gana (tiene 500)
        assert.equal(resolverMonedaPrincipalProveedor(balances), "USD");
    });
});

// ─── calcularEquivalenteProveedor ────────────────────────────────────────────

describe("calcularEquivalenteProveedor", () => {
    it("retorna null cuando las monedas son iguales (ARS → ARS)", () => {
        assert.equal(calcularEquivalenteProveedor("1000", "1200", "ARS", "ARS"), null);
    });

    it("retorna null cuando las monedas son iguales (USD → USD)", () => {
        assert.equal(calcularEquivalenteProveedor("500", "1200", "USD", "USD"), null);
    });

    it("retorna null cuando el TC esta vacio", () => {
        assert.equal(calcularEquivalenteProveedor("1000", "", "ARS", "USD"), null);
    });

    it("retorna null cuando el monto esta vacio", () => {
        assert.equal(calcularEquivalenteProveedor("", "1200", "ARS", "USD"), null);
    });

    it("retorna null cuando el TC es cero", () => {
        assert.equal(calcularEquivalenteProveedor("1000", "0", "ARS", "USD"), null);
    });

    it("retorna null cuando el TC es negativo", () => {
        assert.equal(calcularEquivalenteProveedor("1000", "-1200", "ARS", "USD"), null);
    });

    it("retorna null cuando monedaCobro es undefined", () => {
        assert.equal(calcularEquivalenteProveedor("1000", "1200", undefined, "USD"), null);
    });

    it("ARS → USD: divide por el TC (1 USD = $1200, pago $1200 → cancelo US$1)", () => {
        assert.equal(calcularEquivalenteProveedor("1200", "1200", "ARS", "USD"), 1);
    });

    it("ARS → USD: pago $2400 con TC 1200 → cancelo US$2", () => {
        assert.equal(calcularEquivalenteProveedor("2400", "1200", "ARS", "USD"), 2);
    });

    it("USD → ARS: multiplica por el TC (1 USD = $1200, pago US$1 → cancelo $1200)", () => {
        assert.equal(calcularEquivalenteProveedor("1", "1200", "USD", "ARS"), 1200);
    });

    it("USD → ARS: pago US$2.5 con TC 1000 → cancelo $2500", () => {
        assert.equal(calcularEquivalenteProveedor("2.5", "1000", "USD", "ARS"), 2500);
    });

    it("retorna null para combinaciones de moneda no soportadas", () => {
        // EUR/USD no tiene formula definida
        assert.equal(calcularEquivalenteProveedor("100", "1.1", "EUR", "USD"), null);
    });
});

// ─── construirPayloadPagoProveedor ───────────────────────────────────────────

describe("construirPayloadPagoProveedor", () => {
    // Datos base reutilizables para la mayoria de los tests
    const camposBase = {
        monto: "1000",
        monedaPago: "ARS",
        metodo: "Transfer",
        fecha: "2026-06-27",
        referencia: "",
        notas: "",
        reservaId: null,
        serviceRecordKind: null,
        servicePublicId: null,
        esCruzado: false,
        saldoImputado: "ARS",
        tipoCambio: "",
        fuenteTC: 5,
        fechaTC: "2026-06-27",
        montoEquivalente: null,
    };

    it("pago simple: NO incluye ningun campo de tipo de cambio", () => {
        const payload = construirPayloadPagoProveedor(camposBase);
        // Estos campos NO deben estar en el payload cuando esCruzado=false.
        // Si se enviaran con valores null/undefined el backend los rechaza.
        assert.equal("imputedCurrency" in payload, false);
        assert.equal("exchangeRate" in payload, false);
        assert.equal("exchangeRateSource" in payload, false);
        assert.equal("exchangeRateAt" in payload, false);
        assert.equal("imputedAmount" in payload, false);
    });

    it("pago simple: incluye amount, currency, method y paidAt correctamente tipados", () => {
        const payload = construirPayloadPagoProveedor(camposBase);
        assert.equal(payload.amount, 1000);
        assert.equal(typeof payload.amount, "number");
        assert.equal(payload.currency, "ARS");
        assert.equal(payload.method, "Transfer");
        // paidAt debe ser ISO string (incluye T y Z)
        assert.ok(payload.paidAt.includes("T"), `paidAt debe ser ISO: "${payload.paidAt}"`);
    });

    it("referencia vacia se convierte a null (no manda string vacio al backend)", () => {
        const payload = construirPayloadPagoProveedor({ ...camposBase, referencia: "  " });
        assert.equal(payload.reference, null);
    });

    it("notas vacias se convierten a null", () => {
        const payload = construirPayloadPagoProveedor({ ...camposBase, notas: "  " });
        assert.equal(payload.notes, null);
    });

    it("referencia con contenido se manda como string limpio", () => {
        const payload = construirPayloadPagoProveedor({ ...camposBase, referencia: "  TRF-123  " });
        assert.equal(payload.reference, "TRF-123");
    });

    it("pago simple con reserva y servicio imputados", () => {
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            reservaId: "uuid-reserva-123",
            serviceRecordKind: "hotel",
            servicePublicId: "uuid-service-456",
        });
        assert.equal(payload.reservaId, "uuid-reserva-123");
        assert.equal(payload.serviceRecordKind, "hotel");
        assert.equal(payload.servicePublicId, "uuid-service-456");
    });

    it("pago simple sin imputacion: reservaId/serviceRecordKind/servicePublicId son null", () => {
        const payload = construirPayloadPagoProveedor(camposBase);
        assert.equal(payload.reservaId, null);
        assert.equal(payload.serviceRecordKind, null);
        assert.equal(payload.servicePublicId, null);
    });

    it("cargo facturado aparte: envia el PublicId puntual que queda liquidado", () => {
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            settlesOperatorChargePublicId: "uuid-cargo-789",
        });
        assert.equal(payload.settlesOperatorChargePublicId, "uuid-cargo-789");
    });

    it("pago cruzado ARS→USD: SÍ incluye los 5 campos de tipo de cambio", () => {
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            monedaPago: "ARS",
            esCruzado: true,
            saldoImputado: "USD",
            tipoCambio: "1200",
            fuenteTC: 5,
            fechaTC: "2026-06-27",
            montoEquivalente: 0.833,
        });
        // Los 5 campos deben estar presentes
        assert.equal("imputedCurrency" in payload, true);
        assert.equal("exchangeRate" in payload, true);
        assert.equal("exchangeRateSource" in payload, true);
        assert.equal("exchangeRateAt" in payload, true);
        assert.equal("imputedAmount" in payload, true);
        // Valores correctos
        assert.equal(payload.imputedCurrency, "USD");
        assert.equal(payload.exchangeRate, 1200);
        assert.equal(payload.imputedAmount, 0.833);
    });

    it("pago cruzado: exchangeRateSource es siempre INT aunque el <select> lo devuelva como string", () => {
        // El <select> HTML devuelve strings; el backend espera un int para el enum ExchangeRateSource.
        // construirPayloadPagoProveedor debe convertir con Number().
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            esCruzado: true,
            saldoImputado: "USD",
            tipoCambio: "1000",
            fuenteTC: "6", // string que viene del <select>
            fechaTC: "2026-06-27",
            montoEquivalente: 0.5,
        });
        assert.strictEqual(typeof payload.exchangeRateSource, "number");
        assert.strictEqual(payload.exchangeRateSource, 6);
    });

    it("pago cruzado: exchangeRateAt es ISO string de fechaTC", () => {
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            esCruzado: true,
            saldoImputado: "USD",
            tipoCambio: "1200",
            fuenteTC: 1,
            fechaTC: "2026-06-15",
            montoEquivalente: 1,
        });
        assert.ok(
            payload.exchangeRateAt.startsWith("2026-06-15"),
            `exchangeRateAt debe arrancar con la fecha ingresada: "${payload.exchangeRateAt}"`
        );
    });

    it("pago cruzado USD→ARS: imputedCurrency es ARS", () => {
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            monedaPago: "USD",
            esCruzado: true,
            saldoImputado: "ARS",
            tipoCambio: "1200",
            fuenteTC: 5,
            fechaTC: "2026-06-27",
            montoEquivalente: 1200,
        });
        assert.equal(payload.imputedCurrency, "ARS");
        assert.equal(payload.currency, "USD");
    });

    it("pago cruzado: tambien incluye los campos base (amount, currency, method, etc.)", () => {
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            monto: "500",
            monedaPago: "ARS",
            metodo: "Cash",
            esCruzado: true,
            saldoImputado: "USD",
            tipoCambio: "1000",
            fuenteTC: 5,
            fechaTC: "2026-06-27",
            montoEquivalente: 0.5,
        });
        // Los campos base siguen presentes incluso en pago cruzado
        assert.equal(payload.amount, 500);
        assert.equal(payload.currency, "ARS");
        assert.equal(payload.method, "Cash");
    });
});

// ─── ordenarBloquesPesosPrimero (Fase D — encabezado de los "dos números") ──

describe("ordenarBloquesPesosPrimero", () => {
    it("pone ARS antes que USD aunque lleguen en el orden contrario", () => {
        const bloques = [{ currency: "USD" }, { currency: "ARS" }];
        const ordenados = ordenarBloquesPesosPrimero(bloques);
        assert.deepEqual(ordenados.map((b) => b.currency), ["ARS", "USD"]);
    });

    it("mantiene el orden si ya viene ARS primero", () => {
        const bloques = [{ currency: "ARS" }, { currency: "USD" }];
        const ordenados = ordenarBloquesPesosPrimero(bloques);
        assert.deepEqual(ordenados.map((b) => b.currency), ["ARS", "USD"]);
    });

    it("con una sola moneda no rompe nada", () => {
        assert.deepEqual(
            ordenarBloquesPesosPrimero([{ currency: "USD" }]).map((b) => b.currency),
            ["USD"]
        );
    });

    it("array vacio retorna array vacio", () => {
        assert.deepEqual(ordenarBloquesPesosPrimero([]), []);
    });

    it("null/undefined no lanzan, retornan array vacio", () => {
        assert.deepEqual(ordenarBloquesPesosPrimero(null), []);
        assert.deepEqual(ordenarBloquesPesosPrimero(undefined), []);
    });

    it("no muta el array original", () => {
        const original = [{ currency: "USD" }, { currency: "ARS" }];
        const copia = JSON.stringify(original);
        ordenarBloquesPesosPrimero(original);
        assert.equal(JSON.stringify(original), copia);
    });

    it("una moneda desconocida (ej. EUR) siempre queda antes que USD", () => {
        const bloques = [{ currency: "USD" }, { currency: "EUR" }];
        const ordenados = ordenarBloquesPesosPrimero(bloques);
        assert.equal(ordenados[0].currency, "EUR");
        assert.equal(ordenados[1].currency, "USD");
    });
});

// ─── debeMostrarseEnGrisNeutro (Fase D — color de los recuadros del encabezado) ──

describe("debeMostrarseEnGrisNeutro", () => {
    it("sin permiso de ver costos, siempre gris aunque el monto sea grande", () => {
        assert.equal(debeMostrarseEnGrisNeutro(1250000, false), true);
    });

    it("sin permiso de ver costos, gris incluso con monto 0", () => {
        assert.equal(debeMostrarseEnGrisNeutro(0, false), true);
    });

    it("con permiso y monto positivo, NO va en gris (usa su color propio)", () => {
        assert.equal(debeMostrarseEnGrisNeutro(1250000, true), false);
    });

    it("con permiso y monto exactamente 0, va en gris", () => {
        assert.equal(debeMostrarseEnGrisNeutro(0, true), true);
    });

    it("con permiso y monto null/undefined, se trata como 0 -> gris", () => {
        assert.equal(debeMostrarseEnGrisNeutro(null, true), true);
        assert.equal(debeMostrarseEnGrisNeutro(undefined, true), true);
    });

    it("con permiso y un resto de redondeo menor a medio centavo, se trata como cero", () => {
        assert.equal(debeMostrarseEnGrisNeutro(0.001, true), true);
    });

    it("con permiso y un monto negativo distinto de cero, NO va en gris", () => {
        // No debería pasar en la práctica (los tres campos son siempre >= 0), pero la función
        // no debe asumir signo: solo mira si el valor absoluto es ~0.
        assert.equal(debeMostrarseEnGrisNeutro(-5, true), false);
    });
});

// ─── aplanarReembolsosPendientesPorMoneda (§4 — selector "Registrar reembolso recibido") ──

describe("aplanarReembolsosPendientesPorMoneda", () => {
    it("retorna array vacío si el argumento es null, undefined o no-array", () => {
        assert.deepEqual(aplanarReembolsosPendientesPorMoneda(null), []);
        assert.deepEqual(aplanarReembolsosPendientesPorMoneda(undefined), []);
        assert.deepEqual(aplanarReembolsosPendientesPorMoneda("no-es-array"), []);
    });

    it("retorna array vacío si items es un array vacío", () => {
        assert.deepEqual(aplanarReembolsosPendientesPorMoneda([]), []);
    });

    it("una cancelación con UNA moneda estimada genera UNA fila", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-1",
                numeroReserva: "R-1050",
                clienteNombre: "Juan Pérez",
                amountsMasked: false,
                estimatedRefundsByCurrency: [{ currency: "ARS", estimatedAmount: 80000 }],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas.length, 1);
        assert.equal(filas[0].key, "bc-1-ARS");
        assert.equal(filas[0].bookingCancellationPublicId, "bc-1");
        assert.equal(filas[0].numeroReserva, "R-1050");
        assert.equal(filas[0].clienteNombre, "Juan Pérez");
        assert.equal(filas[0].currency, "ARS");
        assert.equal(filas[0].estimatedAmount, 80000);
        assert.equal(filas[0].amountsMasked, false);
    });

    it("una cancelación con DOS monedas estimadas genera DOS filas separadas", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-2",
                numeroReserva: "R-2000",
                clienteNombre: "María López",
                amountsMasked: false,
                estimatedRefundsByCurrency: [
                    { currency: "ARS", estimatedAmount: 50000 },
                    { currency: "USD", estimatedAmount: 300 },
                ],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas.length, 2);
        assert.deepEqual(filas.map((f) => f.key), ["bc-2-ARS", "bc-2-USD"]);
        assert.equal(filas[0].currency, "ARS");
        assert.equal(filas[1].currency, "USD");
    });

    it("varias cancelaciones producen filas independientes, cada una con su propia key", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-1",
                numeroReserva: "R-1",
                estimatedRefundsByCurrency: [{ currency: "ARS", estimatedAmount: 1000 }],
            },
            {
                bookingCancellationPublicId: "bc-2",
                numeroReserva: "R-2",
                estimatedRefundsByCurrency: [{ currency: "ARS", estimatedAmount: 2000 }],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas.length, 2);
        assert.notEqual(filas[0].key, filas[1].key);
    });

    it("una cancelación sin estimatedRefundsByCurrency (undefined) no genera filas", () => {
        const items = [{ bookingCancellationPublicId: "bc-3", numeroReserva: "R-3" }];
        assert.deepEqual(aplanarReembolsosPendientesPorMoneda(items), []);
    });

    it("respeta amountsMasked=true por fila (viene del item completo, no de la moneda)", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-4",
                numeroReserva: "R-4",
                amountsMasked: true,
                estimatedRefundsByCurrency: [{ currency: "ARS", estimatedAmount: 0 }],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas[0].amountsMasked, true);
        assert.equal(filas[0].estimatedAmount, 0);
    });

    it("usa cadena vacía como fallback cuando numeroReserva/clienteNombre son null", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-5",
                numeroReserva: null,
                clienteNombre: null,
                estimatedRefundsByCurrency: [{ currency: "ARS", estimatedAmount: 100 }],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas[0].numeroReserva, "");
        assert.equal(filas[0].clienteNombre, "");
    });

    // ─── Cuenta del operador (2026-07-03): campos nuevos de decisiones 1/4/RESTOS ──

    it("copia paidToOperator/penaltyRetained/amountReceived/zeroRefundReason DE LA LÍNEA (por moneda)", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-6",
                numeroReserva: "R-6",
                estimatedRefundsByCurrency: [
                    {
                        currency: "USD",
                        estimatedAmount: 400,
                        paidToOperator: 500,
                        penaltyRetained: 100,
                        amountReceived: 0,
                        zeroRefundReason: null,
                    },
                ],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas[0].paidToOperator, 500);
        assert.equal(filas[0].penaltyRetained, 100);
        assert.equal(filas[0].amountReceived, 0);
        assert.equal(filas[0].zeroRefundReason, null);
    });

    it("copia penaltyPendingConfirmation/rowStatus/canRegisterRefund/reservaPublicId DEL ITEM (no de la línea)", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-7",
                reservaPublicId: "reserva-7",
                penaltyPendingConfirmation: true,
                rowStatus: 4,
                canRegisterRefund: false,
                estimatedRefundsByCurrency: [
                    { currency: "ARS", estimatedAmount: 0 },
                    { currency: "USD", estimatedAmount: 0 },
                ],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas.length, 2);
        for (const fila of filas) {
            assert.equal(fila.reservaPublicId, "reserva-7");
            assert.equal(fila.penaltyPendingConfirmation, true);
            assert.equal(fila.rowStatus, 4);
            assert.equal(fila.canRegisterRefund, false);
        }
    });

    it("campos nuevos ausentes en el item (DTO viejo) caen a defaults conservadores", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-8",
                numeroReserva: "R-8",
                estimatedRefundsByCurrency: [{ currency: "ARS", estimatedAmount: 1000 }],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas[0].paidToOperator, 0);
        assert.equal(filas[0].penaltyRetained, 0);
        assert.equal(filas[0].amountReceived, 0);
        assert.equal(filas[0].zeroRefundReason, null);
        assert.equal(filas[0].penaltyPendingConfirmation, false);
        assert.equal(filas[0].rowStatus, 0);
        assert.equal(filas[0].canRegisterRefund, false);
        assert.equal(filas[0].reservaPublicId, "");
    });
});

// ─── construirTextoCuentaReembolso (decisiones 1 y 4, spec 2026-07-03) ───────

describe("construirTextoCuentaReembolso", () => {
    it("null/undefined → string vacío (no rompe)", () => {
        assert.equal(construirTextoCuentaReembolso(null), "");
        assert.equal(construirTextoCuentaReembolso(undefined), "");
    });

    it("amountsMasked=true → '—' (nunca el motivo ni la cuenta, aunque vengan)", () => {
        const fila = {
            amountsMasked: true,
            estimatedAmount: 0,
            paidToOperator: 500,
            penaltyRetained: 100,
            amountReceived: 0,
            zeroRefundReason: "NothingPaidToOperator",
            currency: "USD",
        };
        assert.equal(construirTextoCuentaReembolso(fila), "—");
    });

    it("decisión 1 (P3=A): copy EXACTO de la spec sin restos", () => {
        const fila = {
            amountsMasked: false,
            estimatedAmount: 400,
            paidToOperator: 500,
            penaltyRetained: 100,
            amountReceived: 0,
            currency: "USD",
        };
        const texto = construirTextoCuentaReembolso(fila);
        assert.match(texto, /^Pagaste US\$500,00 − Multa del operador US\$100,00 = te devuelven US\$400,00 \(estimado\)\.$/);
    });

    it("con restos (AmountReceived > 0): agrega '− Ya devuelto X' para que la cuenta cierre", () => {
        const fila = {
            amountsMasked: false,
            estimatedAmount: 350,
            paidToOperator: 500,
            penaltyRetained: 100,
            amountReceived: 50,
            currency: "USD",
        };
        const texto = construirTextoCuentaReembolso(fila);
        assert.match(texto, /Ya devuelto US\$50,00/);
        // El orden respeta la spec: Pagado, Multa, Ya devuelto, y recién el resultado.
        const idxPagaste = texto.indexOf("Pagaste");
        const idxMulta = texto.indexOf("Multa del operador");
        const idxDevuelto = texto.indexOf("Ya devuelto");
        const idxTeDevuelven = texto.indexOf("te devuelven");
        assert.ok(idxPagaste < idxMulta && idxMulta < idxDevuelto && idxDevuelto < idxTeDevuelven);
    });

    it("sin restos (AmountReceived = 0): NO menciona 'Ya devuelto'", () => {
        const fila = {
            amountsMasked: false,
            estimatedAmount: 400,
            paidToOperator: 500,
            penaltyRetained: 100,
            amountReceived: 0,
            currency: "USD",
        };
        assert.ok(!construirTextoCuentaReembolso(fila).includes("Ya devuelto"));
    });

    it("decisión 4 (P4=A): estimado 0 + NothingPaidToOperator → motivo exacto de la spec", () => {
        const fila = {
            amountsMasked: false,
            estimatedAmount: 0,
            zeroRefundReason: "NothingPaidToOperator",
            currency: "ARS",
        };
        assert.equal(
            construirTextoCuentaReembolso(fila),
            "Todavía no le pagaste nada al operador por este viaje."
        );
    });

    it("decisión 4 (P4=A): estimado 0 + PenaltyCoversAll → motivo exacto de la spec", () => {
        const fila = {
            amountsMasked: false,
            estimatedAmount: 0,
            zeroRefundReason: "PenaltyCoversAll",
            currency: "ARS",
        };
        assert.equal(
            construirTextoCuentaReembolso(fila),
            "No hay nada para devolver: la multa del operador se quedó con todo lo que le pagaste."
        );
    });

    it("decisión 4 (P4=A): estimado 0 + FullyRefunded → texto sugerido por la spec", () => {
        const fila = {
            amountsMasked: false,
            estimatedAmount: 0,
            zeroRefundReason: "FullyRefunded",
            currency: "ARS",
        };
        assert.equal(construirTextoCuentaReembolso(fila), "Ya te devolvió todo por este viaje.");
    });

    it("estimado 0 sin zeroRefundReason reconocido → fallback neutro (no rompe, no inventa)", () => {
        const fila = { amountsMasked: false, estimatedAmount: 0, zeroRefundReason: null, currency: "ARS" };
        const texto = construirTextoCuentaReembolso(fila);
        assert.equal(typeof texto, "string");
        assert.ok(texto.length > 0);
    });

    it("nunca expone el código crudo del enum (NothingPaidToOperator/PenaltyCoversAll/FullyRefunded) en el texto visible", () => {
        for (const reason of ["NothingPaidToOperator", "PenaltyCoversAll", "FullyRefunded"]) {
            const texto = construirTextoCuentaReembolso({ amountsMasked: false, estimatedAmount: 0, zeroRefundReason: reason, currency: "ARS" });
            assert.ok(!texto.includes(reason), `El texto no debe exponer el código crudo '${reason}': ${texto}`);
        }
    });

    it("todos los montos de la cuenta usan la MISMA moneda de la fila (nunca mezcla ARS/USD)", () => {
        const filaUSD = { amountsMasked: false, estimatedAmount: 400, paidToOperator: 500, penaltyRetained: 100, amountReceived: 50, currency: "USD" };
        const texto = construirTextoCuentaReembolso(filaUSD);
        // Los 3 montos (pagado, multa, ya devuelto, resultado) están en US$; ninguno "se escapó" a ARS.
        const cantidadUSD = (texto.match(/US\$/g) || []).length;
        assert.equal(cantidadUSD, 4, `Esperaba 4 montos en US$ (pagado/multa/devuelto/resultado): ${texto}`);
    });
});

// ─── validarFormularioReembolsoRecibido (§4 — validación local antes de llamar al backend) ──

describe("validarFormularioReembolsoRecibido", () => {
    const filaValida = {
        key: "bc-1-ARS",
        bookingCancellationPublicId: "bc-1",
        currency: "ARS",
        estimatedAmount: 80000,
        amountsMasked: false,
    };

    it("exige elegir un reembolso pendiente (filaSeleccionada null)", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: null,
            monto: "1000",
            fecha: "2026-07-01",
        });
        assert.match(mensaje, /Elegí a qué reembolso pendiente/);
    });

    it("rechaza monto vacío", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida,
            monto: "",
            fecha: "2026-07-01",
        });
        assert.match(mensaje, /mayor a 0/);
    });

    it("rechaza monto 0 o negativo", () => {
        assert.match(
            validarFormularioReembolsoRecibido({ filaSeleccionada: filaValida, monto: "0", fecha: "2026-07-01" }),
            /mayor a 0/
        );
        assert.match(
            validarFormularioReembolsoRecibido({ filaSeleccionada: filaValida, monto: "-50", fecha: "2026-07-01" }),
            /mayor a 0/
        );
    });

    it("rechaza monto no numérico", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida,
            monto: "abc",
            fecha: "2026-07-01",
        });
        assert.match(mensaje, /mayor a 0/);
    });

    it("rechaza un monto mayor al estimado cuando el estimado es conocido", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida,
            monto: "90000",
            fecha: "2026-07-01",
        });
        assert.match(mensaje, /no puede superar el estimado/);
    });

    it("acepta un monto igual al estimado (límite exacto)", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida,
            monto: "80000",
            fecha: "2026-07-01",
        });
        assert.equal(mensaje, null);
    });

    it("NO compara contra el estimado cuando amountsMasked=true (evita error falso)", () => {
        const filaEnmascarada = { ...filaValida, amountsMasked: true, estimatedAmount: 0 };
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaEnmascarada,
            monto: "999999",
            fecha: "2026-07-01",
        });
        assert.equal(mensaje, null);
    });

    it("exige fecha", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida,
            monto: "1000",
            fecha: "",
        });
        assert.match(mensaje, /fecha es obligatoria/);
    });

    it("formulario completo y válido no genera error", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida,
            monto: "50000",
            fecha: "2026-07-01",
        });
        assert.equal(mensaje, null);
    });

    // ─── RESTOS (2026-07-03): defensa en profundidad — canRegisterRefund ────────

    it("rechaza una fila con canRegisterRefund=false (defensa en profundidad, el selector ya la deshabilita)", () => {
        const filaNoRegistrable = { ...filaValida, canRegisterRefund: false };
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaNoRegistrable,
            monto: "50000",
            fecha: "2026-07-01",
        });
        assert.match(mensaje, /todavía no se puede registrar/i);
    });

    it("acepta una fila con canRegisterRefund=true", () => {
        const filaRegistrable = { ...filaValida, canRegisterRefund: true };
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaRegistrable,
            monto: "50000",
            fecha: "2026-07-01",
        });
        assert.equal(mensaje, null);
    });

    it("acepta una fila SIN canRegisterRefund (DTO viejo, undefined) — degradación segura", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida, // filaValida no trae canRegisterRefund
            monto: "50000",
            fecha: "2026-07-01",
        });
        assert.equal(mensaje, null);
    });
});

// ─── filtrarServiciosPorMonedaDePago (Tanda 1, contrato pantalla-motor, 2026-07-18) ──────
// Pre-chequeo (a): el selector de servicio solo lista los que coinciden con la moneda del pago.

describe("filtrarServiciosPorMonedaDePago", () => {
    it("devuelve solo los servicios en la moneda del pago", () => {
        const servicios = [
            { publicId: "s1", currency: "USD" },
            { publicId: "s2", currency: "ARS" },
            { publicId: "s3", currency: "USD" },
        ];
        const resultado = filtrarServiciosPorMonedaDePago(servicios, "USD");
        assert.deepEqual(resultado.map((s) => s.publicId), ["s1", "s3"]);
    });

    it("trata currency null/vacío como ARS (servicios legacy, ADR-021 §15.4)", () => {
        const servicios = [
            { publicId: "s1", currency: null },
            { publicId: "s2", currency: "" },
            { publicId: "s3", currency: "USD" },
        ];
        const resultado = filtrarServiciosPorMonedaDePago(servicios, "ARS");
        assert.deepEqual(resultado.map((s) => s.publicId), ["s1", "s2"]);
    });

    it("si cambia la moneda del pago, la lista se recalcula sola (misma entrada, otra moneda)", () => {
        const servicios = [
            { publicId: "s1", currency: "USD" },
            { publicId: "s2", currency: "ARS" },
        ];
        assert.deepEqual(filtrarServiciosPorMonedaDePago(servicios, "USD").map((s) => s.publicId), ["s1"]);
        assert.deepEqual(filtrarServiciosPorMonedaDePago(servicios, "ARS").map((s) => s.publicId), ["s2"]);
    });

    it("devuelve vacío cuando ningún servicio matchea la moneda", () => {
        const servicios = [{ publicId: "s1", currency: "ARS" }];
        assert.deepEqual(filtrarServiciosPorMonedaDePago(servicios, "USD"), []);
    });

    it("no rompe con entrada null/undefined", () => {
        assert.deepEqual(filtrarServiciosPorMonedaDePago(null, "ARS"), []);
        assert.deepEqual(filtrarServiciosPorMonedaDePago(undefined, "ARS"), []);
    });
});

// ─── hayServiciosDelProveedorEnReserva (Tanda 1, contrato pantalla-motor, 2026-07-18) ────
// Pre-chequeo (b): avisar ANTES de confirmar si la reserva elegida no tiene ningún
// servicio de este proveedor (mismo caso que el backend rechazaba con un 409 tardío).

describe("hayServiciosDelProveedorEnReserva", () => {
    it("true cuando hay al menos un servicio", () => {
        assert.equal(hayServiciosDelProveedorEnReserva([{ publicId: "s1" }]), true);
    });

    it("false cuando la lista está vacía", () => {
        assert.equal(hayServiciosDelProveedorEnReserva([]), false);
    });

    it("false con null/undefined (todavía no cargó o no hay reserva elegida)", () => {
        assert.equal(hayServiciosDelProveedorEnReserva(null), false);
        assert.equal(hayServiciosDelProveedorEnReserva(undefined), false);
    });
});

// ─── armarFilasDeudaPorReserva (rediseño 2026-07-20, Paso 1 "¿Qué estás pagando?") ────

describe("armarFilasDeudaPorReserva", () => {
    it("null/undefined/no-array no rompen, devuelven []", () => {
        assert.deepEqual(armarFilasDeudaPorReserva(null), []);
        assert.deepEqual(armarFilasDeudaPorReserva(undefined), []);
        assert.deepEqual(armarFilasDeudaPorReserva("no es un array"), []);
    });

    it("una reserva con una sola moneda con deuda genera una fila", () => {
        const reservas = [{
            reservaPublicId: "r1",
            numeroReserva: "F-2026-1051",
            fileName: "Familia Pérez",
            currencies: [{ currency: "ARS", balance: 45000 }],
        }];
        const filas = armarFilasDeudaPorReserva(reservas);
        assert.equal(filas.length, 1);
        assert.equal(filas[0].reservaPublicId, "r1");
        assert.equal(filas[0].numeroReserva, "F-2026-1051");
        assert.equal(filas[0].fileName, "Familia Pérez");
        assert.equal(filas[0].currency, "ARS");
        assert.equal(filas[0].balance, 45000);
    });

    it("una reserva con deuda en DOS monedas genera DOS filas", () => {
        const reservas = [{
            reservaPublicId: "r1",
            numeroReserva: "F-1",
            currencies: [
                { currency: "ARS", balance: 1000 },
                { currency: "USD", balance: 120 },
            ],
        }];
        const filas = armarFilasDeudaPorReserva(reservas);
        assert.equal(filas.length, 2);
        assert.deepEqual(filas.map((f) => f.currency), ["ARS", "USD"]);
    });

    it("una línea con balance 0 NO genera fila (no hay deuda que pagar)", () => {
        const reservas = [{
            reservaPublicId: "r1",
            currencies: [{ currency: "ARS", balance: 0 }],
        }];
        assert.deepEqual(armarFilasDeudaPorReserva(reservas), []);
    });

    it("una línea con balance negativo (saldo a favor de esa reserva) NO genera fila", () => {
        const reservas = [{
            reservaPublicId: "r1",
            currencies: [{ currency: "ARS", balance: -500 }],
        }];
        assert.deepEqual(armarFilasDeudaPorReserva(reservas), []);
    });

    it("un resto de redondeo menor a medio centavo se trata como cero (sin fila)", () => {
        const reservas = [{
            reservaPublicId: "r1",
            currencies: [{ currency: "ARS", balance: 0.001 }],
        }];
        assert.deepEqual(armarFilasDeudaPorReserva(reservas), []);
    });

    it("varias reservas producen filas independientes, cada una con sus propios datos", () => {
        const reservas = [
            { reservaPublicId: "r1", numeroReserva: "F-1", currencies: [{ currency: "ARS", balance: 1000 }] },
            { reservaPublicId: "r2", numeroReserva: "F-2", currencies: [{ currency: "USD", balance: 200 }] },
        ];
        const filas = armarFilasDeudaPorReserva(reservas);
        assert.equal(filas.length, 2);
        assert.equal(filas[0].numeroReserva, "F-1");
        assert.equal(filas[1].numeroReserva, "F-2");
    });

    it("numeroReserva/fileName ausentes caen a defaults seguros", () => {
        const reservas = [{ reservaPublicId: "r1", currencies: [{ currency: "ARS", balance: 500 }] }];
        const filas = armarFilasDeudaPorReserva(reservas);
        assert.equal(filas[0].numeroReserva, "Reserva");
        assert.equal(filas[0].fileName, null);
    });

    it("reserva sin currencies (undefined) no rompe, no genera filas", () => {
        assert.deepEqual(armarFilasDeudaPorReserva([{ reservaPublicId: "r1" }]), []);
    });

    // FIX bloqueante (review 2026-07-21, frontend-reviewer #2): sin cobranzas.see_cost el
    // backend enmascara Balance a 0 en TODAS las líneas por igual — filtrar por balance>0
    // dejaba la grilla siempre vacía para ese perfil, aunque hubiera deuda real, y el
    // cajero se quedaba sin poder elegir ninguna reserva (rompía la imputación, no solo
    // el número mostrado).
    it("puedeVerMontos=false: NO filtra por balance, incluye filas con balance 0 (enmascarado)", () => {
        const reservas = [{
            reservaPublicId: "r1",
            numeroReserva: "F-1",
            currencies: [{ currency: "ARS", balance: 0 }], // el backend manda 0 por masking, no porque esté saldada
        }];
        const filas = armarFilasDeudaPorReserva(reservas, { puedeVerMontos: false });
        assert.equal(filas.length, 1);
        assert.equal(filas[0].numeroReserva, "F-1");
        assert.equal(filas[0].balance, 0);
    });

    it("puedeVerMontos=false: también incluye una reserva con varias monedas, todas en 0", () => {
        const reservas = [{
            reservaPublicId: "r1",
            numeroReserva: "F-1",
            currencies: [
                { currency: "ARS", balance: 0 },
                { currency: "USD", balance: 0 },
            ],
        }];
        const filas = armarFilasDeudaPorReserva(reservas, { puedeVerMontos: false });
        assert.equal(filas.length, 2);
    });

    it("puedeVerMontos=true (default, sin pasar el parámetro): sigue filtrando como antes", () => {
        const reservas = [{ reservaPublicId: "r1", currencies: [{ currency: "ARS", balance: 0 }] }];
        assert.deepEqual(armarFilasDeudaPorReserva(reservas), []);
        assert.deepEqual(armarFilasDeudaPorReserva(reservas, { puedeVerMontos: true }), []);
    });

    it("puedeVerMontos=false con reservas sin currencies no rompe", () => {
        assert.deepEqual(armarFilasDeudaPorReserva([{ reservaPublicId: "r1" }], { puedeVerMontos: false }), []);
    });
});

// ─── agruparServiciosPorReserva (rediseño 2026-07-20, detalle del Paso 1) ─────────────

describe("agruparServiciosPorReserva", () => {
    it("null/undefined/no-array no rompen, devuelven {}", () => {
        assert.deepEqual(agruparServiciosPorReserva(null), {});
        assert.deepEqual(agruparServiciosPorReserva(undefined), {});
    });

    it("agrupa varios servicios de la MISMA reserva bajo la misma clave", () => {
        const servicios = [
            { reservaPublicId: "r1", type: "Hotel", description: "Bariloche", currency: "ARS" },
            { reservaPublicId: "r1", type: "Traslado", description: "Aeropuerto", currency: "ARS" },
            { reservaPublicId: "r2", type: "Paquete", description: "Cancún", currency: "USD" },
        ];
        const mapa = agruparServiciosPorReserva(servicios);
        assert.equal(mapa.r1.length, 2);
        assert.equal(mapa.r2.length, 1);
        assert.equal(mapa.r1[0].type, "Hotel");
        assert.equal(mapa.r2[0].description, "Cancún");
    });

    it("servicios sin reservaPublicId se ignoran (no aportan a ninguna fila)", () => {
        const servicios = [{ type: "Hotel", description: "Sin reserva", currency: "ARS" }];
        assert.deepEqual(agruparServiciosPorReserva(servicios), {});
    });

    it("currency ausente cae a ARS (mismo criterio legacy que el resto de la pantalla)", () => {
        const servicios = [{ reservaPublicId: "r1", type: "Hotel", currency: null }];
        const mapa = agruparServiciosPorReserva(servicios);
        assert.equal(mapa.r1[0].currency, "ARS");
    });

    it("reservaPublicId numérico o de otro tipo se normaliza a string (clave consistente)", () => {
        const servicios = [{ reservaPublicId: 123, type: "Hotel", currency: "ARS" }];
        const mapa = agruparServiciosPorReserva(servicios);
        assert.ok(mapa["123"]);
    });
});

// ─── construirDetalleFilaDeuda (rediseño 2026-07-20, columna "Detalle" del Paso 1) ────

describe("construirDetalleFilaDeuda", () => {
    it("un solo servicio en la misma reserva y moneda: 'Tipo — Descripción'", () => {
        const fila = { reservaPublicId: "r1", currency: "ARS", fileName: "Familia Pérez" };
        const mapa = { r1: [{ type: "Hotel", description: "Bariloche", currency: "ARS" }] };
        assert.equal(construirDetalleFilaDeuda(fila, mapa), "Hotel — Bariloche");
    });

    it("servicio sin descripción: solo el tipo", () => {
        const fila = { reservaPublicId: "r1", currency: "ARS" };
        const mapa = { r1: [{ type: "Hotel", description: null, currency: "ARS" }] };
        assert.equal(construirDetalleFilaDeuda(fila, mapa), "Hotel");
    });

    it("varios servicios en la misma moneda: nombra el primero y suma 'y N más'", () => {
        const fila = { reservaPublicId: "r1", currency: "ARS" };
        const mapa = {
            r1: [
                { type: "Hotel", description: "Bariloche", currency: "ARS" },
                { type: "Traslado", description: "Aeropuerto", currency: "ARS" },
                { type: "Asistencia", description: null, currency: "ARS" },
            ],
        };
        assert.equal(construirDetalleFilaDeuda(fila, mapa), "Hotel — Bariloche y 2 más");
    });

    it("filtra por la MONEDA de la fila: un servicio en otra moneda no cuenta", () => {
        const fila = { reservaPublicId: "r1", currency: "USD", fileName: "Familia Pérez" };
        const mapa = { r1: [{ type: "Hotel", description: "Bariloche", currency: "ARS" }] };
        assert.equal(construirDetalleFilaDeuda(fila, mapa), "Familia Pérez");
    });

    it("sin servicios para esa reserva: usa fileName como respaldo", () => {
        const fila = { reservaPublicId: "r1", currency: "ARS", fileName: "Familia Pérez" };
        assert.equal(construirDetalleFilaDeuda(fila, {}), "Familia Pérez");
    });

    it("sin servicios NI fileName: '—' (nunca deja la celda vacía)", () => {
        const fila = { reservaPublicId: "r1", currency: "ARS", fileName: null };
        assert.equal(construirDetalleFilaDeuda(fila, {}), "—");
    });

    it("mapa de servicios null/undefined no rompe", () => {
        const fila = { reservaPublicId: "r1", currency: "ARS", fileName: "Familia Pérez" };
        assert.equal(construirDetalleFilaDeuda(fila, null), "Familia Pérez");
        assert.equal(construirDetalleFilaDeuda(fila, undefined), "Familia Pérez");
    });
});

// ─── construirMensajeExitoPago (rediseño 2026-07-20, cartel de éxito) ─────────────────

describe("construirMensajeExitoPago", () => {
    // FIX bloqueante (review 2026-07-21): antes esta función devolvía null cuando faltaba
    // `impact`, y el componente interpretaba "sin cartel" = "no se guardó", reabriendo el
    // formulario con "Confirmar pago" habilitado → doble pago real (el pago a cuenta no
    // tiene tope ni idempotencia). Ahora SIEMPRE devuelve un mensaje: el POST ya se guardó
    // del lado del servidor cuando se llama a esta función, así que siempre hay que avisarlo.
    it("impact null/undefined: NUNCA devuelve null, cae al mensaje genérico 'Pago registrado.'", () => {
        const conNull = construirMensajeExitoPago({ impact: null, montoImputado: 100, monedaImputada: "ARS" });
        const conUndefined = construirMensajeExitoPago({ impact: undefined, montoImputado: 100, monedaImputada: "ARS" });
        assert.notEqual(conNull, null);
        assert.notEqual(conUndefined, null);
        assert.equal(conNull.tipo, "generico");
        assert.deepEqual(conNull.lineas, ["Pago registrado."]);
        assert.deepEqual(conUndefined.lineas, ["Pago registrado."]);
    });

    it("pago a cuenta (wasImputedToReserva=false): mensaje fijo de saldo a favor, sin montos", () => {
        const resultado = construirMensajeExitoPago({
            impact: { wasImputedToReserva: false, currency: "ARS", amountsVisible: true },
            montoImputado: 5000,
            monedaImputada: "ARS",
        });
        assert.equal(resultado.tipo, "a-cuenta");
        assert.equal(resultado.lineas.length, 1);
        assert.match(resultado.lineas[0], /saldo a favor/i);
        // Nunca menciona un monto en el pago a cuenta (el impact no trae "cuánto" para este caso)
        assert.ok(!/\$/.test(resultado.lineas[0]));
    });

    it("pago imputado a reserva SIN permiso de ver montos (amountsVisible=false): solo el destino, sin números", () => {
        const resultado = construirMensajeExitoPago({
            impact: {
                wasImputedToReserva: true,
                numeroReserva: "F-2026-1051",
                amountsVisible: false,
            },
            montoImputado: 45000,
            monedaImputada: "ARS",
        });
        assert.equal(resultado.tipo, "reserva-sin-monto");
        assert.equal(resultado.lineas.length, 1);
        assert.equal(resultado.lineas[0], "Pago registrado a la reserva F-2026-1051.");
        assert.ok(!/\$/.test(resultado.lineas[0]), "no debe exponer montos sin permiso");
    });

    it("pago imputado a reserva CON permiso, queda saldo pendiente: dos líneas con el monto y el resto", () => {
        const resultado = construirMensajeExitoPago({
            impact: {
                wasImputedToReserva: true,
                numeroReserva: "F-2026-1051",
                servicioDescripcion: "Hotel Bariloche",
                currency: "ARS",
                remainingBalance: 12000,
                amountsVisible: true,
            },
            montoImputado: 45000,
            monedaImputada: "ARS",
        });
        assert.equal(resultado.tipo, "reserva");
        assert.equal(resultado.lineas.length, 2);
        assert.equal(
            resultado.lineas[0],
            `Bajó la deuda de la reserva F-2026-1051 (Hotel Bariloche) en ${formatCurrency(45000, "ARS")}.`
        );
        assert.equal(resultado.lineas[1], `Quedan ${formatCurrency(12000, "ARS")} pendientes.`);
    });

    it("pago imputado a reserva CON permiso, queda saldada (remainingBalance=0): frase de saldada, no '$0'", () => {
        const resultado = construirMensajeExitoPago({
            impact: {
                wasImputedToReserva: true,
                numeroReserva: "F-2026-1051",
                currency: "ARS",
                remainingBalance: 0,
                amountsVisible: true,
            },
            montoImputado: 45000,
            monedaImputada: "ARS",
        });
        assert.equal(resultado.lineas[1], "Esa reserva queda saldada con este operador.");
    });

    it("resto de redondeo menor a medio centavo también cuenta como saldada", () => {
        const resultado = construirMensajeExitoPago({
            impact: {
                wasImputedToReserva: true,
                numeroReserva: "F-1",
                currency: "ARS",
                remainingBalance: 0.001,
                amountsVisible: true,
            },
            montoImputado: 1000,
            monedaImputada: "ARS",
        });
        assert.equal(resultado.lineas[1], "Esa reserva queda saldada con este operador.");
    });

    it("sin servicioDescripcion pero con fileName: usa el fileName como detalle entre paréntesis", () => {
        const resultado = construirMensajeExitoPago({
            impact: {
                wasImputedToReserva: true,
                numeroReserva: "F-1",
                fileName: "Familia Pérez",
                currency: "ARS",
                remainingBalance: 0,
                amountsVisible: true,
            },
            montoImputado: 1000,
            monedaImputada: "ARS",
        });
        assert.match(resultado.lineas[0], /la reserva F-1 \(Familia Pérez\)/);
    });

    it("sin servicioDescripcion NI fileName: menciona la reserva sin paréntesis", () => {
        const resultado = construirMensajeExitoPago({
            impact: {
                wasImputedToReserva: true,
                numeroReserva: "F-1",
                currency: "ARS",
                remainingBalance: 0,
                amountsVisible: true,
            },
            montoImputado: 1000,
            monedaImputada: "ARS",
        });
        assert.match(resultado.lineas[0], /^Bajó la deuda de la reserva F-1 en/);
        assert.ok(!resultado.lineas[0].includes("("));
    });

    it("el monto mostrado es el imputado en la moneda imputada (pago cruzado): usa montoEquivalente, no el monto de caja", () => {
        // Ej.: pagó $120.000 en pesos para cancelar deuda en dólares; el equivalente imputado es US$100.
        const resultado = construirMensajeExitoPago({
            impact: {
                wasImputedToReserva: true,
                numeroReserva: "F-1",
                currency: "USD",
                remainingBalance: 0,
                amountsVisible: true,
            },
            montoImputado: 100, // ya convertido a USD por el llamador (esCruzado ? montoEquivalente : monto)
            monedaImputada: "USD",
        });
        assert.ok(resultado.lineas[0].includes(formatCurrency(100, "USD")));
    });
});
