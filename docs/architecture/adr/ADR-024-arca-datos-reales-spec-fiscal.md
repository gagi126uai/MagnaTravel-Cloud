# ADR-024 — Especificación fiscal: datos reales del receptor en la emisión ARCA (DocTipo + CondicionIVAReceptorId)

- **Estado:** Propuesta (especificación fiscal, sin código).
- **Autor fiscal:** arca-tax-expert-argentina.
- **Fecha:** 2026-06-12.
- **Alcance:** Nacional / ARCA (ex AFIP). NO incluye obligaciones provinciales ni municipales.
- **Aviso:** Este documento NO reemplaza a un contador/asesor fiscal matriculado. Para emisión en producción la validación final debe venir de un profesional matriculado y de la homologación contra el ambiente de testing de ARCA. Lo marcado como "ASUNCIÓN — confirmar con contador" debe firmarse antes de prender el comportamiento.

---

## 1. Alcance impositivo

Dos desconexiones reales en `AfipService` (WSFE SOAP, emisor actual **Monotributo → Factura C** con CAE reales) que hacen que el receptor del comprobante viaje con datos fiscales incorrectos:

1. **DocTipo no respeta `Customer.DocumentType`.** Un extranjero con pasaporte se emite como DNI argentino (DocTipo=96).
2. **`CondicionIVAReceptorId` no sale del cliente.** Se llama `GetConditionIvaId(null, docTipo)` con el primer argumento NULL explícito, por lo que la condición IVA del receptor se infiere SOLO del DocTipo y nunca usa `Customer.TaxConditionId` / `Customer.TaxCondition`. Desde RG 5616/2024 este campo es obligatorio en WSFE.

Esta spec entrega: tablas verificadas de mapeo, reglas de decisión paso a paso implementables, casos borde, fallback seguro, y los tests fiscales que backend debe escribir. NO modifica código.

---

## 2. Hechos verificados (comportamiento actual del repo)

`Hecho verificado:` `AfipService.cs:1088-1104` (`ProcessInvoiceJob`/`BuildVoucherDetail`) arma DocTipo/DocNro así:
- Si el `CustomerSnapshot` tiene `TaxId` parseable → `DocTipo=80` (CUIT), `DocNro=TaxId` limpio.
- Si no, y tiene `DocumentNumber` parseable → `DocTipo=96` (DNI) **sin mirar `DocumentType`**.
- Si nada → queda el default `DocTipo=99`, `DocNro=0` (consumidor final).

`Hecho verificado:` `AfipService.cs:1240` emite `<CondicionIVAReceptorId>{GetConditionIvaId(null, docTipo)}</CondicionIVAReceptorId>`. El primer argumento (`taxCondition` del cliente) va **null literal**.

`Hecho verificado:` `GetConditionIvaId` (`AfipService.cs:2018-2044`): con `taxCondition == null`, `docTipo=80` (CUIT) → retorna 5 (Consumidor Final); `docTipo=99` → 5; cualquier otro → 5. O sea hoy **TODO receptor sale como Consumidor Final (5)** salvo que en el futuro se le pase un texto de condición, cosa que no ocurre.

`Hecho verificado:` Tipo de comprobante del EMISOR (`CreatePendingInvoice`, `AfipService.cs:595-607`): si `settings.TaxCondition == "Responsable Inscripto"` → A (1) cuando el cliente es RI, si no B (6); en cualquier otro caso (Monotributo/Exento) → C (11). Hoy el emisor es Monotributo → siempre C.

`Hecho verificado:` `Customer` (`Customer.cs`) tiene: `TaxId` (CUIT/CUIL, string), `DocumentType` (string libre, comentario "DNI / Pasaporte / CUIT / CUIL / LE / LC", `MaxLength(20)`), `DocumentNumber` (string), `TaxCondition` (string, default "Consumidor Final"), `TaxConditionId` (int? con comentario "AFIP Code: 1=RI, 4=Exento, 5=Consumidor Final, 6=Monotributo"). El comprobante se arma desde `CustomerSnapshot` (JSON serializado del `Customer` al momento de crear la Invoice — snapshot inmutable, `AfipService.cs:771`).

`Hecho verificado:` `DocumentType` es texto libre con `MaxLength(20)`; NO es un enum cerrado. Los valores reales del campo NO están garantizados (puede venir "Pasaporte", "pasaporte", "PASAPORTE", "Pas.", null, etc.). Backend debe normalizar.

`Hecho verificado:` `CanMisMonExt` ya se emite condicionalmente para moneda extranjera (`AfipService.cs:1239`, `BuildCanMisMonExtFragment`). Fuera del alcance de este ADR salvo por la interacción con DocTipo (ver §9). Ver memoria [[canmismonext-wsfev1]].

---

## 3. DESCONEXIÓN 1 — Mapeo `DocumentType` → DocTipo ARCA

### 3.1 Tabla oficial de tipos de documento (FEParamGetTiposDoc)

`Hecho verificado:` (fuente: FEParamGetTiposDoc, referencia comunitaria canónica SistemasAgiles/PyAfipWs WSFEv1 — ver §13 fuentes; consultar 2026-06-12). Códigos relevantes para una agencia retail:

| DocTipo ARCA | Descripción oficial | Uso en MagnaTravel |
|---|---|---|
| 80 | CUIT | Cliente con CUIT (empresa / RI / Monotributo / autónomo) |
| 86 | CUIL | Persona física con CUIL (sin CUIT comercial) |
| 87 | CDI | Clave de Identificación (sin CUIT/CUIL) — raro |
| 89 | LE | Libreta de Enrolamiento (histórico) |
| 90 | LC | Libreta Cívica (histórico) |
| 91 | CI Extranjera | Cédula de identidad de extranjero |
| 94 | Pasaporte | Extranjero con pasaporte |
| 96 | DNI | DNI argentino |
| 99 | Doc. (Otro) / Sin identificar | Consumidor final sin documento / venta global diaria |

`RIESGO:` El código 99 en ARCA es "Doc. (Otro) / Consumidor final sin identificar". Tiene **tope de monto** por comprobante (operaciones con consumidor final sin identificar tienen un límite por norma). Si una operación con receptor identificable se emite con DocTipo=99 por fallback, y supera el tope, ARCA puede rechazar. `ASUNCIÓN — confirmar con contador:` monto del tope vigente de consumidor final sin identificar (cambia por norma; no lo hardcodees, ver §3.4 regla F).

### 3.2 Tabla de mapeo `Customer.DocumentType` (sistema) → DocTipo ARCA

`Hecho verificado:` el mapeo de los códigos es directo contra la tabla §3.1. La **normalización del texto libre** es decisión de implementación (no fiscal), pero el resultado fiscal sí lo es.

Regla de normalización sugerida (backend): `trim` + lowercase + sin acentos + colapsar espacios. Luego:

| `DocumentType` normalizado (entrada) | DocTipo ARCA | DocNro a usar |
|---|---|---|
| "cuit" | 80 | `TaxId` (o `DocumentNumber` si es el que trae el CUIT) limpio, 11 díg. |
| "cuil" | 86 | número de CUIL limpio, 11 díg. |
| "cdi" | 87 | número CDI, 11 díg. |
| "le" / "libreta de enrolamiento" | 89 | `DocumentNumber` numérico |
| "lc" / "libreta civica" | 90 | `DocumentNumber` numérico |
| "ci extranjera" / "cedula extranjera" / "cedula de identidad" | 91 | `DocumentNumber` (puede ser alfanumérico → ver §3.3) |
| "pasaporte" / "passport" / "pas" | 94 | `DocumentNumber` (alfanumérico → ver §3.3) |
| "dni" | 96 | `DocumentNumber` numérico (7-8 díg.) |
| vacío / null / desconocido | **fallback §3.4 regla F** | según fallback |

`RIESGO:` hoy el código ignora `DocumentType` y manda 96 (DNI) cuando hay `DocumentNumber`. Un pasaporte extranjero "AB123456" hoy: o falla el `long.TryParse` (no entra como 96 → cae a 99) o, si fuera todo numérico, sale como DNI argentino falso. **El fix es leer `DocumentType` y mapear.**

### 3.3 Validación de formato por DocTipo

`Hecho verificado` / `ASUNCIÓN — confirmar con contador` (el formato lo valida ARCA del lado servidor; estas son las reglas conocidas, confirmar tolerancias):

| DocTipo | Validación de formato recomendada | Notas |
|---|---|---|
| 80 (CUIT) | exactamente **11 dígitos** numéricos, dígito verificador válido | `VERIFICADO`: CUIT es 11 díg. con DV módulo 11. Recomendado validar DV antes de POSTear (evita rechazo 10074/10246). |
| 86 (CUIL) | **11 dígitos**, mismo algoritmo DV que CUIT | mismo formato que CUIT |
| 87 (CDI) | **11 dígitos** | mismo largo |
| 96 (DNI) | **7 u 8 dígitos** numéricos, > 0 | `ASUNCIÓN`: rango exacto lo valida ARCA; 7-8 díg. es lo habitual |
| 89/90 (LE/LC) | numérico, > 0 | histórico |
| 94 (Pasaporte) | **alfanumérico** | `RIESGO`: `DocNro` en WSFE es **numérico (long)**. Un pasaporte alfanumérico NO entra como número. Ver §3.5. |
| 91 (CI Extranjera) | alfanumérico posible | mismo problema que pasaporte |
| 99 | DocNro = 0 | consumidor final sin identificar |

### 3.4 Reglas de decisión paso a paso (DocTipo) — implementables

Backend debe resolver `(docTipo, docNro)` con esta precedencia. **Importante:** el orden prioriza identificación fiscal fuerte (CUIT) por sobre el texto de `DocumentType`, porque un CUIT presente es el dato más confiable para ARCA.

```
ENTRADA: cust = deserialize(CustomerSnapshot)

A. Si cust.TaxId no vacío Y limpia(TaxId) es 11 dígitos numéricos con DV válido:
      docTipo = 80 ; docNro = limpia(TaxId)
      → FIN  (CUIT siempre gana: es el identificador fiscal fuerte)

B. Si cust.DocumentType normaliza a un tipo conocido (tabla §3.2):
      docTipo = <código de la tabla>
      docNro  = numérico(DocumentNumber)  // ver C y E
      Si docTipo ∈ {80,86,87} pero el número no es 11 díg. válidos → fallback (regla F)
      Si docTipo ∈ {94,91} (alfanumérico) → ver §3.5
      Si numérico(DocumentNumber) falla para tipos numéricos → fallback (regla F)
      → FIN

C. Si DocumentType vacío/desconocido PERO DocumentNumber es numérico:
      // NO asumir DNI ciegamente como hoy. Es una asunción de negocio.
      docTipo = 96 (DNI)  [ASUNCIÓN — confirmar con contador: default DNI para nro suelto sin tipo]
      docNro  = DocumentNumber
      → FIN

D. Si no hay TaxId ni DocumentNumber:
      docTipo = 99 ; docNro = 0  (consumidor final sin identificar)
      → FIN

F. FALLBACK SEGURO (tipo desconocido, número inválido para el tipo, o DV CUIT inválido):
      docTipo = 99 ; docNro = 0
      // Emitir como consumidor final sin identificar es el fallback que NO miente
      // sobre la identidad. PERO está sujeto al tope de monto (§3.1).
      // Si ImpTotal supera el tope vigente → NO emitir: marcar la Invoice como
      // requiere-dato-fiscal y avisar al usuario (no POSTear a ARCA un 99 que rebotará).
```

`RIESGO (decisión B vs A):` si el cliente cargó CUIT en `TaxId` y además un pasaporte en `DocumentType/DocumentNumber`, esta spec prioriza el CUIT (regla A). Es lo correcto fiscalmente para un receptor con CUIT. `ASUNCIÓN — confirmar con contador:` que para receptor con CUIT siempre se use DocTipo=80 aunque tenga otro documento cargado.

### 3.5 Caso borde crítico — Pasaporte / CI extranjera alfanuméricos

`RIESGO FISCAL (alto):` `DocNro` en el envelope WSFE es **numérico** (hoy `docNro` es `long`, `AfipService.cs:1085`). Un pasaporte "AB123456" NO se puede representar como `long`.

Opciones para backend (decisión de arquitectura + contador):
- **Opción 1 (conservadora):** si `DocumentType` es Pasaporte/CI extranjera y el número es alfanumérico → tratar como **consumidor final sin identificar** (DocTipo=99, DocNro=0), sujeto al tope §3.1. No miente la identidad, pero pierde el dato.
- **Opción 2:** capturar/derivar un identificador numérico válido (ej. CUIT de no residente, si la operación lo requiere). Requiere dato adicional del cliente.

`ASUNCIÓN — confirmar con contador:` cuál corresponde para turismo emisor Monotributo (Factura C) con pasajero extranjero. La práctica común en factura C a consumidor final extranjero es DocTipo=99 (consumidor final), salvo que la operación sea exportación de servicios (factura E, fuera de este ADR). **No inventar** un DocTipo numérico para un pasaporte.

`No verificado:` si ARCA acepta hoy DocTipo=94 (Pasaporte) con DocNro=0 o exige número. No POSTear especulativamente; homologar.

---

## 4. DESCONEXIÓN 2 — Poblar `CondicionIVAReceptorId` desde el cliente

### 4.1 Tabla oficial de condición IVA del receptor (RG 5616)

`Hecho verificado:` (fuente: tabla CondicionIVAReceptorId, AfipSDK + RG 5616/2024 — ver §13; consultar 2026-06-12). Columna "Clases aplicables" indica en qué clase de comprobante es válido el código:

| Id | Descripción | Clases aplicables |
|---|---|---|
| 1 | IVA Responsable Inscripto | A / M / C |
| 6 | Responsable Monotributo | A / M / C |
| 13 | Monotributista Social | A / M / C |
| 16 | Monotributo Trabajador Independiente Promovido | A / M / C |
| 4 | IVA Sujeto Exento | B / C |
| 5 | Consumidor Final | B / C |
| 7 | Sujeto No Categorizado | B / C |
| 8 | Proveedor del Exterior | B / C |
| 9 | Cliente del Exterior | B / C |
| 10 | IVA Liberado – Ley N° 19.640 | B / C |
| 15 | IVA No Alcanzado | B / C |

`Hecho verificado:` para **comprobantes clase C** (caso del emisor Monotributo actual), **todos** los códigos son aceptados (la columna marca "C" tanto en el bloque A/M/C como en el B/C). Es decir: en Factura C el `CondicionIVAReceptorId` no restringe la emisión por clase, pero **debe ser un código válido** (no puede ir vacío ni un número fuera de tabla → error 10242/10246).

`RIESGO:` el comentario en `GetConditionIvaId` (`AfipService.cs:2020-2030`) lista códigos parcialmente desalineados con la tabla verificada: dice "11: IVA Responsable Inscripto - Agente de Percepción" que NO aparece como código de condición IVA receptor en la tabla RG 5616 verificada. **No usar el código 11 como condición IVA receptor.** Backend debe corregir el comentario y la lógica a la tabla §4.1.

### 4.2 De dónde tomar el dato (snapshot del cliente)

`Hecho verificado:` el comprobante se arma desde `CustomerSnapshot` (inmutable). La condición IVA debe salir de ESE snapshot, no del Customer vivo. Orden de preferencia:

```
ENTRADA: cust = deserialize(CustomerSnapshot) ; docTipo ya resuelto (§3)

1. Si cust.TaxConditionId tiene valor Y es un código válido de la tabla §4.1:
      condicionIva = cust.TaxConditionId
      → FIN   (dato explícito del cliente, máxima confianza)

2. Si TaxConditionId es null pero cust.TaxCondition (texto) matchea inequívocamente:
      "responsable inscripto" (sin "monotributo")  → 1
      "monotributo" / "monotributista"             → 6
      "exento"                                      → 4
      "consumidor final"                            → 5
      → FIN  (parseo de texto, confianza media)

3. DERIVACIÓN CONSERVADORA desde docTipo (snapshot viejo sin TaxConditionId ni texto claro):
      docTipo 99 (sin identificar)  → 5  (Consumidor Final)
      docTipo 96/94/91/89/90 (DNI/Pasap/CI/LE/LC, persona física sin CUIT) → 5 (Consumidor Final)
      docTipo 86 (CUIL)             → 5  (Consumidor Final)  [persona física]
      docTipo 80 (CUIT)            → ver regla 4 (NO asumir)
      → FIN salvo CUIT

4. docTipo = 80 (CUIT) sin condición conocida:
      [ASUNCIÓN — confirmar con contador] default = 5 (Consumidor Final)
      // Tener CUIT NO implica ser RI ni Monotributo (puede ser CUIT de consumidor
      // final / no inscripto en IVA). Defaultear a 5 es conservador y válido en C/B.
      // El riesgo de defaultear a RI(1) o Mono(6) sin saber es PEOR (afirma una
      // condición fiscal falsa del receptor). 5 no afirma inscripción.
      → FIN
```

`RIESGO:` el código actual con `taxCondition=null` y CUIT (docTipo 80) ya retorna 5 — coincide con el default conservador de la regla 4. Pero NUNCA usa `TaxConditionId` cuando SÍ existe (reglas 1-2), que es el bug a corregir. El fix principal es **pasar el dato del snapshot a la función**, no inventar lógica nueva.

### 4.3 Regla por tipo de comprobante del EMISOR

`Hecho verificado:`

- **Emisor Monotributo → Factura C (11) / NC C (13) / ND C (12):** todos los códigos §4.1 son válidos en clase C. El `CondicionIVAReceptorId` se informa por obligatoriedad RG 5616 pero **no cambia el IVA** (la C no discrimina IVA, `ImpIVA=0`, ver `AfipService.cs:1186`). Riesgo de rechazo solo si el código es inválido/ausente.

- **Emisor RI → Factura A (1):** el sistema solo emite A cuando el cliente es RI (`AfipService.cs:597-603`). Por lo tanto, en A el `CondicionIVAReceptorId` debe ser de la columna **A/M/C** (1, 6, 13, 16). `RIESGO:` si el cliente es Monotributo y el emisor RI, hoy `CreatePendingInvoice` arma **B (6)** (porque el `if` solo da A cuando el cliente es exactamente "Responsable Inscripto"). `ASUNCIÓN — confirmar con contador:` desde RG 5003/2021, a un Monotributista un RI debería emitir **A** (no B). Esto es una posible incongruencia del tipo de comprobante, no de este ADR, pero se señala porque afecta qué `CondicionIVAReceptorId` corresponde. **Confirmar con contador la matriz cliente→clase para emisor RI.**

- **Emisor RI → Factura B (6):** consumidor final / exento / no categorizado → códigos columna B/C (4, 5, 7, 8, 9, 10, 15).

`RIESGO (coherencia clase ↔ condición):` backend debe garantizar que el `CondicionIVAReceptorId` elegido sea válido para la CLASE del comprobante que se emite. Si el emisor pasa a RI y emite A a un receptor con condición 5 (Consumidor Final, NO válido en A) → **rechazo**. La elección de clase (§3 de `CreatePendingInvoice`) y la condición IVA deben quedar consistentes. Para el emisor Monotributo actual (todo C) no hay conflicto.

### 4.4 Fallback seguro (no generar 10246/10242)

`Hecho verificado:` errores ARCA relevantes — 10242 ("CondicionIVAReceptor no válido / obligatorio"), 10246 (validación condición IVA receptor vs clase/condición emisor).

```
FALLBACK CondicionIVAReceptorId:
  - Si tras §4.2 no se pudo determinar un código válido para la clase del comprobante:
       → usar 5 (Consumidor Final) SI la clase lo admite (B o C).
       → Si la clase es A (no admite 5): NO emitir A a ese receptor; revisar la
         clase del comprobante (probablemente debería ser B). NUNCA forzar un código
         inválido para la clase.
  - NUNCA enviar el nodo vacío ni un código fuera de la tabla §4.1.
```

`RIESGO:` defaultear a 5 en clase C es seguro hoy (emisor Mono). Si en el futuro el emisor es RI y un comprobante intenta clase A con fallback 5, el sistema debe degradar la clase a B antes que mandar 5 en A. Esa coherencia es responsabilidad del armado del comprobante, no del fallback de condición.

---

## 5. Datos fiscales requeridos (qué debe capturar/leer el sistema)

| Dato | Origen | Estado |
|---|---|---|
| DocTipo | derivado de `DocumentType` + `TaxId` (snapshot) | HOY mal derivado |
| DocNro | `TaxId` / `DocumentNumber` (snapshot) | OK salvo alfanuméricos |
| CondicionIVAReceptorId | `TaxConditionId` (snapshot) con fallbacks | HOY siempre null→5 |
| Clase del comprobante | condición fiscal del EMISOR + cliente | OK para Mono; revisar RI→Mono |
| Snapshot inmutable del cliente | `CustomerSnapshot` JSON | OK (ya existe) |

`Riesgo fiscal:` `CustomerSnapshot` viejos pueden no tener `TaxConditionId` poblado (campo agregado después). Las reglas §4.2 (pasos 2-4) lo cubren con derivación conservadora. Backend NO debe asumir que el snapshot siempre trae `TaxConditionId`.

---

## 6. Impacto en facturación

- **DocTipo correcto** evita emitir comprobantes que afirman identidad falsa (extranjero como DNI argentino). Reduce rechazos 10074/10246 y problemas de validez del comprobante.
- **CondicionIVAReceptorId correcto** es **obligatorio RG 5616**. Hoy se manda 5 por accidente (porque null→5). Funciona para consumidor final, pero es **incorrecto** para cualquier receptor RI/Monotributo/Exento, y será fuente de inconsistencias cuando el emisor pase a RI y emita A/B.
- **No hay cambio de importes** en Factura C (IVA sigue 0). El fix es de **identificación del receptor**, no de cálculo.

---

## 7. Impacto en IVA / Ganancias / retenciones / percepciones

- `IVA:` en Factura C el `CondicionIVAReceptorId` no altera el IVA (sigue 0). En A/B (emisor RI) sí condiciona la validez pero el cálculo de IVA del envelope ya existe; este ADR no lo toca.
- `Ganancias:` sin impacto directo. La correcta identificación del receptor mejora la trazabilidad de ingresos, pero no cambia el reconocimiento.
- `Retenciones / percepciones:` `No verificado` / fuera de alcance. La condición IVA del receptor puede gatillar regímenes de percepción cuando el emisor sea RI, pero eso es materia provincial (IIBB) o de régimen específico. `Necesita confirmación: jurisdicción provincial/municipal aplicable.` No se modela acá.

---

## 8. Impacto en cancelaciones / notas de crédito / reembolsos

`Hecho verificado:` NC C (13) y ND C (12) son clase C → mismas reglas §4 que la factura. La NC/ND **debe heredar el mismo DocTipo, DocNro y CondicionIVAReceptorId del comprobante asociado** (el receptor es el mismo). Backend debería tomar estos datos del snapshot de la Invoice ORIGINAL (o de su `CustomerSnapshot`) para que la NC/ND no diverja del receptor de la factura original.

`RIESGO:` si la corrección de DocTipo/condición se aplica solo a facturas nuevas y la NC se arma releyendo el snapshot de la original (que pudo emitirse con el bug), la NC puede salir con DocTipo distinto que la factura. `ASUNCIÓN — confirmar con contador:` que una NC asociada conserve exactamente el DocTipo/DocNro de la factura original aunque hoy se corrija el algoritmo (coherencia del par factura↔NC).

---

## 9. Interacción necesaria con contabilidad

Involucrar a `accounting-expert-argentina` / `travel-agency-accountant-argentina` para:
- Confirmar que el snapshot fiscal del receptor (DocTipo + condición IVA) quede registrado de forma auditable en la Invoice al emitir (evidencia fiscal del momento del hecho).
- El campo menor `IssuedAt`/`IssuedByUserId` que backend va a agregar: **tiene arista fiscal** — es la evidencia de quién y cuándo emitió. No es la fecha del comprobante (`CbteFch`), pero sí parte del rastro de auditoría que un contador/inspección pediría. Recomendado persistirlo junto al snapshot de DocTipo/condición usado.

---

## 10. Necesita confirmación profesional

1. `ASUNCIÓN — confirmar con contador:` default DocTipo=96 (DNI) cuando hay número suelto sin `DocumentType` (§3.4 regla C).
2. `ASUNCIÓN — confirmar con contador:` tratamiento del pasaporte/CI extranjera alfanumérico (DocTipo=99 vs capturar identificador numérico) (§3.5).
3. `ASUNCIÓN — confirmar con contador:` para receptor con CUIT, usar siempre DocTipo=80 aunque tenga otro documento cargado (§3.4 regla A).
4. `ASUNCIÓN — confirmar con contador:` default CondicionIVAReceptorId=5 (Consumidor Final) para CUIT sin condición conocida (§4.2 regla 4).
5. `ASUNCIÓN — confirmar con contador:` matriz cliente→clase para emisor RI (RI a Monotributo: ¿A o B? RG 5003/2021) (§4.3).
6. `ASUNCIÓN — confirmar con contador:` tope de monto vigente de consumidor final sin identificar (DocTipo=99) (§3.1).
7. `ASUNCIÓN — confirmar con contador:` que la NC/ND herede DocTipo/DocNro/condición de la factura original (§8).
8. **Homologación obligatoria contra el ambiente de testing de ARCA** antes de prender el comportamiento en producción (DocTipo=94/91, condición IVA por código real).

---

## 11. Criterios de aceptación (para backend / QA)

Comportamiento esperado tras el fix:

1. Cliente con `TaxId` CUIT válido (11 díg, DV ok) → DocTipo=80, DocNro=CUIT, condición = `TaxConditionId` si existe, si no 5.
2. Cliente con `DocumentType="Pasaporte"` y número alfanumérico → NO sale como DNI; sale según §3.5 (Opción aprobada por contador). NUNCA DocTipo=96.
3. Cliente con `DocumentType="DNI"` y DNI numérico → DocTipo=96, DocNro=DNI, condición 5 (Consumidor Final) salvo `TaxConditionId` distinto.
4. Cliente sin TaxId ni DocumentNumber → DocTipo=99, DocNro=0, condición 5.
5. Cliente con `TaxConditionId=1` (RI) → `CondicionIVAReceptorId=1` en el envelope (no 5).
6. Cliente con `TaxConditionId=6` (Monotributo) → `CondicionIVAReceptorId=6`.
7. `CustomerSnapshot` viejo sin `TaxConditionId` pero con `TaxCondition="Responsable Inscripto"` → condición 1 (parseo texto §4.2 regla 2).
8. CUIT con DV inválido → NO se emite DocTipo=80; cae a fallback §3.4 regla F (99/0) o se bloquea con mensaje, NUNCA se POSTea un CUIT inválido.
9. Nunca se emite `CondicionIVAReceptorId` vacío ni fuera de la tabla §4.1.
10. NC/ND asociada conserva el mismo receptor (DocTipo/DocNro/condición) que la factura original.
11. Emisor Monotributo: todo lo anterior con clase C (IVA=0). El `CondicionIVAReceptorId` no debe romper la emisión C con ningún código válido.

---

## 12. Tests fiscales que backend debe escribir

Tests unitarios sobre el mapeo (sin tocar ARCA real):

- `DocTipo_CuitValido_Mapea80` — TaxId 11 díg DV ok → (80, cuit).
- `DocTipo_CuitDvInvalido_CaeAFallback` — TaxId 11 díg DV malo → (99, 0) o bloqueo, NO 80.
- `DocTipo_Pasaporte_NoSaleComoDni` — DocumentType="Pasaporte", número alfanumérico → resultado §3.5, **assert que NO es 96**.
- `DocTipo_PasaporteVariantes_Normaliza` — "PASAPORTE", "pasaporte", "Pasaporte" → mismo DocTipo (94 o 99 según decisión).
- `DocTipo_DniNumerico_Mapea96` — DocumentType="DNI" → (96, dni).
- `DocTipo_SinTipoConNumero_DefaultDni` — DocumentType vacío + número numérico → 96 (regla C, marcar como asunción).
- `DocTipo_SinDato_ConsumidorFinal` — sin TaxId ni DocumentNumber → (99, 0).
- `DocTipo_DocumentTypeDesconocido_Fallback99` — DocumentType="Carnet" → (99, 0).
- `CondicionIva_TaxConditionId1_Emite1` — TaxConditionId=1 → 1 (no 5).
- `CondicionIva_TaxConditionId6_Emite6` — Monotributo → 6.
- `CondicionIva_SnapshotViejoSinId_ParseaTexto` — TaxConditionId=null, TaxCondition="Responsable Inscripto" → 1.
- `CondicionIva_CuitSinCondicion_Default5` — CUIT, sin TaxConditionId ni texto → 5.
- `CondicionIva_NuncaVacio` — cualquier entrada → código ∈ tabla §4.1 (assert pertenencia).
- `CondicionIva_ValidaParaClaseC` — todo código generado es válido en C.
- `Envelope_NcHereda_ReceptorDeFacturaOriginal` — NC asociada usa mismo DocTipo/DocNro/condición que la original.
- (Si se implementa emisor RI) `CondicionIva_ClaseA_NoAdmite5` — armar A con receptor consumidor final → degrada a B o bloquea, NUNCA manda 5 en A.

Estos tests deben blindar el MISMO código que corre en producción (mismo patrón que `AfipServiceMonedaSoapFormatTests` / `AfipServiceCanMisMonExt*`).

---

## 13. Fuentes (consulta 2026-06-12)

- Tabla CondicionIVAReceptorId con clases A/M/C vs B/C: AfipSDK (blog error 10242) — https://afipsdk.com/blog/factura-electronica-solucion-a-error-10242/
- Tabla FEParamGetTiposDoc (tipos de documento): SistemasAgiles WSFEv1 (PyAfipWs, referencia comunitaria canónica) — https://www.sistemasagiles.com.ar/trac/wiki/ProyectoWSFEv1
- RG 5616/2024 (obligatoriedad CondicionIVAReceptorId + moneda extranjera): SimpleSoftware — https://www.simplesoftware.com.ar/arca-establecio-nuevas-condiciones-para-factura-electronica-por-webservice-de-operaciones-en-moneda-extranjera-segun-rg-5616-2024/ ; IdEAL Software — https://idealsoftware.com.ar/arca-establece-nuevas-condiciones-para-la-facturacion-electronica-segun-la-rg-5616-2024novedades-en-las-facturas-serie-b-transparencia-fiscal-al-consumidor/
- Manual del desarrollador ARCA COMPG v4.0 (fuente oficial primaria, tablas en PDF): https://www.afip.gob.ar/ws/documentacion/manuales/manual-desarrollador-ARCA-COMPG-v4-0.pdf

---

## 14. No verificado

- `No verificado: vigencia normativa actual.` Las tablas se verificaron contra fuentes oficiales/comunitarias el 2026-06-12, pero los códigos de ARCA pueden actualizarse por norma. Validar con `FEParamGetTiposDoc` y `FEParamGetCondicionIvaReceptor` (los propios métodos del WS devuelven la tabla vigente) y/o el manual oficial vigente antes de prender el comportamiento.
- `No verificado:` si ARCA acepta DocTipo=94 (Pasaporte) / 91 (CI Extranjera) con DocNro=0 en Factura C, o exige número. Homologar.
- `No verificado:` tope de monto exacto de consumidor final sin identificar (DocTipo=99) — cambia por norma.
- `No verificado:` matriz exacta cliente→clase para emisor RI (RG 5003/2021 RI→Monotributo). Fuera del emisor Monotributo actual; confirmar con contador antes de habilitar RI.
- `No verificado:` el código "11" mencionado en el comentario actual de `GetConditionIvaId` como "RI Agente de Percepción" NO figura en la tabla CondicionIVAReceptorId verificada — el comentario del código está desalineado y debe corregirse a la tabla §4.1.
