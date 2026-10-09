# ArmandoDB Studio 🚀

A lightning-fast, lightweight, and distraction-free database manager designed for developers who are tired of bloated GUIs, intrusive paywalls, and rigid keyboard shortcuts. 

Built out of pure frustration with not finding a tool that fit, this is a custom-tailored client for **MySQL**, **SQLite** and **Microsoft SQL Server**, with shortcuts and a workflow that will feel familiar to anyone coming from SQL Server Management Studio (SSMS).

---

## Why this exists?
For over a year and a half, I couldn't find a lightweight manager that matched the way I work: some interfaces felt dated to me, while other tools used more system resources than I wanted or kept core features behind paywalls and pop-ups.

If you love hitting **`Ctrl + E`** to instantly execute your selected query (just like in SSMS) and want something blazing fast that doesn't consume half your RAM, this tool is for you.

## Features
- **SSMS-Style Execution:** Press `Ctrl + E` to run the entire script or just the highlighted selection seamlessly.
- **Multi-Engine Support:** Works with **MySQL** (remote VMs, local instances), **SQLite** and **Microsoft SQL Server** (SQL login or Windows authentication, named instances, LocalDB, `GO` batches) databases, plus experimental support for **Sybase ASE**.
- **Query history:** Every query you run is kept with its connection, duration, row count and result, so you can search it and reopen it later (`Ctrl + H`). The query text is stored unencrypted in the app's data folder on your computer; from the Tools menu you can choose how long it is kept (this session only, 30 days, 90 days by default, or no time limit), turn it off or clear it.
- **Search the database:** Find a text in table, view, procedure, function and trigger names, in column names and inside the code of views, routines and triggers, and jump to the object's script (`Ctrl + Shift + B`).
- **Zero Bloat / Zero Ads:** Open-source, lightweight, no telemetry, no "upgrade to Pro" nags.
- **Customized UI:** Built by a developer, for developers, focusing purely on productivity and speed.

## Known limitations
- **Windows only.** The installer is not code-signed, so Windows SmartScreen shows a warning the first time you run it.
- **Process monitor:** MySQL only. To see connections from other accounts, the MySQL user needs the global `PROCESS` privilege.
- **SQL Server support is new.** It has been tested against LocalDB with Windows authentication. SQL logins, remote servers, named instances over TCP, Azure SQL, SSH tunnels and older server versions should work but have not been verified yet.
- **Sybase ASE support is experimental and has not been tested against a real server yet.** It covers connecting (user and password), the object explorer, running `GO` batches, the execution plan as text (`SET SHOWPLAN`), `CREATE` scripts, the diagram, autocompletion and exporting results. Backup, restore and data import are not available for Sybase. SQL Anywhere and IQ are not supported.
- **SQL Server objects are shown with their schema** (`dbo.Customers`), and scripts are sent in `GO`-separated batches, not statement by statement.
- **Backup and restore use `.sql` scripts**, not native formats (`.bak`, `mysqldump` options). Users and permissions are not included. On SQL Server, restoring only some tables with "drop first" removes the foreign keys that tables outside the backup had towards them.
- **Dangerous-statement confirmation is a safety net, not a guarantee.** On SQL Server and Sybase, statements inside a batch are told apart by `;` and by the keyword that starts each line, which is an approximation; and a stored procedure run with `EXEC` only asks for confirmation on connections marked as production.
- **SQL formatting** is skipped for scripts containing `DELIMITER` or `GO`; select just the query you want to format.
- **Execution plans on SQL Server** require the `SHOWPLAN` permission on the database.
- **Searching the database** has been tested on SQLite and SQL Server; on MySQL and Sybase ASE it has not been verified yet. On Sybase, a text that falls across two 255-character chunks of stored code is not found.
- **Result sets are capped at 500,000 rows.** When the cap is reached the query is cancelled, so later result sets of that same statement or batch are not read.
- **Result grids are read-only:** there is no in-grid data editing, table designer or schema comparison.

---

# ArmandoDB Studio 🚀 (Español)

Un gestor de bases de datos ultrarrápido, ligero y libre de distracciones, diseñado para desarrolladores cansados de interfaces pesadas, muros de pago molestos y atajos de teclado rígidos.

Nacido de la pura frustración de no encontrar una herramienta a mi medida, este es un cliente para **MySQL**, **SQLite** y **Microsoft SQL Server** con atajos y una forma de trabajo que resultarán familiares a quien venga de SQL Server Management Studio (SSMS).

---

## ¿Por qué existe esto?
Durante más de año y medio no encontré un gestor ligero que encajara con mi forma de trabajar: algunas interfaces me resultaban anticuadas, y otras herramientas consumían más recursos de los que quería o dejaban funciones clave tras avisos de pago y publicidad.

Si te encanta presionar **`Ctrl + E`** para ejecutar al instante tu consulta seleccionada (exactamente igual que en SSMS) y buscas algo rapidísimo que no consuma media memoria RAM, esta herramienta es para ti.

## Características
- **Ejecución estilo SSMS:** Presiona `Ctrl + E` para ejecutar todo el script o únicamente el texto seleccionado sin fricciones.
- **Soporte Multi-motor:** Funciona con bases de datos **MySQL** (en VMs remotas, servidores locales), **SQLite** y **Microsoft SQL Server** (usuario de SQL o autenticación de Windows, instancias con nombre, LocalDB, lotes `GO`), más soporte experimental para **Sybase ASE**.
- **Historial de consultas:** Cada consulta que ejecutas se guarda con su conexión, duración, filas y resultado, para buscarla y reabrirla después (`Ctrl + H`). El texto de las consultas se guarda sin cifrar en la carpeta de datos de la aplicación, en tu equipo; desde el menú Herramientas se elige cuánto se conserva (solo esta sesión, 30 días, 90 días por defecto o sin límite de tiempo), y se puede desactivar o borrar.
- **Buscar en la base de datos:** Encuentra un texto en los nombres de tablas, vistas, procedimientos, funciones y triggers, en los nombres de columna y dentro del código de vistas, rutinas y triggers, y salta al script del objeto (`Ctrl + Mayús + B`).
- **Cero peso innecesario / Cero publicidad:** Código abierto, ligero, sin telemetría ni anuncios de "actualizar a Pro".
- **Interfaz a medida:** Creado por un desarrollador para desarrolladores, enfocado 100% en la productividad y la velocidad.

## Limitaciones conocidas
- **Solo Windows.** El instalador no está firmado, así que Windows SmartScreen muestra un aviso la primera vez que se ejecuta.
- **Monitor de procesos:** solo para MySQL. Para ver las conexiones de otras cuentas, el usuario de MySQL necesita el privilegio global `PROCESS`.
- **El soporte de SQL Server es reciente.** Está probado contra LocalDB con autenticación de Windows. El acceso con usuario de SQL, los servidores remotos, las instancias con nombre por TCP, Azure SQL, el túnel SSH y las versiones antiguas del servidor deberían funcionar, pero aún no se han verificado.
- **El soporte de Sybase ASE es experimental y todavía no se ha probado contra un servidor real.** Cubre la conexión (usuario y contraseña), el explorador de objetos, la ejecución por lotes `GO`, el plan de ejecución como texto (`SET SHOWPLAN`), los scripts `CREATE`, el diagrama, el autocompletado y la exportación de resultados. La copia de seguridad, la restauración y la importación de datos no están disponibles para Sybase. SQL Anywhere e IQ no están soportados.
- **Los objetos de SQL Server se muestran con su esquema** (`dbo.Clientes`), y los scripts se envían por lotes separados con `GO`, no sentencia a sentencia.
- **La copia de seguridad y la restauración usan scripts `.sql`**, no formatos nativos (`.bak`, opciones de `mysqldump`). No incluyen usuarios ni permisos. En SQL Server, restaurar solo algunas tablas con "borrar antes" elimina las claves foráneas que tuvieran hacia ellas las tablas que no están en la copia.
- **La confirmación de sentencias peligrosas es una red de seguridad, no una garantía.** En SQL Server y Sybase, las sentencias de un lote se distinguen por el `;` y por la palabra con la que empieza cada línea, que es una aproximación; y un procedimiento ejecutado con `EXEC` solo pide confirmación en las conexiones marcadas como producción.
- **Formatear SQL** no se aplica a scripts que contengan `DELIMITER` o `GO`; selecciona solo la consulta que quieras formatear.
- **Los planes de ejecución en SQL Server** requieren el permiso `SHOWPLAN` sobre la base.
- **Buscar en la base de datos** está probado en SQLite y SQL Server; en MySQL y Sybase ASE aún no se ha verificado. En Sybase, un texto que cae entre dos trozos de 255 caracteres del código guardado no se encuentra.
- **Los resultados tienen un tope de 500.000 filas.** Al alcanzarlo la consulta se cancela, así que los conjuntos de resultados posteriores de esa misma sentencia o lote no se leen.
- **Las cuadrículas de resultados son de solo lectura:** no hay edición de datos en la cuadrícula, diseñador de tablas ni comparación de esquemas.

---

## Disclaimer / Aviso

ArmandoDB Studio is an independent project. It is not affiliated with, sponsored by, or endorsed by Microsoft, Oracle, SAP, or the SQLite project. SQL Server and SQL Server Management Studio are trademarks of Microsoft Corporation; MySQL is a trademark of Oracle Corporation and/or its affiliates; Sybase and SAP ASE are trademarks of SAP SE or its affiliates. These names are used only to describe compatibility and familiar behavior.

ArmandoDB Studio es un proyecto independiente. No está afiliado a Microsoft, Oracle, SAP ni al proyecto SQLite, ni cuenta con su patrocinio o respaldo. SQL Server y SQL Server Management Studio son marcas de Microsoft Corporation; MySQL es una marca de Oracle Corporation y/o sus filiales; Sybase y SAP ASE son marcas de SAP SE o sus filiales. Estos nombres se usan únicamente para describir compatibilidad y un comportamiento familiar.

## License / Licencia

This project is open-source and available under the **MIT License**. Feel free to use, modify, audit, and contribute!
