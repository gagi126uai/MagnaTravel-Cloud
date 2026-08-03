# 2026-08-03 — La obra triple que quedó a medio hacer el 31/07, cerrada y en producción

## De dónde veníamos

El 31/07 a la tarde Gaston pidió una obra triple (detallecitos del día + deudas de copias de
seguridad + bugs medianos del 25/07) y a mitad de camino hubo que apagar la PC de urgencia.
Quedó TODO implementado pero sin commitear, sin revisar y sin deployar: 42 archivos tocados
esperando en la máquina. Hoy se retomó exactamente ahí y se llevó hasta producción.

## Qué se hizo hoy, paso a paso

1. **Tanda Q (QA)**: se agregaron los tests que faltaban — que los duplicados respondan con el
   código correcto Y el mensaje en criollo, que el freno de duplicados llegue bien desde la
   pantalla hasta el motor, y la lógica nueva de espera de restauración que no tenía ni un test.
2. **4 reviews** (backend, frontend, seguridad y el gate de exposición de datos): encontraron
   **7 bloqueantes reales**. Los importantes:
   - **La purga de tareas de fondo borraba lo que no debía**: las tareas de fondo (Hangfire)
     viven adentro de la misma base que se restaura, así que las tareas "viejas" mueren solas
     con el cambio de base; lo que la purga mataba eran las tareas que venían ADENTRO de la
     copia — incluidas facturas esperando el CAE — dejándolas colgadas para siempre y sin rastro.
   - **El timeout del proxy rompía la espera de restauración**: una restauración tarda minutos,
     el nginx del host corta a los 60 segundos, y ese corte apagaba la pantalla de mantenimiento
     y mostraba la página de error cruda de nginx.
   - **El candado de paquete/asistencia era más estricto que el motor**: escondía el botón
     "Marcar confirmado" en casos que el motor aceptaba, con un texto que encima mentía.
   - **El recorte de espacios quedó a medias**: comparaba sin espacios pero guardaba con
     espacios, salteando los candados de voucher/factura.
3. **Decisión de Gaston sobre la purga** (firmada hoy): **"limpiar y avisar"** — nada se dispara
   solo después de una restauración, pero el sistema clasifica lo que frenó (emisión de
   comprobante / anulación / nota de crédito parcial / otras), deja el desglose en criollo en la
   auditoría y avisa en el resumen si quedaron comprobantes a medio emitir o anular. Una purga
   que falla o queda incompleta ya no se disfraza de limpia: avisa igual.
4. **Fixes + re-reviews**: cada bloqueante se arregló y se re-verificó con el reviewer que lo
   había encontrado. Los fixes trajeron 3 defectos nuevos (pasa siempre) que también se cazaron
   y arreglaron: la nota de crédito parcial que el clasificador no conocía, el aviso que se
   callaba justo cuando la purga fallaba, y el candado que quedaba mudo si fallaba la consulta.
5. **Commits y deploy**: dos commits en criollo (backend `98022c98`, frontend `1b42e9f5`),
   push a main, CI verde completo (tests unit + integración contra Postgres — los de la purga
   corrieron por primera vez ahí) y deploy al VPS.

## Hallazgo grande que quedó anotado (obra aparte, NO hecha)

**19 de 27 archivos de tests de integración no corren en NINGÚN job del CI** (les falta la
etiqueta que el filtro busca). Incluye toda la red de tests de autorización/permisos. Es deuda
vieja, no de esta obra; prenderlos de golpe puede poner el CI rojo por motivos viejos, así que
se enganchó solo lo de esta obra y el resto quedó como obra aparte. En esta obra solo se movió
el de anular factura (que usaba base en memoria) a la carpeta Unit, donde sí corre.

## Qué falta

La verificación final en el navegador de Gaston contra producción: chip rojo de pasaporte en la
ficha F-2026-1064, candado de la cuenta del operador, "Sin tipo" en el selector viejo de
documentos. Sin anular facturas reales.
