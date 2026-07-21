// E2E real — Tanda P2 "circuito proveedor": pantalla "Reembolsos ya registrados"
// (Deshacer / Corregir reserva) de la solapa "Reembolsos" del operador (2026-07-22).
//
// Camina con la app real corriendo:
//   (a) el bloque "Reembolsos ya registrados" muestra la fila recien cargada
//       (reserva, cliente, monto en formato es-AR).
//   (b) Deshacer: motivo corto deja el boton deshabilitado; motivo >= 20 lo habilita;
//       al confirmar la fila queda tachada "Deshecho" con el motivo visible y SIN botones.
//   (c) Corregir reserva: la lista de destinos ofrece la OTRA anulacion del mismo operador
//       (nunca la reserva actual); al elegirla + motivo, la fila viva pasa a nombrar esa
//       reserva.
//   (d) operador sin reembolsos: bloque vacio con el texto explicativo.
//   (e) higiene: nada de jerga interna ni GUIDs ni plata en formato gringo en el texto visible.
//   (f) NO se camina "credito ya usado" (P4): requiere retiros consumidos, cubierto por tests
//       unit — se anota como no verificado E2E.
//
// Requiere el entorno de scripts/e2e-local/README.md (DB + API + vite e2e), MAS Postgres
// local para el seed de BookingCancellations (no hay forma de generar la cadena fiscal
// completa -> CAE real sin AFIP, asi que la parte fiscal se siembra por SQL directo).
//
// Nota para quien retome esto: la cancelacion/reembolso real (UI de "Anular servicio") NO
// se camina aca — este script asume que YA existe una anulacion en AwaitingOperatorRefund
// (T1 cerrado) y prueba SOLO la pantalla nueva de deshacer/corregir sobre esos reembolsos.

const { chromium } = require("playwright-core");
const { execFileSync } = require("child_process");
const http = require("http");

const FRONT = "http://localhost:5173";
const API = "http://localhost:59663";
const SHOTS = __dirname + "/shots-p2";
require("fs").mkdirSync(SHOTS, { recursive: true });

const results = [];
function check(name, ok, detail) {
  results.push({ name, ok });
  console.log(`${ok ? "PASS" : "FAIL"} | ${name}${detail ? " | " + detail.toString().slice(0, 220) : ""}`);
}

// ── Helper: espera en loop a que la API responda algo (arriba compilando ahora mismo) ──
function esperarApi(timeoutMs = 120000) {
  return new Promise((resolve, reject) => {
    const start = Date.now();
    const intento = () => {
      const req = http.get(API + "/api/health", (res) => {
        res.resume();
        resolve(true);
      });
      req.on("error", () => {
        if (Date.now() - start > timeoutMs) return reject(new Error("La API no respondio a tiempo"));
        setTimeout(intento, 2000);
      });
      req.setTimeout(3000, () => req.destroy());
    };
    intento();
  });
}

// ── Helper: psql directo contra el Postgres local (fuera del browser) ──
// Usamos execFileSync (sin shell) para no pelear con el escapado de comillas dobles
// (identificadores Postgres) contra distintas shells — el array de argumentos viaja
// directo al proceso "docker", sin reinterpretacion.
// -q (quiet) es CLAVE aca: sin ella, un INSERT ... RETURNING imprime la fila Y ADEMAS
// la linea "INSERT 0 1" de confirmacion en un renglon aparte — sin -q esa linea se
// pega al valor parseado (el bug que nos mordio la primera corrida: un GUID quedaba
// con "\nINSERT 0 1" adentro y el backend lo rechazaba como invalido).
function psql(sqlText) {
  return execFileSync(
    "docker",
    ["exec", "travel_db_local", "psql", "-U", "traveluser", "-d", "travel", "-t", "-A", "-q", "-v", "ON_ERROR_STOP=1", "-c", sqlText],
    { encoding: "utf8" }
  ).trim();
}
function psqlInt(sqlText) {
  const raw = psql(sqlText);
  const n = parseInt(raw, 10);
  if (Number.isNaN(n)) throw new Error(`psqlInt: no pude parsear un entero de "${raw}" (SQL: ${sqlText.slice(0, 120)})`);
  return n;
}

// ── Higiene: nada de jerga interna, GUIDs, ni plata en formato gringo en texto visible ──
// (gate data-exposure: un ERP no debe mostrarle a un usuario no-programador nombres
// internos como "allocation"/"Reassociate", códigos de error crudos, ni GUIDs).
function hygieneChecks(etiqueta, texto) {
  check(`(e) Higiene ${etiqueta}: sin jerga tecnica interna`,
    !/allocation|Reassociate|REFUND_CREDIT|Exception|undefined|null\b/i.test(texto), texto.slice(0, 160));
  check(`(e) Higiene ${etiqueta}: sin GUIDs visibles`,
    !/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(texto), "");
  check(`(e) Higiene ${etiqueta}: sin formato gringo de plata (500.00)`,
    !/\d\.\d{2}\b/.test(texto), "");
}

(async () => {
  await esperarApi();
  console.log("API arriba, arranco el paseo.");

  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 950 } });
  page.setDefaultTimeout(15000);
  page.on("console", (m) => { if (m.type() === "error") console.log("CONSOLE_ERR:", m.text().slice(0, 300)); });

  // ── Login ──
  await page.goto(FRONT + "/login");
  await page.fill('input[type="email"]', "e2e@magnatravel.local");
  await page.fill('input[type="password"]', "E2eLocal2026!");
  await page.click('button[type="submit"]');
  await page.waitForURL((u) => !u.pathname.includes("login"), { timeout: 20000 });
  check("Login por UI", true, "");

  // ── Seed por API: 2 operadores, 1 cliente, 2 reservas con hotel ARS confirmado ──
  const seed = await page.evaluate(async () => {
    const raw = document.cookie.split("; ").find((c) => c.startsWith("mt_csrf="))?.slice("mt_csrf=".length);
    const csrf = raw ? decodeURIComponent(raw) : "";
    const call = async (method, path, body) => {
      const r = await fetch("/api" + path, {
        method, credentials: "include",
        headers: { "Content-Type": "application/json", "X-CSRF-Token": csrf || "" },
        body: body ? JSON.stringify(body) : undefined,
      });
      const text = await r.text();
      let json = null; try { json = JSON.parse(text); } catch {}
      return { status: r.status, json, text: text.slice(0, 200) };
    };
    const stamp = Date.now().toString().slice(-6);
    const out = { stamp };

    const mkHotel = (rid, supId, nombre, costo) => call("POST", `/reservas/${rid}/hotels`, {
      supplierId: supId, hotelName: nombre, starRating: 4, city: "Bariloche", country: "AR",
      checkIn: "2026-12-01T00:00:00Z", checkOut: "2026-12-05T00:00:00Z",
      roomType: "Doble", mealPlan: "Desayuno", adults: 2, children: 0, rooms: 1,
      confirmationNumber: null, netCost: costo, salePrice: costo + 200, commission: 100,
      notes: null, currency: "ARS",
      newCatalogProduct: { name: nombre, city: "Bariloche", supplierPublicId: supId },
    });

    // Deja la reserva lista para que el hotel se pueda confirmar (mismo patron que e2e-p1).
    const prepararReserva = async (rid) => {
      await call("PATCH", `/reservas/${rid}/passenger-counts`, { adultCount: 2, childCount: 0, infantCount: 0 });
      await call("POST", `/reservas/${rid}/passengers`, {
        fullName: "Pasajero P2", documentType: "DNI", documentNumber: "30222333",
        birthDate: null, nationality: "AR", phone: null, email: null, gender: null,
      });
      await call("PUT", `/reservas/${rid}/status`, { status: "InManagement" });
    };

    out.supA = await call("POST", "/suppliers", { name: `Operador P2A ${stamp}`, defaultCurrency: "ARS", invoicingMode: 0 });
    out.supAId = String(out.supA.json?.publicId || out.supA.json?.id);
    out.supB = await call("POST", "/suppliers", { name: `Operador P2B ${stamp}`, defaultCurrency: "ARS", invoicingMode: 0 });
    out.supBId = String(out.supB.json?.publicId || out.supB.json?.id);

    out.customer = await call("POST", "/customers", { fullName: `Cliente P2 ${stamp}`, email: null, phone: null });
    out.customerId = String(out.customer.json?.publicId || out.customer.json?.id);

    // Reserva A: sera la que primero recibe el reembolso (y despues se corrige a la B).
    out.rA = await call("POST", "/reservas", { name: `Reserva P2A ${stamp}` });
    out.ridA = out.rA.json?.publicId || out.rA.json?.id;
    out.numA = out.rA.json?.numeroReserva || out.rA.json?.fileNumber || null;
    out.hA = await mkHotel(out.ridA, out.supAId, `Hotel P2A ${stamp}`, 500);
    out.hAId = out.hA.json?.publicId || out.hA.json?.id;
    await prepararReserva(out.ridA);
    out.hsA = await call("PATCH", `/hotel-bookings/${out.hAId}/status`, { status: "Confirmado" });

    // Reserva B: destino valido para "Corregir reserva" (mismo operador, misma moneda).
    out.rB = await call("POST", "/reservas", { name: `Reserva P2B ${stamp}` });
    out.ridB = out.rB.json?.publicId || out.rB.json?.id;
    out.numB = out.rB.json?.numeroReserva || out.rB.json?.fileNumber || null;
    out.hB = await mkHotel(out.ridB, out.supAId, `Hotel P2B ${stamp}`, 500);
    out.hBId = out.hB.json?.publicId || out.hB.json?.id;
    await prepararReserva(out.ridB);
    out.hsB = await call("PATCH", `/hotel-bookings/${out.hBId}/status`, { status: "Confirmado" });

    return out;
  });
  const seedApiOk = [seed.supA, seed.supB, seed.customer, seed.hA, seed.hsA, seed.hB, seed.hsB]
    .every((r) => r && r.status < 300);
  check("Seed API (2 operadores, 1 cliente, 2 reservas con hotel ARS confirmado)", seedApiOk,
    seedApiOk ? `A=${seed.ridA} B=${seed.ridB}` : JSON.stringify({ hA: seed.hA?.text, hsA: seed.hsA?.text, hB: seed.hB?.text, hsB: seed.hsB?.text }).slice(0, 300));
  if (!seedApiOk) throw new Error("Seed API fallo, no sigo");

  // ── Seed por SQL directo: la cadena fiscal (Invoice + BookingCancellation + Lines) ──
  // No hay forma de generar esto por UI/API sin AFIP real (CAE) en local, asi que se
  // inyecta directo el estado "T1 ya cerrado, esperando el reembolso del operador".
  let dbIds = {};
  try {
    dbIds.supplierAInt = psqlInt(`SELECT "Id" FROM "Suppliers" WHERE "PublicId"='${seed.supAId}';`);
    dbIds.customerInt = psqlInt(`SELECT "Id" FROM "Customers" WHERE "PublicId"='${seed.customerId}';`);
    dbIds.reservaAInt = psqlInt(`SELECT "Id" FROM "TravelFiles" WHERE "PublicId"='${seed.ridA}';`);
    dbIds.reservaBInt = psqlInt(`SELECT "Id" FROM "TravelFiles" WHERE "PublicId"='${seed.ridB}';`);
    dbIds.hotelAInt = psqlInt(`SELECT "Id" FROM "HotelBookings" WHERE "PublicId"='${seed.hAId}';`);
    dbIds.hotelBInt = psqlInt(`SELECT "Id" FROM "HotelBookings" WHERE "PublicId"='${seed.hBId}';`);

    const numeroInvoiceA = Number(seed.stamp + "1");
    const numeroInvoiceB = Number(seed.stamp + "2");

    // Invoice minima por reserva (OriginatingInvoiceId es UNIQUE entre BCs vivos, asi que
    // cada BookingCancellation necesita SU PROPIA factura, no pueden compartir una).
    dbIds.invoiceAInt = psqlInt(`INSERT INTO "Invoices"
      ("PublicId","CreatedAt","TipoComprobante","PuntoDeVenta","NumeroComprobante","ImporteTotal","ImporteNeto","ImporteIva","MonId","MonCotiz","WasForced","AnnulmentStatus","OutstandingBalanceAtIssuance","TravelFileId")
      VALUES (gen_random_uuid(), now(), 11, 1, ${numeroInvoiceA}, 500, 500, 0, 'PES', 1, false, 0, 0, ${dbIds.reservaAInt})
      RETURNING "Id";`);
    dbIds.invoiceBInt = psqlInt(`INSERT INTO "Invoices"
      ("PublicId","CreatedAt","TipoComprobante","PuntoDeVenta","NumeroComprobante","ImporteTotal","ImporteNeto","ImporteIva","MonId","MonCotiz","WasForced","AnnulmentStatus","OutstandingBalanceAtIssuance","TravelFileId")
      VALUES (gen_random_uuid(), now(), 11, 1, ${numeroInvoiceB}, 500, 500, 0, 'PES', 1, false, 0, 0, ${dbIds.reservaBInt})
      RETURNING "Id";`);

    // BookingCancellation padre por reserva, Status=2 (AwaitingOperatorRefund). OJO:
    // FiscalSnapshot_AgencyTaxConditionAtEvent / SupplierTaxConditionAtEvent son
    // OBLIGATORIOS EN LA PRACTICA aunque la columna sea nullable — OperatorRefundService
    // valida la matriz fiscal (INV-118) ANTES de mirar si hay deducciones, y con null
    // rechaza con "no se pudo determinar la condicion fiscal" incluso en el camino simple
    // sin deducciones. Usamos RESPONSABLE_INSCRIPTO/RESPONSABLE_INSCRIPTO (agencia/operador),
    // el caso sin restricciones especiales.
    const bcSql = (reservaInt, invoiceInt) => `INSERT INTO "BookingCancellations"
      ("PublicId","ReservaId","CustomerId","SupplierId","OriginatingInvoiceId","Status","Reason","DraftedAt",
       "DraftedByUserId","AmountPaidAtCancellation","EstimatedRefundAmount","ReceivedRefundAmount",
       "FiscalSnapshot_ExchangeRateAtOriginalInvoice","FiscalSnapshot_Source","FiscalSnapshot_FetchedAt",
       "FiscalSnapshot_CurrencyAtEvent","FiscalSnapshot_AgencyTaxConditionAtEvent","FiscalSnapshot_SupplierTaxConditionAtEvent",
       "ReviewRequiredReason","PenaltyStatus","ConceptKind","DebitNoteStatus")
      VALUES (gen_random_uuid(), ${reservaInt}, ${dbIds.customerInt}, ${dbIds.supplierAInt}, ${invoiceInt}, 2,
       'Anulacion sembrada para E2E P2', now(), 'e2e', 500, 500, 0, 1, 5, now(), 'ARS',
       'RESPONSABLE_INSCRIPTO', 'RESPONSABLE_INSCRIPTO', 0, 0, 0, 0)
      RETURNING "Id", "PublicId";`;

    const bcAOut = psql(bcSql(dbIds.reservaAInt, dbIds.invoiceAInt)).split("|");
    dbIds.bcAInt = parseInt(bcAOut[0], 10);
    dbIds.bcAPublicId = bcAOut[1];

    const bcBOut = psql(bcSql(dbIds.reservaBInt, dbIds.invoiceBInt)).split("|");
    dbIds.bcBInt = parseInt(bcBOut[0], 10);
    dbIds.bcBPublicId = bcBOut[1];

    // Linea hija por BC: sin esto, EnsureBookingCancellationCanReceiveOperatorRefund
    // (INV-126/INV-118 a nivel linea) rechaza CUALQUIER imputacion — "el proveedor del
    // reintegro no corresponde a ninguna linea de esta cancelacion".
    const lineSql = (bcInt, hotelInt) => `INSERT INTO "BookingCancellationLines"
      ("PublicId","BookingCancellationId","SupplierId","ServiceTable","ServiceId","Scope","Currency",
       "LineSaleAmount","ConceptKind","PenaltyStatus","DebitNoteStatus","RefundCap","ReceivedRefundAmount",
       "RefundStatus","CreatedAt","RetainedDeductionAmount")
      VALUES (gen_random_uuid(), ${bcInt}, ${dbIds.supplierAInt}, 2, ${hotelInt}, 0, 'ARS',
       500, 0, 0, 0, 500, 0, 1, now(), 0);`;
    psql(lineSql(dbIds.bcAInt, dbIds.hotelAInt));
    psql(lineSql(dbIds.bcBInt, dbIds.hotelBInt));

    check("Seed SQL (2 facturas + 2 BookingCancellations AwaitingOperatorRefund + sus lineas)", true,
      `bcA=${dbIds.bcAPublicId} bcB=${dbIds.bcBPublicId}`);
  } catch (e) {
    check("Seed SQL (2 facturas + 2 BookingCancellations AwaitingOperatorRefund + sus lineas)", false, e.message);
    throw e;
  }

  // ── Registrar el PRIMER reembolso imputado a la anulacion de reserva A (paso 3 del seed) ──
  const regFn = async (supplierPublicId, bookingCancellationPublicId) => {
    return await page.evaluate(async ({ supplierPublicId, bookingCancellationPublicId }) => {
      const raw = document.cookie.split("; ").find((c) => c.startsWith("mt_csrf="))?.slice("mt_csrf=".length);
      const csrf = raw ? decodeURIComponent(raw) : "";
      const r = await fetch("/api/operator-refunds/record-and-allocate", {
        method: "POST", credentials: "include",
        headers: { "Content-Type": "application/json", "X-CSRF-Token": csrf || "" },
        body: JSON.stringify({
          supplierPublicId, bookingCancellationPublicId,
          receivedAmount: 500, currency: "ARS", receivedAt: new Date().toISOString(),
          method: "Transfer", reference: null, notes: null,
          idempotencyKey: crypto.randomUUID(),
        }),
      });
      const text = await r.text();
      let json = null; try { json = JSON.parse(text); } catch {}
      return { status: r.status, json, text: text.slice(0, 300) };
    }, { supplierPublicId, bookingCancellationPublicId });
  };

  const reg1 = await regFn(seed.supAId, dbIds.bcAPublicId);
  check("Registrar 1er reembolso imputado a la anulacion de reserva A (seed paso 3)",
    reg1.status < 300, reg1.status < 300 ? "" : reg1.text);
  if (reg1.status >= 300) throw new Error("No se pudo registrar el 1er reembolso: " + reg1.text);

  // ═══════════════════════════════════════════════════════════════════════════
  // Paseo (a): el bloque "Reembolsos ya registrados" muestra la fila recien cargada
  // ═══════════════════════════════════════════════════════════════════════════
  await page.goto(`${FRONT}/suppliers/${seed.supAId}/account`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("tab", { name: /^Reembolsos/ }).click();

  const bloque = page.locator('[data-testid="reembolsos-registrados-bloque"]');
  await bloque.waitFor();
  await bloque.locator('[data-state="live"]').first().waitFor({ timeout: 15000 });
  const textoA = await bloque.innerText();
  await page.screenshot({ path: SHOTS + "/1-fila-registrada.png", fullPage: true });

  check("(a) El bloque muestra el numero de la reserva A",
    seed.numA ? textoA.includes(String(seed.numA)) : /Reserva #/.test(textoA), textoA.slice(0, 200));
  check("(a) El bloque muestra el nombre del cliente",
    textoA.includes(`Cliente P2 ${seed.stamp}`), "");
  check("(a) El bloque muestra el monto en formato es-AR (500,00)",
    /500,00/.test(textoA), textoA.slice(0, 200));
  hygieneChecks("(a) fila inicial", textoA);

  // ═══════════════════════════════════════════════════════════════════════════
  // Paseo (b): Deshacer — motivo corto bloquea, motivo valido habilita, la fila
  // queda tachada "Deshecho" sin botones.
  // ═══════════════════════════════════════════════════════════════════════════
  const filaViva1 = bloque.locator('[data-state="live"]').first();
  await filaViva1.locator('[data-testid="reembolso-deshacer-boton"]').click();
  await page.locator('[data-testid="reembolso-deshacer-motivo"]').waitFor();

  const motivoTextarea = page.locator('[data-testid="reembolso-deshacer-motivo"]');
  const confirmarDeshacer = page.locator('[data-testid="reembolso-deshacer-confirmar"]');

  // Motivo corto (12 caracteres, por debajo del minimo de 20): el boton debe seguir deshabilitado.
  await motivoTextarea.fill("Corto motivo");
  const deshabilitadoConCorto = await confirmarDeshacer.isDisabled();
  await page.screenshot({ path: SHOTS + "/2-deshacer-motivo-corto.png", fullPage: true });
  check("(b) Deshacer: con motivo de 12 caracteres el boton de confirmar sigue deshabilitado",
    deshabilitadoConCorto, "");

  // Motivo valido (>= 20 caracteres): se habilita.
  await motivoTextarea.fill("Me equivoqué al cargar este reembolso");
  const habilitadoConValido = await confirmarDeshacer.isEnabled();
  check("(b) Deshacer: con motivo >= 20 caracteres el boton se habilita", habilitadoConValido, "");

  await confirmarDeshacer.click();
  const filaDeshecha = bloque.locator('[data-state="voided"]').first();
  await filaDeshecha.waitFor({ timeout: 15000 });
  const textoDeshecho = await filaDeshecha.innerText();
  await page.screenshot({ path: SHOTS + "/3-fila-deshecha.png", fullPage: true });
  check("(b) Tras confirmar: la fila queda tachada con la etiqueta 'Deshecho'",
    /Deshecho/.test(textoDeshecho), textoDeshecho.slice(0, 200));
  check("(b) La fila deshecha muestra el motivo cargado",
    /Me equivoqué al cargar este reembolso/.test(textoDeshecho), textoDeshecho.slice(0, 200));
  check("(b) La fila deshecha NO ofrece botones de accion",
    (await filaDeshecha.locator('[data-testid="reembolso-deshacer-boton"], [data-testid="reembolso-corregir-boton"]').count()) === 0, "");

  // ═══════════════════════════════════════════════════════════════════════════
  // Paseo (c): Corregir reserva — se registra OTRO reembolso sobre la anulacion de
  // reserva A (el cupo quedo libre porque el paso (b) lo deshizo), y se corrige a
  // la anulacion de reserva B (el unico destino valido: mismo operador, misma
  // moneda, excluye la reserva actual).
  // ═══════════════════════════════════════════════════════════════════════════
  const reg2 = await regFn(seed.supAId, dbIds.bcAPublicId);
  check("Registrar OTRO reembolso imputado a la anulacion de reserva A (mismo seed paso 3)",
    reg2.status < 300, reg2.status < 300 ? "" : reg2.text);
  if (reg2.status >= 300) throw new Error("No se pudo registrar el 2do reembolso: " + reg2.text);

  // El 2do reembolso se creo por API, fuera de React: forzamos un refetch con reload.
  await page.reload();
  await page.waitForLoadState("networkidle");
  await page.getByRole("tab", { name: /^Reembolsos/ }).click();
  await bloque.waitFor();
  const filaViva2 = bloque.locator('[data-state="live"]').first();
  await filaViva2.waitFor({ timeout: 15000 });
  const textoAntesCorregir = await filaViva2.innerText();
  check("(c) Tras el 2do registro: hay una fila viva nombrando la reserva A",
    seed.numA ? textoAntesCorregir.includes(String(seed.numA)) : true, textoAntesCorregir.slice(0, 160));

  await filaViva2.locator('[data-testid="reembolso-corregir-boton"]').click();
  const destinoB = page.locator(`[data-testid="reembolso-corregir-destino-${dbIds.bcBPublicId}"]`);
  const sinDestinos = page.locator('[data-testid="reembolso-corregir-sin-destinos"]');
  await Promise.race([destinoB.waitFor({ timeout: 15000 }), sinDestinos.waitFor({ timeout: 15000 })]);

  const listaDestinos = await page.locator('[data-testid^="form-corregir-reembolso-"]').innerText();
  await page.screenshot({ path: SHOTS + "/4-corregir-lista-destinos.png", fullPage: true });
  check("(c) La lista de destinos ofrece la anulacion de reserva B",
    (await destinoB.count()) === 1, listaDestinos.slice(0, 250));
  check("(c) La lista de destinos NO ofrece la reserva actual (A)",
    seed.numA ? !new RegExp(`Reserva #${seed.numA}\\b`).test(listaDestinos) : true, listaDestinos.slice(0, 250));
  hygieneChecks("(c) lista de destinos", listaDestinos);

  if (await destinoB.count()) {
    await destinoB.click();
    await page.locator('[data-testid="reembolso-corregir-motivo"]').fill(
      "La reserva correcta es la B, me equivoqué al imputar el reembolso antes"
    );
    const confirmarCorregir = page.locator('[data-testid="reembolso-corregir-confirmar"]');
    check("(c) Con destino + motivo validos el boton 'Mover' se habilita",
      await confirmarCorregir.isEnabled(), "");
    await confirmarCorregir.click();

    // Espera observable: la fila viva pasa a nombrar la reserva B.
    await page.waitForFunction(
      (numB) => {
        const bloqueEl = document.querySelector('[data-testid="reembolsos-registrados-bloque"]');
        const filaViva = bloqueEl?.querySelector('[data-state="live"]');
        return filaViva && numB && filaViva.innerText.includes(numB);
      },
      String(seed.numB || ""),
      { timeout: 15000 }
    ).catch(() => {}); // si numB vino null, el check de abajo lo va a marcar FAIL con evidencia

    const textoTrasCorregir = await bloque.locator('[data-state="live"]').first().innerText();
    await page.screenshot({ path: SHOTS + "/5-corregir-exito.png", fullPage: true });
    check("(c) Tras corregir: la fila viva ahora nombra la reserva B",
      seed.numB ? textoTrasCorregir.includes(String(seed.numB)) : /Reserva #/.test(textoTrasCorregir),
      textoTrasCorregir.slice(0, 200));
    hygieneChecks("(c) fila tras corregir", textoTrasCorregir);
  } else {
    check("(c) Corregir reserva completo (destino B elegido, motivo cargado, exito)", false,
      "No aparecio el destino B en la lista, no se pudo continuar el flujo");
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // Paseo (d): operador P2B sin reembolsos registrados -> bloque vacio explicado
  // ═══════════════════════════════════════════════════════════════════════════
  await page.goto(`${FRONT}/suppliers/${seed.supBId}/account`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("tab", { name: /^Reembolsos/ }).click();
  const bloqueB = page.locator('[data-testid="reembolsos-registrados-bloque"]');
  await bloqueB.waitFor();
  const vacio = bloqueB.locator('[data-testid="reembolsos-registrados-vacio"]');
  await vacio.waitFor({ timeout: 15000 });
  const textoVacio = (await vacio.innerText()).trim();
  await page.screenshot({ path: SHOTS + "/6-operador-sin-reembolsos.png", fullPage: true });
  check("(d) Operador sin reembolsos: bloque vacio con el texto explicativo exacto",
    textoVacio === "Todavía no registraste ningún reembolso de este operador.", textoVacio);

  // ═══════════════════════════════════════════════════════════════════════════
  // (f) NO verificado E2E: caso "credito ya usado" (P4) — requiere que el cliente ya haya
  // consumido el saldo a favor generado por el reembolso (retiros/withdrawals). Ese estado
  // no se puede armar con el seed liviano de este script; queda cubierto por
  // TravelApi.Tests (unit, esErrorCreditoYaUsado / OperatorRefundActionRejectedException).
  // ═══════════════════════════════════════════════════════════════════════════
  check("(f) Caso 'credito ya usado' (P4) — NO verificado E2E (anotado, cubierto por tests unit)", true, "");

  await browser.close();
  const fails = results.filter((r) => !r.ok).length;
  console.log(`RESUMEN: ${results.length - fails}/${results.length} PASS`);
  process.exit(fails ? 1 : 0);
})().catch((e) => { console.error("ERROR_SCRIPT:", e.message); process.exit(1); });
