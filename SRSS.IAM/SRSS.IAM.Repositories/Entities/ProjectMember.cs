using Shared.Entities.BaseEntity;

namespace SRSS.IAM.Repositories.Entities
{
    public class ProjectMember : BaseEntity<Guid>
    {
        public Guid ProjectId { get; set; }
        public Guid UserId { get; set; }

        public ProjectRole Role { get; private set; }

        public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;

        // Navigation
        public SystematicReviewProject Project { get; set; } = null!;
        public User User { get; set; } = null!;

        private ProjectMember() { }

        public ProjectMember(Guid projectId, Guid userId, ProjectRole role)
        {
            Id = Guid.NewGuid();
            ProjectId = projectId;
            UserId = userId;
            Role = role;
            JoinedAt = DateTimeOffset.UtcNow;
            CreatedAt = DateTimeOffset.UtcNow;
        }

        public void ChangeRole(ProjectRole role)
        {
            Role = role;
            ModifiedAt = DateTimeOffset.UtcNow;
        }

        public ICollection<PaperAssignment> PaperAssignments { get; set; } = new List<PaperAssignment>();
    }
    public enum ProjectRole
    {
        Leader = 1,
        Member = 2
    }
}
