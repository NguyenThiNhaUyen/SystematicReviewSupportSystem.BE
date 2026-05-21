using SRSS.IAM.Services.DTOs.DataExtraction;

namespace SRSS.IAM.Services.DataExtractionService
{
	public interface IDataExtractionConductingService
	{
		Task<DataExtractionProcessResponse?> GetByIdAsync(Guid extractionProcessId);
		Task<ExtractionDashboardResponseDto> GetDashboardAsync(Guid extractionProcessId, ExtractionDashboardFilterDto filter);
		Task AssignReviewersAsync(Guid extractionProcessId, Guid paperId, AssignReviewersDto dto);
		Task<DataExtractionProcessResponse> StartAsync(Guid extractionProcessId);
		Task SubmitExtractionAsync(Guid extractionProcessId, Guid paperId, SubmitExtractionRequestDto request);
		Task<ConsensusWorkspaceDto> GetConsensusWorkspaceAsync(Guid extractionProcessId, Guid paperId);
		Task SubmitConsensusAsync(Guid extractionProcessId, Guid paperId, SubmitConsensusRequestDto request);
		Task<byte[]> ExportExtractedDataAsync(Guid extractionProcessId);
		Task<byte[]> ExportExtractedDataCsvAsync(Guid extractionProcessId, CancellationToken cancellationToken = default);
		Task<ExtractionPreviewDto> GetPivotedExtractionDataAsync(Guid extractionProcessId);
		Task<ExtractionEditableGridDto> GetEditableExtractionGridAsync(Guid extractionProcessId);
		Task UpdateGridCellAsync(Guid extractionProcessId, UpdateGridCellRequestDto request);
		Task ReopenExtractionAsync(Guid extractionProcessId, Guid paperId, ReopenExtractionRequestDto request);
		Task<List<ExtractedValueDto>> AutoExtractWithAiAsync(Guid extractionProcessId, Guid paperId);
		Task<ExtractedValueDto?> AskAiSingleFieldAsync(Guid extractionProcessId, AskAiFieldRequestDto request, CancellationToken cancellationToken = default);
		Task DirectExtractByLeaderAsync(Guid extractionProcessId, Guid paperId, SubmitExtractionRequestDto payload, CancellationToken cancellationToken);
		Task<ExtractionWorkloadSummaryDto> GetWorkloadSummaryAsync(Guid extractionProcessId, CancellationToken cancellationToken);
		Task CompleteAsync(Guid extractionProcessId, CancellationToken cancellationToken);
		Task<List<ExtractedDataAuditLogDto>> GetCellAuditLogsAsync(Guid processId, Guid paperId, Guid fieldId, Guid? matrixColumnId, int? matrixRowIndex);
		Task<ExtractionCommentDto> AddCommentAsync(Guid extractionProcessId, Guid paperId, Guid fieldId, AddCommentRequestDto request);
		Task<ReviewerWorkspaceDto> GetReviewerWorkspaceAsync(Guid extractionProcessId, Guid paperId);
	}
}
