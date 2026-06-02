using SRSS.IAM.Repositories.Entities;
using System.ComponentModel.DataAnnotations;

namespace SRSS.IAM.Services.DTOs.ProjectMemberInvitation
{
    public class CreateProjectInvitationRequest
    {
        [Required]
        [MinLength(1, ErrorMessage = "At least one user must be selected.")]
        public List<Guid> UserIds { get; set; } = new();

        [EnumDataType(typeof(ProjectRole))]
        public ProjectRole Role { get; set; }

        public DateTimeOffset? ExpiredAt { get; set; }
    }
}
