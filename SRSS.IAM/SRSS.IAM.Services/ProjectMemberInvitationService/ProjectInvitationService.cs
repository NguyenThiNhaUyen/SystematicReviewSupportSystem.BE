using Microsoft.EntityFrameworkCore;
using Shared.Exceptions;
using SRSS.IAM.Repositories.Entities;
using SRSS.IAM.Repositories.UnitOfWork;
using SRSS.IAM.Services.DTOs.ProjectMemberInvitation;
using SRSS.IAM.Services.NotificationService;
using SRSS.IAM.Services.Mappers;
using System.Text.Json;

namespace SRSS.IAM.Services.ProjectMemberInvitationService
{
    public class ProjectInvitationService : IProjectInvitationService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly INotificationService _notificationService;

        public ProjectInvitationService(IUnitOfWork unitOfWork, INotificationService notificationService)
        {
            _unitOfWork = unitOfWork;
            _notificationService = notificationService;
        }

        public async Task CreateInvitationsAsync(Guid projectId, Guid inviterUserId, CreateProjectInvitationRequest request)
        {
            if (request.UserIds == null || request.UserIds.Count == 0)
            {
                throw new BadRequestException("At least one user must be selected.");
            }

            if (!Enum.IsDefined(typeof(ProjectRole), request.Role))
            {
                throw new BadRequestException("Invalid project role.");
            }

            if (request.ExpiredAt.HasValue && request.ExpiredAt.Value <= DateTimeOffset.UtcNow)
            {
                throw new BadRequestException("Invitation expiration must be in the future.");
            }

            if (request.UserIds.Distinct().Count() != request.UserIds.Count)
            {
                throw new BadRequestException("Duplicate users are not allowed in one invitation request.");
            }

            var project = await _unitOfWork.SystematicReviewProjects.FindSingleAsync(p => p.Id == projectId, isTracking: true);
            if (project == null)
            {
                throw new NotFoundException($"Project with ID {projectId} not found.");
            }

            var inviter = await _unitOfWork.Users.FindSingleAsync(u => u.Id == inviterUserId);
            if (inviter == null)
            {
                throw new UnauthorizedException("Inviter not found.");
            }

            var inviterProjectMembers = await _unitOfWork.SystematicReviewProjects.GetMembersByProjectIdAsync(projectId);
            var inviterMember = inviterProjectMembers.FirstOrDefault(m => m.UserId == inviterUserId);

            bool isSystemAdmin = inviter.Role == Role.Admin;
            bool isProjectLeader = inviterMember?.Role == ProjectRole.Leader;

            if (!isSystemAdmin && !isProjectLeader)
            {
                throw new ForbiddenException("Only Admins or Project Leaders can invite members.");
            }

            if (!isSystemAdmin && request.Role == ProjectRole.Leader)
            {
                throw new ForbiddenException("Only Admins can invite a Project Leader.");
            }

            var invitations = new List<ProjectMemberInvitation>();
            var members = await _unitOfWork.SystematicReviewProjects.GetMembersByProjectIdAsync(projectId);

            await _unitOfWork.BeginTransactionAsync();
            try
            {
                foreach (var userId in request.UserIds)
                {
                    if (userId == inviterUserId)
                    {
                        throw new BadRequestException("Inviter cannot invite themselves.");
                    }

                    var invitedUser = await _unitOfWork.Users.FindSingleAsync(u => u.Id == userId);
                    if (invitedUser == null)
                    {
                        throw new NotFoundException($"User with ID {userId} not found.");
                    }

                    if (members.Any(m => m.UserId == userId))
                    {
                        throw new ConflictException($"User with ID {userId} is already a member of this project.", "PROJECT_MEMBER_ALREADY_EXISTS");
                    }

                    if (await _unitOfWork.SystematicReviewProjects.ExistsPendingInvitationAsync(projectId, userId))
                    {
                        throw new ConflictException($"User with ID {userId} already has a pending invitation for this project.", "PENDING_INVITATION_EXISTS");
                    }

                    if (request.Role == ProjectRole.Leader)
                    {
                        if (await _unitOfWork.SystematicReviewProjects.HasPendingLeaderInvitationAsync(projectId))
                        {
                            throw new InvalidOperationException("A pending leader replacement invitation already exists.");
                        }
                    }

                    var invitation = new ProjectMemberInvitation(
                        projectId,
                        userId,
                        inviterUserId,
                        request.Role,
                        request.ExpiredAt);

                    invitations.Add(invitation);
                }

                await _unitOfWork.ProjectMemberInvitations.AddRangeAsync(invitations);
                await _unitOfWork.SaveChangesAsync();
                await _unitOfWork.CommitTransactionAsync();

                try
                {
                    foreach (var invitation in invitations)
                    {
                        var title = "Project Invitation";
                        var message = $"You have been invited to join project {project.Title} as {invitation.Role}";
                        var metadataObj = new
                        {
                            projectId = projectId,
                            invitationId = invitation.Id,
                            role = invitation.Role.ToString()
                        };

                        await _notificationService.SendAsync(
                            invitation.InvitedUserId,
                            title,
                            message,
                            NotificationType.Invitation,
                            invitation.Id,
                            NotificationEntityType.ProjectInvitation,
                            JsonSerializer.Serialize(metadataObj));
                    }
                }
                catch (Exception) { /* Fail-safe */ }
            }
            catch (Exception)
            {
                await _unitOfWork.RollbackTransactionAsync();
                throw;
            }
        }

        public async Task<IEnumerable<ProjectInvitationResponse>> GetProjectInvitationsAsync(Guid projectId, Guid currentUserId, ProjectMemberInvitationStatus? status = null)
        {
            var user = await _unitOfWork.Users.FindSingleAsync(u => u.Id == currentUserId);
            var members = await _unitOfWork.SystematicReviewProjects.GetMembersByProjectIdAsync(projectId);
            var member = members.FirstOrDefault(m => m.UserId == currentUserId);

            if (user?.Role != Role.Admin && member?.Role != ProjectRole.Leader)
            {
                throw new ForbiddenException("Only Admins or Project Leaders can view project invitations.");
            }

            var invitations = await _unitOfWork.ProjectMemberInvitations.GetByProjectIdAsync(projectId, status);

            invitations = invitations.Where(i => i.InvitedByUserId == currentUserId);
            return invitations.ToResponseList();
        }

        public async Task<ProjectInvitationResponse> GetByIdAsync(Guid invitationId, Guid currentUserId)
        {
            var invitation = await _unitOfWork.ProjectMemberInvitations.GetByIdWithDetailsAsync(invitationId);

            if (invitation == null)
            {
                throw new NotFoundException($"Invitation with ID {invitationId} not found.");
            }

            var user = await _unitOfWork.Users.FindSingleAsync(u => u.Id == currentUserId);

            if (user?.Role != Role.Admin && invitation.InvitedUserId != currentUserId)
            {
                throw new ForbiddenException("You do not have permission to view this invitation.");
            }

            return invitation.ToResponse();
        }

        public async Task AcceptInvitationAsync(Guid invitationId, Guid currentUserId)
        {
            var invitation = await _unitOfWork.ProjectMemberInvitations.GetByIdWithDetailsAsync(invitationId);

            if (invitation == null)
                throw new NotFoundException("Invitation not found.");

            if (invitation.InvitedUserId != currentUserId)
                throw new ForbiddenException("This invitation is not for you.");

            if (invitation.Status != ProjectMemberInvitationStatus.Pending)
                throw new ConflictException($"Cannot accept invitation in {invitation.Status} status.", "INVITATION_NOT_PENDING");

            if (invitation.ExpiredAt.HasValue && invitation.ExpiredAt < DateTimeOffset.UtcNow)
            {
                invitation.Expire();
                await _unitOfWork.SaveChangesAsync();
                throw new ConflictException("This invitation has expired.", "INVITATION_EXPIRED");
            }

            var members = await _unitOfWork.SystematicReviewProjects.GetMembersByProjectIdAsync(invitation.ProjectId);
            var invitedMember = members.FirstOrDefault(m => m.UserId == currentUserId);
            if (invitedMember != null && invitation.Role != ProjectRole.Leader)
            {
                throw new ConflictException("You are already a member of this project.", "PROJECT_MEMBER_ALREADY_EXISTS");
            }

            await _unitOfWork.BeginTransactionAsync();
            try
            {
                invitation.Accept();

                if (invitation.Role == ProjectRole.Leader)
                {
                    foreach (var currentLeader in members.Where(m => m.Role == ProjectRole.Leader && m.UserId != invitation.InvitedUserId))
                    {
                        currentLeader.ChangeRole(ProjectRole.Member);
                    }

                    if (invitedMember != null)
                    {
                        invitedMember.ChangeRole(ProjectRole.Leader);
                    }
                    else
                    {
                        var newLeader = new ProjectMember(invitation.ProjectId, invitation.InvitedUserId, ProjectRole.Leader);
                        await _unitOfWork.SystematicReviewProjects.AddMemberAsync(newLeader);
                    }
                }
                else
                {
                    var newMember = new ProjectMember(invitation.ProjectId, invitation.InvitedUserId, invitation.Role);
                    await _unitOfWork.SystematicReviewProjects.AddMemberAsync(newMember);
                }

                await _unitOfWork.SaveChangesAsync();
                await _unitOfWork.CommitTransactionAsync();

                // Notify inviter
                try
                {
                    var metadataObj = new
                    {
                        projectId = invitation.ProjectId,
                        invitationId = invitation.Id,
                        role = invitation.Role.ToString()
                    };

                    await _notificationService.SendAsync(
                        invitation.InvitedByUserId,
                        "Invitation Accepted",
                        $"{invitation.InvitedUser.FullName} has accepted your invitation to join {invitation.Project.Title}.",
                        NotificationType.Invitation,
                        invitation.Id,
                        NotificationEntityType.ProjectInvitation,
                        JsonSerializer.Serialize(metadataObj));
                }
                catch { }
            }
            catch (Exception)
            {
                await _unitOfWork.RollbackTransactionAsync();
                throw;
            }
        }

        public async Task RejectInvitationAsync(Guid invitationId, Guid currentUserId, RejectInvitationRequest request)
        {
            var invitation = await _unitOfWork.ProjectMemberInvitations.GetByIdWithDetailsAsync(invitationId);

            if (invitation == null)
                throw new NotFoundException("Invitation not found.");

            if (invitation.InvitedUserId != currentUserId)
                throw new ForbiddenException("This invitation is not for you.");

            if (invitation.Status != ProjectMemberInvitationStatus.Pending)
                throw new ConflictException("Invitation is no longer pending.", "INVITATION_NOT_PENDING");

            invitation.Reject(request.ResponseMessage);
            await _unitOfWork.SaveChangesAsync();

            // Notify inviter
            try
            {
                var metadataObj = new
                {
                    projectId = invitation.ProjectId,
                    invitationId = invitation.Id,
                    role = invitation.Role.ToString()
                };

                await _notificationService.SendAsync(
                    invitation.InvitedByUserId,
                    "Invitation Rejected",
                    $"{invitation.InvitedUser.FullName} has rejected your invitation to join {invitation.Project.Title}. Message: {invitation.ResponseMessage ?? "No message provided"}",
                    NotificationType.Invitation,
                    invitation.Id,
                    NotificationEntityType.ProjectInvitation,
                    JsonSerializer.Serialize(metadataObj));
            }
            catch { }
        }

        public async Task CancelInvitationAsync(Guid invitationId, Guid currentUserId)
        {
            var invitation = await _unitOfWork.ProjectMemberInvitations.GetByIdWithDetailsAsync(invitationId);

            if (invitation == null)
                throw new NotFoundException("Invitation not found.");

            await CancelInvitationAsync(invitation, currentUserId);
        }

        public async Task CancelInvitationAsync(Guid projectId, Guid invitationId, Guid currentUserId)
        {
            var invitation = await _unitOfWork.ProjectMemberInvitations.GetByIdWithDetailsAsync(invitationId);

            if (invitation == null || invitation.ProjectId != projectId)
                throw new NotFoundException("Invitation not found in this project.");

            await CancelInvitationAsync(invitation, currentUserId);
        }

        private async Task CancelInvitationAsync(ProjectMemberInvitation invitation, Guid currentUserId)
        {
            var user = await _unitOfWork.Users.FindSingleAsync(u => u.Id == currentUserId);
            var members = await _unitOfWork.SystematicReviewProjects.GetMembersByProjectIdAsync(invitation.ProjectId);
            var member = members.FirstOrDefault(m => m.UserId == currentUserId);

            if (user?.Role != Role.Admin && member?.Role != ProjectRole.Leader)
            {
                throw new ForbiddenException("Only Admins or Project Leaders can cancel invitations.");
            }

            if (invitation.Status != ProjectMemberInvitationStatus.Pending)
                throw new ConflictException("Only Pending invitations can be cancelled.", "INVITATION_NOT_PENDING");

            invitation.Cancel();
            await _unitOfWork.SaveChangesAsync();

            // Notify invited user
            try
            {
                var metadataObj = new
                {
                    projectId = invitation.ProjectId,
                    invitationId = invitation.Id,
                    role = invitation.Role.ToString()
                };

                await _notificationService.SendAsync(
                    invitation.InvitedUserId,
                    "Invitation Cancelled",
                    $"The invitation to join project {invitation.Project.Title} has been cancelled.",
                    NotificationType.Invitation,
                    invitation.Id,
                    NotificationEntityType.ProjectInvitation,
                    JsonSerializer.Serialize(metadataObj));
            }
            catch { }
        }
    }
}
