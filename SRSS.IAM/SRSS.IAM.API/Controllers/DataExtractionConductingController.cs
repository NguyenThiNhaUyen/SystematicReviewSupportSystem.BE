using Microsoft.AspNetCore.Mvc;
using Shared.Models;
using Shared.Builder;
using SRSS.IAM.Services.DataExtractionService;
using SRSS.IAM.Services.DTOs.DataExtraction;

namespace SRSS.IAM.API.Controllers
{
    [ApiController]
    [Route("api/data-extraction-processes")]
    public class DataExtractionConductingController : BaseController
    {
        private readonly IDataExtractionConductingService _extractionService;

        public DataExtractionConductingController(IDataExtractionConductingService extractionService)
        {
            _extractionService = extractionService;
        }

        [HttpGet("{extractionProcessId}")]
        public async Task<ActionResult<ApiResponse<DataExtractionProcessResponse>>> GetById(
            [FromRoute] Guid extractionProcessId)
        {
            var result = await _extractionService.GetByIdAsync(extractionProcessId);
            if (result == null)
            {
                return NotFound(new ApiResponse<DataExtractionProcessResponse>
                {
                    IsSuccess = false,
                    Message = "Data extraction process not found."
                });
            }

            return Ok(result, "Data extraction process retrieved successfully.");
        }

        [HttpGet("{extractionProcessId}/dashboard")]
        public async Task<ActionResult<ApiResponse<ExtractionDashboardResponseDto>>> GetDashboard(
            [FromRoute] Guid extractionProcessId,
            [FromQuery] ExtractionDashboardFilterDto filter)
        {
            var result = await _extractionService.GetDashboardAsync(extractionProcessId, filter);
            return Ok(result, "Dashboard data retrieved successfully.");
        }

        [HttpPut("{extractionProcessId}/papers/{paperId}/assign")]
        public async Task<ActionResult<ApiResponse<object>>> AssignReviewers(
            [FromRoute] Guid extractionProcessId,
            [FromRoute] Guid paperId,
            [FromBody] AssignReviewersDto dto)
        {
            await _extractionService.AssignReviewersAsync(extractionProcessId, paperId, dto);
            return Ok<object>(new object(), "Reviewers assigned successfully.");
        }

        [HttpPost("{extractionProcessId}/start")]
        public async Task<ActionResult<ApiResponse<DataExtractionProcessResponse>>> Start([FromRoute] Guid extractionProcessId)
        {
            var result = await _extractionService.StartAsync(extractionProcessId);
            return Ok(result, "Data extraction process started successfully.");
        }

        [HttpPost("{extractionProcessId}/papers/{paperId}/submit")]
        public async Task<ActionResult<ApiResponse<object>>> SubmitExtraction(
            [FromRoute] Guid extractionProcessId,
            [FromRoute] Guid paperId,
            [FromBody] SubmitExtractionRequestDto request)
        {
            await _extractionService.SubmitExtractionAsync(extractionProcessId, paperId, request);
            return Ok<object>(new object(), "Data extraction submitted successfully.");
        }

        [HttpGet("{extractionProcessId}/papers/{paperId}/consensus")]
        public async Task<ActionResult<ApiResponse<ConsensusWorkspaceDto>>> GetConsensusWorkspace(
            [FromRoute] Guid extractionProcessId,
            [FromRoute] Guid paperId)
        {
            var result = await _extractionService.GetConsensusWorkspaceAsync(extractionProcessId, paperId);
            return Ok(result, "Consensus workspace retrieved successfully.");
        }

        [HttpPost("{extractionProcessId}/papers/{paperId}/consensus/submit")]
        public async Task<ActionResult<ApiResponse<object>>> SubmitConsensus(
            [FromRoute] Guid extractionProcessId,
            [FromRoute] Guid paperId,
            [FromBody] SubmitConsensusRequestDto request)
        {
            await _extractionService.SubmitConsensusAsync(extractionProcessId, paperId, request);
            return Ok<object>(new object(), "Consensus data submitted successfully.");
        }
        [HttpGet("{extractionProcessId}/export")]
        public async Task<IActionResult> ExportExtractedData([FromRoute] Guid extractionProcessId)
        {
            var fileContent = await _extractionService.ExportExtractedDataAsync(extractionProcessId);
            return File(fileContent, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"extraction_data_{extractionProcessId}.xlsx");
        }

        [HttpGet("{extractionProcessId}/export/csv")]
        public async Task<IActionResult> ExportExtractedDataCsv([FromRoute] Guid extractionProcessId, CancellationToken cancellationToken)
        {
            var fileContent = await _extractionService.ExportExtractedDataCsvAsync(extractionProcessId, cancellationToken);
            return File(fileContent, "text/csv", $"extraction_data_{extractionProcessId}.csv");
        }

        [HttpGet("{extractionProcessId}/export/preview")]
        public async Task<ActionResult<ApiResponse<ExtractionPreviewDto>>> PreviewExtractedData([FromRoute] Guid extractionProcessId)
        {
            var result = await _extractionService.GetPivotedExtractionDataAsync(extractionProcessId);
            return Ok(result, "Extraction preview data retrieved successfully.");
        }

        [HttpGet("{extractionProcessId}/editable-grid")]
        public async Task<ActionResult<ApiResponse<ExtractionEditableGridDto>>> GetEditableExtractionGrid([FromRoute] Guid extractionProcessId)
        {
            var result = await _extractionService.GetEditableExtractionGridAsync(extractionProcessId);
            return Ok(result, "Editable extraction grid retrieved successfully.");
        }

        [HttpPut("{extractionProcessId}/editable-grid/cell")]
        public async Task<ActionResult<ApiResponse<object>>> UpdateGridCell(
            [FromRoute] Guid extractionProcessId,
            [FromBody] UpdateGridCellRequestDto request)
        {
            await _extractionService.UpdateGridCellAsync(extractionProcessId, request);
            return Ok<object>(new object(), "Grid cell updated successfully.");
        }

        [HttpGet("{extractionProcessId}/audit-logs")]
        public async Task<ActionResult<ApiResponse<List<ExtractedDataAuditLogDto>>>> GetCellAuditLogs(
            [FromRoute] Guid extractionProcessId,
            [FromQuery] Guid paperId,
            [FromQuery] Guid fieldId,
            [FromQuery] Guid? matrixColumnId,
            [FromQuery] int? matrixRowIndex)
        {
            var result = await _extractionService.GetCellAuditLogsAsync(extractionProcessId, paperId, fieldId, matrixColumnId, matrixRowIndex);
            return Ok(result, "Audit logs retrieved successfully.");
        }

        [HttpPost("{extractionProcessId}/papers/{paperId}/reopen")]
        public async Task<ActionResult<ApiResponse<object>>> ReopenExtraction(
            [FromRoute] Guid extractionProcessId,
            [FromRoute] Guid paperId,
            [FromBody] ReopenExtractionRequestDto request)
        {
            await _extractionService.ReopenExtractionAsync(extractionProcessId, paperId, request);
            return Ok<object>(new object(), "Extraction reopened successfully for revision.");
        }

        [HttpPost("{extractionProcessId}/papers/{paperId}/auto-extract")]
        public async Task<ActionResult<ApiResponse<List<ExtractedValueDto>>>> AutoExtractWithAi(
            [FromRoute] Guid extractionProcessId,
            [FromRoute] Guid paperId)
        {
            var result = await _extractionService.AutoExtractWithAiAsync(extractionProcessId, paperId);
            return Ok(result, "Auto-extracted data retrieved successfully.");
        }

        [HttpPost("{extractionProcessId}/ask-ai-field")]
        public async Task<ActionResult<ApiResponse<ExtractedValueDto>>> AskAiSingleField(
            [FromRoute] Guid extractionProcessId,
            [FromBody] AskAiFieldRequestDto request,
            CancellationToken cancellationToken)
        {
            var result = await _extractionService.AskAiSingleFieldAsync(extractionProcessId, request, cancellationToken);
            return Ok(result, "AI extracted single field successfully.");
        }

        /// <summary>
        /// Leader Bypass: Allows a Project Leader to directly submit final extraction data
        /// for a paper, skipping the double-blind reviewer workflow and consensus phase entirely.
        /// The submitted values are persisted as IsConsensusFinal = true.
        /// </summary>
        [HttpPost("{extractionProcessId}/papers/{paperId}/direct-extract")]
        public async Task<ActionResult<ApiResponse<object>>> DirectExtractByLeader(
            [FromRoute] Guid extractionProcessId,
            [FromRoute] Guid paperId,
            [FromBody] SubmitExtractionRequestDto request,
            CancellationToken cancellationToken)
        {
            await _extractionService.DirectExtractByLeaderAsync(extractionProcessId, paperId, request, cancellationToken);
            return Ok<object>(new object(), "Direct extraction by leader completed successfully.");
        }

        /// <summary>
        /// Returns workload and progress analytics for the extraction process.
        /// Leaders receive statistics for all reviewers.
        /// Members receive only their personal workload summary.
        /// </summary>
        [HttpGet("{extractionProcessId}/workload-summary")]
        public async Task<ActionResult<ApiResponse<ExtractionWorkloadSummaryDto>>> GetWorkloadSummary(
            [FromRoute] Guid extractionProcessId,
            CancellationToken cancellationToken)
        {
            var result = await _extractionService.GetWorkloadSummaryAsync(extractionProcessId, cancellationToken);
            return Ok(result, "Workload summary retrieved successfully.");
        }
        [HttpPost("{extractionProcessId}/papers/{paperId}/fields/{fieldId}/comments")]
        public async Task<ActionResult<ApiResponse<ExtractionCommentDto>>> AddComment(
            [FromRoute] Guid extractionProcessId,
            [FromRoute] Guid paperId,
            [FromRoute] Guid fieldId,
            [FromBody] AddCommentRequestDto request)
        {
            var result = await _extractionService.AddCommentAsync(extractionProcessId, paperId, fieldId, request);
            return Ok(result, "Comment added successfully.");
        }

        [HttpGet("{extractionProcessId}/papers/{paperId}/reviewer-workspace")]
        public async Task<ActionResult<ApiResponse<ReviewerWorkspaceDto>>> GetReviewerWorkspace(
            [FromRoute] Guid extractionProcessId,
            [FromRoute] Guid paperId)
        {
            var result = await _extractionService.GetReviewerWorkspaceAsync(extractionProcessId, paperId);
            return Ok(result, "Reviewer workspace retrieved successfully.");
        }

        [HttpPost("{extractionProcessId}/complete")]
        public async Task<ActionResult<ApiResponse<object>>> CompletePhase(
            [FromRoute] Guid extractionProcessId,
            CancellationToken cancellationToken)
        {
            await _extractionService.CompleteAsync(extractionProcessId, cancellationToken);
            return Ok<object>(new object(), "Data extraction phase completed successfully and locked.");
        }
    }
}
