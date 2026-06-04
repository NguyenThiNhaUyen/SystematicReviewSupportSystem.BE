using Shared.Repositories;
using SRSS.IAM.Repositories.Entities;

namespace SRSS.IAM.Repositories.SystematicReviewProjectRepo
{
    public interface ISystematicReviewProjectRepository : IGenericRepository<SystematicReviewProject, Guid, AppDbContext>
    {
        Task<SystematicReviewProject?> GetByIdWithProcessesAsync(Guid id, CancellationToken cancellationToken = default);
        Task<IEnumerable<SystematicReviewProject>> GetActiveProjectsAsync(CancellationToken cancellationToken = default);
        Task<List<ProjectMember>> GetMembersByProjectIdAsync(Guid projectId);
        IQueryable<SystematicReviewProject> GetQueryable();
        IQueryable<ProjectMember> GetProjectMembersQueryable(Guid projectId);
        IQueryable<ProjectMember> GetProjectMembersForUpdateQueryable(Guid projectId);
        Task<bool> ExistsPendingInvitationAsync(Guid projectId, Guid userId);
        Task<bool> ProjectHasLeaderAsync(Guid projectId);
        Task<bool> HasPendingLeaderInvitationAsync(Guid projectId);
        Task<List<ProjectMember>> GetProjectsByUserIdAsync(Guid userId);
        IQueryable<ProjectMember> GetMembershipQueryable(Guid userId);
        Task AddMemberAsync(ProjectMember member);
        Task RemoveMemberAsync(ProjectMember member);
        Task<bool> IsProjectLeaderAsync(Guid projectId, Guid userId);
    }
}
