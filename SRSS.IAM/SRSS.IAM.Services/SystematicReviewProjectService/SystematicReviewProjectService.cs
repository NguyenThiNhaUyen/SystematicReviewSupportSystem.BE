using Microsoft.EntityFrameworkCore;
using SRSS.IAM.Repositories.Entities;
using SRSS.IAM.Repositories.UnitOfWork;
using SRSS.IAM.Services.DTOs.SystematicReviewProject;
using SRSS.IAM.Services.DTOs.Common;
using Shared.Exceptions;
using SRSS.IAM.Services.UserService;
using SRSS.IAM.Services.DTOs.ResearchQuestion;
using System.Text.Json;


namespace SRSS.IAM.Services.SystematicReviewProjectService
{
    public class SystematicReviewProjectService : ISystematicReviewProjectService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly ICurrentUserService _currentUserService;

        public SystematicReviewProjectService(IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
        {
            _unitOfWork = unitOfWork;
            _currentUserService = currentUserService;
        }

        private static readonly Random _random = new Random();

        public async Task<SystematicReviewProjectResponse> CreateProjectAsync(
            CreateSystematicReviewProjectRequest request,
            CancellationToken cancellationToken = default)
        {
            var (userId, role) = _currentUserService.GetCurrentUser();
            if (role != Role.Admin.ToString())
            {
                throw new UnauthorizedAccessException("You are not authorized to create a project.");
            }
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                throw new ArgumentException("Title is required.", nameof(request.Title));
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var random = _random.Next(1000); // 0–999

            var code = $"SLR{(now + random) % 1_000_000:D6}";

            var project = new Repositories.Entities.SystematicReviewProject
            {
                Id = Guid.NewGuid(),
                Title = request.Title,
                Code = code,
                Domain = request.Domain,
                Description = request.Description,
                Status = ProjectStatus.Draft,
                CreatedAt = DateTimeOffset.UtcNow,
                ModifiedAt = DateTimeOffset.UtcNow,
                UpdatedByUserId = Guid.Parse(userId),
                CreatedByUserId = Guid.Parse(userId)
            };

            await _unitOfWork.SystematicReviewProjects.AddAsync(project, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return MapToResponse(project);
        }

        public async Task<SystematicReviewProjectDetailResponse> GetProjectByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            var project = await _unitOfWork.SystematicReviewProjects
                .GetByIdWithProcessesAsync(id, cancellationToken);

            if (project == null)
            {
                throw new NotFoundException("Project not found.");
            }
            if (project.IsDeleted)
            {
                throw new NotFoundException("Project not found.");
            }

            var (userId, _) = _currentUserService.GetCurrentUser();
            bool isLeader = false;

            if (!string.IsNullOrEmpty(userId))
            {
                var userIdGuid = Guid.Parse(userId);
                var membership = await _unitOfWork.SystematicReviewProjects.GetMembershipQueryable(userIdGuid)
                    .FirstOrDefaultAsync(m => m.ProjectId == id, cancellationToken);

                isLeader = membership?.Role == ProjectRole.Leader;
            }

            var response = MapToDetailResponse(project);
            response.IsLeader = isLeader;
            return response;
        }

        public async Task<PaginatedResponse<SystematicReviewProjectResponse>> GetProjectsAsync(
            ProjectStatus? status,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (pageNumber < 1) pageNumber = 1;
            if (pageSize < 1) pageSize = 10;
            if (pageSize > 100) pageSize = 100;

            var query = _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Where(p => !p.IsDeleted);

            if (status.HasValue)
            {
                query = query.Where(p => p.Status == status.Value);
            }

            var totalCount = await query.CountAsync(cancellationToken);

            var projects = await query
                .Include(p => p.ReviewProcesses)
                .Include(p => p.ProjectMembers)
                    .ThenInclude(pm => pm.User)
                .OrderByDescending(p => p.CreatedAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            return new PaginatedResponse<SystematicReviewProjectResponse>
            {
                Items = projects.Select(MapToResponse).ToList(),
                TotalCount = totalCount,
                PageNumber = pageNumber,
                PageSize = pageSize
            };
        }

        public async Task<SystematicReviewProjectResponse> UpdateProjectAsync(
            UpdateSystematicReviewProjectRequest request,
            CancellationToken cancellationToken = default)
        {
            var project = await _unitOfWork.SystematicReviewProjects
                .GetByIdWithProcessesAsync(request.Id, cancellationToken);

            if (project == null)
            {
                throw new InvalidOperationException($"Project with ID {request.Id} not found.");
            }
            if (project.IsDeleted)
            {
                throw new NotFoundException("Project not found.");
            }

            if (!string.IsNullOrWhiteSpace(request.Title))
            {
                project.Title = request.Title;
            }

            if (request.Domain != null)
            {
                project.Domain = request.Domain;
            }

            if (request.Description != null)
            {
                project.Description = request.Description;
            }

            project.ModifiedAt = DateTimeOffset.UtcNow;

            await _unitOfWork.SystematicReviewProjects.UpdateAsync(project, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return MapToResponse(project);
        }

        public async Task<SystematicReviewProjectResponse> UpdateProjectDatesAsync(
            UpdateProjectDatesRequest request,
            CancellationToken cancellationToken = default)
        {
            var project = await _unitOfWork.SystematicReviewProjects
                .GetByIdWithProcessesAsync(request.Id, cancellationToken);

            if (project == null)
            {
                throw new InvalidOperationException($"Project with ID {request.Id} not found.");
            }
            if (project.IsDeleted)
            {
                throw new NotFoundException("Project not found.");
            }

            var (userId, _) = _currentUserService.GetCurrentUser();
            if (!string.IsNullOrEmpty(userId))
            {
                var userIdGuid = Guid.Parse(userId);
                var membership = await _unitOfWork.SystematicReviewProjects.GetMembershipQueryable(userIdGuid)
                    .FirstOrDefaultAsync(m => m.ProjectId == request.Id, cancellationToken);

                if (membership == null || membership.Role != ProjectRole.Leader)
                {
                    throw new UnauthorizedAccessException("Only project leader can perform this action.");
                }
            }

            project.StartDate = request.StartDate;
            project.EndDate = request.EndDate;
            project.ModifiedAt = DateTimeOffset.UtcNow;

            await _unitOfWork.SystematicReviewProjects.UpdateAsync(project, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return MapToResponse(project);
        }

        public async Task<SystematicReviewProjectResponse> ActivateProjectAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            var project = await _unitOfWork.SystematicReviewProjects
                .GetByIdWithProcessesAsync(id, cancellationToken);

            if (project == null)
            {
                throw new InvalidOperationException($"Project with ID {id} not found.");
            }
            if (project.IsDeleted)
            {
                throw new NotFoundException("Project not found.");
            }

            project.Activate();

            await _unitOfWork.SystematicReviewProjects.UpdateAsync(project, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return MapToResponse(project);
        }

        public async Task<SystematicReviewProjectResponse> CompleteProjectAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            var project = await _unitOfWork.SystematicReviewProjects
                .GetByIdWithProcessesAsync(id, cancellationToken);

            if (project == null)
            {
                throw new InvalidOperationException($"Project with ID {id} not found.");
            }
            if (project.IsDeleted)
            {
                throw new NotFoundException("Project not found.");
            }

            project.Complete();

            await _unitOfWork.SystematicReviewProjects.UpdateAsync(project, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return MapToResponse(project);
        }



        public async Task<bool> DeleteProjectAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            var (userId, role) = _currentUserService.GetCurrentUser();
            var userIdGuid = Guid.Parse(userId);
            if (role != Role.Admin.ToString())
            {
                throw new UnauthorizedAccessException("You are not authorized to delete this project.");
            }
            var project = await _unitOfWork.SystematicReviewProjects
                .FindSingleAsync(p => p.Id == id, cancellationToken: cancellationToken);

            if (project == null)
            {
                throw new NotFoundException("Project not found.");
            }
            if (project.IsDeleted)
            {
                throw new NotFoundException("Project not found.");
            }
            project.IsDeleted = true;
            project.ModifiedAt = DateTimeOffset.UtcNow;
            project.UpdatedByUserId = userIdGuid;

            await _unitOfWork.SystematicReviewProjects.UpdateAsync(project, cancellationToken);
            await AddAuditLogAsync(project.Id, userIdGuid, "DeleteProject", "DeleteProject", "systematic_review_projects", project.Id.ToString(), null, new { project.Id, project.Title, project.IsDeleted }, new[] { "IsDeleted", "ModifiedAt", "UpdatedByUserId" }, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return true;
        }

        public async Task<PaginatedResponse<ProjectMemberDto>> GetProjectMembersAsync(
            Guid projectId,
            string? search,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            var project = await _unitOfWork.SystematicReviewProjects
                .FindSingleAsync(p => p.Id == projectId && !p.IsDeleted, cancellationToken: cancellationToken);

            if (project == null)
            {
                throw new NotFoundException("Project not found.");
            }

            if (pageNumber < 1) pageNumber = 1;
            if (pageSize < 1) pageSize = 10;
            if (pageSize > 100) pageSize = 100;

            var query = _unitOfWork.SystematicReviewProjects.GetProjectMembersQueryable(projectId);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var searchLower = search.ToLower();
                query = query.Where(m =>
                    m.User.FullName.ToLower().Contains(searchLower) ||
                    m.User.Email.ToLower().Contains(searchLower) ||
                    m.User.Username.ToLower().Contains(searchLower));
            }

            var totalCount = await query.CountAsync(cancellationToken);

            var members = await query
                .OrderBy(m => m.User.FullName)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            return new PaginatedResponse<ProjectMemberDto>
            {
                Items = members.Select(m => new ProjectMemberDto
                {
                    UserId = m.UserId,
                    ProjectId = m.ProjectId,
                    Role = m.Role,
                    JoinedAt = m.JoinedAt,
                    UserName = m.User.Username,
                    FullName = m.User.FullName,
                    Email = m.User.Email
                }).ToList(),
                TotalCount = totalCount,
                PageNumber = pageNumber,
                PageSize = pageSize
            };
        }

        public async Task<ProjectMemberDto> ChangeProjectMemberRoleAsync(
            Guid projectId,
            Guid memberId,
            ChangeProjectMemberRoleRequest request,
            CancellationToken cancellationToken = default)
        {
            EnsureAdmin();
            EnsureValidProjectRole(request.Role);
            await EnsureProjectExistsAsync(projectId, cancellationToken);

            await _unitOfWork.BeginTransactionAsync(cancellationToken);
            try
            {
                var members = await _unitOfWork.SystematicReviewProjects
                    .GetProjectMembersForUpdateQueryable(projectId)
                    .ToListAsync(cancellationToken);
                var member = members.FirstOrDefault(m => m.UserId == memberId);

                if (member == null)
                {
                    throw new NotFoundException("Project member not found.");
                }

                var oldRole = member.Role;

                if (request.Role == ProjectRole.Leader)
                {
                    foreach (var currentLeader in members.Where(m => m.Role == ProjectRole.Leader && m.UserId != memberId))
                    {
                        currentLeader.ChangeRole(ProjectRole.Member);
                    }
                }

                member.ChangeRole(request.Role);
                EnsureSingleLeader(members);

                var (userId, _) = _currentUserService.GetCurrentUser();
                await AddAuditLogAsync(projectId, Guid.Parse(userId), "ChangeProjectMemberRole", "ChangeProjectMemberRole", "project_members", member.Id.ToString(), new { member.UserId, Role = oldRole.ToString() }, new { member.UserId, Role = member.Role.ToString() }, new[] { "Role" }, cancellationToken);

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await _unitOfWork.CommitTransactionAsync(cancellationToken);

                return MapToProjectMemberDto(member);
            }
            catch
            {
                await _unitOfWork.RollbackTransactionAsync(cancellationToken);
                throw;
            }
        }

        public async Task<ProjectMemberDto> ReplaceProjectLeaderAsync(
            Guid projectId,
            ReplaceProjectLeaderRequest request,
            CancellationToken cancellationToken = default)
        {
            EnsureAdmin();
            if (request.DemotePreviousLeaderToRole == ProjectRole.Leader)
            {
                throw new BadRequestException("Previous leader must be demoted to a non-leader role.");
            }

            await EnsureProjectExistsAsync(projectId, cancellationToken);

            await _unitOfWork.BeginTransactionAsync(cancellationToken);
            try
            {
                var members = await _unitOfWork.SystematicReviewProjects
                    .GetProjectMembersForUpdateQueryable(projectId)
                    .ToListAsync(cancellationToken);
                var newLeader = members.FirstOrDefault(m => m.UserId == request.MemberId);

                if (newLeader == null)
                {
                    throw new NotFoundException("Replacement leader must be an existing project member.");
                }

                var oldLeader = members.FirstOrDefault(m => m.Role == ProjectRole.Leader && m.UserId != request.MemberId);
                if (newLeader.Role == ProjectRole.Leader && oldLeader == null)
                {
                    throw new ConflictException("Selected member is already the project leader.", "PROJECT_LEADER_UNCHANGED");
                }

                if (oldLeader != null)
                {
                    oldLeader.ChangeRole(request.DemotePreviousLeaderToRole);
                }

                var oldRole = newLeader.Role;
                newLeader.ChangeRole(ProjectRole.Leader);
                EnsureSingleLeader(members);

                var (userId, _) = _currentUserService.GetCurrentUser();
                await AddAuditLogAsync(projectId, Guid.Parse(userId), "ReplaceProjectLeader", "ReplaceProjectLeader", "project_members", newLeader.Id.ToString(), new { OldLeaderUserId = oldLeader?.UserId, NewLeaderUserId = request.MemberId, NewLeaderOldRole = oldRole.ToString() }, new { NewLeaderUserId = request.MemberId, NewLeaderRole = ProjectRole.Leader.ToString(), OldLeaderRole = oldLeader?.Role.ToString() }, new[] { "Role" }, cancellationToken);

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await _unitOfWork.CommitTransactionAsync(cancellationToken);

                return MapToProjectMemberDto(newLeader);
            }
            catch
            {
                await _unitOfWork.RollbackTransactionAsync(cancellationToken);
                throw;
            }
        }

        public async Task<bool> RemoveProjectMemberAsync(
            Guid projectId,
            Guid memberId,
            CancellationToken cancellationToken = default)
        {
            EnsureAdmin();
            await EnsureProjectExistsAsync(projectId, cancellationToken);

            await _unitOfWork.BeginTransactionAsync(cancellationToken);
            try
            {
                var member = await _unitOfWork.SystematicReviewProjects
                    .GetProjectMembersForUpdateQueryable(projectId)
                    .FirstOrDefaultAsync(m => m.UserId == memberId, cancellationToken);

                if (member == null)
                {
                    throw new NotFoundException("Project member not found.");
                }

                var oldValue = new { member.UserId, Role = member.Role.ToString() };
                await _unitOfWork.SystematicReviewProjects.RemoveMemberAsync(member);

                var (userId, _) = _currentUserService.GetCurrentUser();
                await AddAuditLogAsync(projectId, Guid.Parse(userId), "RemoveProjectMember", "RemoveProjectMember", "project_members", member.Id.ToString(), oldValue, null, new[] { "ProjectMembers" }, cancellationToken);

                var changes = await _unitOfWork.SaveChangesAsync(cancellationToken);
                if (changes == 0)
                {
                    throw new ConflictException("Project member was not removed.", "PROJECT_MEMBER_NOT_REMOVED");
                }

                await _unitOfWork.CommitTransactionAsync(cancellationToken);
                return true;
            }
            catch
            {
                await _unitOfWork.RollbackTransactionAsync(cancellationToken);
                throw;
            }
        }

        public async Task<PaginatedResponse<MyProjectResponse>> GetMyProjectsAsync(
            ProjectStatus? status,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (pageNumber < 1) pageNumber = 1;
            if (pageSize < 1) pageSize = 10;
            if (pageSize > 100) pageSize = 100;

            var (userId, _) = _currentUserService.GetCurrentUser();
            var userIdGuid = Guid.Parse(userId);

            var query = _unitOfWork.SystematicReviewProjects.GetMembershipQueryable(userIdGuid);

            if (status.HasValue)
            {
                query = query.Where(m => m.Project.Status == status.Value);
            }

            var totalCount = await query.CountAsync(cancellationToken);

            var memberships = await query
                .OrderByDescending(m => m.Project.CreatedAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(m => new MyProjectResponse
                {
                    Id = m.ProjectId,
                    Title = m.Project.Title,
                    Code = m.Project.Code,
                    Domain = m.Project.Domain,
                    Description = m.Project.Description,
                    Status = m.Project.Status,
                    StatusText = m.Project.Status.ToString(),
                    Role = m.Role,
                    RoleText = m.Role.ToString(),
                    IsLeader = m.Role == ProjectRole.Leader,
                    StartDate = m.Project.StartDate,
                    EndDate = m.Project.EndDate,
                    CreatedAt = m.Project.CreatedAt,
                    ModifiedAt = m.Project.ModifiedAt,
                    Leader = m.Project.ProjectMembers
                        .Where(pm => pm.Role == ProjectRole.Leader)
                        .Select(pm => new ProjectLeaderDto
                        {
                            UserId = pm.UserId,
                            FullName = pm.User.FullName,
                            Username = pm.User.Username,
                            Email = pm.User.Email
                        }).FirstOrDefault()
                })
                .ToListAsync(cancellationToken);

            return new PaginatedResponse<MyProjectResponse>
            {
                Items = memberships,
                TotalCount = totalCount,
                PageNumber = pageNumber,
                PageSize = pageSize
            };
        }

        public async Task<ProjectMembershipResponse> GetMyProjectMembershipAsync(
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            var (userId, _) = _currentUserService.GetCurrentUser();
            if (string.IsNullOrEmpty(userId))
            {
                throw new UnauthorizedAccessException("User is not authenticated.");
            }

            var userIdGuid = Guid.Parse(userId);
            var membership = await _unitOfWork.SystematicReviewProjects.GetMembershipQueryable(userIdGuid)
                .FirstOrDefaultAsync(m => m.ProjectId == projectId, cancellationToken);

            if (membership == null)
            {
                throw new NotFoundException($"User is not a member of project with ID {projectId}.");
            }

            return new ProjectMembershipResponse
            {
                Role = membership.Role,
                RoleText = membership.Role.ToString()
            };
        }

        public async Task<List<ProjectMemberDto>> GetAvailableMembersForPaperAsync(
            Guid projectId,
            Guid paperId,
            CancellationToken cancellationToken = default)
        {
            var project = await _unitOfWork.SystematicReviewProjects
                .FindSingleAsync(p => p.Id == projectId && !p.IsDeleted, cancellationToken: cancellationToken);

            if (project == null)
            {
                throw new NotFoundException("Project not found.");
            }

            var query = _unitOfWork.SystematicReviewProjects.GetProjectMembersQueryable(projectId)
                .Where(m => m.Role != ProjectRole.Leader)
                .Where(m => !m.PaperAssignments.Any(pa => pa.PaperId == paperId));

            var members = await query
                .OrderBy(m => m.User.FullName)
                .ToListAsync(cancellationToken);

            return members.Select(m => new ProjectMemberDto
            {
                UserId = m.UserId,
                ProjectId = m.ProjectId,
                Role = m.Role,
                JoinedAt = m.JoinedAt,
                UserName = m.User.Username,
                FullName = m.User.FullName,
                Email = m.User.Email
            }).ToList();
        }

        public async Task<List<ProjectPicocResponse>> GetProjectPicocsAsync(
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            var project = await _unitOfWork.SystematicReviewProjects
                .FindSingleAsync(p => p.Id == projectId && !p.IsDeleted, cancellationToken: cancellationToken);

            if (project == null)
            {
                throw new NotFoundException("Project not found.");
            }

            var picocs = await _unitOfWork.ProjectPicocs.GetQueryable()
                .Where(p => p.ProjectId == projectId)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(cancellationToken);

            return picocs.Select(p => new ProjectPicocResponse
            {
                Id = p.Id,
                ProjectId = p.ProjectId,
                Population = p.Population,
                Intervention = p.Intervention,
                Comparator = p.Comparator,
                Outcome = p.Outcome,
                Context = p.Context,
                CreatedAt = p.CreatedAt
            }).ToList();
        }

        public async Task<List<ResearchQuestionDetailResponse>> GetProjectResearchQuestionsAsync(
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            var project = await _unitOfWork.SystematicReviewProjects
                .FindSingleAsync(p => p.Id == projectId && !p.IsDeleted, cancellationToken: cancellationToken);

            if (project == null)
            {
                throw new NotFoundException("Project not found.");
            }

            var questions = await _unitOfWork.ResearchQuestions.GetQueryable()
                .Include(q => q.QuestionType)
                .Where(q => q.ProjectId == projectId)
                .OrderBy(q => q.CreatedAt)
                .ToListAsync(cancellationToken);

            return questions.Select(q => new ResearchQuestionDetailResponse
            {
                ResearchQuestionId = q.Id,
                ProjectId = q.ProjectId,
                QuestionType = q.QuestionType?.Name,
                QuestionText = q.QuestionText,
                Rationale = q.Rationale,
                CreatedAt = q.CreatedAt
            }).ToList();
        }

        private static SystematicReviewProjectResponse MapToResponse(Repositories.Entities.SystematicReviewProject project)

        {
            return new SystematicReviewProjectResponse
            {
                Id = project.Id,
                Title = project.Title,
                Code = project.Code,
                Domain = project.Domain,
                Description = project.Description,
                Status = project.Status,
                StatusText = project.Status.ToString(),
                StartDate = project.StartDate,
                EndDate = project.EndDate,
                CreatedAt = project.CreatedAt,
                ModifiedAt = project.ModifiedAt,
                TotalProcesses = project.ReviewProcesses.Count,
                CompletedProcesses = project.ReviewProcesses.Count(p => p.Status == ProcessStatus.Completed),
                Processes = project.ReviewProcesses.Select(p => new ReviewProcessSummaryDto
                {
                    Id = p.Id,
                    Status = p.Status,
                    StatusText = p.Status.ToString(),
                    StartedAt = p.StartedAt,
                    CompletedAt = p.CompletedAt
                }).ToList(),
                Leader = MapToLeaderDto(project.ProjectMembers)
            };
        }

        private static SystematicReviewProjectDetailResponse MapToDetailResponse(Repositories.Entities.SystematicReviewProject project)
        {
            return new SystematicReviewProjectDetailResponse
            {
                Id = project.Id,
                Title = project.Title,
                Code = project.Code,
                Domain = project.Domain,
                Description = project.Description,
                Status = project.Status,
                StatusText = project.Status.ToString(),
                StartDate = project.StartDate,
                EndDate = project.EndDate,
                CreatedAt = project.CreatedAt,
                ModifiedAt = project.ModifiedAt,
                ReviewProcesses = project.ReviewProcesses.Select(p => new DTOs.ReviewProcess.ReviewProcessResponse
                {
                    Id = p.Id,
                    Name = p.Name,
                    ProjectId = p.ProjectId,
                    Status = p.Status,
                    StatusText = p.Status.ToString(),
                    CurrentPhase = p.CurrentPhase,
                    CurrentPhaseText = p.CurrentPhase.ToString(),
                    StartedAt = p.StartedAt,
                    CompletedAt = p.CompletedAt,
                    Notes = p.Notes,
                    CreatedAt = p.CreatedAt,
                    ModifiedAt = p.ModifiedAt
                }).ToList(),
                Leader = MapToLeaderDto(project.ProjectMembers)
            };
        }

        private static ProjectLeaderDto? MapToLeaderDto(IEnumerable<ProjectMember> members)
        {
            var leader = members?.FirstOrDefault(m => m.Role == ProjectRole.Leader);
            if (leader == null || leader.User == null) return null;

            return new ProjectLeaderDto
            {
                UserId = leader.UserId,
                FullName = leader.User.FullName,
                Username = leader.User.Username,
                Email = leader.User.Email
            };
        }

        private void EnsureAdmin()
        {
            var (_, role) = _currentUserService.GetCurrentUser();
            if (role != Role.Admin.ToString())
            {
                throw new UnauthorizedAccessException("You are not authorized to manage project members.");
            }
        }

        private static void EnsureValidProjectRole(ProjectRole role)
        {
            if (!Enum.IsDefined(typeof(ProjectRole), role))
            {
                throw new BadRequestException("Invalid project role.");
            }
        }

        private async Task EnsureProjectExistsAsync(Guid projectId, CancellationToken cancellationToken)
        {
            var exists = await _unitOfWork.SystematicReviewProjects
                .AnyAsync(p => p.Id == projectId && !p.IsDeleted, cancellationToken: cancellationToken);
            if (!exists)
            {
                throw new NotFoundException("Project not found.");
            }
        }

        private static void EnsureSingleLeader(IEnumerable<ProjectMember> members)
        {
            var leaderCount = members.Count(m => m.Role == ProjectRole.Leader);
            if (leaderCount > 1)
            {
                throw new ConflictException("Project cannot have more than one leader.", "PROJECT_LEADER_LIMIT_EXCEEDED");
            }
        }

        private async Task AddAuditLogAsync(
            Guid projectId,
            Guid userId,
            string action,
            string actionType,
            string resourceType,
            string resourceId,
            object? oldValue,
            object? newValue,
            IEnumerable<string> affectedColumns,
            CancellationToken cancellationToken)
        {
            var user = await _unitOfWork.Users.GetQueryable(isTracking: false)
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

            await _unitOfWork.AuditLogs.AddAsync(new AuditLog
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                UserId = userId.ToString(),
                UserName = user?.Username ?? "System",
                Action = action,
                ActionType = actionType,
                ResourceType = resourceType,
                ResourceId = resourceId,
                Importance = "medium",
                OldValue = oldValue == null ? null : JsonSerializer.Serialize(oldValue),
                NewValue = newValue == null ? null : JsonSerializer.Serialize(newValue),
                AffectedColumns = JsonSerializer.Serialize(affectedColumns),
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
        }

        private static ProjectMemberDto MapToProjectMemberDto(ProjectMember member)
        {
            return new ProjectMemberDto
            {
                UserId = member.UserId,
                ProjectId = member.ProjectId,
                Role = member.Role,
                JoinedAt = member.JoinedAt,
                UserName = member.User.Username,
                FullName = member.User.FullName,
                Email = member.User.Email
            };
        }
    }
}
