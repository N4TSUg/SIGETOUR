using System.ComponentModel.DataAnnotations;

namespace SIGETOUR.API.Models
{
    public class CreateUserViewModel
    {
        [Required(ErrorMessage = "El nombre es obligatorio")]
        public string FirstName { get; set; } = string.Empty;

        [Required(ErrorMessage = "El apellido es obligatorio")]
        public string LastName { get; set; } = string.Empty;

        [Required(ErrorMessage = "El nombre de usuario es obligatorio")]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "El correo electrónico es obligatorio")]
        [EmailAddress(ErrorMessage = "Ingrese un correo válido")]
        public string Email { get; set; } = string.Empty;

        public string? PhoneNumber { get; set; }

        [Required(ErrorMessage = "La contraseña es obligatoria")]
        [MinLength(6, ErrorMessage = "La contraseña debe tener al menos 6 caracteres")]
        public string Password { get; set; } = string.Empty;

        public string Role { get; set; } = "User";

        public bool IsActive { get; set; } = true;
    }

    public class EditUserViewModel
    {
        [Required]
        public string Id { get; set; } = string.Empty;

        [Required(ErrorMessage = "El nombre es obligatorio")]
        public string FirstName { get; set; } = string.Empty;

        [Required(ErrorMessage = "El apellido es obligatorio")]
        public string LastName { get; set; } = string.Empty;

        [Required(ErrorMessage = "El nombre de usuario es obligatorio")]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "El correo electrónico es obligatorio")]
        [EmailAddress(ErrorMessage = "Ingrese un correo válido")]
        public string Email { get; set; } = string.Empty;

        public string? PhoneNumber { get; set; }

        public string? NewPassword { get; set; }

        public string Role { get; set; } = "User";

        public bool IsActive { get; set; } = true;
    }
}
