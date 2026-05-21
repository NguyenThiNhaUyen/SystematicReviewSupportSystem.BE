using SRSS.IAM.Services.DTOs.QualityAssessment;

namespace SRSS.IAM.Services.QualityAssessmentService
{
	public interface IQualityAssessmentService
	{
		// Quality Assessment Strategies
		Task<QualityAssessmentStrategyDto> UpsertStrategyAsync(QualityAssessmentStrategyDto dto);
		Task<List<QualityAssessmentStrategyDto>> GetStrategiesByReviewProcessIdAsync(Guid reviewProcessId);
		Task<List<QualityAssessmentStrategyDto>> GetStrategiesByProcessIdAsync(Guid processId);
		Task DeleteStrategyAsync(Guid strategyId);

		// Quality Checklists
		Task<List<QualityAssessmentChecklistDto>> BulkUpsertChecklistsAsync(List<QualityAssessmentChecklistDto> dtos);
		Task<List<QualityAssessmentChecklistDto>> GetChecklistsByStrategyIdAsync(Guid strategyId);

		// Quality Criteria
		Task<List<QualityAssessmentCriterionDto>> BulkUpsertCriteriaAsync(List<QualityAssessmentCriterionDto> dtos);
		Task<List<QualityAssessmentCriterionDto>> GetCriteriaByChecklistIdAsync(Guid checklistId);

		// Quality Assessment Processes
		Task<QualityAssessmentProcessResponse?> GetProcessByIdAsync(Guid id);
		Task<QualityAssessmentProcessResponse?> GetProcessByReviewProcessIdAsync(Guid reviewProcessId);
        Task<QualityAssessmentProcessResponse> CreateProcessAsync(CreateQualityAssessmentProcessDto dto);
		Task<QualityAssessmentProcessResponse> StartProcessAsync(Guid qaId);
		Task<QualityAssessmentProcessResponse> CompleteProcessAsync(Guid qaId);

		// Papers
		Task<QALeaderDashboardResponse> GetLeaderDashboardAsync(Guid qaId, int pageNumber = 1, int pageSize = 10, string? search = null);
		Task<QualityAssessmentStatisticsResponse> GetQualityStatisticsAsync(Guid processId);

		// Assignments
		Task AssignPapersToReviewersAsync(CreateQualityAssessmentAssignmentRequest dto);
        Task<QAMemberDashboardResponse> GetMemberDashboardAsync(Guid reviewProcessId, int pageNumber = 1, int pageSize = 10, string? search = null);

        // Decisions
        Task CreateDecisionAsync(CreateQualityAssessmentDecisionRequest dto);
        Task UpdateDecisionAsync(Guid decisionId, UpdateQualityAssessmentDecisionRequest dto);
        Task<List<QualityAssessmentDecisionResponse>> GetDecisionsByPaperIdAsync(Guid qaPaperId);

        // Resolutions
        Task<QualityAssessmentResolutionResponse> CreateResolutionAsync(CreateQualityAssessmentResolutionRequest dto);
        Task<QualityAssessmentResolutionResponse> UpdateResolutionAsync(Guid id, UpdateQualityAssessmentResolutionRequest dto);
        Task<QualityAssessmentResolutionResponse> GetResolutionByQaPaperIdAsync(Guid qaPaperId);
        Task<List<QAPaperResponse>> GetHighQualityPaperIdsAsync(Guid processId);
        Task AutoResolveProcessAsync(AutoResolveQualityAssessmentRequest request);

        // Automate
		Task<AutomateQualityAssessmentResponse> AutomateQualityAssessmentAsync(AutomateQualityAssessmentRequest request);

		// Export
		Task<byte[]> ExportProcessToExcelAsync(Guid processId);
	}
}
