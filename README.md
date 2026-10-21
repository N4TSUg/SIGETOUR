# SIGETOUR - Turismo Chilón S.A.C.
> **Sistema Integrado de Gestión de Tours, Despacho Operativo y Control de Flota**  
> *Cajamarca, Perú • Acreditado por MINCETUR & DIRCETUR (Cert. N° 4412)*

---

## 📌 Descripción General
**SIGETOUR** es una plataforma web integral desarrollada para la empresa de transporte y turismo **Turismo Chilón S.A.C.** (RUC: 20601248901), con sede central en Jr. 2 de Mayo 569, Cajamarca.

El sistema resuelve la gestión comercial de cara al turista (catálogo interactivo, checkout guiado y emisión de vouchers oficiales) y centraliza las operaciones internas de la agencia:
- Control de caja chica y arqueo de turnos diarios.
- Ficha técnica y control normativo de la flota vehicular (SOAT, CITV, TUC MTC).
- Registro y asignación de conductores profesionales certificados ante el MTC.
- Asignación inteligente de unidades por volumen de pasajeros y generación del **Manifiesto de Pasajeros Oficial en Excel** para fiscalización de DIRCETUR y SUTRAN.

---

## 🔐 Acceso al Panel Administrativo

### Endpoints Oficiales
- **Formulario de Inicio de Sesión:** `https://localhost:5001/Account/Login` (o `http://localhost:5000/Account/Login`)
- **Panel Central / Dashboard Operativo:** `https://localhost:5001/Admin/Dashboard`  
  *(Si se intenta acceder a `/Admin` sin sesión activa, el sistema redirige automáticamente al login con parámetro `ReturnUrl`)*.

### Credenciales de Acceso Sembradas por Defecto

| Rol | Usuario | Correo Corporativo | Contraseña | Permisos |
| :--- | :--- | :--- | :--- | :--- |
| **SuperAdmin** | `superadmin` | `superadmin@sigetour.com` | `SuperAdmin123!` | Control total del sistema, gestión de usuarios, roles y configuración global. |
| **Admin** | `admin` | `admin@sigetour.com` | `Admin123!` | Administración de tours, despacho de circuitos, caja chica, flota y reservas. |

---

## 📸 Banco de Imágenes Oficiales (Recursos de Gerencia)
Los siguientes enlaces corresponden a carpetas compartidas en Google Drive, facilitadas directamente por la **Gerencia de Turismo Chilón**, con fotografías en alta resolución de los atractivos turísticos para la creación, actualización y publicación de los paquetes y circuitos en el sistema:

1. 🌲 **Granja Porcón**  
   [https://drive.google.com/drive/folders/1TuNSSZLvPQlfJ7igR9T0TO_moiK2Vent](https://drive.google.com/drive/folders/1TuNSSZLvPQlfJ7igR9T0TO_moiK2Vent)

2. 🎸 **Namora (Guitarras y Laguna San Nicolás)**  
   [https://drive.google.com/drive/folders/1htID0HCeG4kGQoMv1KxF-8qyeTplrWOQ](https://drive.google.com/drive/folders/1htID0HCeG4kGQoMv1KxF-8qyeTplrWOQ)

3. 🏰 **Castillo de Chancay / Alrededores**  
   [https://drive.google.com/drive/folders/1LQ2gorrwzbYdFOGAE00Ots9aQ7gBBECQ](https://drive.google.com/drive/folders/1LQ2gorrwzbYdFOGAE00Ots9aQ7gBBECQ)

4. ♨️ **Yumagual (Aguas Termomedicinales)**  
   [https://drive.google.com/drive/folders/1hUdgCg6Xr_2sl1ULMB8cY_LESFa5BWGv](https://drive.google.com/drive/folders/1hUdgCg6Xr_2sl1ULMB8cY_LESFa5BWGv)

5. 👑 **Baños del Inca (Complejo Termal Histórico)**  
   [https://drive.google.com/drive/folders/15e4IdtH_wQdJtxMyC92zoUvXd4qx_DGS](https://drive.google.com/drive/folders/15e4IdtH_wQdJtxMyC92zoUvXd4qx_DGS)

6. 🏔️ **Lagunas de Alto Perú**  
   [https://drive.google.com/drive/folders/13vp35CtrAfWhom2fVmaQnBI_PfyP3_rC](https://drive.google.com/drive/folders/13vp35CtrAfWhom2fVmaQnBI_PfyP3_rC)

7. ⛪ **Polloc (Santuario Virgen del Rosario)**  
   [https://drive.google.com/drive/folders/1rgW1OBWHTJzp5EvkdUiXk5SAYCmf01_e](https://drive.google.com/drive/folders/1rgW1OBWHTJzp5EvkdUiXk5SAYCmf01_e)

---

## 📋 Fichas de Tours para Demostración en Vivo (Copiar y Pegar)
> 💡 *Para el archivo completo con todas las casillas desglosadas listas para copiar y pegar durante la reunión con el cliente, consulta [`DEMO_TOURS_DATA.md`](DEMO_TOURS_DATA.md).*

### Resumen de Circuitos Comerciales Oficiales

| N° | Circuito / Tour | Subtítulo / Badge | Horario / Turno | Tarifa Sugerida | Lugares Principales Incluidos |
| :-: | :--- | :--- | :--- | :-: | :--- |
| **1** | **Ventanillas de Otuzco** | Cementerio pre-inca | 3:30 PM – 6:30 PM | S/ 35.00 | Ventanillas pre-incas, Jardín de las Hortensias, Artesanía, Fundo Tres Molinos (fábrica de quesos y rosquitas). |
| **2** | **Collpa y Cascadas de Llacanora** | Llamado de vacas por su nombre | 3:30 PM – 7:00 PM | S/ 35.00 | Ex-hacienda La Collpa, laguna artificial, capilla, establo central, llamado de vacas, degustación de lácteos, cascadas de Llacanora. |
| **3** | **Castillo de Yanamarca** | Estilo medieval en los Andes | Am o Pm (A coordinar) | S/ 40.00 | Castillo medieval, pinturas y esculturas en piedra/madera, vistas panorámicas, cafetería y áreas temáticas. |
| **4** | **City Tour Peatonal** | Mezcla incaica, colonial y moderna | 10:00 AM o 3:30 PM | S/ 25.00 | Plaza de Armas, Cuarto del Rescate, Catedral, San Francisco, Belén, Museos, Santa Apolonia y Sillas del Inca. |
| **5** | **Namora y Laguna San Nicolás** | Guitarras, totora y gastronomía | 9:30 AM – 3:00 PM | S/ 45.00 | Alameda de los 14 Incas, Bosque de sapitos, talleres de guitarras en Namora, Laguna San Nicolás con botes de totora y gastronomía. |
| **6** | **Cumbe Mayo** | 3ra. Maravilla del Perú | 9:30 AM – 2:00 PM | S/ 40.00 | Mirador Bellavista, bosque de rocas Los Frailones, santuario, abrigos rocosos, piedra del sacrificio y acueducto milenario (3,000 años). |
| **7** | **Granja de Porcón** | La Suiza peruana | 9:30 AM – 2:00 PM | S/ 40.00 | Huambocancha (picapedreros), bosque de pinos más grande del Perú, zoológico andino (otorongos, pumas, osos de anteojos) y textilería. |

---

## 🛠️ Stack Tecnológico

- **Plataforma:** .NET 10.0 (C#)
- **Framework Web:** ASP.NET Core MVC & RESTful Web API
- **Persistencia & ORM:** Entity Framework Core 10 con PostgreSQL (Npgsql)
- **Seguridad:** ASP.NET Core Identity con esquema Application Cookie persistente y JWT Bearer para APIs
- **Frontend:** Razor Views (.cshtml), Tailwind CSS v3, Material Symbols Outlined
- **Procesamiento de Documentos:** ClosedXML para generación de Manifiestos Oficiales y Reportes Financieros en formato `.xlsx`
- **Testing:** xUnit, Moq, InMemory DbContext (77+ pruebas unitarias automatizadas)

---

## 📦 Módulos Principales

### 1. Panel de Despacho y Manifiesto de Pasajeros MTC
- Vista de salidas programadas por fecha, circuito y turno.
- **Asignación Eficiente de Flota:** Las reservas no tienen un vehículo asignado de forma fija al momento de crearse; la asignación de unidad y conductor se realiza durante el despacho oficial, permitiendo enviar vehículos con la capacidad justa (ej. van de 14 plazas vs. minibús de 20 plazas) para evitar sobrecostos operativos.
- Exportación en un clic del formato oficial de **Manifiesto de Pasajeros en Excel** requerido por las autoridades de transporte.

### 2. Flota Vehicular y Conductores
- Registro técnico de unidades: placa, modelo, año, VIN, motor, tipo de combustible y capacidad de pasajeros.
- Semáforo y alertas automáticas de vencimiento de **SOAT Turístico**, **CITV (Revisión Técnica)** y **TUC MTC**.
- Galería fotográfica múltiple por vehículo con foto de portada para el catálogo público.
- Padrón de choferes con control de número y categoría de brevete (A-IIIa, A-IIIc), vigencia y unidad habitual.

### 3. Caja Chica y Turnos de Caja Diaria
- **Caja Chica (Fondo Fijo):** Registro de egresos operativos (combustible, peajes, refrigerios), control de comprobantes (boletas/facturas) y cálculo automático del gasto pendiente de reposición (`PendingSpent`).
- **Turnos de Caja:** Apertura y cierre de turnos con arqueo en efectivo, registro automático de pagos provenientes de reservas y conciliación de diferencias.

### 4. Portal Público para el Turista
- Catálogo de tours con filtros por categoría, fecha, turno, modalidad (regular/privado) y dificultad.
- Portada dinámica con paquete estrella y tarjetas de paquetes destacados promocionales.
- Catálogo de flota turística certificada con especificaciones de confort y seguridad.
- Checkout responsivo en pasos y emisión de **Boleto Oficial de Viaje** optimizado para impresión en una sola hoja A4 y envío directo por WhatsApp.

---

## 🚀 Puesta en Marcha Local

### Prerrequisitos
- [.NET 10.0 SDK](https://dotnet.microsoft.com/)
- [PostgreSQL](https://www.postgresql.org/) (v14 o superior)

### Configuración
1. Clonar el repositorio:
   ```bash
   git clone https://github.com/N4TSUg/SIGETOUR.git
   cd SIGETOUR
   ```

2. Configurar la cadena de conexión en `SIGETOUR.API/appsettings.json`:
   ```json
   {
     "ConnectionStrings": {
       "DefaultConnection": "Host=localhost;Port=5432;Database=sigetour_db;Username=postgres;Password=tu_password"
     }
   }
   ```

3. Restaurar dependencias y compilar:
   ```bash
   dotnet restore
   dotnet build
   ```

4. Ejecutar el proyecto:
   ```bash
   cd SIGETOUR.API
   dotnet run
   ```
   *Al iniciar por primera vez, las migraciones automáticas y el `IdentitySeeder` crearán las tablas y los usuarios administradores automáticamente.*

5. Ejecutar la suite de pruebas:
   ```bash
   dotnet test
   ```

---

## 📄 Licencia y Derechos
Desarrollado para **Turismo Chilón S.A.C.** como parte del Proyecto Capstone de Ingeniería de Software.  
Todos los derechos reservados © 2025-2026.
