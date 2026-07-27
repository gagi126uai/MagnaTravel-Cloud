# Pruebas del Lote 2 para el navegador de Gaston (un solo login)

> https://backoffice.magnaviajesyturismo.com/ — 10 pruebas cortas + limpieza.
> Las facturas de prueba SIEMPRE en homologación.

## Las 10 pruebas (en orden de pantalla, para no dar vueltas)

**Dashboard**
1. Mirar las tarjetas de arriba: Ventas, Cobros y Saldo Pendiente tienen que
   mostrar $ y US$ en líneas separadas (si hay movimiento en las dos monedas).
   Ningún número con formato gringo (coma de decimales).

**Reservas**
2. La primera pestaña es "Todas" y el contador suma todo. Entrar y ver que
   lista de todo (atención: al entrar, la vista arranca en las activas — ver
   pregunta 4 abajo).
3. Buscador de arriba: escribir el nombre de un hotel de alguna reserva
   (ej. "Palace"). Tiene que aparecer la reserva que lo contiene.

**Clientes**
4. Alta de cliente nueva: elegir tipo DNI, tipear un DNI real, tocar la lupita
   y elegir el resultado → el casillero tiene que pasar solo a CUIT con el
   número del padrón. Guardar.
5. Intentar dar de alta OTRO cliente con el mismo CUIT pero escrito con
   guiones → tiene que avisar que ya existe.
6. Abrir a editar un cliente viejo que tenga CUIT y DNI (de los cargados con
   el formulario anterior), cambiarle SOLO el teléfono y guardar → volver a
   abrir: el DNI tiene que seguir ahí.

**Pasajeros**
7. Abrir un pasajero: en "Datos personales" está siempre "Vencimiento
   pasaporte" (opcional). Cargarle una fecha y guardar.

**Caja**
8. Anular un movimiento manual → las dos filas del par quedan con etiqueta
   "Anulado" y los botones apagados con el motivo al pasar el mouse.
9. Hacer un retiro de saldo a favor de un cliente (si hay) → en el Libro de
   Caja el texto dice "en efectivo" / "por transferencia", sin palabras raras.

**Extractos**
10. En el estado de cuenta de una reserva con cobros: cada cobro muestra
    "Registrado: hh:mm" con la hora argentina real.

## Limpieza pendiente (mismo login)

- Borrar/anular los datos de prueba PI0724 y los clientes/reservas viejas de
  prueba, por la app (ya decidido el 25/07).

## Preguntas nuevas (contestar cuando puedas, no frenan nada)

1. **Retiros en Caja**: ¿te van los textos "en efectivo" / "por transferencia" /
   "devuelto al operador"? Propuesta extra: para el último, cambiar la frase
   entera a "Devolución al operador del saldo a favor de {cliente}" (hoy dice
   "Retiro ... devuelto al operador" y se lee raro).
2. **Par de Caja por EDICIÓN**: cuando editás un movimiento, el par viejo
   también queda marcado "Anulado". ¿Lo dejamos así o preferís "Reemplazado"?
3. **Ficha del cliente**: la pestaña de datos sigue con 2 campos sueltos
   (documento y CUIT) vs el casillero único nuevo del alta. ¿Unificamos la
   ficha también? (obra aparte)
4. **Pestaña al entrar a Reservas**: hoy al entrar no queda resaltada ninguna
   pestaña (se ven las activas). Con "Todas" primera, ¿querés que al entrar
   arranque en "Todas", o que quede resaltada una pestaña "Activas"?
5. **Dashboard del vendedor**: la tarjeta "Ventas personales" muestra en
   realidad las ventas de TODA la agencia (viene de antes). ¿La arreglamos
   para que sea del vendedor, la renombramos, o la sacamos? Recomendación:
   arreglarla en el motor para que sea personal de verdad.
6. **Estados en inglés** en los dashboards ("Confirmed", "InManagement"):
   ¿lo sumamos al próximo lote de pulido? Recomendación: sí.
