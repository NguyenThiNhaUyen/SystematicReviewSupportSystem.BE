using SRSS.IAM.Repositories;
using Shared.Repositories;
using SRSS.IAM.Repositories.Entities;
using SRSS.IAM.Repositories.UnitOfWork;
using SRSS.IAM.Services.DTOs.QualityAssessment;
using SRSS.IAM.Services.Mappers;
using SRSS.IAM.Repositories.Entities.Enums;
using ClosedXML.Excel;
using System.IO;
using Microsoft.EntityFrameworkCore;
using SRSS.IAM.Services.NotificationService;
using SRSS.IAM.Services.DTOs.Notification;
using SRSS.IAM.Services.UserService;
using SRSS.IAM.Services.GeminiService;
using SRSS.IAM.Services.GrobidClient;
using SRSS.IAM.Services.RagService; // Added
using System.Net.Http;
using SRSS.IAM.Services.AuditLogService;
using Shared.Exceptions;
using SRSS.IAM.Services.DTOs.StudySelection;

using SRSS.IAM.Services.StudySelectionProcessPaperService;

namespace SRSS.IAM.Services.QualityAssessmentService
{
    public class QualityAssessmentService : IQualityAssessmentService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly INotificationService _notificationService;
        private readonly ICurrentUserService _currentUserService;
        private readonly IGeminiService _geminiService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IGrobidService _grobidService;
        private readonly IRagRetrievalService _ragRetrievalService; // Added
        private readonly IAuditLogService _auditLogService;
        private readonly IStudySelectionProcessPaperService _studySelectionProcessPaperService;

        public QualityAssessmentService(
            IUnitOfWork unitOfWork,
            INotificationService notificationService,
            ICurrentUserService currentUserService,
            IGeminiService geminiService,
            IHttpClientFactory httpClientFactory,
            IGrobidService grobidService,
            IRagRetrievalService ragRetrievalService, // Added
            IAuditLogService auditLogService,
            IStudySelectionProcessPaperService studySelectionProcessPaperService)
        {
            _unitOfWork = unitOfWork;
            _notificationService = notificationService;
            _currentUserService = currentUserService;
            _geminiService = geminiService;
            _httpClientFactory = httpClientFactory;
            _grobidService = grobidService;
            _ragRetrievalService = ragRetrievalService; // Added
            _auditLogService = auditLogService;
            _studySelectionProcessPaperService = studySelectionProcessPaperService;
        }

        private async Task EnsureLeaderAsync(Guid projectId)
        {
            var userIdString = _currentUserService.GetUserId();
            if (string.IsNullOrEmpty(userIdString))
            {
                throw new UnauthorizedException("User is not authenticated.");
            }

            var userId = Guid.Parse(userIdString);
            var isLeader = await _unitOfWork.SystematicReviewProjects.IsProjectLeaderAsync(projectId, userId);
            if (!isLeader)
            {
                throw new ForbiddenException("Only project leader can perform this action.");
            }
        }

        private async Task EnsureLeaderByQAProcessIdAsync(Guid processId)
        {
            var qaProcess = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == processId)
                ?? throw new KeyNotFoundException($"Quality Assessment Process {processId} không tồn tại");

            await EnsureLeaderByReviewProcessIdAsync(qaProcess.ReviewProcessId);
        }

        private async Task EnsureLeaderByStrategyIdAsync(Guid strategyId)
        {
            var strategy = await _unitOfWork.QualityStrategies.FindSingleAsync(s => s.Id == strategyId)
                ?? throw new KeyNotFoundException($"Strategy {strategyId} không tồn tại");

            await EnsureLeaderByQAProcessIdAsync(strategy.QualityAssessmentProcessId);
        }

        private async Task EnsureLeaderByChecklistIdAsync(Guid checklistId)
        {
            var checklist = await _unitOfWork.QualityChecklists.FindSingleAsync(c => c.Id == checklistId)
                ?? throw new KeyNotFoundException($"Checklist {checklistId} không tồn tại");

            await EnsureLeaderByStrategyIdAsync(checklist.QaStrategyId);
        }

        private async Task EnsureLeaderByReviewProcessIdAsync(Guid reviewProcessId)
        {
            var reviewProcess = await _unitOfWork.ReviewProcesses.FindSingleAsync(rp => rp.Id == reviewProcessId)
                ?? throw new KeyNotFoundException($"Review Process {reviewProcessId} không tồn tại");

            await EnsureLeaderAsync(reviewProcess.ProjectId);
        }

        // ==================== Quality Assessment Strategies ====================
        public async Task<QualityAssessmentStrategyDto> UpsertStrategyAsync(QualityAssessmentStrategyDto dto)
        {
            await EnsureLeaderByQAProcessIdAsync(dto.QualityAssessmentProcessId);

            QualityAssessmentStrategy entity;

            if (dto.QaStrategyId.HasValue && dto.QaStrategyId.Value != Guid.Empty)
            {
                entity = await _unitOfWork.QualityStrategies.FindSingleAsync(s => s.Id == dto.QaStrategyId.Value)
                    ?? throw new KeyNotFoundException($"Strategy {dto.QaStrategyId.Value} không tồn tại");

                dto.UpdateEntity(entity);
                await _unitOfWork.QualityStrategies.UpdateAsync(entity);
            }
            else
            {
                entity = dto.ToEntity();
                await _unitOfWork.QualityStrategies.AddAsync(entity);
            }

            await _unitOfWork.SaveChangesAsync();
            return entity.ToDto();
        }

        public async Task<List<QualityAssessmentStrategyDto>> GetStrategiesByReviewProcessIdAsync(Guid reviewProcessId)
        {
            var entities = await _unitOfWork.QualityStrategies.GetByReviewProcessIdAsync(reviewProcessId);
            return entities.ToDtoList();
        }

        /// <summary>
        /// Given a QualityAssessmentProcess id, return the full QA strategies for the underlying project
        /// including checklists and criteria.
        /// </summary>
        public async Task<List<QualityAssessmentStrategyDto>> GetStrategiesByProcessIdAsync(Guid processId)
        {
            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == processId);
            if (process == null) return new List<QualityAssessmentStrategyDto>();

            var strategy = await _unitOfWork.QualityStrategies.GetFullStrategyByQualityAssessmentProcessIdAsync(process.Id);

            return strategy.ToDtoList();
        }

        public async Task DeleteStrategyAsync(Guid strategyId)
        {
            var entity = await _unitOfWork.QualityStrategies.FindSingleAsync(s => s.Id == strategyId);
            if (entity != null)
            {
                await EnsureLeaderByQAProcessIdAsync(entity.QualityAssessmentProcessId);
                await _unitOfWork.QualityStrategies.RemoveAsync(entity);
                await _unitOfWork.SaveChangesAsync();
            }
        }

        // ==================== Quality Checklists ====================
        public async Task<List<QualityAssessmentChecklistDto>> BulkUpsertChecklistsAsync(List<QualityAssessmentChecklistDto> dtos)
        {
            if (dtos.Any())
            {
                await EnsureLeaderByStrategyIdAsync(dtos.First().QaStrategyId);
            }

            var results = new List<QualityChecklist>();

            foreach (var dto in dtos)
            {
                QualityChecklist? entity;

                if (dto.ChecklistId.HasValue && dto.ChecklistId.Value != Guid.Empty)
                {
                    entity = await _unitOfWork.QualityChecklists.FindSingleAsync(c => c.Id == dto.ChecklistId.Value);

                    if (entity != null)
                    {
                        dto.UpdateEntity(entity);
                        await _unitOfWork.QualityChecklists.UpdateAsync(entity);
                    }
                    else
                    {
                        entity = dto.ToEntity();
                        await _unitOfWork.QualityChecklists.AddAsync(entity);
                    }
                }
                else
                {
                    entity = dto.ToEntity();
                    await _unitOfWork.QualityChecklists.AddAsync(entity);
                }

                results.Add(entity);
            }

            await _unitOfWork.SaveChangesAsync();
            return results.ToDtoList();
        }

        public async Task<List<QualityAssessmentChecklistDto>> GetChecklistsByStrategyIdAsync(Guid strategyId)
        {
            var entities = await _unitOfWork.QualityChecklists.GetByStrategyIdAsync(strategyId);
            return entities.ToDtoList();
        }

        // ==================== Quality Criteria ====================
        public async Task<List<QualityAssessmentCriterionDto>> BulkUpsertCriteriaAsync(List<QualityAssessmentCriterionDto> dtos)
        {
            if (dtos.Any())
            {
                await EnsureLeaderByChecklistIdAsync(dtos.First().ChecklistId);
            }

            var results = new List<QualityCriterion>();

            foreach (var dto in dtos)
            {
                QualityCriterion? entity;

                if (dto.CriterionId.HasValue && dto.CriterionId.Value != Guid.Empty)
                {
                    entity = await _unitOfWork.QualityCriteria.FindSingleAsync(c => c.Id == dto.CriterionId.Value);

                    if (entity != null)
                    {
                        dto.UpdateEntity(entity);
                        await _unitOfWork.QualityCriteria.UpdateAsync(entity);
                    }
                    else
                    {
                        entity = dto.ToEntity();
                        await _unitOfWork.QualityCriteria.AddAsync(entity);
                    }
                }
                else
                {
                    entity = dto.ToEntity();
                    await _unitOfWork.QualityCriteria.AddAsync(entity);
                }

                results.Add(entity);
            }

            await _unitOfWork.SaveChangesAsync();
            return results.ToDtoList();
        }

        public async Task<List<QualityAssessmentCriterionDto>> GetCriteriaByChecklistIdAsync(Guid checklistId)
        {
            var entities = await _unitOfWork.QualityCriteria.GetByChecklistIdAsync(checklistId);
            return entities.ToDtoList();
        }

        // ==================== Quality Assessment Process ====================
        public async Task<QualityAssessmentProcessResponse?> GetProcessByIdAsync(Guid id)
        {
            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(
                p => p.Id == id);

            return process?.ToResponse();
        }

        public async Task<QualityAssessmentProcessResponse?> GetProcessByReviewProcessIdAsync(Guid reviewProcessId)
        {
            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(
                p => p.ReviewProcessId == reviewProcessId);

            if (process == null) return null!;

            return process.ToResponse();
        }

        public async Task<QualityAssessmentProcessResponse> CreateProcessAsync(CreateQualityAssessmentProcessDto dto)
        {
            await EnsureLeaderByReviewProcessIdAsync(dto.ReviewProcessId);

            // Check existence
            var existing = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.ReviewProcessId == dto.ReviewProcessId);
            if (existing != null)
                throw new InvalidOperationException($"Quality Assessment Process for Review Process {dto.ReviewProcessId} already exists.");

            var entity = dto.ToEntity();

            await _unitOfWork.QualityAssessmentProcesses.AddAsync(entity);
            await _unitOfWork.SaveChangesAsync();

            return entity.ToResponse();
        }

        public async Task<QualityAssessmentProcessResponse> StartProcessAsync(Guid id)
        {
            var entity = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == id)
                 ?? throw new KeyNotFoundException($"Không tìm thấy QA Process {id}");

            await EnsureLeaderByReviewProcessIdAsync(entity.ReviewProcessId);

            var hasCriteria = await _unitOfWork.QualityStrategies.AnyAsync(s => s.QualityAssessmentProcessId == entity.Id);
            if (!hasCriteria)
            {
                return new QualityAssessmentProcessResponse
                {
                    IsHaveCriteria = false
                };
            }

            var studySelectionProcess = await _unitOfWork.StudySelectionProcesses
                .FindSingleAsync(ssp => ssp.ReviewProcessId == entity.ReviewProcessId);

            if (studySelectionProcess != null && studySelectionProcess.Status != SelectionProcessStatus.Completed)
            {
                throw new InvalidOperationException("Study Selection Process must be completed before starting the Quality Assessment Process.");
            }

            entity.Start();

            await _unitOfWork.QualityAssessmentProcesses.UpdateAsync(entity);
            await _unitOfWork.SaveChangesAsync();

            var response = entity.ToResponse();
            response.IsHaveCriteria = true;
            return response;
        }

        public async Task<QualityAssessmentProcessResponse> CompleteProcessAsync(Guid id)
        {
            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == id)
                 ?? throw new KeyNotFoundException("Quality Assessment Process not found");

            await EnsureLeaderByReviewProcessIdAsync(process.ReviewProcessId);

            process.Status = QualityAssessmentProcessStatus.Completed;
            process.CompletedAt = DateTimeOffset.UtcNow;
            await _unitOfWork.QualityAssessmentProcesses.UpdateAsync(process);
            await _unitOfWork.SaveChangesAsync();

            return process.ToResponse();
        }

        public async Task<QALeaderDashboardResponse> GetLeaderDashboardAsync(Guid id, int pageNumber = 1, int pageSize = 10, string? search = null)
        {
            var result = new QALeaderDashboardResponse();

            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == id);
            if (process == null) return result;

            var eligiblePaperPage = await _studySelectionProcessPaperService.GetIncludedPapersByReviewProcessIdAsync(process.ReviewProcessId, search, pageNumber, pageSize, default);
            if (eligiblePaperPage == null) return result;

            var datasetPaperIds = eligiblePaperPage.Items.Select(x => x.PaperId).ToHashSet();
            var eligiblePapers = eligiblePaperPage.Items.ToList();

            // Also include any papers that are already assigned or have resolutions just in case
            var assignments = await _unitOfWork.QualityAssessmentAssignments.GetAllWithPapersByProcessIdAsync(process.Id);
            foreach (var assignment in assignments)
            {
                if (assignment.Papers != null)
                {
                    assignment.Papers = assignment.Papers.Where(p => datasetPaperIds.Contains(p.Id)).ToList();
                }
            }

            var processResolutions = await _unitOfWork.QualityAssessmentResolutions.FindAllAsync(r => r.QualityAssessmentProcessId == process.Id);

            // Calculate criteria count to compute percentage
            var criteriaCount = 0;
            var strategies = await _unitOfWork.QualityStrategies.GetFullStrategyByQualityAssessmentProcessIdAsync(process.Id);
            foreach (var strat in strategies)
            {
                foreach (var cl in strat.Checklists)
                {
                    criteriaCount += cl.Criteria.Count;
                }
            }

            // Also prepare Member Progress tracking
            var memberProgressDict = assignments
                .Where(a => a.User != null)
                .GroupBy(a => a.User.Id)
                .ToDictionary(g => g.Key, g => new QAReviewerProgressResponse
                {
                    ReviewerId = g.Key,
                    ReviewerName = g.First().User.FullName ?? g.First().User.Username,
                    TotalExpectedDecisions = g.SelectMany(a => a.Papers).Distinct().Count() * criteriaCount,
                    ActualDecisions = 0,
                    CompletedPapers = 0,
                    InProgressPapers = 0,
                    NotStartedPapers = 0
                });

            foreach (var paper in eligiblePapers)
            {
                var paperDecisions = await _unitOfWork.QualityAssessmentDecisions.GetByQaPaperIdWithDetailsAsync(paper.PaperId);

                var reviewersAssignedToPaper = assignments
                    .Where(a => a.Papers.Any(p => p.Id == paper.PaperId))
                    .Select(a => a.User)
                    .Where(u => u != null)
                    .GroupBy(u => u.Id)
                    .Select(g => g.First())
                    .ToList();

                // Build Member Progress per paper
                foreach (var reviewer in reviewersAssignedToPaper)
                {
                    if (memberProgressDict.TryGetValue(reviewer.Id, out var tracker))
                    {
                        var reviewerDecision = paperDecisions.FirstOrDefault(d => d.ReviewerId == reviewer.Id);
                        var itemsCount = reviewerDecision?.DecisionItems?.Count ?? 0;
                        tracker.ActualDecisions += itemsCount;

                        double reviewerCompletionPercentage = criteriaCount > 0 ? (double)itemsCount / criteriaCount * 100 : 0;

                        if (reviewerCompletionPercentage >= 100)
                        {
                            tracker.CompletedPapers++;
                        }
                        else if (reviewerCompletionPercentage > 0)
                        {
                            tracker.InProgressPapers++;
                        }
                        else
                        {
                            tracker.NotStartedPapers++;
                        }
                    }
                }

                // All criteria == all assigned reviewers * criteria count 
                var expectedDecisions = reviewersAssignedToPaper.Count * criteriaCount;
                var actualDecisions = 0;
                foreach (var decision in paperDecisions)
                {
                    actualDecisions += decision.DecisionItems.Count;
                }

                var percentage = expectedDecisions > 0 ? (double)actualDecisions / expectedDecisions * 100 : 0;
                if (percentage > 100) percentage = 100;

                var resolution = processResolutions.FirstOrDefault(r => r.PaperId == paper.PaperId);

                string? resolvedByName = null;
                if (resolution != null)
                {
                    var resolutionReviewer = await _unitOfWork.Users.FindSingleAsync(u => u.Id == resolution.ResolvedBy);
                    resolvedByName = resolutionReviewer?.FullName ?? resolutionReviewer?.Username;
                }

                var summary = paper.ToLeaderDashboardPaperResponse(
                    percentage,
                    resolution,
                    reviewersAssignedToPaper,
                    paperDecisions,
                    resolvedByName
                );

                if (summary.Status == "resolved") result.CompletedPapers++;
                else if (summary.Status == "in-progress") result.InProgressPapers++;
                else result.NotStartedPapers++;

                result.Papers.Items.Add(summary);
            }

            result.TotalPapers = eligiblePaperPage.TotalCount;
            result.Papers.TotalCount = eligiblePaperPage.TotalCount;
            result.Papers.PageNumber = eligiblePaperPage.PageNumber;
            result.Papers.PageSize = eligiblePaperPage.PageSize;

            foreach (var kvp in memberProgressDict)
            {
                var info = kvp.Value;
                var completion = info.TotalExpectedDecisions > 0
                    ? (double)info.ActualDecisions / info.TotalExpectedDecisions * 100
                    : 0;

                if (completion > 100) completion = 100;

                info.CompletionPercentage = completion;
                result.ReviewerProgresses.Add(info);
            }

            return result;
        }

        public async Task<QualityAssessmentStatisticsResponse> GetQualityStatisticsAsync(Guid processId)
        {
            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == processId);
            if (process == null) return new QualityAssessmentStatisticsResponse();

            var hasSetupCriteria = await _unitOfWork.QualityStrategies.AnyAsync(s => s.QualityAssessmentProcessId == process.Id);

            var eligiblePaperPage = await _studySelectionProcessPaperService.GetIncludedPapersByReviewProcessIdAsync(process.ReviewProcessId, null, 1, 1000000, default);
            var eligiblePapers = eligiblePaperPage?.Items.ToList() ?? new List<IncludedPaperResponse>();
            var processResolutions = await _unitOfWork.QualityAssessmentResolutions.FindAllAsync(r => r.QualityAssessmentProcessId == process.Id);

            var totalPapers = eligiblePapers.Count;
            var highQuality = processResolutions.Count(r => r.FinalDecision == Repositories.Entities.Enums.QualityAssessmentResolutionDecision.HighQuality);
            var lowQuality = processResolutions.Count(r => r.FinalDecision == Repositories.Entities.Enums.QualityAssessmentResolutionDecision.LowQuality);

            var assignments = await _unitOfWork.QualityAssessmentAssignments.GetAllWithPapersByProcessIdAsync(process.Id);

            int notStartedCount = 0;
            int inProgressCount = 0;

            foreach (var paper in eligiblePapers)
            {
                var resolution = processResolutions.FirstOrDefault(r => r.PaperId == paper.PaperId);
                if (resolution != null)
                {
                    continue; // Handled by High/Low
                }

                // It's not resolved, check progress
                var paperDecisions = await _unitOfWork.QualityAssessmentDecisions.GetByQaPaperIdWithDetailsAsync(paper.PaperId);
                var reviewersAssignedToPaper = assignments
                    .Where(a => a.Papers.Any(p => p.Id == paper.PaperId))
                    .Select(a => a.User)
                    .Where(u => u != null)
                    .GroupBy(u => u.Id)
                    .Select(g => g.First())
                    .ToList();

                var expectedDecisionsCount = reviewersAssignedToPaper.Count; // This is just checking if we expect anything
                var actualDecisionsCount = paperDecisions.Sum(d => d.DecisionItems.Count);

                if (expectedDecisionsCount > 0 && actualDecisionsCount > 0)
                {
                    inProgressCount++;
                }
                else
                {
                    notStartedCount++;
                }
            }

            return new QualityAssessmentStatisticsResponse
            {
                TotalPapers = totalPapers,
                HighQualityPapers = highQuality,
                LowQualityPapers = lowQuality,
                InProgressPapers = inProgressCount,
                NotStartedPapers = notStartedCount,
                HasSetupCriteria = hasSetupCriteria,
            };
        }

        public async Task AssignPapersToReviewersAsync(CreateQualityAssessmentAssignmentRequest dto)
        {
            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == dto.QualityAssessmentProcessId)
                ?? throw new KeyNotFoundException("Quality Assessment Process not found");

            await EnsureLeaderByReviewProcessIdAsync(process.ReviewProcessId);

            var processAssignments = await _unitOfWork.QualityAssessmentAssignments.GetAllWithPapersByProcessIdAsync(process.Id);

            // Validate reviewer limit per paper (max 2)
            foreach (var qualityAssessmentPaperId in dto.PaperIds)
            {
                var assignedUsers = processAssignments
                    .Where(a => a.Papers != null && a.Papers.Any(p => p.Id == qualityAssessmentPaperId))
                    .Select(a => a.UserId)
                    .ToList();

                var newUsers = dto.UserIds.Except(assignedUsers).ToList();
                if (assignedUsers.Count + newUsers.Count > 2)
                {
                    throw new InvalidOperationException($"Cannot assign paper with ID {qualityAssessmentPaperId} to more than 2 reviewers.");
                }
            }

            // Validate Users and Papers exist (optional but recommended)

            foreach (var userId in dto.UserIds)
            {
                // Find existing assignment or create new
                var assignment = await _unitOfWork.QualityAssessmentAssignments
                    .FindSingleAsync(a => a.QualityAssessmentProcessId == dto.QualityAssessmentProcessId && a.UserId == userId);

                if (assignment == null)
                {
                    // isNewAssignment = true;
                    assignment = dto.ToEntity(userId);
                    await _unitOfWork.QualityAssessmentAssignments.AddAsync(assignment);
                    await _unitOfWork.SaveChangesAsync(); // Save to get ID
                }

                // Check resolution for each paper before assigning
                var papersToAdd = new List<Paper>();
                foreach (var qualityAssessmentPaperId in dto.PaperIds)
                {
                    // If paper has resolution, skip adding
                    var qaPaper = await _unitOfWork.Papers
                        .FindSingleAsync(p => p.Id == qualityAssessmentPaperId);

                    if (qaPaper == null) continue;

                    var hasResolution = await _unitOfWork.QualityAssessmentResolutions
                       .AnyAsync(r => r.PaperId == qaPaper.Id && r.QualityAssessmentProcessId == dto.QualityAssessmentProcessId);

                    if (hasResolution) continue;

                    papersToAdd.Add(qaPaper);
                }

                if (assignment.Papers == null) assignment.Papers = new List<Paper>();

                // Add papers to assignment (many-to-many) - This requires loading the collection or using a direct insert approach if valid
                // Since EF Core generic repo might not expose direct collection manipulation easily without tracking,
                // we might need to rely on the tracking. Ensure FindSingleAsync tracks or attached.
                // Best to load with Include if possible, but for now let's assume we can add.

                // Actually, for many-to-many updates in disconnected scenarios or without full loading, it can be tricky.
                // Let's assume we need to load the assignment with papers first to avoid duplicates.
                var assignmentWithPapers = await _unitOfWork.QualityAssessmentAssignments.GetWithPapersAsync(assignment.Id);

                if (assignmentWithPapers != null)
                {
                    foreach (var paper in papersToAdd)
                    {
                        if (assignmentWithPapers.Papers != null && !assignmentWithPapers.Papers.Any(p => p.Id == paper.Id))
                        {
                            assignmentWithPapers.Papers.Add(paper);
                        }
                    }
                    await _unitOfWork.QualityAssessmentAssignments.UpdateAsync(assignmentWithPapers);
                }

                // Send Notification
                if (papersToAdd.Any())
                {
                    await _notificationService.SendAsync(userId, "New QA Assignment", $"You have been assigned {papersToAdd.Count} papers for Quality Assessment.", NotificationType.System);
                }
            }
            await _unitOfWork.SaveChangesAsync();
        }

        public async Task<QAMemberDashboardResponse> GetMemberDashboardAsync(Guid id, int pageNumber = 1, int pageSize = 10, string? search = null)
        {
            var (userIdString, _) = _currentUserService.GetCurrentUser();
            Guid userId = Guid.Parse(userIdString);

            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == id);
            if (process == null) return new QAMemberDashboardResponse();

            var assignment = await _unitOfWork.QualityAssessmentAssignments.GetWithPapersByProcessAndUserAsync(process.Id, userId);

            if (assignment == null || assignment.Papers == null) return new QAMemberDashboardResponse();

            var includedPapersPage = await _studySelectionProcessPaperService.GetIncludedPapersByReviewProcessIdAsync(process.ReviewProcessId, search, pageNumber, pageSize, default);
            if (includedPapersPage == null) return new QAMemberDashboardResponse();

            var datasetPaperIds = includedPapersPage.Items.Select(x => x.PaperId).ToHashSet();
            assignment.Papers = assignment.Papers.Where(p => datasetPaperIds.Contains(p.Id)).ToList();

            // Need to calculate completion percentage.
            // 1. Get Project -> QA Strategy -> Checklists -> Criteria count
            var reviewProcess = await _unitOfWork.ReviewProcesses.FindSingleAsync(rp => rp.Id == process.ReviewProcessId);
            if (reviewProcess == null) return new QAMemberDashboardResponse();

            var strategies = await _unitOfWork.QualityStrategies.GetFullStrategyByQualityAssessmentProcessIdAsync(process.Id);
            var criteriaCount = 0;
            foreach (var strat in strategies)
            {
                foreach (var cl in strat.Checklists)
                {
                    criteriaCount += cl.Criteria.Count;
                }
            }

            var result = new QAMemberDashboardResponse();
            var totalExpectedDecisions = assignment.Papers.Count * criteriaCount;
            var actualDecisions = 0;

            foreach (var assignPaper in assignment.Papers)
            {
                var dictPaper = includedPapersPage.Items.FirstOrDefault(x => x.PaperId == assignPaper.Id);
                if (dictPaper == null) continue;

                // Check for resolution
                var resolution = await _unitOfWork.QualityAssessmentResolutions.FindSingleAsync(
                   r => r.PaperId == assignPaper.Id && r.QualityAssessmentProcessId == process.Id);

                string? resolvedByName = null;
                if (resolution != null)
                {
                    var resovledBy = await _unitOfWork.Users.FindSingleAsync(u => u.Id == resolution.ResolvedBy);
                    resolvedByName = resovledBy?.FullName ?? resovledBy?.Username;
                }

                // Calculate decisions count for this paper by this user
                var userDecisions = await _unitOfWork.QualityAssessmentDecisions.GetByQaPaperIdAndUserIdWithDetailsAsync(assignPaper.Id, userId);
                var userDecisionsCount = userDecisions?.DecisionItems?.Count ?? 0;
                actualDecisions += userDecisionsCount;

                double percentage = criteriaCount > 0 ? (double)userDecisionsCount / criteriaCount * 100 : 0;
                if (percentage > 100) percentage = 100; // Cap at 100 if updates happen

                var dto = dictPaper.ToMemberDashboardPaperResponse(percentage, resolution, userDecisions, resolvedByName);
                result.Papers.Items.Add(dto);

                if (dto.Status == "completed" || dto.Status == "resolved") result.CompletedPapers++;
                else if (dto.Status == "in-progress") result.InProgressPapers++;
                else result.NotStartedPapers++;
            }

            result.TotalPapers = includedPapersPage.TotalCount;
            result.CompletionPercentage = totalExpectedDecisions > 0 ? (double)actualDecisions / totalExpectedDecisions * 100 : 0;
            if (result.CompletionPercentage > 100) result.CompletionPercentage = 100;

            result.Papers.TotalCount = includedPapersPage.TotalCount;
            result.Papers.PageNumber = includedPapersPage.PageNumber;
            result.Papers.PageSize = includedPapersPage.PageSize;

            return result;
        }

        // ==================== Decisions ====================
        public async Task CreateDecisionAsync(CreateQualityAssessmentDecisionRequest dto)
        {
            var (currentUserIdStr, _) = _currentUserService.GetCurrentUser();
            var userId = Guid.Parse(currentUserIdStr);

            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == dto.QualityAssessmentProcessId)
                ?? throw new KeyNotFoundException("Process not found");

            var reviewProcess = await _unitOfWork.ReviewProcesses.FindSingleAsync(rp => rp.Id == process.ReviewProcessId)
                ?? throw new KeyNotFoundException("Review process not found");

            var isLeader = await _unitOfWork.SystematicReviewProjects.IsProjectLeaderAsync(reviewProcess.ProjectId, userId);

            // Validate and find assignment if not leader
            if (!isLeader)
            {
                var assignment = await _unitOfWork.QualityAssessmentAssignments.GetWithPapersByProcessAndUserAsync(dto.QualityAssessmentProcessId, userId);
                if (assignment == null) throw new KeyNotFoundException("Assignment not found for this user in this process");

                var paperInAssignment = assignment.Papers?.FirstOrDefault(x => x.Id == dto.PaperId);
                if (paperInAssignment == null) throw new KeyNotFoundException("Assignment not found for this user and QA paper");

                // Check if resolution exists
                var resolution = await _unitOfWork.QualityAssessmentResolutions.FindSingleAsync(
                    r => r.QualityAssessmentProcessId == process.Id && r.PaperId == dto.PaperId);

                if (resolution != null)
                {
                    throw new InvalidOperationException("Cannot add decision because a final resolution has already been made for this paper.");
                }
            }

            // Check existing decision
            var existing = await _unitOfWork.QualityAssessmentDecisions.FindSingleAsync(
                d => d.ReviewerId == userId && d.PaperId == dto.PaperId);

            if (existing != null)
                throw new InvalidOperationException("Decision for this paper already exists. Use update instead.");

            // Construct new decision
            var decision = dto.ToEntity(userId, dto.PaperId);

            if (dto.DecisionItems != null && dto.DecisionItems.Any())
            {
                foreach (var item in dto.DecisionItems)
                {
                    var criterionExists = await _unitOfWork.QualityCriteria.FindSingleAsync(c => c.Id == item.QualityCriterionId) != null;
                    if (!criterionExists)
                    {
                        throw new KeyNotFoundException($"Quality Criterion not found: {item.QualityCriterionId}");
                    }

                    decision.DecisionItems.Add(item.ToEntity());
                }
            }

            await _unitOfWork.QualityAssessmentDecisions.AddAsync(decision);
            await _unitOfWork.SaveChangesAsync();
        }

        public async Task UpdateDecisionAsync(Guid decisionId, UpdateQualityAssessmentDecisionRequest dto)
        {
            dto.Id = decisionId;
            var (currentUserIdStr, _) = _currentUserService.GetCurrentUser();
            var userId = Guid.Parse(currentUserIdStr);

            var decision = await _unitOfWork.QualityAssessmentDecisions.GetByIdWithDetailsAsync(decisionId);
            if (decision == null) throw new KeyNotFoundException("Decision not found.");

            var qaPaperId = decision.PaperId;

            var qaPaper = await _unitOfWork.Papers.FindSingleAsync(p => p.Id == qaPaperId);
            if (qaPaper == null) throw new KeyNotFoundException("Paper not found");

            if (decision.ReviewerId != userId)
                throw new UnauthorizedAccessException("You can only update your own decisions.");

            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == decision.QualityAssessmentProcessId);
            var reviewProcess = await _unitOfWork.ReviewProcesses.FindSingleAsync(rp => rp.Id == process!.ReviewProcessId);
            var isLeader = await _unitOfWork.SystematicReviewProjects.IsProjectLeaderAsync(reviewProcess!.ProjectId, userId);

            if (!isLeader)
            {
                // Check if resolution exists
                var resolution = await _unitOfWork.QualityAssessmentResolutions.FindSingleAsync(
                    r => r.QualityAssessmentProcessId == decision.QualityAssessmentProcessId && r.PaperId == qaPaperId);

                if (resolution != null)
                {
                    throw new InvalidOperationException("Cannot update decision because a final resolution has already been made for this paper.");
                }
            }

            dto.UpdateEntity(decision);

            // Update items
            foreach (var itemDto in dto.DecisionItems)
            {
                QualityAssessmentDecisionItem? item = null;

                if (itemDto.Id.HasValue && itemDto.Id.Value != Guid.Empty)
                {
                    item = decision.DecisionItems.FirstOrDefault(di => di.Id == itemDto.Id.Value);
                }

                if (item == null && itemDto.QualityCriterionId.HasValue && itemDto.QualityCriterionId.Value != Guid.Empty)
                {
                    item = decision.DecisionItems.FirstOrDefault(di => di.QualityCriterionId == itemDto.QualityCriterionId.Value);
                }

                if (item != null)
                {
                    itemDto.UpdateEntity(item);
                }
                else if (itemDto.QualityCriterionId.HasValue && itemDto.QualityCriterionId.Value != Guid.Empty)
                {
                    var criterionExists = await _unitOfWork.QualityCriteria.FindSingleAsync(c => c.Id == itemDto.QualityCriterionId.Value) != null;
                    if (!criterionExists)
                    {
                        throw new InvalidOperationException($"Quality Criterion not found: {itemDto.QualityCriterionId.Value}");
                    }

                    // If item does not exist, add new
                    decision.DecisionItems.Add(itemDto.ToEntity());
                }
                else
                {
                    throw new InvalidOperationException("Could not identify the decision item to update or create.");
                }
            }

            await _unitOfWork.QualityAssessmentDecisions.UpdateAsync(decision);
            await _unitOfWork.SaveChangesAsync();
        }

        public async Task<List<QualityAssessmentDecisionResponse>> GetDecisionsByPaperIdAsync(Guid paperId)
        {
            var decisions = await _unitOfWork.QualityAssessmentDecisions.GetByQaPaperIdWithDetailsAsync(paperId);

            return decisions.Select(d => d.ToDto()).ToList();
        }

        // ==================== Resolutions ====================
        public async Task<QualityAssessmentResolutionResponse> CreateResolutionAsync(CreateQualityAssessmentResolutionRequest dto)
        {
            await EnsureLeaderByQAProcessIdAsync(dto.QualityAssessmentProcessId);
            var (currentUserIdStr, _) = _currentUserService.GetCurrentUser();
            var userId = Guid.Parse(currentUserIdStr);

            var qaPaper = await _unitOfWork.Papers.FindSingleAsync(p => p.Id == dto.PaperId);
            if (qaPaper == null) throw new KeyNotFoundException("Paper not found");

            // Verify existing
            var existing = await _unitOfWork.QualityAssessmentResolutions.FindSingleAsync(
                r => r.QualityAssessmentProcessId == dto.QualityAssessmentProcessId && r.PaperId == dto.PaperId);

            if (existing != null) throw new InvalidOperationException("Resolution already exists for this paper");

            // set the resolved by using current user
            var entity = dto.ToEntity(userId);

            await _unitOfWork.QualityAssessmentResolutions.AddAsync(entity);
            await _unitOfWork.SaveChangesAsync();

            return entity.ToResponse();
        }

        public async Task<QualityAssessmentResolutionResponse> UpdateResolutionAsync(Guid id, UpdateQualityAssessmentResolutionRequest dto)
        {
            dto.Id = id;
            var (currentUserIdStr, _) = _currentUserService.GetCurrentUser();
            var userId = Guid.Parse(currentUserIdStr);

            var entity = await _unitOfWork.QualityAssessmentResolutions.FindSingleAsync(r => r.Id == id)
                ?? throw new KeyNotFoundException("Resolution not found");

            await EnsureLeaderByQAProcessIdAsync(entity.QualityAssessmentProcessId);

            dto.UpdateEntity(entity);
            // Optionally update ResolvedBy or ResolvedAt here if desired:
            entity.ResolvedBy = userId;
            entity.ResolvedAt = DateTimeOffset.UtcNow;

            await _unitOfWork.QualityAssessmentResolutions.UpdateAsync(entity);
            await _unitOfWork.SaveChangesAsync();

            return entity.ToResponse();
        }

        public async Task<QualityAssessmentResolutionResponse> GetResolutionByQaPaperIdAsync(Guid qaPaperId)
        {
            var qaPaper = await _unitOfWork.Papers.FindSingleAsync(p => p.Id == qaPaperId);
            if (qaPaper == null) return null!;

            var res = await _unitOfWork.QualityAssessmentResolutions.FindSingleAsync(r => r.PaperId == qaPaper.Id);
            if (res == null) return null!;

            return res.ToResponse();
        }

        public async Task<List<QAPaperResponse>> GetHighQualityPaperIdsAsync(Guid processId)
        {
            var resolutions = await _unitOfWork.QualityAssessmentResolutions
                .FindAllAsync(r => r.QualityAssessmentProcessId == processId && r.FinalDecision == QualityAssessmentResolutionDecision.HighQuality);

            var qaPaperIds = resolutions.Select(r => r.PaperId).ToList();
            var papers = await _unitOfWork.Papers.FindAllAsync(p => qaPaperIds.Contains(p.Id));

            return papers.Select(p => p.ToResponse()).ToList();
        }

        public async Task AutoResolveProcessAsync(AutoResolveQualityAssessmentRequest request)
        {
            await EnsureLeaderByQAProcessIdAsync(request.QualityAssessmentProcessId);
            var (currentUserIdStr, _) = _currentUserService.GetCurrentUser();
            Guid currentUserId = Guid.Parse(currentUserIdStr);

            if (!request.Score.HasValue && !request.Percentage.HasValue)
                throw new ArgumentException("Either Score or Percentage must be provided");

            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == request.QualityAssessmentProcessId);
            if (process == null) throw new KeyNotFoundException("Process not found");

            var eligiblePaperPage = await _studySelectionProcessPaperService.GetIncludedPapersByReviewProcessIdAsync(process.ReviewProcessId, null, 1, 1000000, default);
            var eligiblePapers = eligiblePaperPage?.Items.ToList() ?? new List<IncludedPaperResponse>();

            var assignments = await _unitOfWork.QualityAssessmentAssignments.GetAllWithPapersByProcessIdAsync(process.Id);
            var processResolutions = await _unitOfWork.QualityAssessmentResolutions.FindAllAsync(r => r.QualityAssessmentProcessId == process.Id);

            var reviewProcess = await _unitOfWork.ReviewProcesses.FindSingleAsync(rp => rp.Id == process.ReviewProcessId);
            var criteriaCount = 0;
            if (reviewProcess != null)
            {
                var strategies = await _unitOfWork.QualityStrategies.GetFullStrategyByReviewProcessIdAsync(reviewProcess.Id);
                foreach (var strat in strategies)
                {
                    foreach (var cl in strat.Checklists)
                    {
                        criteriaCount += cl.Criteria.Count;
                    }
                }
            }

            foreach (var paper in eligiblePapers)
            {
                if (processResolutions.Any(r => r.PaperId == paper.PaperId)) continue;

                var paperDecisions = await _unitOfWork.QualityAssessmentDecisions.GetByQaPaperIdWithDetailsAsync(paper.PaperId);

                var reviewersAssignedToPaper = assignments
                    .Where(a => a.Papers != null && a.Papers.Any(p => p.Id == paper.PaperId))
                    .Select(a => a.User)
                    .Where(u => u != null)
                    .GroupBy(u => u.Id)
                    .Select(g => g.First())
                    .ToList();

                var expectedDecisions = reviewersAssignedToPaper.Count * criteriaCount;
                var actualDecisions = paperDecisions.Sum(d => d.DecisionItems.Count);

                var completionPercentage = expectedDecisions > 0 ? (double)actualDecisions / expectedDecisions * 100 : 0;

                if (expectedDecisions == 0 || completionPercentage < 100) continue;

                double totalScore = 0;
                foreach (var decision in paperDecisions)
                {
                    foreach (var item in decision.DecisionItems)
                    {
                        if (item.Value == QualityAssessmentDecisionValue.Yes) totalScore += 1;
                        else if (item.Value == QualityAssessmentDecisionValue.Unclear) totalScore += 0.5;
                    }
                }

                double avgScore = reviewersAssignedToPaper.Count > 0 ? totalScore / reviewersAssignedToPaper.Count : 0;

                bool isPassed = false;
                if (request.Score.HasValue)
                {
                    isPassed = avgScore >= request.Score.Value;
                }
                else if (request.Percentage.HasValue)
                {
                    double maxPossibleAvgScore = criteriaCount;
                    double currPercentage = maxPossibleAvgScore > 0 ? (avgScore / maxPossibleAvgScore) * 100 : 0;
                    isPassed = currPercentage >= request.Percentage.Value;
                }

                var resolution = new QualityAssessmentResolution
                {
                    QualityAssessmentProcessId = process.Id,
                    PaperId = paper.PaperId,
                    FinalDecision = isPassed ? QualityAssessmentResolutionDecision.HighQuality : QualityAssessmentResolutionDecision.LowQuality,
                    FinalScore = (decimal)avgScore,
                    ResolutionNotes = "Auto-resolved",
                    ResolvedBy = currentUserId,
                    ResolvedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ModifiedAt = DateTimeOffset.UtcNow
                };

                await _unitOfWork.QualityAssessmentResolutions.AddAsync(resolution);
            }

            await _unitOfWork.SaveChangesAsync();
        }

        // ==================== Quality Assessment Automate ====================
        public async Task<AutomateQualityAssessmentResponse> AutomateQualityAssessmentAsync(AutomateQualityAssessmentRequest request)
        {
            var (currentUserIdStr, _) = _currentUserService.GetCurrentUser();
            var userId = Guid.Parse(currentUserIdStr);

            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == request.QualityAssessmentProcessId)
                ?? throw new KeyNotFoundException("Process not found");

            var reviewProcess = await _unitOfWork.ReviewProcesses.FindSingleAsync(rp => rp.Id == process.ReviewProcessId)
                ?? throw new KeyNotFoundException("Review process not found");

            var paper = await _unitOfWork.Papers.FindSingleAsync(p => p.Id == request.PaperId);
            if (paper == null) throw new KeyNotFoundException("Paper Not Found");

            var isLeader = await _unitOfWork.SystematicReviewProjects.IsProjectLeaderAsync(reviewProcess.ProjectId, userId);

            // Validate and find assignment if not leader
            if (!isLeader)
            {
                var assignment = await _unitOfWork.QualityAssessmentAssignments.GetWithPapersByProcessAndUserAsync(request.QualityAssessmentProcessId, userId);
                if (assignment == null) throw new KeyNotFoundException("Assignment not found for this user in this process");

                var qaPaperInAssignment = assignment.Papers?.FirstOrDefault(x => x.Id == request.PaperId);
                if (qaPaperInAssignment == null) throw new KeyNotFoundException("Assignment not found for this user and QA paper");

                // Check if resolution exists
                var resolution = await _unitOfWork.QualityAssessmentResolutions.FindSingleAsync(
                    r => r.QualityAssessmentProcessId == process.Id && r.PaperId == paper.Id);

                if (resolution != null)
                {
                    throw new InvalidOperationException("Cannot automate assessment because a final resolution has already been made for this paper.");
                }
            }

            var strategy = await _unitOfWork.QualityStrategies.GetFullStrategyByReviewProcessIdAsync(reviewProcess.Id);

            var criteriaQuestions = strategy.SelectMany(s => s.Checklists.SelectMany(c => c.Criteria)).Select(c => new { c.Id, c.Question }).ToList();

            var paperPdf = await _unitOfWork.PaperPdfs.GetQueryable(pp => pp.PaperId == paper.Id)
                .Include(pp => pp.PaperFullText)
                .Include(pp => pp.GrobidHeaderResult)
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(paperPdf?.PaperFullText?.RawXml) || string.IsNullOrWhiteSpace(paperPdf?.GrobidHeaderResult?.RawXml))
            {
                throw new InvalidOperationException("Failed to get full text for the paper.");
            }

            var prompt = $@"
Assume you are an expert reviewer conducting a quality assessment of a scientific paper. I want you to evaluate this paper against the following criteria questions.

Note: 
- For each question, decide if the answer is Yes (0), No (1), or Unclear (2), and provide a brief comment explaining your reasoning.
- Also give pdfHighlightCoordinates based on the `coords` attribute from the raw XML provided. Keep precise format `page,x,y,height,width` for example: `'1,72.0,103.0,174.0,20.0'` (semicolon separated for multiple). Ensure the bounding boxes match where you drew your evidence.

-------------------------

Criteria Questions:

{string.Join("\n\n", criteriaQuestions.Select(c =>
   $"--- CRITERION START ---\n" +
   $"ID: {c.Id}\n" +
   $"Question: {c.Question}\n" +
   $"--- CRITERION END ---"
))}

--------------------------------

Here is the paper in TEI XML format (with `coords` attributes for bounding boxes):
```xml
{$"Header: \n {paperPdf.GrobidHeaderResult.RawXml}\n" +
   $"Full Text: \n {paperPdf.PaperFullText.RawXml}\n"}
```
";

            var result = await _geminiService.GenerateStructuredContentAsync<List<QualityAssessmentDecisionItemAIResponse>>(prompt);

            if (result == null || !result.Any())
            {
                throw new Exception("AI failed to generate assessment.");
            }

            // Extract page dimensions if missing
            if (!paperPdf!.PageWidth.HasValue || !paperPdf.PageHeight.HasValue)
            {
                var (pageWidth, pageHeight) = GrobidTeiParser.ParseSurfaceSize(paperPdf.PaperFullText!.RawXml);
                if (pageWidth.HasValue)
                    paperPdf.PageWidth = pageWidth.Value;
                if (pageHeight.HasValue)
                    paperPdf.PageHeight = pageHeight.Value;

                paperPdf.ModifiedAt = DateTimeOffset.UtcNow;
                await _unitOfWork.PaperPdfs.UpdateAsync(paperPdf);
                await _unitOfWork.SaveChangesAsync();
            }

            await _auditLogService.AppendCustomAuditLogAsync(
                projectId: reviewProcess.ProjectId,
                action: "AI Automated Quality Assessment executed",
                actionType: "Automate",
                resourceType: "Paper",
                resourceId: paper.Id.ToString(),
                newValue: new { GeneratedDecisionsCount = result.Count, PaperTitle = paper.Title }
            );

            return new AutomateQualityAssessmentResponse
            {
                PageWidth = paperPdf.PageWidth,
                PageHeight = paperPdf.PageHeight,
                DecisionItems = result
            };
        }
        // ==================== Export Excel ====================
        public async Task<byte[]> ExportProcessToExcelAsync(Guid processId)
        {
            await EnsureLeaderByQAProcessIdAsync(processId);

            var process = await _unitOfWork.QualityAssessmentProcesses.FindSingleAsync(p => p.Id == processId);
            if (process == null) throw new KeyNotFoundException("Quality Assessment Process not found");

            // Reuse existing method to get paper summaries
            var papersResult = await GetLeaderDashboardAsync(processId);
            var papers = papersResult.Papers;

            // Fetch full strategy to get criteria list (domains)
            var strategies = await GetStrategiesByProcessIdAsync(processId);
            // Flatten all criteria from all strategies and checklists
            var allCriteria = strategies
                .SelectMany(s => s.Checklists)
                .SelectMany(c => c.Criteria)
                .ToList();

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("QualityAssessment");

            // Build headers: Row1 main headers, Row2 subheaders
            // Columns: A=Study Id, B=Reviewer
            ws.Cell("A1").Value = "Study Id";
            ws.Range("A1:A2").Merge().Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Cell("B1").Value = "Reviewer";
            ws.Range("B1:B2").Merge().Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            var startCol = 3; // C
            for (int i = 0; i < allCriteria.Count; i++)
            {
                var criterion = allCriteria[i];
                var col = startCol + i * 3;

                // Merge three cells for criterion question (domain)
                var startAddress = ws.Cell(1, col).Address.ToStringRelative();
                var endAddress = ws.Cell(1, col + 2).Address.ToStringRelative();
                ws.Range(startAddress + ":" + endAddress).Merge();

                ws.Cell(1, col).Value = criterion.Question; // Use criteria name/question as domain name
                ws.Cell(2, col).Value = "Judgement";
                ws.Cell(2, col + 1).Value = "Quotes";
                ws.Cell(2, col + 2).Value = "Comments";

                // center domain title
                var domainRange = ws.Range(startAddress + ":" + endAddress);
                domainRange.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                domainRange.Style.Font.SetBold();
            }

            // Style first two header cells
            var headerRange = ws.Range("A1:B2");
            headerRange.Style.Font.SetBold();
            headerRange.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            // Populate data
            var currentRow = 3;
            foreach (var paper in papers.Items)
            {
                // Use decisions list (each decision is per reviewer)
                var decisions = paper.Decisions ?? new List<QualityAssessmentDecisionResponse>();

                // Also include resolution as a consensus row if exists
                // if (paper.Resolution != null)
                // {
                //     // Create a synthetic decision row for consensus
                //     decisions = decisions.Append(new QualityAssessmentDecisionResponse
                //     {
                //         ReviewerId = paper.Resolution.ResolvedBy,
                //         ReviewerName = paper.Resolution.ResolvedByName ?? "Consensus",
                //         QualityAssessmentPaperId = paper.Resolution.QualityAssessmentPaperId,
                //         DecisionItems = new List<QualityAssessmentDecisionItemResponse>()
                //         // Note: Resolution doesn't currently store per-criterion items in DTO easily accessibly 
                //         // unless we load them separately or if they are just the final decision.
                //         // For now, Consensus row might be empty for items unless Resolution has items (it usually doesn't in this simplified model 
                //         // unless we query QualityAssessmentResolution -> which is just FinalDecision/Score in DTO).
                //         // If Resolution had items, we would map them here. Assuming empty for now as per DTO.
                //     }).ToList();
                // }

                foreach (var dec in decisions)
                {
                    ws.Cell(currentRow, 1).Value = paper.Title ?? paper.PaperId.ToString();
                    ws.Cell(currentRow, 2).Value = dec.ReviewerName ?? string.Empty;

                    // For each criterion column, find the matching decision item
                    for (int i = 0; i < allCriteria.Count; i++)
                    {
                        var criterion = allCriteria[i];
                        var col = startCol + i * 3;

                        // Find item by CriterionId
                        var item = dec.DecisionItems?.FirstOrDefault(di => di.QualityCriterionId == criterion.CriterionId);

                        if (item != null)
                        {
                            ws.Cell(currentRow, col).Value = item.Value?.ToString() ?? string.Empty; // Judgement
                            ws.Cell(currentRow, col + 1).Value = string.Empty; // Quotes
                            ws.Cell(currentRow, col + 2).Value = item.Comment ?? string.Empty; // Comments
                        }
                        else
                        {
                            ws.Cell(currentRow, col).Value = string.Empty;
                            ws.Cell(currentRow, col + 1).Value = string.Empty;
                            ws.Cell(currentRow, col + 2).Value = string.Empty;
                        }
                    }

                    currentRow++;
                }
            }

            // Auto-fit columns
            ws.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        // private async Task EnsureLeaderAsync(Guid protocolId)
        // {
        //     var userIdString = _currentUserService.GetUserId();
        //     if (string.IsNullOrEmpty(userIdString))
        //     {
        //         throw new UnauthorizedException("User is not authenticated.");
        //     }

        //     var protocol = await _unitOfWork.Protocols.FindSingleAsync(p => p.Id == protocolId)
        //         ?? throw new KeyNotFoundException($"Protocol {protocolId} không tồn tại");

        //     var userId = Guid.Parse(userIdString);
        //     var isLeader = await _unitOfWork.SystematicReviewProjects.IsProjectLeaderAsync(protocol.ProjectId, userId);
        //     if (!isLeader)
        //     {
        //         throw new ForbiddenException("Only project leader can perform this action.");
        //     }
        // }

        // private async Task EnsureLeaderByStrategyIdAsync(Guid strategyId)
        // {
        //     var strategy = await _unitOfWork.QualityStrategies.FindSingleAsync(s => s.Id == strategyId)
        //         ?? throw new KeyNotFoundException($"Strategy {strategyId} không tồn tại");

        //     await EnsureLeaderAsync(strategy.ProtocolId);
        // }

        // private async Task EnsureLeaderByChecklistIdAsync(Guid checklistId)
        // {
        //     var checklist = await _unitOfWork.QualityChecklists.FindSingleAsync(c => c.Id == checklistId)
        //         ?? throw new KeyNotFoundException($"Checklist {checklistId} không tồn tại");

        //     await EnsureLeaderByStrategyIdAsync(checklist.QaStrategyId);
        // }

        // private async Task EnsureLeaderByReviewProcessIdAsync(Guid reviewProcessId)
        // {
        //     var reviewProcess = await _unitOfWork.ReviewProcesses.FindSingleAsync(rp => rp.Id == reviewProcessId)
        //         ?? throw new KeyNotFoundException($"Review Process {reviewProcessId} không tồn tại");

        //     if (reviewProcess.ProtocolId == null)
        //     {
        //         throw new InvalidOperationException("Review Process không có Protocol đi kèm.");
        //     }

        //     await EnsureLeaderAsync(reviewProcess.ProtocolId.Value);
        // }
    }
}
