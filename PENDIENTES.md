# Pendientes de ArmandoDB Studio

Lista de lo que queda por hacer, por probar o por decidir. Estado a 6 de octubre de 2026, rama `sysbase`.

## 1. Antes de publicar una versión de prueba

- [ ] Hacer commit de los cambios pendientes (vista dividida, cancelación en SQLite y correcciones de la revisión de lógica).
- [ ] Sacar de la carpeta del proyecto el `.zip` con el conector del trabajo. El `.gitignore` ya impide subirlo, pero no debería estar ahí.
- [ ] Cambiar `<Version>` en `MySmdb.csproj` (hoy `1.0.0`) a algo como `1.1.0-beta.1`, para distinguirla de una versión estable. Ese número sale también en los informes de error.
- [ ] Regenerar el instalador con `.\installer\build-installer.ps1`. El actual es anterior a SQL Server, Sybase, el tratamiento de errores y todo lo demás.
- [ ] Publicarla en GitHub como *Release* marcada como **pre-release**, adjuntando el instalador. No guardar el `.exe` dentro del repositorio.
- [ ] Decidir si se fusiona `sysbase` en `main` o se publica desde la rama.

## 2. Sin probar con el uso real

Lo siguiente funciona en pruebas automáticas o compila, pero nadie lo ha usado todavía en la situación real.

### Sybase ASE (nada probado contra un servidor)

- [ ] Inicio de sesión con los datos del conector del trabajo.
- [ ] Explorador: bases, tablas, vistas, procedimientos, triggers, columnas e índices.
- [ ] Ejecución de lotes, mensajes `PRINT` y línea del error.
- [ ] Plan de ejecución (`SET SHOWPLAN ON` + `SET NOEXEC ON`).
- [ ] Scripts `CREATE` de tablas y definición de procedimientos y vistas.
- [ ] Acentos y ñ, según el juego de caracteres del servidor.
- [ ] Cancelar una consulta en curso.
- [ ] Diagrama y autocompletado.

Orden recomendado para la primera prueba: conexión marcada como **Producción**, "Probar conexión" (debe mostrar la versión del servidor), explorador, un `select top 10`, plan y un script `CREATE`. Solo lecturas.

### SQL Server (probado solo contra LocalDB con autenticación de Windows)

- [ ] Entrar con usuario y contraseña de SQL Server.
- [ ] Servidor remoto por TCP e instancia con nombre real.
- [ ] Azure SQL.
- [ ] Túnel SSH.
- [ ] Versiones antiguas del servidor.
- [x] Volver a ejecutar la prueba completa contra LocalDB: ahora está en `tests\MySmdb.Tests\SqlServerTests.cs` y pasa (explorador, scripts, copia, restauración e importación). El plan de ejecución y la pestaña de consulta siguen sin prueba automática.

### MySQL (en el servidor del trabajo solo se permiten lecturas)

- [ ] Copia de seguridad, restauración e importación: solo probadas en SQLite y SQL Server.
- [ ] Botones "Cancelar consulta" y "Cerrar conexión" del monitor de procesos (`KILL`).
- [ ] Monitor con muchas conexiones a la vez.
- [ ] Caída real del túnel SSH (se simuló cortando el cliente, no la red).

### Buscar en la base de datos e historial de consultas

- [ ] Buscar en la base de datos en **MySQL** y en **Sybase**: las consultas al catálogo solo se han probado en SQLite y SQL Server. Son solo lecturas (`information_schema` en MySQL; `sysobjects`, `syscolumns` y `syscomments` en Sybase).
- [ ] En Sybase, un texto partido entre dos trozos de `syscomments` (255 caracteres) no se encuentra al buscar en el código.
- [ ] Historial: no distingue qué sentencia de un script falló, guarda el script ejecutado entero con su primer error.

### Línea de comandos (`armandodb.exe`)

- [x] Probarla contra **MySQL con túnel SSH**: probada el 2026-10-09 con la conexión de monitor, solo con `SELECT`. `consulta` tarda unos 3 s por llamada; en `sesion`, abrir tarda 2,6 s y cada consulta entre 0,1 y 0,3 s, todas por la misma conexión. Al salir no queda ninguna conexión abierta en el servidor.
- [x] En esa prueba, un intento de abrir la sesión falló por un problema de red (tiempo de espera del SSH, 15 s) y el siguiente funcionó. **Decidido: no se reintenta de forma automática.** El programa devuelve el error (código 2) y es el agente o el usuario quien decide si insiste.
- [ ] Usarla con una cuenta de solo lectura (`SELECT`) cuando la maneje un agente: la comprobación de solo lectura del programa es una red de seguridad, no una garantía.
- [ ] El instalador añade la carpeta al `PATH` (casilla marcada por defecto) y la quita al desinstalar. El script compila, pero **falta probarlo instalando**: que `armandodb` responda en una terminal nueva, que una reinstalación no duplique la entrada y que desinstalar la quite sin tocar el resto del `PATH`.
- [ ] No hay comandos para explorar (listar bases, tablas o columnas): se hace con consultas al catálogo.
- [ ] Modo sesión: la reconexión tras caerse la conexión o el túnel está escrita pero no probada (no se pudo provocar la caída en una prueba).
- [ ] Modo sesión: no se puede cancelar una consulta en curso sin terminar el proceso.

La referencia de uso está en `CLI.md`.

### Interfaz (comprobado por programa, no con teclado y ratón)

- [ ] Vistas duplicadas, pestañas sin conexión, "Abrir reciente", "Guardar todo" y "Cerrar todas".
- [ ] Ventanas de historial y de búsqueda: abrir un resultado con doble clic, filtros y atajos (Ctrl+H, Ctrl+Mayús+B; Reemplazar pasó a Ctrl+Mayús+H).

- [ ] Atajos: Alt+flechas, Alt+Intro, Ctrl+R y Ctrl+Mayús+R.
- [ ] Arrastrar la esquina de una tabla del diagrama para redimensionarla.
- [ ] Sombreado al pasar el puntero en el tema claro: el tono es cuestión de gusto.
- [ ] Iconos en el tema oscuro: algunos trazos grises (lupa, teclado, barras de ordenar) tienen poco contraste.
- [ ] Barra de progreso del diagrama y del plan con latencia real.
- [ ] Cierre por un error grave en un hilo secundario: no se pudo provocar en una prueba.

## 3. Detectado en la revisión de código y no corregido

- [ ] **Copia de seguridad en MySQL con columnas generadas:** el `INSERT` las incluye y la restauración fallaría. Hay que excluirlas; requiere probar contra un MySQL donde se pueda escribir.
- [x] **Reconectar una conexión ya abierta tras cambiarle servidor, puerto o usuario:** ahora todo lo abierto pasa al nuevo destino (explorador, pestañas, túnel y autocompletado). Falta probarlo a mano con una conexión real.
- [x] **`DELETE` o `UPDATE` precedidos de `WITH`:** ya entran en la revisión de sentencias peligrosas.
- [x] **Resultado truncado a 500.000 filas:** al llegar al tope se cancela la lectura. Probado en SQLite y SQL Server; en MySQL y Sybase falta comprobarlo con un resultado grande de verdad.
- [ ] **Columnas calculadas, restricciones `CHECK` y reglas en Sybase:** el script `CREATE` de una tabla no las incluye.
- [ ] **Formateador de SQL e iconos:** no se revisaron a fondo.

## 4. Limitaciones conocidas que se podrían resolver

### Sybase ASE

- [ ] Copia de seguridad, restauración e importación: deshabilitadas hasta validar lo básico.
- [ ] Opción de contraseña cifrada en la conexión, si el servidor la exige.
- [ ] Opción para fijar el juego de caracteres, si los acentos salen mal.
- [ ] `TOP` no existe en versiones muy antiguas (las plantillas "primeras 1000 filas" lo usan).
- [ ] Plan de ejecución solo como texto; los lotes con `EXEC` no se envían.
- [ ] Si falla el controlador `AdoNetCore.AseClient`, el plan B es conectar por ODBC.
- [ ] SQL Anywhere e IQ no están soportados.

### SQL Server

- [ ] La copia de seguridad es un script `.sql`, no un `.bak`; no incluye usuarios ni permisos.
- [ ] Restaurar solo algunas tablas con "borrar antes" elimina las claves foráneas que tuvieran hacia ellas las tablas no incluidas.
- [ ] El plan de ejecución requiere el permiso `SHOWPLAN`.

### General

- [ ] Monitor de procesos: solo MySQL. Falta avisar cuando la cuenta no tiene el privilegio `PROCESS` y solo ve sus propias conexiones (hoy la lista corta parece un fallo).
- [ ] El estado del panel de resultados (minimizado o maximizado) no se guarda entre sesiones.
- [ ] En el diagrama no se recuerda qué tablas quedaron expandidas en modo "Solo claves".
- [ ] El botón "Explorador" de la barra es el único sin icono.
- [ ] "Formatear SQL" no se aplica a scripts con `DELIMITER` o `GO`.
- [ ] El instalador no está firmado: Windows SmartScreen avisa al ejecutarlo. Solo se evita con un certificado de firma de código, que es de pago.
- [ ] Solo Windows de 64 bits.

## 5. Mejoras del proyecto

- [x] **Pruebas dentro del repositorio.** `tests\MySmdb.Tests` (xUnit), se ejecutan con `dotnet test tests\MySmdb.Tests`. Cubren: división de scripts, revisión de sentencias peligrosas, formateador, autocompletado, plantillas, exportación, importación, lectura de archivos, planes, perfiles de conexión, copia de recuperación, y explorador, copia y restauración en SQLite y en SQL Server (LocalDB; se omiten si no está instalado).
- [ ] **Pruebas que faltan:** interfaz (pestañas, vista dividida, diagrama, diálogos), ejecución de consultas desde la pestaña (cancelar, mensajes, plan), MySQL (hace falta un servidor donde se pueda escribir, por ejemplo en Docker) y Sybase.
- [ ] Ejecutar las pruebas automáticamente en cada subida (GitHub Actions con `windows-latest`, que trae LocalDB).
- [ ] Actualizar el README con lo último: tratamiento de errores, copia de recuperación, panel de resultados, atajos de la vista dividida.
- [ ] Decidir si los menús de dividir deben ofrecer los cuatro lados, como las flechas (hoy van a la derecha o arriba por defecto).

## 6. Descartado a propósito

No son pendientes; se anotan para no volver a plantearlos sin motivo.

- Comparar esquemas.
- Editar datos en la cuadrícula.
- Diseñador de tablas.
- Historial de consultas.
- Modo solo lectura por conexión.
- Ctrl+flechas y Ctrl+Espacio para la vista dividida (ya son saltar por palabras y autocompletar en el editor).
- Alt+Espacio para quitar la división (es el menú de sistema de la ventana en Windows).
