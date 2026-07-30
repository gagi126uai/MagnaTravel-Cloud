# 2026-07-29 — Backup/restore arreglado de punta a punta y datos recuperados

> Explicación en criollo de lo que pasó hoy, para cualquiera que agarre esto sin contexto.

## De dónde veníamos

La obra "Empezar de cero / Volver atrás" (ADR-051) estaba en PROD pero con dos bugs que
la hacían inusable, y — lo más grave — **la base de PROD estaba vacía de datos de negocio**:
la prueba real del 28/07 había borrado todo (con su resguardo bien hecho), y la restauración
desde la app estaba trabada por los bugs. Los datos reales vivían solo adentro del archivo
`wipe-20260727-223313.dump`.

## Qué se arregló hoy (commits `a6593263` api · `a90084b3` front · `6b77d7af` tests · `53f6a6e3` ops)

1. **El borrado por grupos abortaba siempre** que se pedían "clientes + reservas y su plata".
   Causa: en la base de PROD sobreviven ~16 tablas del esquema VIEJO (de antes de enero) que
   ya no existen en el modelo actual — cupos, conciliación BSP, tesorería vieja, renglones de
   factura con nombre en plural, versiones de presupuestos. Ninguna estaba clasificada en un
   grupo, y una de ellas apuntaba a la tabla de reservas: la red de seguridad (que está bien
   diseñada: ante lo desconocido, aborta sin tocar nada) frenaba todo. El arreglo: esas tablas
   viejas ahora están clasificadas y el borrado solo trunca las que existen de verdad en la
   base. Se validó contra PROD ANTES de deployar: las 16 tablas existen, están TODAS vacías,
   y sus claves foráneas coinciden exactamente con el análisis.
2. **Cada intento rechazado dejaba un resguardo huérfano.** Ahora todas las validaciones que
   pueden rechazar corren ANTES de generar el resguardo.
3. **"Restaurar todo" pedía un motivo que la pantalla no tenía dónde escribir** (bug que
   reportó Gaston). Ahora el modal tiene el campo "Motivo — obligatorio para 'Restaurar todo'",
   con el botón deshabilitado y el aviso visible hasta completarlo.

Todo pasó por 4 revisiones (backend, seguridad de datos, front, exposición de internals) +
2 re-reviews de los bloqueantes; CI verde con los 4 tests de integración nuevos contra
Postgres real.

## La recuperación de los datos (lo importante del día)

Al intentar "Restaurar todo" desde la app con el resguardo del 27/07, la red de compatibilidad
lo **rechazó bien**: ese resguardo es anterior al deploy que aplicó 2 migraciones de esquema,
y restaurarlo crudo dejaría la base sin columnas que el sistema actual necesita.

El camino correcto fue el workflow de operaciones (`ops-restore.yml`, acción
`restaurar-tablas`), al que hoy se le agregaron dos cosas para poder traer TODO el negocio y
no solo configuración: `--disable-triggers` (las referencias circulares reserva↔presupuesto no
tienen ningún orden de carga válido) y la sincronización de las secuencias de numeración (sin
eso, la próxima alta repetiría un número ya usado).

**Resultado verificado contra PROD**: 16 clientes, 59 reservas, 97 facturas (105 renglones),
142 cobros, 148 movimientos de caja, 34 tarifas, 7 operadores, 60 pasajeros — y la app los ve.

## La prueba final del ciclo completo (por la app, en PROD)

Borrado del grupo más chico (posibles clientes, 3 registros) → generó su resguardo fresco →
**"Restaurar todo" desde ese resguardo: HTTP 200 en 3 segundos** → los presupuestos volvieron.
El ciclo entero (borrar → resguardo → restaurar) funciona de punta a punta desde la pantalla.

## Limpieza hecha

- Cliente de prueba "PRUEBA RESTAURAR CLAUDE": se fue con el borrado selectivo.
- Usuario temporal "PRUEBA AUTOMATICA": borrado por API (HTTP 204).
- Ojo: los resguardos generados HOY (`wipe-20260729-*.dump`) todavía contienen a ese usuario
  temporal; si algún día se restaura uno de esos, el usuario vuelve (inofensivo, pero se sabe).

## Deudas y hallazgos NUEVOS de hoy

1. **⭐ Producto: los resguardos "vencen" con cada deploy que trae una migración.** La red de
   compatibilidad de "Restaurar todo" exige igualdad exacta de migraciones, así que cualquier
   resguardo anterior al último cambio de esquema queda inutilizable desde la app (hoy pasó
   con un resguardo de 2 días). La obra que falta: que la restauración total aplique las
   migraciones que falten después de restaurar (restore + migrate), con diseño y firma del
   dueño. Mientras tanto, el camino es `ops-restore.yml` (documentado y probado hoy).
2. **Los 4 adjuntos** (vouchers/archivos de reservas) recuperados por tabla apuntan a archivos
   de MinIO que el borrado original movió a resguardo; puede que la descarga de esos 4 falle
   hasta reponerlos. Menor, pero anotado.
3. El script del nginx del HOST (`ops-nginx`) tuvo review de infra con hallazgos serios
   (elección de backup equivocada en "revertir", preflight de sudo que fallaba siempre, riesgo
   de backup-symlink); se está rehaciendo con esos arreglos. Nota: con el tamaño actual de la
   base, "Restaurar todo" tardó 3 segundos — el timeout de 60s de nginx no molesta HOY, pero
   va a molestar cuando la base crezca.

## Anexo (misma noche): la obra de fondo también quedó construida (ADR-052)

La deuda estrella de la mañana ("los resguardos vencen con cada deploy que trae una migración")
se diseñó, se desafió, se construyó y se deployó EN EL DÍA, con las tres decisiones firmadas por
el dueño: aceptar resguardos de versiones anteriores con actualización automática del esquema,
volver solo atrás si esa actualización falla, y aviso claro sin fricción extra (más una línea en
el "¿Seguro?" final).

Cómo funciona ahora "Restaurar todo": el resguardo se restaura a una base NUEVA al costado (un
archivo dañado ya no puede tocar la base viva — antes sí podía), recién si todo salió bien se
intercambian los nombres, se aplican las actualizaciones de esquema que falten con sus rellenos
de datos, y se fuerza AFIP a homologación. Si algo falla después del intercambio, la vuelta
atrás es el intercambio inverso: segundos, sin segunda restauración. El único caso que deja el
sistema frenado en mantenimiento es el doble fallo (falla la actualización Y falla la vuelta),
y queda auditado siempre, aunque el navegador ya se haya desconectado.

La pantalla marca cada resguardo con su versión ("Versión anterior" / "Versión más nueva" /
"Versión desconocida") y avisa en criollo qué va a pasar. Ningún botón se apaga por una
sospecha: el único freno es el chequeo definitivo del motor, que rechaza sin tocar nada.

Números del cierre: 2 rondas de arquitectura (4 bloqueantes de diseño cazados antes de codear),
4 revisiones + 2 re-verificaciones (2 bloqueantes graves de seguridad + 2 de front + 1 de
backend, todos cerrados), 4244 tests unitarios y 10 tests de integración del intercambio de
bases contra Postgres real, CI verde y deploy automático.

Queda para el dueño la verificación manual del ADR §7 en su navegador: restaurar el resguardo
del 27/07 (el que hoy a la mañana era imposible) viendo el badge, el cartel y el sistema
poniéndose al día solo — y comprobar que los saldos y la caja no quedaron en cero.
