using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Moq;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Application.Services;
using SIGETOUR.API.Controllers.UI;
using SIGETOUR.API.Core.Common;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Models;
using SIGETOUR.API.ViewModels;
using SIGETOUR.Tests.Helpers;
using Xunit;

namespace SIGETOUR.Tests.Controllers
{
    public class Block6DashboardAndCapacityTests
    {
        [Fact]
        public void PeruTime_ReturnsAccurateTime_InPeruUtcMinus5()
        {
            // Arrange
            var utcNow = DateTime.UtcNow;

            // Act
            var peruNow = PeruTime.Now;
            var peruToday = PeruTime.Today;

            // Assert
            // La diferencia entre UTC y Perú debe ser de exactamente 5 horas
            var diff = utcNow - peruNow;
            Assert.True(Math.Abs(diff.TotalHours - 5.0) < 0.05, $"La hora de Perú debe ser UTC-5. Diff: {diff.TotalHours}");
            Assert.Equal(peruNow.Date, peruToday);

            var (utcStart, utcEnd) = PeruTime.GetTravelDateRangeUtc(new DateTime(2026, 11, 15));
            Assert.Equal(DateTimeKind.Utc, utcStart.Kind);
            Assert.Equal(new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc), utcStart);
            Assert.Equal(new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc), utcEnd);
        }

        [Fact]
        public async Task CapacityValidationService_RejectsBooking_WhenRequestedPassengersExceedCapacity()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Cumbemayo Místico",
                MaxCapacity = 10,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var travelDate = PeruTime.Today.AddDays(2);
            var (startUtc, _) = PeruTime.GetTravelDateRangeUtc(travelDate);

            // Reserva existente con 8 pasajeros confirmados
            var existingBooking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-1111",
                CustomerName = "Cliente 1",
                TravelDate = startUtc,
                ShiftName = "Turno Mañana - 09:30 AM",
                TotalPassengers = 8,
                Status = BookingStatus.Confirmed
            };
            existingBooking.Items.Add(new BookingItem
            {
                TourPackageId = tour.Id,
                Passengers = 8,
                SubTotal = 400
            });
            context.Bookings.Add(existingBooking);
            await context.SaveChangesAsync();

            var service = new CapacityValidationService(context);

            // Act: Intentar reservar 3 pasajeros (8 + 3 = 11 > 10)
            var result = await service.ValidateCapacityAsync(
                tour.Id, travelDate, "Turno Mañana - 09:30 AM", requestedPassengers: 3);

            // Assert
            Assert.False(result.IsAvailable);
            Assert.Equal(8, result.BookedSeats);
            Assert.Equal(10, result.MaxCapacity);
            Assert.Contains("No hay cupos suficientes", result.ErrorMessage ?? "");
            Assert.Contains("ocupados: 8", result.ErrorMessage ?? "");
            Assert.Contains("disponibles: 2", result.ErrorMessage ?? "");
        }

        [Fact]
        public async Task CapacityValidationService_IgnoresCancelledBookings_WhenCheckingAvailableSeats()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Granja Porcón",
                MaxCapacity = 15,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var travelDate = PeruTime.Today.AddDays(1);
            var (startUtc, _) = PeruTime.GetTravelDateRangeUtc(travelDate);

            // Reserva cancelada de 10 pasajeros
            var cancelledBooking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-CANCEL",
                CustomerName = "Cliente Cancelado",
                TravelDate = startUtc,
                ShiftName = "Turno Mañana - 09:30 AM",
                TotalPassengers = 10,
                Status = BookingStatus.Cancelled
            };
            cancelledBooking.Items.Add(new BookingItem
            {
                TourPackageId = tour.Id,
                Passengers = 10,
                SubTotal = 450
            });
            context.Bookings.Add(cancelledBooking);
            await context.SaveChangesAsync();

            var service = new CapacityValidationService(context);

            // Act: Solicitar 12 cupos (como la reserva previa está cancelada, hay 15 libres)
            var result = await service.ValidateCapacityAsync(
                tour.Id, travelDate, "Turno Mañana - 09:30 AM", requestedPassengers: 12);

            // Assert
            Assert.True(result.IsAvailable);
            Assert.Equal(0, result.BookedSeats); // 0 ocupados reales
            Assert.Null(result.ErrorMessage);
        }

        [Fact]
        public async Task Dashboard_ExcludesCancelledBookings_FromDeparturesAndOccupancy()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var todayPeru = PeruTime.Today;
            var (startUtc, _) = PeruTime.GetTravelDateRangeUtc(todayPeru);

            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Ventanillas de Otuzco",
                Duration = "3.5 Horas",
                MaxCapacity = 18,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            // 1. Reserva Cancelada de 5 pasajeros
            var cancelledBooking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-CANCEL1",
                CustomerName = "Cancelado",
                TravelDate = startUtc,
                ShiftName = "Turno Tarde - 03:30 PM",
                TotalPassengers = 5,
                Status = BookingStatus.Cancelled
            };
            cancelledBooking.Items.Add(new BookingItem
            {
                TourPackageId = tour.Id,
                Passengers = 5,
                SubTotal = 150
            });

            // 2. Reserva Confirmada de 4 pasajeros
            var confirmedBooking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-CONFIRM1",
                CustomerName = "Confirmado",
                TravelDate = startUtc,
                ShiftName = "Turno Tarde - 03:30 PM",
                TotalPassengers = 4,
                Status = BookingStatus.Confirmed
            };
            confirmedBooking.Items.Add(new BookingItem
            {
                TourPackageId = tour.Id,
                Passengers = 4,
                SubTotal = 120
            });

            context.Bookings.AddRange(cancelledBooking, confirmedBooking);
            await context.SaveChangesAsync();

            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.Dashboard();

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DashboardViewModel>(viewResult.Model);

            // Pasajeros de hoy deben ser exactamente 4 (excluyendo los 5 de la cancelada)
            Assert.Equal(4, model.TodayPassengers);

            // En las salidas operativas de hoy debe haber exactamente 1 salida con 4 cupos ocupados
            var otuzcoDeparture = model.Departures.FirstOrDefault(d => d.TourId == tour.Id);
            Assert.NotNull(otuzcoDeparture);
            Assert.Equal(4, otuzcoDeparture.BookedSeats);
        }

        [Fact]
        public async Task AdminCreateBooking_EnforcesCapacity_RejectsOverbooking()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Laguna San Nicolás",
                MaxCapacity = 12,
                BasePrice = 50,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var travelDate = PeruTime.Today.AddDays(3);
            var (startUtc, _) = PeruTime.GetTravelDateRangeUtc(travelDate);

            // Ya hay 10 ocupados
            var b1 = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-BASE",
                CustomerName = "Grupo 1",
                TravelDate = startUtc,
                ShiftName = "Turno Mañana - 09:30 AM",
                TotalPassengers = 10,
                Status = BookingStatus.Confirmed
            };
            b1.Items.Add(new BookingItem { TourPackageId = tour.Id, Passengers = 10, SubTotal = 500 });
            context.Bookings.Add(b1);
            await context.SaveChangesAsync();

            var controller = TestHelpers.CreateAdminController(context);

            var model = new EditBookingViewModel
            {
                TourPackageId = tour.Id,
                CustomerName = "Grupo Nuevo Excedente",
                CustomerEmail = "nuevo@gmail.com",
                TravelDate = travelDate,
                ShiftName = "Turno Mañana - 09:30 AM",
                TotalPassengers = 5, // 10 + 5 = 15 > 12 -> Debe rechazarse
                TotalAmount = 250,
                PaidAmount = 0,
                Status = BookingStatus.Confirmed
            };

            // Act
            var result = await controller.CreateBooking(model, null);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.False(controller.ModelState.IsValid);
            var errors = controller.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
            Assert.Contains(errors, e => e.Contains("No hay cupos suficientes"));

            // Verificar que no se guardó en la base de datos
            var bookingsCount = await context.Bookings.CountAsync(b => b.CustomerName == "Grupo Nuevo Excedente");
            Assert.Equal(0, bookingsCount);
        }

        [Fact]
        public async Task AdminEditBooking_EnforcesCapacity_RejectsOverbookingOnPassengerIncrease()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "Cascadas de Cumbemayo",
                MaxCapacity = 15,
                BasePrice = 40,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var travelDate = PeruTime.Today.AddDays(4);
            var (startUtc, _) = PeruTime.GetTravelDateRangeUtc(travelDate);

            // Reserva A con 12 pasajeros
            var bA = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-A",
                CustomerName = "Grupo A",
                TravelDate = startUtc,
                ShiftName = "Turno Mañana - 09:30 AM",
                TotalPassengers = 12,
                Status = BookingStatus.Confirmed
            };
            bA.Items.Add(new BookingItem { TourPackageId = tour.Id, Passengers = 12, SubTotal = 480 });

            // Reserva B con 2 pasajeros (total = 14 / 15)
            var bB = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-B",
                CustomerName = "Grupo B",
                TravelDate = startUtc,
                ShiftName = "Turno Mañana - 09:30 AM",
                TotalPassengers = 2,
                PaidAmount = 80,
                TotalAmount = 80,
                Status = BookingStatus.Confirmed
            };
            bB.Items.Add(new BookingItem { TourPackageId = tour.Id, Passengers = 2, SubTotal = 80 });

            context.Bookings.AddRange(bA, bB);
            await context.SaveChangesAsync();

            var controller = TestHelpers.CreateAdminController(context);

            // Intentar modificar Reserva B aumentando de 2 a 8 pasajeros (12 + 8 = 20 > 15)
            var editModel = new EditBookingViewModel
            {
                Id = bB.Id,
                TourPackageId = tour.Id,
                CustomerName = bB.CustomerName,
                CustomerEmail = "b@gmail.com",
                TravelDate = travelDate,
                ShiftName = "Turno Mañana - 09:30 AM",
                TotalPassengers = 8, // Aumento indebido
                TotalAmount = 320,
                PaidAmount = 80,
                Status = BookingStatus.Confirmed
            };

            // Act
            var result = await controller.EditBooking(editModel);

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("EditBooking", redirect.ActionName);
            Assert.Contains("No hay cupos suficientes", controller.TempData["ErrorMessage"]?.ToString() ?? "");

            // Verificar que no se modificaron los pasajeros en BD
            var reloaded = await context.Bookings.FindAsync(bB.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(2, reloaded.TotalPassengers);
        }

        [Fact]
        public async Task AdminReactivateBooking_EnforcesCapacity_RejectsWhenSeatsSoldOut()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "City Tour Peatonal",
                MaxCapacity = 10,
                BasePrice = 30,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var travelDate = PeruTime.Today.AddDays(5);
            var (startUtc, _) = PeruTime.GetTravelDateRangeUtc(travelDate);

            // 1. Reserva previamente cancelada con 4 pasajeros
            var cancelledBooking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-CAN4",
                CustomerName = "Pasajero Cancelado",
                TravelDate = startUtc,
                ShiftName = "Turno Mañana - 10:00 AM",
                TotalPassengers = 4,
                Status = BookingStatus.Cancelled
            };
            cancelledBooking.Items.Add(new BookingItem { TourPackageId = tour.Id, Passengers = 4, SubTotal = 120 });

            // 2. Mientras estaba cancelada, otros clientes coparon la capacidad (9 ocupados de 10)
            var activeBooking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-NEW9",
                CustomerName = "Nuevos Clientes",
                TravelDate = startUtc,
                ShiftName = "Turno Mañana - 10:00 AM",
                TotalPassengers = 9,
                Status = BookingStatus.Confirmed
            };
            activeBooking.Items.Add(new BookingItem { TourPackageId = tour.Id, Passengers = 9, SubTotal = 270 });

            context.Bookings.AddRange(cancelledBooking, activeBooking);
            await context.SaveChangesAsync();

            var controller = TestHelpers.CreateAdminController(context);

            // Act: Intentar reactivar la reserva cancelada (9 + 4 = 13 > 10)
            var result = await controller.ReactivateBooking(cancelledBooking.Id);

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("EditBooking", redirect.ActionName);
            Assert.Contains("No es posible reactivar la reserva", controller.TempData["ErrorMessage"]?.ToString() ?? "");
            Assert.Contains("No hay cupos suficientes", controller.TempData["ErrorMessage"]?.ToString() ?? "");

            // La reserva debe permanecer en Cancelled
            var reloaded = await context.Bookings.FindAsync(cancelledBooking.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(BookingStatus.Cancelled, reloaded.Status);
        }

        [Fact]
        public async Task AdminReactivateBooking_Succeeds_WhenCapacityAvailable()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var tour = new TourPackage
            {
                Id = Guid.NewGuid(),
                Title = "City Tour Peatonal",
                MaxCapacity = 10,
                BasePrice = 30,
                IsActive = true
            };
            context.TourPackages.Add(tour);

            var travelDate = PeruTime.Today.AddDays(5);
            var (startUtc, _) = PeruTime.GetTravelDateRangeUtc(travelDate);

            var cancelledBooking = new Booking
            {
                Id = Guid.NewGuid(),
                ReferenceCode = "RES-CAN2",
                CustomerName = "Pasajero Cancelado",
                TravelDate = startUtc,
                ShiftName = "Turno Mañana - 10:00 AM",
                TotalPassengers = 3,
                Status = BookingStatus.Cancelled
            };
            cancelledBooking.Items.Add(new BookingItem { TourPackageId = tour.Id, Passengers = 3, SubTotal = 90 });

            context.Bookings.Add(cancelledBooking);
            await context.SaveChangesAsync();

            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.ReactivateBooking(cancelledBooking.Id);

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("EditBooking", redirect.ActionName);
            Assert.Contains("reactivada con éxito", controller.TempData["SuccessMessage"]?.ToString() ?? "");

            var reloaded = await context.Bookings.FindAsync(cancelledBooking.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(BookingStatus.Confirmed, reloaded.Status);
        }

        [Fact]
        public async Task Dashboard_DepartureStatus_TransitionsAccurately_AcrossTimeBoundaries()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var peruNow = PeruTime.Now;
            var todayPeru = peruNow.Date;
            var (todayUtc, _) = PeruTime.GetTravelDateRangeUtc(todayPeru);
            var (tomorrowUtc, _) = PeruTime.GetTravelDateRangeUtc(todayPeru.AddDays(1));
            var (yesterdayUtc, _) = PeruTime.GetTravelDateRangeUtc(todayPeru.AddDays(-1));

            // Tour 1: Salida Mañana (Futura)
            var tourFuturo = new TourPackage { Id = Guid.NewGuid(), Title = "Tour Futuro", Duration = "4 Horas", MaxCapacity = 20, IsActive = true };
            // Tour 2: Salida Hoy con antelación mayor a 45 min (En Espera)
            var timeEnEspera = peruNow.AddHours(3).ToString("hh:mm tt", System.Globalization.CultureInfo.InvariantCulture);
            var tourEnEspera = new TourPackage { Id = Guid.NewGuid(), Title = "Tour En Espera", Duration = "4 Horas", MaxCapacity = 20, IsActive = true };
            // Tour 3: Salida Hoy en ventana de 25 minutos (Abordando)
            var timeAbordando = peruNow.AddMinutes(25).ToString("hh:mm tt", System.Globalization.CultureInfo.InvariantCulture);
            var tourAbordando = new TourPackage { Id = Guid.NewGuid(), Title = "Tour Abordando", Duration = "4 Horas", MaxCapacity = 20, IsActive = true };
            // Tour 4: Salida Hoy que partió hace 1 hora y dura 4 horas (En Ruta)
            var timeEnRuta = peruNow.AddHours(-1).ToString("hh:mm tt", System.Globalization.CultureInfo.InvariantCulture);
            var tourEnRuta = new TourPackage { Id = Guid.NewGuid(), Title = "Tour En Ruta", Duration = "4 Horas", MaxCapacity = 20, IsActive = true };
            // Tour 5: Salida Hoy que partió hace 6 horas y duró 3 horas (Finalizado)
            var timeFinalizado = peruNow.AddHours(-6).ToString("hh:mm tt", System.Globalization.CultureInfo.InvariantCulture);
            var tourFinalizado = new TourPackage { Id = Guid.NewGuid(), Title = "Tour Finalizado Hoy", Duration = "3 Horas", MaxCapacity = 20, IsActive = true };
            // Tour 6: Salida de Ayer (Finalizado)
            var tourAyer = new TourPackage { Id = Guid.NewGuid(), Title = "Tour Ayer", Duration = "4 Horas", MaxCapacity = 20, IsActive = true };

            context.TourPackages.AddRange(tourFuturo, tourEnEspera, tourAbordando, tourEnRuta, tourFinalizado, tourAyer);

            // Crear vehículo y chofer asignado para probar "Unidad Asignada"
            var veh = new Vehicle { Id = Guid.NewGuid(), Model = "Mercedes Sprinter", LicensePlate = "M1-123", SeatCapacity = 19, IsActive = true };
            var driver = new Driver { Id = Guid.NewGuid(), FullName = "Conductor Asignado", IsActive = true };
            context.Vehicles.Add(veh);
            context.Drivers.Add(driver);

            // Reservas para cada caso
            var bFuturo = new Booking { Id = Guid.NewGuid(), ReferenceCode = "RES-FUT", TravelDate = tomorrowUtc, ShiftName = "Turno 09:30 AM", TotalPassengers = 2, Status = BookingStatus.Confirmed };
            bFuturo.Items.Add(new BookingItem { TourPackageId = tourFuturo.Id, Passengers = 2, SubTotal = 100 });

            var bEspera = new Booking { Id = Guid.NewGuid(), ReferenceCode = "RES-ESP", TravelDate = PeruTime.GetTravelDateRangeUtc(peruNow.AddHours(3).Date).Item1, ShiftName = $"Turno {timeEnEspera}", TotalPassengers = 3, Status = BookingStatus.Confirmed, AssignedVehicleId = veh.Id, AssignedDriverId = driver.Id };
            bEspera.Items.Add(new BookingItem { TourPackageId = tourEnEspera.Id, Passengers = 3, SubTotal = 150 });

            var bAbordando = new Booking { Id = Guid.NewGuid(), ReferenceCode = "RES-ABO", TravelDate = todayUtc, ShiftName = $"Turno {timeAbordando}", TotalPassengers = 4, Status = BookingStatus.Confirmed };
            bAbordando.Items.Add(new BookingItem { TourPackageId = tourAbordando.Id, Passengers = 4, SubTotal = 200 });

            var bEnRuta = new Booking { Id = Guid.NewGuid(), ReferenceCode = "RES-RUT", TravelDate = todayUtc, ShiftName = $"Turno {timeEnRuta}", TotalPassengers = 5, Status = BookingStatus.Confirmed, AssignedVehicleId = veh.Id, AssignedDriverId = driver.Id };
            bEnRuta.Items.Add(new BookingItem { TourPackageId = tourEnRuta.Id, Passengers = 5, SubTotal = 250 });

            var bFinalizado = new Booking { Id = Guid.NewGuid(), ReferenceCode = "RES-FIN", TravelDate = todayUtc, ShiftName = $"Turno {timeFinalizado}", TotalPassengers = 6, Status = BookingStatus.Confirmed };
            bFinalizado.Items.Add(new BookingItem { TourPackageId = tourFinalizado.Id, Passengers = 6, SubTotal = 300 });

            var bAyer = new Booking { Id = Guid.NewGuid(), ReferenceCode = "RES-AYE", TravelDate = yesterdayUtc, ShiftName = "Turno 09:30 AM", TotalPassengers = 2, Status = BookingStatus.Confirmed };
            bAyer.Items.Add(new BookingItem { TourPackageId = tourAyer.Id, Passengers = 2, SubTotal = 100 });

            context.Bookings.AddRange(bFuturo, bEspera, bAbordando, bEnRuta, bFinalizado, bAyer);
            await context.SaveChangesAsync();

            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.Dashboard();

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DashboardViewModel>(viewResult.Model);

            // 1. Futuro -> En Espera
            var depFuturo = model.Departures.FirstOrDefault(d => d.TourId == tourFuturo.Id);
            Assert.NotNull(depFuturo);
            Assert.Contains("En Espera", depFuturo.Status);

            // 2. Hoy antes de ventana -> En Espera (Unidad Asignada)
            var depEspera = model.Departures.FirstOrDefault(d => d.TourId == tourEnEspera.Id);
            Assert.NotNull(depEspera);
            Assert.Equal("En Espera (Unidad Asignada)", depEspera.Status);
            Assert.Equal("sky", depEspera.StatusColor);

            // 3. Hoy en ventana <= 45 min -> Abordando (Por Asignar)
            var depAbordando = model.Departures.FirstOrDefault(d => d.TourId == tourAbordando.Id);
            Assert.NotNull(depAbordando);
            Assert.Equal("Abordando (Por Asignar)", depAbordando.Status);
            Assert.Equal("amber", depAbordando.StatusColor);

            // 4. Hoy durante el recorrido -> En Ruta (Despachado)
            if (peruNow.TimeOfDay.TotalHours >= 1.5)
            {
                var depEnRuta = model.Departures.FirstOrDefault(d => d.TourId == tourEnRuta.Id);
                Assert.NotNull(depEnRuta);
                Assert.Equal("En Ruta (Despachado)", depEnRuta.Status);
                Assert.Equal("emerald", depEnRuta.StatusColor);
            }

            // 5. Hoy concluido el horario de duración -> Finalizado (Retorno Concluido)
            if (peruNow.TimeOfDay.TotalHours >= 4.0)
            {
                var depFinalizado = model.Departures.FirstOrDefault(d => d.TourId == tourFinalizado.Id);
                Assert.NotNull(depFinalizado);
                Assert.Equal("Finalizado (Retorno Concluido)", depFinalizado.Status);
                Assert.Equal("zinc", depFinalizado.StatusColor);
            }
        }

        [Fact]
        public async Task Dashboard_PastDepartures_WhenOnlyPastBookingsExist_MarksAsFinalizado()
        {
            // Arrange
            using var context = TestDbContextFactory.Create();
            var todayPeru = PeruTime.Today;
            var (yesterdayUtc, _) = PeruTime.GetTravelDateRangeUtc(todayPeru.AddDays(-1));

            var tourAyer = new TourPackage { Id = Guid.NewGuid(), Title = "Tour Histórico Ayer", Duration = "4 Horas", MaxCapacity = 20, IsActive = true };
            context.TourPackages.Add(tourAyer);

            var bAyer = new Booking { Id = Guid.NewGuid(), ReferenceCode = "RES-AYE", TravelDate = yesterdayUtc, ShiftName = "Turno 09:30 AM", TotalPassengers = 2, Status = BookingStatus.Confirmed };
            bAyer.Items.Add(new BookingItem { TourPackageId = tourAyer.Id, Passengers = 2, SubTotal = 100 });
            context.Bookings.Add(bAyer);
            await context.SaveChangesAsync();

            var controller = TestHelpers.CreateAdminController(context);

            // Act
            var result = await controller.Dashboard();

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DashboardViewModel>(viewResult.Model);

            var depAyer = model.Departures.FirstOrDefault(d => d.TourId == tourAyer.Id);
            Assert.NotNull(depAyer);
            Assert.Equal("Finalizado (Retorno Concluido)", depAyer.Status);
            Assert.Equal("zinc", depAyer.StatusColor);
        }
    }
}
