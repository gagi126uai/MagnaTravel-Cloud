# 2026-08-05 — El día de los 8 frentes

Gaston pidió cerrar EN UN DÍA todos los frentes pendientes pre-rediseño + los 4 bugs de su
prueba integral. Se cerraron los 8 bloques (uno resultó ya construido). 6 deploys, todos con
CI verde. Explicación nivel trainee, bloque por bloque.

## Bloque 0 — Los 4 bugs de la prueba integral (`ff4e04dd`, verificado EN PROD por keeper)

1. **"Falta facturar" fantasma**: una reserva Perdida mostraba "falta facturar $700/US$7.470"
   con venta firme $0. Causa: el cuadre usaba la venta COTIZADA (`TotalSale`) mientras
   "Vendido firme" usaba la EXIGIBLE (`ConfirmedSale`). El fix alineó TODO a la venta firme
   (regla única F-1): ficha, eje materializado, listado, resumen y bandeja de Facturación
   (estos dos últimos los cazó el reviewer — habrían quedado divergentes), el SQL compartido
   del backfill, y una migración nueva que re-proyectó el dato ya guardado en PROD (sin ella,
   el chip del listado quedaba viejo hasta que la reserva moviera plata).
2. **"Eliminar" sobre un cobro**: la palabra murió. El botón dice "Deshacer", confirma en
   criollo, y rutea por estado al camino con rastro (soft-delete en vivos, anulación con
   auditoría en finales). El motor ya hacía lo correcto (nunca borró); era palabra y cableado.
   De paso: la causa real era que 2 de 3 variantes de la fila no aplicaban el candado.
3. **Saldo del extracto**: "-$ 5.000,00" pelado → "$ 5.000,00 a favor".
4. **Margen negativo**: violeta como ganancia → rojo con "Pérdida de $X", cada moneda su signo.
   De yapa: murió el símbolo duplicado "US$ US$" en ~15 pantallas (el badge es la única
   fuente del símbolo) y se sanearon 2 tests con lógica replicada (falso verde conocido).

## Bloque 1 — Documentación de viaje (`345e4ebf`)

- El aviso de pasaporte ya no aparece en viajes 100% cabotaje (falso aviso: con DNI alcanza).
  Conservador: sin ámbito definido, sigue avisando. Aplicado también en la CAMPANITA (el
  reviewer cazó que tenía su propia regla — dos superficies, dos verdades) y calculado sobre
  servicios VIVOS (un tramo cancelado ya no cuenta).
- El texto ámbar ya no afirma "6 meses" como LA regla: "Verificá el requisito del destino:
  cada país pide una vigencia distinta."
- Nace el chip ámbar "Menor: revisar autorización de salida" (menor de 18 + tramo
  internacional; aviso, jamás freno; diseño derivado de patrones firmados, label a validar).
- La CONSTANCIA "le avisé los requisitos" quedó DISEÑADA con 10 preguntas para Gaston
  (docs/ux/specs/2026-08-05-constancia-aviso-pasajero.md, no construir sin firma).

## Bloque 2 — Limpieza de datos de prueba

La entrada "AEP-COR PRUEBA DNI" del tarifario YA NO EXISTE (verificado). F-2026-1065 ya está
Anulada y el motor correctamente no deja archivar anuladas. HALLAZGO: 14 tarifas "Prueba
Claude" + 2 operadores de prueba de julio ensucian el tarifario → pregunta a Gaston (nada se
borra sin su palabra).

## Bloque 3 — Chicos técnicos (`95775546`)

- **PUT de operador**: omitir un campo ya no pisa la config de multas guardada. Finura: la
  guarda ingenua habría roto el reset explícito legítimo ("No se sabe" elegido a propósito) —
  se distingue campo AUSENTE de campo ENVIADO leyendo el JSON crudo. Con candado de tamaño,
  validación de nombre (400, no 500) y contrato idéntico al anterior.
- **Redondeos de centavos** en el reparto de multa entre renglones: la fuga real (el último
  renglón clampeado perdía el residuo) + el caso simétrico de sobre-asignación con topes
  empatados (cazado por el reviewer con contraejemplo). Tres pases con invariante PROBADA:
  suma de las partes == multa. Los otros 2 sitios sospechados cierran por construcción
  (fijados con tests).
- Del diagnóstico: "multa > total factura" YA estaba resuelto; el backfill de moneda del pago
  proveedor ya corrió el 21/07. La inmutabilidad post-confirmación es OBRA GRANDE → anotada.

## Bloque 4 — Emergentes fase 2: YA ESTABA CONSTRUIDO

Sorpresa: el inventario COMPLETO de la spec firmada del 22/07 (6 casos + P1=A) ya migra por
el Cartel Emergente único desde julio. La memoria decía "fase 2 pendiente" y estaba vieja.
Lo único que faltaba: la sección prometida en la guía UX (agregada). Quedan 2 "¿Seguro?"
viejos con estética propia como mejora menor futura.

## Bloque 5 — Candado de facturar con cambios sin revisar (`d9eecce0`, ADR-043 Fase 1)

La marca "el operador avisó cambios" (que ya prendía avisos) ahora FRENA la factura de venta:
capacidad en No con motivo criollo, guard real en el motor con código estable, botón de la
ficha se apaga solo (el front ya reflejaba capacidades). La NC/ND de una anulación sale
igual (el flujo de anular jamás se traba). Anotado para fase 2: el REINTENTO de una factura
rechazada no re-chequea la marca + la mesa de renglones completa.

## Bloque 6 — EL NORTE: el dólar oficial entra solo (`090dc461` + `71a7e3de`, ADR-011)

El circuito completo (fiscal → arquitecto → arch-reviewer → backend → 3 reviewers → front →
reviewer) con hallazgos que cambiaron el diseño:

- **El experto fiscal descubrió que ARCA misma publica el dólar oficial por el MISMO web
  service que ya usamos para facturar** (FEParamGetCotizacion) — sin scraping, y es la única
  fuente que garantiza pasar la validación de banda de ARCA. Eso mató a los proveedores
  de terceros del diseño original.
- **Libreta de cotizaciones** (tabla nueva): historial inmutable por moneda+fecha+fuente,
  corrección por reemplazo (jamás update), homologación separada de producción.
- **Trabajo diario** (12:00 AR): trae el dólar de hoy + repara 7 días. SOLO el trabajo habla
  con ARCA; jamás renueva el ticket que usan las facturas. Las pantallas leen de la libreta.
- **Etiqueta fiscal honesta**: el SERVIDOR decide "salió de ARCA" o "a mano" por igualdad
  exacta; el navegador ya no puede forzar la fuente (mató el bug de la etiqueta falsa fija).
- **Pantallas**: el TC se precarga con la sugerencia (editable), justificación SOLO si
  escribís otro número, y murió la franja que mentía "BNA vendedor divisa".
- Bloqueantes cazados por reviews ANTES de deploy: query no traducible a Postgres (habría
  matado la emisión USD en la primera factura), procedencia forzable por HTTP en 3 caminos,
  botón de la ficha muerto en el caso normal, y el guardián del borrado selectivo exigió
  clasificar la tabla nueva (único CI rojo del día, arreglado en un commit).
- **Pendiente de verificar en vivo**: que ARCA conteste en homologación (la libreta arranca
  vacía y degrada seguro a carga manual). El "CanMisMonExt" que la memoria vieja marcaba
  como bloqueante YA ESTABA construido y verificado contra el XSD.

## Bloque 7 — Adelanto a cuenta: SPEC escrita

docs/especificaciones/2026-08-05-adelanto-a-cuenta-cliente.md — reusa el bolsillo de saldo a
favor existente (4º origen), recibo interno, aplicación con el botón que ya existe. Lo fiscal
en rojo para el contador (bloquea construcción). 3 preguntas para Gaston.

## Lo que NO quedó verificado (honestidad, PR-3)

- **Verificación VISUAL en PROD de los bloques 1, 3, 5 y 6**: la sesión del keeper se venció
  con el deploy del mediodía y Gaston no se relogueó. El Bloque 0 SÍ está verificado visual
  (8 chequeos con captura). Todo lo demás: CI verde con suites completas + integración
  Postgres, deploy sano — pero ningún ojo lo vio en pantalla.
- ARCA en homologación (cotización real, formato de fecha) — primero a probar.
- Los tests de integración Postgres de cada bloque corrieron en CI, no localmente (sin Docker).

## Decisiones/preguntas elevadas a Gaston

Ver el paquete único del día (memoria de retomo): 4 del dólar, 10 de la constancia, 4 de las
pantallas de facturar, 3 del adelanto, label del chip menor, "Deshacer" en congeladas,
archivar datos de prueba (1063/1064 + 14 tarifas Prueba Claude), factura proveedor solo
confirmados, y la lista de 8 puntos para el CONTADOR REAL del informe fiscal.
