using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace SIGETOUR.API.Models
{
    public class CheckoutRequestViewModel
    {
        [Required(ErrorMessage = "El tour es obligatorio")]
        public Guid TourId { get; set; }
        
        [Required(ErrorMessage = "La fecha de viaje es obligatoria")]
        public DateTime TravelDate { get; set; }
        
        [Required(ErrorMessage = "El turno es obligatorio")]
        public string Shift { get; set; } = string.Empty;
        
        [Required(ErrorMessage = "El punto de embarque es obligatorio")]
        public string BoardingPoint { get; set; } = string.Empty;
        
        public string? HotelZone { get; set; }
        public string? HotelName { get; set; }
        public string? HotelAddress { get; set; }
        
        [Required(ErrorMessage = "El nombre del cliente es obligatorio")]
        public string CustomerName { get; set; } = string.Empty;

        [Required(ErrorMessage = "El DNI del cliente es obligatorio")]
        public string CustomerDni { get; set; } = string.Empty;
        
        [Required(ErrorMessage = "El teléfono del cliente es obligatorio")]
        public string CustomerPhone { get; set; } = string.Empty;
        
        [Required(ErrorMessage = "El correo electrónico es obligatorio")]
        [EmailAddress(ErrorMessage = "El correo electrónico no es válido")]
        public string CustomerEmail { get; set; } = string.Empty;
        
        [Required(ErrorMessage = "La cantidad de pasajeros es obligatoria")]
        [Range(1, 100, ErrorMessage = "Debe haber al menos 1 pasajero")]
        public int Passengers { get; set; }

        // Pasajeros adicionales (desde Pasajero 2 en adelante)
        public List<string> PassengerNames { get; set; } = new();
        public List<string> PassengerDnis { get; set; } = new();
    }
}

