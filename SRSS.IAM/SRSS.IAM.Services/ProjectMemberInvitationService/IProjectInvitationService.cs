using SRSS.IAM.Repositories.Entities;
using SRSS.IAM.Services.DTOs.ProjectMemberInvitation;

namespace SRSS.IAM.Services.ProjectMemberInvitationService
{
    public interface IProjectInvitationService
    {
        Task CreateInvitationsAsync(Guid projectId, Guid inviterUserId, CreateProjectInvitationRequest request);
        Task<IEnumerable<ProjectInvitationResponse>> GetProjectInvitationsAsync(Guid projectId, Guid currentUserId, ProjectMemberInvitationStatus? status = null);
        Task<ProjectInvitationResponse> GetByIdAsync(Guid invitationId, Guid currentUserId);
        Task AcceptInvitationAsync(Guid invitationId, Guid currentUserId);
        Task RejectInvitationAsync(Guid invitationId, Guid currentUserId, RejectInvitationRequest request);
        Task CancelInvitationAsync(Guid invitationId, Guid currentUserId);
        Task CancelInvitationAsync(Guid projectId, Guid invitationId, Guid currentUserId);
    }
}
