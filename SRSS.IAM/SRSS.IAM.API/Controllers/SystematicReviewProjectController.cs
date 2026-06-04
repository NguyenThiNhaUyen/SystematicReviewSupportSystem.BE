using Microsoft.AspNetCore.Mvc;
using Shared.Builder;
using Shared.Models;
using SRSS.IAM.Repositories.Entities;
using SRSS.IAM.Services.DTOs.SystematicReviewProject;
using SRSS.IAM.Services.DTOs.Common;
using SRSS.IAM.Services.SystematicReviewProjectService;
using Microsoft.AspNetCore.Authorization;
using SRSS.IAM.Services.ProjectMemberInvitationService;
using SRSS.IAM.Services.DTOs.ProjectMemberInvitation;
using SRSS.IAM.Services.UserService;
using SRSS.IAM.Services.DTOs.ResearchQuestion;
using SRSS.IAM.Services.ProjectExportService;


namespace SRSS.IAM.API.Controllers
{
    /// <summary>
    /// API endpoints for managing Systematic Review Projects
    /// </summary>
    [ApiController]
    [Route("api/projects")]
    public class SystematicReviewProjectController : BaseController
    {
        private readonly ISystematicReviewProjectService _projectService;
        private readonly IProjectInvitationService _invitationService;
        private readonly ICurrentUserService _currentUserService;
        private readonly IProjectExportService _projectExportService;

        public SystematicReviewProjectController(
            ISystematicReviewProjectService projectService,
            IProjectInvitationService invitationService,
            ICurrentUserService currentUserService,
            IProjectExportService projectExportService)
        {
            _projectService = projectService;
            _invitationService = invitationService;
            _currentUserService = currentUserService;
            _projectExportService = projectExportService;
        }

        /// <summary>
        /// Create a new systematic review project
        /// </summary>
        /// <param name="request">Project creation request</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Created project details</returns>
        [HttpPost]
        public async Task<ActionResult<ApiResponse<SystematicReviewProjectResponse>>> CreateProject(
            [FromBody] CreateSystematicReviewProjectRequest request,
            CancellationToken cancellationToken)
        {
            var result = await _projectService.CreateProjectAsync(request, cancellationToken);
            return Created(result, "Project created successfully.");
        }

        /// <summary>
        /// Get project by ID with full details including review processes
        /// </summary>
        /// <param name="id">Project ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Project details</returns>
        [HttpGet("{id}")]
        [HttpGet("/api/admin/projects/{id}")]
        public async Task<ActionResult<ApiResponse<SystematicReviewProjectDetailResponse>>> GetProjectById(
            [FromRoute] Guid id,
            CancellationToken cancellationToken)
        {
            var result = await _projectService.GetProjectByIdAsync(id, cancellationToken);


            return Ok(result, "Project retrieved successfully.");
        }

        /// <summary>
        /// Get paginated list of projects with optional status filter
        /// </summary>
        /// <param name="status">Optional status filter (Draft, Active, Completed, Archived)</param>
        /// <param name="pageNumber">Page number (default: 1)</param>
        /// <param name="pageSize">Page size (default: 10, max: 100)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Paginated list of projects</returns>
        [HttpGet]
        [HttpGet("/api/admin/projects")]
        public async Task<ActionResult<ApiResponse<PaginatedResponse<SystematicReviewProjectResponse>>>> GetProjects(
            [FromQuery] ProjectStatus? status,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            CancellationToken cancellationToken = default)
        {
            var result = await _projectService.GetProjectsAsync(status, pageNumber, pageSize, cancellationToken);
            return Ok(result, "Projects retrieved successfully.");
        }

        /// <summary>
        /// Export all projects to an Excel file.
        /// </summary>
        /// <param name="request">Export parameters</param>
        /// <returns>Excel file (.xlsx)</returns>
        [HttpGet("export/excel")]
        [HttpGet("/api/admin/projects/export/excel")]
        public async Task<IActionResult> ExportToExcel([FromQuery] ProjectExportRequest request)
        {
            var fileBytes = await _projectExportService.ExportProjectsToExcelAsync(request);
            var fileName = $"projects-export-{DateTimeOffset.UtcNow:yyyy-MM-dd}.xlsx";
            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        /// <summary>
        /// Update project details (title, domain, description)
        /// </summary>
        /// <param name="id">Project ID</param>
        /// <param name="request">Update request</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Updated project details</returns>
        [HttpPut("{id}")]
        public async Task<ActionResult<ApiResponse<SystematicReviewProjectResponse>>> UpdateProject(
            [FromRoute] Guid id,
            [FromBody] UpdateSystematicReviewProjectRequest request,
            CancellationToken cancellationToken)
        {
            if (id != request.Id)
            {
                return BadRequest<SystematicReviewProjectResponse>("ID in route does not match ID in request body.");
            }

            var result = await _projectService.UpdateProjectAsync(request, cancellationToken);
            return Ok(result, "Project updated successfully.");
        }

        /// <summary>
        /// Update project dates (StartDate, EndDate)
        /// </summary>
        /// <param name="id">Project ID</param>
        /// <param name="request">Update project dates request</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Updated project details</returns>
        [HttpPut("{id}/dates")]
        public async Task<ActionResult<ApiResponse<SystematicReviewProjectResponse>>> UpdateProjectDates(
            [FromRoute] Guid id,
            [FromBody] UpdateProjectDatesRequest request,
            CancellationToken cancellationToken)
        {
            if (id != request.Id)
            {
                throw new ArgumentException("ID in route does not match ID in request body.");
            }

            var result = await _projectService.UpdateProjectDatesAsync(request, cancellationToken);
            return Ok(result, "Project dates updated successfully.");
        }

        /// <summary>
        /// Activate a project (Draft → Active)
        /// </summary>
        /// <param name="id">Project ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Updated project details</returns>
        [HttpPost("{id}/activate")]
        public async Task<ActionResult<ApiResponse<SystematicReviewProjectResponse>>> ActivateProject(
            [FromRoute] Guid id,
            CancellationToken cancellationToken)
        {
            var result = await _projectService.ActivateProjectAsync(id, cancellationToken);
            return Ok(result, "Project activated successfully.");
        }

        /// <summary>
        /// Complete a project (Active → Completed). All processes must be completed first.
        /// </summary>
        /// <param name="id">Project ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Updated project details</returns>
        [HttpPost("{id}/complete")]
        public async Task<ActionResult<ApiResponse<SystematicReviewProjectResponse>>> CompleteProject(
            [FromRoute] Guid id,
            CancellationToken cancellationToken)
        {
            var result = await _projectService.CompleteProjectAsync(id, cancellationToken);
            return Ok(result, "Project completed successfully.");
        }



        /// <summary>
        /// Delete a project
        /// </summary>
        /// <param name="id">Project ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Success status</returns>
        [HttpDelete("{id}")]
        [HttpDelete("/api/admin/projects/{id}")]
        public async Task<ActionResult<ApiResponse>> DeleteProject(
            [FromRoute] Guid id,
            CancellationToken cancellationToken)
        {
            var result = await _projectService.DeleteProjectAsync(id, cancellationToken);


            return Ok("Project deleted successfully.");
        }

        /// <summary>
        /// Get members of a project by project ID with optional pagination and search
        /// </summary>
        /// <param name="projectId">Project ID</param>
        /// <param name="search">Optional search term for name, email, or username</param>
        /// <param name="pageNumber">Page number (default: 1)</param>
        /// <param name="pageSize">Page size (default: 10)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Paginated list of project members</returns>
        [HttpGet("{projectId}/members")]
        [HttpGet("/api/admin/projects/{projectId}/members")]
        public async Task<ActionResult<ApiResponse<PaginatedResponse<ProjectMemberDto>>>> GetProjectMembers(
            [FromRoute] Guid projectId,
            [FromQuery] string? search,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            CancellationToken cancellationToken = default)
        {
            var result = await _projectService.GetProjectMembersAsync(projectId, search, pageNumber, pageSize, cancellationToken);
            return Ok(result, "Project members retrieved successfully.");
        }

        /// <summary>
        /// Change a project member role.
        /// </summary>
        [HttpPut("/api/admin/projects/{projectId}/members/{memberId}/role")]
        public async Task<ActionResult<ApiResponse<ProjectMemberDto>>> ChangeProjectMemberRole(
            [FromRoute] Guid projectId,
            [FromRoute] Guid memberId,
            [FromBody] ChangeProjectMemberRoleRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await _projectService.ChangeProjectMemberRoleAsync(projectId, memberId, request, cancellationToken);
            return Ok(result, "Project member role updated successfully.");
        }

        /// <summary>
        /// Remove a project member.
        /// </summary>
        [HttpDelete("/api/admin/projects/{projectId}/members/{memberId}")]
        public async Task<ActionResult<ApiResponse>> RemoveProjectMember(
            [FromRoute] Guid projectId,
            [FromRoute] Guid memberId,
            CancellationToken cancellationToken = default)
        {
            await _projectService.RemoveProjectMemberAsync(projectId, memberId, cancellationToken);
            return Ok("Project member removed successfully.");
        }

        /// <summary>
        /// Replace a project leader with an existing member.
        /// </summary>
        [HttpPut("/api/admin/projects/{projectId}/leader")]
        public async Task<ActionResult<ApiResponse<ProjectMemberDto>>> ReplaceProjectLeader(
            [FromRoute] Guid projectId,
            [FromBody] ReplaceProjectLeaderRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await _projectService.ReplaceProjectLeaderAsync(projectId, request, cancellationToken);
            return Ok(result, "Project leader replaced successfully.");
        }

        /// <summary>
        /// Get all projects that the currently authenticated user is a member of
        /// </summary>
        /// <param name="status">Optional status filter</param>
        /// <param name="pageNumber">Page number</param>
        /// <param name="pageSize">Page size</param>
        /// <returns>Paginated list of projects with user's role</returns>
        [HttpGet("my")]
        public async Task<ActionResult<ApiResponse<PaginatedResponse<MyProjectResponse>>>> GetMyProjects(
            [FromQuery] ProjectStatus? status,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10)
        {
            var result = await _projectService.GetMyProjectsAsync(status, pageNumber, pageSize);
            return Ok(result, "Your projects retrieved successfully.");
        }

        /// <summary>
        /// Get the membership of the current authenticated user in a project
        /// </summary>
        /// <param name="projectId">Project ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Current user's role in the project</returns>
        [HttpGet("{projectId}/my-membership")]
        public async Task<ActionResult<ApiResponse<ProjectMembershipResponse>>> GetMyProjectMembership(
            [FromRoute] Guid projectId,
            CancellationToken cancellationToken = default)
        {
            var result = await _projectService.GetMyProjectMembershipAsync(projectId, cancellationToken);
            return Ok(result, "Project membership retrieved successfully.");
        }

        /// <summary>
        /// Create invitations for a project
        /// </summary>
        /// <param name="projectId">Project ID</param>
        /// <param name="request">Invitation request</param>
        /// <returns>Success response</returns>
        [HttpPost("{projectId}/invitations")]
        [HttpPost("/api/admin/projects/{projectId}/invitations")]
        public async Task<ActionResult<ApiResponse>> CreateInvitations(
            [FromRoute] Guid projectId,
            [FromBody] CreateProjectInvitationRequest request)
        {
            var (userId, _) = _currentUserService.GetCurrentUser();
            await _invitationService.CreateInvitationsAsync(projectId, Guid.Parse(userId), request);
            return Ok("Invitations created successfully.");
        }

        /// <summary>
        /// Get invitations for a project
        /// </summary>
        /// <param name="projectId">Project ID</param>
        /// <param name="status">Optional invitation status filter</param>
        /// <returns>List of project invitations</returns>
        [HttpGet("{projectId}/invitations")]
        [HttpGet("/api/admin/projects/{projectId}/invitations")]
        public async Task<ActionResult<ApiResponse<IEnumerable<ProjectInvitationResponse>>>> GetProjectInvitations(
            [FromRoute] Guid projectId,
            [FromQuery] ProjectMemberInvitationStatus? status = ProjectMemberInvitationStatus.Pending)
        {
            var (userId, _) = _currentUserService.GetCurrentUser();
            var result = await _invitationService.GetProjectInvitationsAsync(projectId, Guid.Parse(userId), status);
            return Ok(result, "Project invitations retrieved successfully.");
        }

        /// <summary>
        /// Cancel a pending invitation in a project
        /// </summary>
        /// <param name="projectId">Project ID</param>
        /// <param name="invitationId">Invitation ID</param>
        /// <returns>Success response</returns>
        [HttpDelete("{projectId}/invitations/{invitationId}")]
        [HttpDelete("/api/admin/projects/{projectId}/invitations/{invitationId}")]
        public async Task<ActionResult<ApiResponse>> CancelProjectInvitation(
            [FromRoute] Guid projectId,
            [FromRoute] Guid invitationId)
        {
            var (userId, _) = _currentUserService.GetCurrentUser();
            await _invitationService.CancelInvitationAsync(projectId, invitationId, Guid.Parse(userId));
            return Ok("Invitation cancelled successfully.");
        }

        /// <summary>
        /// Get all project members except Leader who are not assigned to a specific paper
        /// </summary>
        /// <param name="projectId">Project ID</param>
        /// <param name="paperId">Paper ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of available project members</returns>
        [HttpGet("{projectId}/papers/{paperId}/available-members")]
        public async Task<ActionResult<ApiResponse<List<ProjectMemberDto>>>> GetAvailableMembersForPaper(
            [FromRoute] Guid projectId,
            [FromRoute] Guid paperId,
            CancellationToken cancellationToken = default)
        {
            var result = await _projectService.GetAvailableMembersForPaperAsync(projectId, paperId, cancellationToken);
            return Ok(result, "Available project members retrieved successfully.");
        }

        /// <summary>
        /// Get list of PICOC elements for a project
        /// </summary>
        /// <param name="id">Project ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of project PICOCs</returns>
        [HttpGet("{id}/picocs")]
        public async Task<ActionResult<ApiResponse<List<ProjectPicocResponse>>>> GetProjectPicocs(
            [FromRoute] Guid id,
            CancellationToken cancellationToken = default)
        {
            var result = await _projectService.GetProjectPicocsAsync(id, cancellationToken);
            return Ok(result, "Project PICOCs retrieved successfully.");
        }

        /// <summary>
        /// Get list of research questions for a project
        /// </summary>
        /// <param name="id">Project ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of project research questions</returns>
        [HttpGet("{id}/research-questions")]
        public async Task<ActionResult<ApiResponse<List<ResearchQuestionDetailResponse>>>> GetProjectResearchQuestions(
            [FromRoute] Guid id,
            CancellationToken cancellationToken = default)
        {
            var result = await _projectService.GetProjectResearchQuestionsAsync(id, cancellationToken);
            return Ok(result, "Project research questions retrieved successfully.");
        }
    }
}

