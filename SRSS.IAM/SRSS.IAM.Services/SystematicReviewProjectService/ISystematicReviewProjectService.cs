using SRSS.IAM.Repositories.Entities;
using SRSS.IAM.Services.DTOs.SystematicReviewProject;
using SRSS.IAM.Services.DTOs.Common;
using SRSS.IAM.Services.DTOs.ResearchQuestion;


namespace SRSS.IAM.Services.SystematicReviewProjectService
{
    public interface ISystematicReviewProjectService
    {
        Task<SystematicReviewProjectResponse> CreateProjectAsync(
            CreateSystematicReviewProjectRequest request,
            CancellationToken cancellationToken = default);

        Task<SystematicReviewProjectDetailResponse> GetProjectByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default);

        Task<PaginatedResponse<SystematicReviewProjectResponse>> GetProjectsAsync(
            ProjectStatus? status,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default);

        Task<SystematicReviewProjectResponse> UpdateProjectAsync(
            UpdateSystematicReviewProjectRequest request,
            CancellationToken cancellationToken = default);

        Task<SystematicReviewProjectResponse> UpdateProjectDatesAsync(
            UpdateProjectDatesRequest request,
            CancellationToken cancellationToken = default);

        Task<SystematicReviewProjectResponse> ActivateProjectAsync(
            Guid id,
            CancellationToken cancellationToken = default);

        Task<SystematicReviewProjectResponse> CompleteProjectAsync(
            Guid id,
            CancellationToken cancellationToken = default);

        Task<bool> DeleteProjectAsync(
            Guid id,
            CancellationToken cancellationToken = default);

        Task<PaginatedResponse<ProjectMemberDto>> GetProjectMembersAsync(
            Guid projectId,
            string? search,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default);

        Task<ProjectMemberDto> ChangeProjectMemberRoleAsync(
            Guid projectId,
            Guid memberId,
            ChangeProjectMemberRoleRequest request,
            CancellationToken cancellationToken = default);

        Task<ProjectMemberDto> ReplaceProjectLeaderAsync(
            Guid projectId,
            ReplaceProjectLeaderRequest request,
            CancellationToken cancellationToken = default);

        Task<bool> RemoveProjectMemberAsync(
            Guid projectId,
            Guid memberId,
            CancellationToken cancellationToken = default);

        Task<PaginatedResponse<MyProjectResponse>> GetMyProjectsAsync(
            ProjectStatus? status,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default);

        Task<ProjectMembershipResponse> GetMyProjectMembershipAsync(
            Guid projectId,
            CancellationToken cancellationToken = default);

        Task<List<ProjectMemberDto>> GetAvailableMembersForPaperAsync(
            Guid projectId,
            Guid paperId,
            CancellationToken cancellationToken = default);

        Task<List<ProjectPicocResponse>> GetProjectPicocsAsync(
            Guid projectId,
            CancellationToken cancellationToken = default);

        Task<List<ResearchQuestionDetailResponse>> GetProjectResearchQuestionsAsync(
            Guid projectId,
            CancellationToken cancellationToken = default);
    }
}

