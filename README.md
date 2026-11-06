# SIGETOUR - Turismo Chilón S.A.C.
> **Sistema Integrado de Gestión de Tours, Despacho Operativo y Control de Flota**  
> *Cajamarca, Perú • Acreditado por MINCETUR & DIRCETUR (Cert. N° 4412)*  
> **Despliegue Oficial en Producción:** [https://sigetour.onrender.com](https://sigetour.onrender.com)

[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16%2B-4169E1?logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![Tailwind CSS](https://img.shields.io/badge/Tailwind_CSS-3.4-38B2AC?logo=tailwind-css&logoColor=white)](https://tailwindcss.com/)
[![Render](https://img.shields.io/badge/Render-Production-46E3B7?logo=render&logoColor=white)](https://sigetour.onrender.com)
[![Cloudinary](https://img.shields.io/badge/Cloudinary-Integrated-3448C5?logo=cloudinary&logoColor=white)](https://cloudinary.com/)
[![Tests Passing](https://img.shields.io/badge/Tests-171%2F171%20Passing%20(100%25)-brightgreen?logo=xunit)]()
[![SonarQube Quality Gate](https://img.shields.io/badge/SonarQube-Passed-4C9BD4?logo=sonarqube&logoColor=white)]()

---

## 📌 Descripción General
**SIGETOUR** es una solución web empresarial desarrollada a medida para la empresa de transporte y turismo **Turismo Chilón S.A.C.** (RUC: 20601248901), con sede central en Jr. 2 de Mayo 569, Cajamarca, Perú.

El sistema resuelve integralmente dos áreas estratégicas:
1. **Área Comercial & Turista:** Catálogo interactivo de circuitos turísticos, promociones dinámicas, catálogo oficial de flota vehicular, checkout responsivo en pasos y emisión de Vouchers de Reserva Oficiales listos para imprimir o compartir vía WhatsApp.
2. **Operaciones Internas & Despacho:** Programación de salidas en vivo con semáforo de estados (*En Espera, Abordando, En Ruta, Completado*), asignación inteligente de flota vehicular según volumen de pasajeros, padrón de conductores MTC, control de caja chica con arqueo de turnos y generación en un clic del **Manifiesto de Pasajeros Oficial en Excel (.xlsx)** exigido por DIRCETUR y SUTRAN.

---

## 🔐 Acceso al Sistema y Credenciales

### URLs Oficiales
* **Servidor de Producción (Render):** [https://sigetour.onrender.com](https://sigetour.onrender.com)
* **Servidor Local (Desarrollo):** `https://localhost:7103` (o `http://localhost:5047`)
* **Inicio de Sesión Administrativo:** `/Account/Login`
* **Panel de Operaciones / Dashboard:** `/Admin/Dashboard`
* **Catálogo Público de Tours:** `/Tour/Catalog`
* **Catálogo Público de Flota:** `/Vehicle/Catalog`

### Credenciales de Acceso Sembradas por Defecto

| Rol | Usuario | Correo Corporativo | Contraseña | Alcance de Permisos |
| :--- | :--- | :--- | :--- | :--- |
| **SuperAdmin** | `superadmin` | `superadmin@sigetour.com` | `SuperAdmin123!` | Control total, auditoría, configuración de sistema y gestión de usuarios. |
| **Admin** | `admin` | `admin@sigetour.com` | `Admin123!` | Administración de tours, despacho de salidas, control de flota, caja chica y reservas. |

> *Nota: Al iniciar la aplicación por primera vez, el seeder crea automáticamente los roles y las credenciales si no existen.*

---

## 🚀 Arquitectura y Stack Tecnológico

* **Backend:** ASP.NET Core 10 (C# 13) con arquitectura orientada a servicios, controladores MVC y endpoints RESTful.
* **Persistencia & ORM:** Entity Framework Core 10 con soporte nativo PostgreSQL (`Npgsql.EntityFrameworkCore.PostgreSQL`).
* **Base de Datos:** PostgreSQL en la nube (Neon / Render) con migraciones automáticas al iniciar.
* **Seguridad & Autenticación:** ASP.NET Core Identity con esquema Cookie Authentication persistente y protección contra CSRF (`[ValidateAntiForgeryToken]`).
* **Frontend:** Razor Views (.cshtml), Tailwind CSS v3 con diseño responsivo mobile-first, Material Symbols Outlined y JavaScript ES6+ modular.
* **Accesibilidad & Calidad:** Cumplimiento de estándares WCAG 2.1 AA auditados por SonarQube Cloud (etiquetas nativas `<button>`, `<label>` semánticas, navegación accesible por teclado).
* **Reportería Oficial:** ClosedXML para la generación en tiempo real de Manifiestos de Pasajeros y Liquidaciones en hojas de cálculo Excel (`.xlsx`).
* **Testing:** xUnit, Moq, InMemory DbContext y FluentAssertions con **154 pruebas unitarias automatizadas (100% exitosas)**.

---

## 📦 Módulos Principales del Sistema

### 1. Despacho en Vivo y Manifiesto de Pasajeros (DIRCETUR / SUTRAN)
* **Tablero de Salidas del Día:** Monitoreo en tiempo real de salidas filtrables por turno (Mañana / Tarde) y fecha.
* **Semáforo Operativo:**
  * 🟡 `Pendiente de Asignación`: Pasajeros confirmados sin vehículo o chofer asignado.
  * 🟠 `Abordando`: Unidad en punto de embarque recibiendo a los turistas.
  * 🟢 `En Ruta`: Vehículo en trayecto hacia el circuito turístico.
  * ⚪ `Completado`: Tour finalizado y pasajeros retornados a la base.
* **Asignación Justa de Capacidad:** Permite asignar unidades de 14, 16 o 19 pasajeros en función de las reservas reales para minimizar costos de combustible.
* **Exportación de Manifiesto en Excel:** Emite el documento reglamentario con datos de la empresa, placa, chofer, número de brevete y nómina completa de turistas con DNI/Pasaporte y asiento.

### 2. Flota Vehicular y Conductores
* **Ficha Técnica Integral:** Placa, modelo, año de fabricación, tipo de combustible, categoría y capacidad de asientos.
* **Alertas Normativas:** Monitoreo de vigencia de SOAT Turístico, Revisión Técnica (CITV) y TUC emitido por el MTC.
* **Unidad Insignia (Flagship):** Selector en panel administrativo para definir qué vehículo representa a la empresa en el encabezado del catálogo de flota.
* **Galería Fotográfica:** Carga múltiple con drag-and-drop, selección de foto de portada (`IsCover`) y visor **Lightbox de pantalla completa** con navegación por teclado (`←`, `→`, `Esc`) y gestos táctiles.
* **Padrón de Conductores:** Control de licencias profesionales (A-IIIa, A-IIIc), vigencia y asignación en despacho.

### 3. Catálogo de Tours y Reserva Comercial
* **Vitrina Dinámica:** Filtros por turno, fecha, presupuesto y nivel de exigencia física.
* **Tour Recomendado en Hero:** Selector en panel administrativo para fijar la oferta estrella en el banner principal.
* **Galería Multimedia con Lightbox:** Visor interactivo en pantalla completa para explorar los atractivos de cada circuito antes de reservar.
* **Checkout Guiado & Voucher Oficial:** Proceso simplificado de compra con emisión de boleto con código QR/código de reserva, desglose de recojo en hotel y enlace directo de asistencia por WhatsApp.

### 4. Caja Chica y Turnos de Caja
* **Fondo Fijo Operativo:** Registro y categorización de egresos (combustible, peajes, compras menores) con soporte de comprobante.
* **Arqueo y Cierre Diario:** Conciliación entre ingresos por reservas (efectivo/transferencia) y egresos de campo.

---

## ⚙️ Puesta en Marcha Local

### Prerrequisitos
* [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
* [PostgreSQL](https://www.postgresql.org/) (versión 14 o superior)

### Pasos de Instalación
1. **Clonar el repositorio:**
   ```bash
   git clone https://github.com/N4TSUg/SIGETOUR.git
   cd SIGETOUR
   ```

2. **Configurar la base de datos:**
   Crear o modificar `SIGETOUR.API/appsettings.Development.json` con la cadena de conexión local:
   ```json
   {
     "ConnectionStrings": {
       "DefaultConnection": "Host=localhost;Port=5432;Database=sigetour_db;Username=postgres;Password=tu_password"
     }
   }
   ```

3. **Restaurar paquetes y compilar:**
   ```bash
   dotnet restore
   dotnet build
   ```

4. **Ejecutar la suite de pruebas automatizadas:**
   ```bash
   dotnet test SIGETOUR.Tests/SIGETOUR.Tests.csproj
   ```
   *(Verificar que se superen los 154 tests sin errores).*

5. **Iniciar la aplicación:**
   ```bash
   dotnet run --project SIGETOUR.API/SIGETOUR.API.csproj
   ```
   Abrir el navegador en `https://localhost:7103` o `http://localhost:5047`.

---

## ☁️ Despliegue en Producción (Render)

El proyecto está configurado para despliegue continuo mediante Git en **Render**:
* **Runtime:** .NET 10 Web Service en contenedor Linux.
* **Build Command:**
  ```bash
  cd SIGETOUR && dotnet publish SIGETOUR.API/SIGETOUR.API.csproj -c Release -o out
  ```
* **Start Command:**
  ```bash
  cd SIGETOUR/out && ./SIGETOUR.API
  ```
* **Variables de Entorno Requeridas en Render:**
  * `ASPNETCORE_ENVIRONMENT`: `Production`
  * `DATABASE_URL`: Cadena de conexión PostgreSQL (formato URI estándar `postgres://user:password@host:port/database`).
  * `PORT`: `80` o puerto asignado por Render (el backend mapea automáticamente `PORT` a las URLs de Kestrel).

---

## 🖼️ Almacenamiento Multimedia en la Nube (Cloudinary)

SIGETOUR integra **Cloudinary** para el almacenamiento permanente, compresión inteligente y distribución CDN de las fotos de tours y vehículos:

### Opciones de Configuración

El servicio admite cualquiera de las dos modalidades de credenciales:

1. **Opción 1: Cadena de conexión única (Recomendada):**
   * Variable de entorno: `CLOUDINARY_URL=cloudinary://<api_key>:<api_secret>@<cloud_name>`

2. **Opción 2: Variables individuales:**
   * En `appsettings.json` o variables de entorno de Render:
     * `Cloudinary__CloudName` (o `Cloudinary:CloudName`)
     * `Cloudinary__ApiKey` (o `Cloudinary:ApiKey`)
     * `Cloudinary__ApiSecret` (o `Cloudinary:ApiSecret`)

> 💡 **Fallback Local Automático:** Si no se proporcionan credenciales de Cloudinary (por ejemplo, en desarrollo local sin conexión a internet), el sistema almacena automáticamente los archivos en `wwwroot/images/` para no bloquear el flujo de trabajo.

---

## 📸 Banco de Imágenes Oficiales (Recursos de Gerencia)

Enlaces compartidos por la **Gerencia de Turismo Chilón** con fotografías en alta resolución para la alimentación del catálogo:

1. 🌲 **Granja Porcón:** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/1TuNSSZLvPQlfJ7igR9T0TO_moiK2Vent)
2. 🎸 **Namora (Guitarras y Laguna San Nicolás):** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/1htID0HCeG4kGQoMv1KxF-8qyeTplrWOQ)
3. 🏰 **Castillo de Chancay / Alrededores:** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/1LQ2gorrwzbYdFOGAE00Ots9aQ7gBBECQ)
4. ♨️ **Yumagual (Aguas Termomedicinales):** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/1hUdgCg6Xr_2sl1ULMB8cY_LESFa5BWGv)
5. 👑 **Baños del Inca (Complejo Termal Histórico):** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/15e4IdtH_wQdJtxMyC92zoUvXd4qx_DGS)
6. 🏔️ **Lagunas de Alto Perú:** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/13vp35CtrAfWhom2fVmaQnBI_PfyP3_rC)
7. ⛪ **Polloc (Santuario Virgen del Rosario):** [Ver fotos en Google Drive](https://drive.google.com/drive/folders/1rgW1OBWHTJzp5EvkdUiXk5SAYCmf01_e)

---

## 📋 Resumen de Circuitos Comerciales Oficiales

| N° | Circuito / Tour | Subtítulo / Badge | Horario / Turno | Tarifa Sugerida | Atractivos Principales |
| :-: | :--- | :--- | :--- | :-: | :--- |
| **1** | **Ventanillas de Otuzco** | Cementerio pre-inca | 3:30 PM – 6:30 PM | S/ 35.00 | Ventanillas pre-incas, Jardín de Hortensias, Artesanía, Fundo Tres Molinos. |
| **2** | **Collpa y Llacanora** | Llamado de vacas | 3:30 PM – 7:00 PM | S/ 35.00 | Ex-hacienda La Collpa, laguna artificial, establo, degustación de lácteos, cascadas. |
| **3** | **Castillo de Yanamarca** | Estilo medieval | Mañana / Tarde | S/ 40.00 | Castillo medieval, esculturas en piedra/madera, vistas panorámicas. |
| **4** | **City Tour Peatonal** | Tradición e historia | 10:00 AM / 3:30 PM | S/ 25.00 | Plaza de Armas, Cuarto del Rescate, Catedral, San Francisco, Belén, Santa Apolonia. |
| **5** | **Namora y San Nicolás** | Guitarras y totora | 9:30 AM – 3:00 PM | S/ 45.00 | Talleres de guitarras en Namora, Laguna San Nicolás, botes de totora, gastronomía. |
| **6** | **Cumbe Mayo** | 3ra. Maravilla del Perú | 9:30 AM – 2:00 PM | S/ 40.00 | Mirador Bellavista, Frailones, piedra de sacrificios, acueducto milenario (3,000 años). |
| **7** | **Granja de Porcón** | La Suiza peruana | 9:30 AM – 2:00 PM | S/ 40.00 | Huambocancha, bosque de pinos, zoológico andino (otorongos, pumas, osos), textilería. |

---

## 📄 Licencia y Derechos
Desarrollado para **Turismo Chilón S.A.C.** como parte del Proyecto de Tesis / Capstone de Ingeniería de Software.  
Todos los derechos reservados © 2025-2026.
