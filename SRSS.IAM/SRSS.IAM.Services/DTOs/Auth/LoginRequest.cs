using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SRSS.IAM.Services.DTOs.Auth
{
    public class LoginRequest : IValidatableObject
    {
        [StringLength(255, ErrorMessage = "Email/username qua dai.")]
        [RegularExpression(@"^\S+$", ErrorMessage = "Email/Username khong duoc co space.")]
        public string KeyLogin { get; set; } = string.Empty;

        [StringLength(255, ErrorMessage = "Email/username qua dai.")]
        [RegularExpression(@"^\S+$", ErrorMessage = "Email/Username khong duoc co space.")]
        public string? EmailOrUsername { get; set; }

        [Required(ErrorMessage = "Mat khau khong duoc de trong")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "Mat khau phai tu 6-100 ky tu")]
        public string Password { get; set; } = string.Empty;

        [JsonIgnore]
        public string EffectiveKeyLogin => !string.IsNullOrWhiteSpace(KeyLogin)
            ? KeyLogin
            : EmailOrUsername ?? string.Empty;

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (string.IsNullOrWhiteSpace(EffectiveKeyLogin))
            {
                yield return new ValidationResult("Email/Username khong duoc de trong.", new[] { nameof(KeyLogin) });
            }
        }
    }
}
