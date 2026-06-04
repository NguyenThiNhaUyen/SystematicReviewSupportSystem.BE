using Microsoft.EntityFrameworkCore;
using Shared.Repositories;
using SRSS.IAM.Repositories.Entities;

namespace SRSS.IAM.Repositories.SystematicReviewProjectRepo
{
    public class SystematicReviewProjectRepository : GenericRepository<SystematicReviewProject, Guid, AppDbContext>, ISystematicReviewProjectRepository
    {
        public SystematicReviewProjectRepository(AppDbContext context) : base(context)
        {
        }

        public async Task<SystematicReviewProject?> GetByIdWithProcessesAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.SystematicReviewProjects
                .Include(p => p.ReviewProcesses)
                .Include(p => p.ProjectMembers)
                    .ThenInclude(pm => pm.User)
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        }

        public async Task<IEnumerable<SystematicReviewProject>> GetActiveProjectsAsync(CancellationToken cancellationToken = default)
        {
            return await _context.SystematicReviewProjects
                .Where(p => p.Status == ProjectStatus.Active && !p.IsDeleted)
                .Include(p => p.ReviewProcesses)
                .ToListAsync(cancellationToken);
        }

        public async Task<List<ProjectMember>> GetMembersByProjectIdAsync(Guid projectId)
        {
            return await _context.Set<ProjectMember>()
                .Include(m => m.User)
                .Where(m => m.ProjectId == projectId)
                .ToListAsync();
        }

        public IQueryable<SystematicReviewProject> GetQueryable()
        {
            return _context.SystematicReviewProjects.Where(p => !p.IsDeleted).AsQueryable();
        }
 
        public IQueryable<ProjectMember> GetProjectMembersQueryable(Guid projectId)
        {
            return _context.ProjectMembers
                .Include(m => m.User)
                .Where(m => m.ProjectId == projectId)
                .AsNoTracking();
        }

        public IQueryable<ProjectMember> GetProjectMembersForUpdateQueryable(Guid projectId)
        {
            return _context.ProjectMembers
                .Include(m => m.User)
                .Where(m => m.ProjectId == projectId);
        }

        public async Task<bool> ExistsPendingInvitationAsync(Guid projectId, Guid userId)
        {
            return await _context.ProjectMemberInvitations
                .AnyAsync(i => i.ProjectId == projectId && i.InvitedUserId == userId && i.Status == ProjectMemberInvitationStatus.Pending);
        }

        public async Task<bool> ProjectHasLeaderAsync(Guid projectId)
        {
            return await _context.ProjectMembers
                .AnyAsync(m => m.ProjectId == projectId && m.Role == ProjectRole.Leader);
        }

        public async Task<bool> HasPendingLeaderInvitationAsync(Guid projectId)
        {
            return await _context.ProjectMemberInvitations
                .AnyAsync(i => i.ProjectId == projectId && i.Role == ProjectRole.Leader && i.Status == ProjectMemberInvitationStatus.Pending);
        }

        public async Task<List<ProjectMember>> GetProjectsByUserIdAsync(Guid userId)
        {
            return await _context.Set<ProjectMember>()
                .Include(m => m.Project)
                .Where(m => m.UserId == userId && !m.Project.IsDeleted)
                .AsNoTracking()
                .ToListAsync();
        }

        public IQueryable<ProjectMember> GetMembershipQueryable(Guid userId)
        {
            return _context.Set<ProjectMember>()
                .Include(m => m.Project)
                .Where(m => m.UserId == userId && !m.Project.IsDeleted)
                .AsNoTracking();
        }

        public async Task AddMemberAsync(ProjectMember member)
        {
            await _context.ProjectMembers.AddAsync(member);
        }

        public Task RemoveMemberAsync(ProjectMember member)
        {
            _context.ProjectMembers.Remove(member);
            return Task.CompletedTask;
        }

        public async Task<bool> IsProjectLeaderAsync(Guid projectId, Guid userId)
        {
            return await _context.ProjectMembers
                .AnyAsync(m => m.ProjectId == projectId && m.UserId == userId && m.Role == ProjectRole.Leader);
        }
    }
}
