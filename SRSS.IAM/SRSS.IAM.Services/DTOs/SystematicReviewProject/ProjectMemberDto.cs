using SRSS.IAM.Repositories.Entities;

namespace SRSS.IAM.Services.DTOs.SystematicReviewProject
{
    public class ProjectMemberDto
    {
        public Guid UserId { get; set; }
        public Guid ProjectId { get; set; }
        public ProjectRole Role { get; set; }
        public DateTimeOffset JoinedAt { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    public class ChangeProjectMemberRoleRequest
    {
        public ProjectRole Role { get; set; }
    }

    public class ReplaceProjectLeaderRequest
    {
        public Guid MemberId { get; set; }
        public ProjectRole DemotePreviousLeaderToRole { get; set; } = ProjectRole.Member;
    }
}
