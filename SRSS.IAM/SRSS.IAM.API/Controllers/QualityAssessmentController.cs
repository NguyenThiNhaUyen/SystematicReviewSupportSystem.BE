using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Builder;
using Shared.Models;
using SRSS.IAM.Services.DTOs.QualityAssessment;
using SRSS.IAM.Services.QualityAssessmentService;

namespace SRSS.IAM.API.Controllers
{
	[Authorize]
	[ApiController]
	[Route("api/quality-assessment")]
	public class QualityAssessmentController : BaseController
	{
		private readonly IQualityAssessmentService _service;

		public QualityAssessmentController(IQualityAssessmentService service)
		{
			_service = service;
		}

		// ==================== Quality Assessment Strategies ====================

		/// <summary>
		/// Upsert một Quality Assessment Strategy
		/// </summary>
		[HttpPost("strategies/upsert")]
		public async Task<ActionResult<ApiResponse<QualityAssessmentStrategyDto>>> UpsertStrategy(
			[FromBody] QualityAssessmentStrategyDto dto)
		{
			var result = await _service.UpsertStrategyAsync(dto);
			return Ok(result, "Lưu quality strategy thành công");
		}

		/// <summary>
		/// Lấy tất cả Quality Strategies theo Review Process ID
		/// </summary>
		[HttpGet("review-process/{reviewProcessId}/strategies")]
		public async Task<ActionResult<ApiResponse<List<QualityAssessmentStrategyDto>>>> GetStrategiesByReviewProcessId(
			Guid reviewProcessId)
		{
			var result = await _service.GetStrategiesByReviewProcessIdAsync(reviewProcessId);
			return Ok(result, "Lấy danh sách strategies thành công");
		}

		/// <summary>
		/// Xóa một Quality Strategy
		/// </summary>
		[HttpDelete("strategies/{strategyId}")]
		public async Task<ActionResult<ApiResponse>> DeleteStrategy(Guid strategyId)
		{
			await _service.DeleteStrategyAsync(strategyId);
			return Ok("Xóa strategy thành công");
		}

		// ==================== Quality Checklists ====================

		/// <summary>
		/// Bulk Upsert Quality Checklists
		/// </summary>
		[HttpPost("checklists/bulk")]
		public async Task<ActionResult<ApiResponse<List<QualityAssessmentChecklistDto>>>> BulkUpsertChecklists(
			[FromBody] List<QualityAssessmentChecklistDto> dtos)
		{
			var result = await _service.BulkUpsertChecklistsAsync(dtos);
			return Ok(result, $"Lưu {result.Count} checklists thành công");
		}

		/// <summary>
		/// Lấy Checklists theo Strategy ID
		/// </summary>
		[HttpGet("strategies/{strategyId}/checklists")]
		public async Task<ActionResult<ApiResponse<List<QualityAssessmentChecklistDto>>>> GetChecklistsByStrategyId(
			Guid strategyId)
		{
			var result = await _service.GetChecklistsByStrategyIdAsync(strategyId);
			return Ok(result, "Lấy danh sách checklists thành công");
		}

		// ==================== Quality Criteria ====================

		/// <summary>
		/// Bulk Upsert Quality Criteria
		/// </summary>
		[HttpPost("criteria/bulk")]
		public async Task<ActionResult<ApiResponse<List<QualityAssessmentCriterionDto>>>> BulkUpsertCriteria(
			[FromBody] List<QualityAssessmentCriterionDto> dtos)
		{
			var result = await _service.BulkUpsertCriteriaAsync(dtos);
			return Ok(result, $"Lưu {result.Count} quality criteria thành công");
		}

		/// <summary>
		/// Lấy Criteria theo Checklist ID
		/// </summary>
		[HttpGet("checklists/{checklistId}/criteria")]
		public async Task<ActionResult<ApiResponse<List<QualityAssessmentCriterionDto>>>> GetCriteriaByChecklistId(
			Guid checklistId)
		{
			var result = await _service.GetCriteriaByChecklistIdAsync(checklistId);
			return Ok(result, "Lấy danh sách criteria thành công");
		}

		// ==================== Quality Assessment Process ====================
		[HttpGet("{id}")]
		public async Task<ActionResult<ApiResponse<QualityAssessmentProcessResponse>>> GetProcessById(Guid id)
		{
			var result = await _service.GetProcessByIdAsync(id);
			if (result == null)
				return NotFound(new ApiResponse<QualityAssessmentProcessResponse> { IsSuccess = false, Message = "Process not found" });
			return Ok(result, "Láº¥y thÃ´ng tin QA process thÃ nh cÃ´ng");
		}

		[HttpGet("process/{reviewProcessId}")]
		public async Task<ActionResult<ApiResponse<QualityAssessmentProcessResponse>>> GetProcessByReviewProcessId(Guid reviewProcessId)
		{
			var result = await _service.GetProcessByReviewProcessIdAsync(reviewProcessId);
			if (result == null)
				return NotFound(new ApiResponse<QualityAssessmentProcessResponse> { IsSuccess = false, Message = "Process not found" });
			return Ok(result, "Lấy thông tin QA process thành công");
		}

		[HttpPost("process")]
		public async Task<ActionResult<ApiResponse<QualityAssessmentProcessResponse>>> CreateProcess([FromBody] CreateQualityAssessmentProcessDto dto)
		{
			var result = await _service.CreateProcessAsync(dto);
			return Ok(result, "Tạo QA process thành công");
		}

		[HttpPost("{id}/start")]
		public async Task<ActionResult<ApiResponse<QualityAssessmentProcessResponse>>> StartProcess(Guid id)
		{
			var result = await _service.StartProcessAsync(id);
			return Ok(result, "Khởi động Quality Assessment Process thành công");
		}

		[HttpPost("{id}/complete")]
		public async Task<ActionResult<ApiResponse<QualityAssessmentProcessResponse>>> CompleteProcess(Guid id)
		{
			var result = await _service.CompleteProcessAsync(id);
			return Ok(result, "Kết thúc Quality Assessment Process thành công");
		}

		// ==================== Assignments ====================
		[HttpPost("assignments")]
		public async Task<ActionResult<ApiResponse>> AssignPapersToReviewers([FromBody] CreateQualityAssessmentAssignmentRequest dto)
		{
			await _service.AssignPapersToReviewersAsync(dto);
			return Ok("Phân công thành công");
		}

		[HttpGet("{id}/assignments/my")]
		public async Task<ActionResult<ApiResponse<QAMemberDashboardResponse>>> GetMemberDashboard(Guid id, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10, [FromQuery] string? search = null)
		{
			var result = await _service.GetMemberDashboardAsync(id, pageNumber, pageSize, search);
			return Ok(result, "Lấy danh sách bài báo được phân công thành công");
		}

		[HttpGet("{id}/leader")]
		public async Task<ActionResult<ApiResponse<QALeaderDashboardResponse>>> GetLeaderDashboardByProcessId(Guid id, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10, [FromQuery] string? search = null)
		{
			var result = await _service.GetLeaderDashboardAsync(id, pageNumber, pageSize, search);
			return Ok(result, "Lấy danh sách bài báo thành công");
		}

		/// <summary>
		/// Export quality assessment for a QA process to Excel
		/// </summary>
		[HttpGet("{id}/export/excel")]
		public async Task<IActionResult> ExportExcel(Guid id)
		{
			var bytes = await _service.ExportProcessToExcelAsync(id);
			return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"qa-export-{id}.xlsx");
		}

		/// <summary>
		/// Lấy full QA Strategy (checklists + criteria) theo QA Process ID
		/// </summary>
		[HttpGet("{id}/strategies")]
		public async Task<ActionResult<ApiResponse<List<QualityAssessmentStrategyDto>>>> GetStrategiesByProcessId(Guid id)
		{
			var result = await _service.GetStrategiesByProcessIdAsync(id);
			return Ok(result, "Lấy chiến lược QA đầy đủ thành công");
		}

		// ==================== Decisions ====================
		[HttpPost("decisions")]
		public async Task<ActionResult<ApiResponse>> CreateDecision([FromBody] CreateQualityAssessmentDecisionRequest dto)
		{
			await _service.CreateDecisionAsync(dto);
			return Ok("Lưu quyết định thành công");
		}

		[HttpPut("decisions/{id}")]
		public async Task<ActionResult<ApiResponse>> UpdateDecision(Guid id, [FromBody] UpdateQualityAssessmentDecisionRequest dto)
		{
			await _service.UpdateDecisionAsync(id, dto);
			return Ok("Cập nhật quyết định thành công");
		}
		
		[HttpPost("decisions/ai")]
		public async Task<ActionResult<ApiResponse<AutomateQualityAssessmentResponse>>> AutomateQualityAssessment([FromBody] AutomateQualityAssessmentRequest request)
		{
			var result = await _service.AutomateQualityAssessmentAsync(request);
			return Ok(result, "Thực hiện tự động đánh giá chất lượng thành công");
		}

		[HttpGet("papers/{paperId}/decisions")]
		public async Task<ActionResult<ApiResponse<List<QualityAssessmentDecisionResponse>>>> GetDecisionsByPaperId(Guid paperId)
		{
			var result = await _service.GetDecisionsByPaperIdAsync(paperId);
			return Ok(result, "Lấy danh sách quyết định thành công");
		}

		// ==================== Resolutions ====================
		[HttpPost("resolutions")]
		public async Task<ActionResult<ApiResponse<QualityAssessmentResolutionResponse>>> CreateResolution([FromBody] CreateQualityAssessmentResolutionRequest dto)
		{
			var result = await _service.CreateResolutionAsync(dto);
			return Ok(result, "Lưu kết quả cuối cùng thành công");
		}

		[HttpPut("resolutions/{id}")]
		public async Task<ActionResult<ApiResponse<QualityAssessmentResolutionResponse>>> UpdateResolution(Guid id, [FromBody] UpdateQualityAssessmentResolutionRequest dto)
		{
			var result = await _service.UpdateResolutionAsync(id, dto);
			return Ok(result, "Cập nhật kết quả cuối cùng thành công");
		}

		[HttpGet("papers/{qaPaperId}/resolution")]
		public async Task<ActionResult<ApiResponse<QualityAssessmentResolutionResponse>>> GetResolutionByQaPaperId(Guid qaPaperId)
		{
			var result = await _service.GetResolutionByQaPaperIdAsync(qaPaperId);
			if (result == null)
				return NotFound(new ApiResponse<QualityAssessmentResolutionResponse> { IsSuccess = false, Message = "Resolution not found" });
			return Ok(result, "Lấy kết quả cuối cùng thành công");
		}

		[HttpPost("auto-resolve")]
		public async Task<ActionResult<ApiResponse>> AutoResolveProcess([FromBody] AutoResolveQualityAssessmentRequest request)
		{
			await _service.AutoResolveProcessAsync(request);
			return Ok("Tự động quyết định thành công");
		}

		[HttpGet("process/{processId}/high-quality-papers")]
		public async Task<ActionResult<ApiResponse<List<QAPaperResponse>>>> GetHighQualityPaperIds(Guid processId)
		{
			var result = await _service.GetHighQualityPaperIdsAsync(processId);
			return Ok(result, "Lấy danh sách ID paper chất lượng cao thành công");
		}
	}
}
