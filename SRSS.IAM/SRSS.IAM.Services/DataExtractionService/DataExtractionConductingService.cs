using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SRSS.IAM.Services.GrobidClient;
using Shared.Models;
using SRSS.IAM.Repositories.Entities;
using SRSS.IAM.Repositories.Entities.Enums;
using SRSS.IAM.Repositories.UnitOfWork;
using SRSS.IAM.Services.DTOs.DataExtraction;
using ClosedXML.Excel;
using SRSS.IAM.Services.UserService;
using SRSS.IAM.Services.RagService;
using SRSS.IAM.Services.NotificationService;
using System.Xml.Linq;
using SRSS.IAM.Services.OpenRouter;


namespace SRSS.IAM.Services.DataExtractionService
{
    public class DataExtractionConductingService : IDataExtractionConductingService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly ICurrentUserService _currentUserService;
        private readonly IGrobidService _grobidService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly IRagRetrievalService _ragRetrievalService;
        private readonly IRagIngestionQueue _ragQueue;
        private readonly INotificationService _notificationService;
        private readonly IOpenRouterService _openRouterService;


        public DataExtractionConductingService(
            IUnitOfWork unitOfWork,
            ICurrentUserService currentUserService,
            IGrobidService grobidService,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IRagRetrievalService ragRetrievalService,
            IRagIngestionQueue ragQueue,
            INotificationService notificationService,
            IOpenRouterService openRouterService)
        {
            _unitOfWork = unitOfWork;
            _currentUserService = currentUserService;
            _grobidService = grobidService;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _ragRetrievalService = ragRetrievalService;
            _ragQueue = ragQueue;
            _notificationService = notificationService;
            _openRouterService = openRouterService;
        }


        private async Task SyncEligiblePapersAsync(Guid extractionProcessId, CancellationToken cancellationToken = default)
        {
            // 1. Lấy thông tin ReviewProcess để biết ID của StudySelectionProcess
            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                    .ThenInclude(rp => rp.StudySelectionProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId, cancellationToken);

            if (extractionProcess?.ReviewProcess?.StudySelectionProcess == null) return;

            var selectionProcessId = extractionProcess.ReviewProcess.StudySelectionProcess.Id;

            // 2. Lấy danh sách Paper đã PASS vòng Full-Text Screening (Include Paper để lấy PdfUrl)
            var (eligiblePapers, _) = await _unitOfWork.StudySelectionProcessPapers.GetWithPaperByProcessAsync(
                selectionProcessId,
                pageSize: int.MaxValue,
                cancellationToken: cancellationToken);

            // 3. Lấy danh sách Task đã tồn tại trong Data Extraction để tránh tạo trùng
            var existingTaskPaperIds = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .Where(t => t.DataExtractionProcessId == extractionProcessId)
                .Select(t => t.PaperId)
                .ToListAsync(cancellationToken);

            // 4. Tìm ra những Paper mới được pass Screening nhưng chưa có Task ở Extraction
            var newPapers = eligiblePapers.Where(sr => !existingTaskPaperIds.Contains(sr.PaperId)).ToList();

            if (newPapers.Any())
            {
                var newTasks = newPapers.Select(sr => new ExtractionPaperTask
                {
                    DataExtractionProcessId = extractionProcessId,
                    PaperId = sr.PaperId,
                    Status = PaperExtractionStatus.NotStarted,
                    Reviewer1Status = ReviewerTaskStatus.NotStarted,
                    Reviewer2Status = ReviewerTaskStatus.NotStarted,
                    CreatedAt = DateTimeOffset.UtcNow
                }).ToList();

                // Lưu hàng loạt xuống DB
                await _unitOfWork.ExtractionPaperTasks.AddRangeAsync(newTasks);
                await _unitOfWork.SaveChangesAsync(cancellationToken);

                // ==========================================
                // KÍCH HOẠT RAG INGESTION
                // ==========================================
                var paperIds = newPapers.Select(t => t.PaperId).ToList();
                var papers = await _unitOfWork.Papers
                    .GetQueryable()
                    .Where(p => paperIds.Contains(p.Id))
                    .ToListAsync(cancellationToken);
                foreach (var paper in papers)
                {
                    await _ragQueue.QueuePaperForIngestionAsync(paper.Id, paper.PdfUrl);
                }

            }
        }

        public async Task<ExtractionDashboardResponseDto> GetDashboardAsync(Guid extractionProcessId, ExtractionDashboardFilterDto filter)
        {
            // --- Role-Based Filtering ---
            var currentUserIdStr = _currentUserService.GetUserId();
            if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
            {
                throw new UnauthorizedAccessException("Current user ID is invalid.");
            }

            await SyncEligiblePapersAsync(extractionProcessId);

            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId);

            if (extractionProcess?.ReviewProcess == null)
            {
                throw new InvalidOperationException($"DataExtractionProcess {extractionProcessId} or its ReviewProcess not found.");
            }

            var projectId = extractionProcess.ReviewProcess.ProjectId;

            var currentUserProjectMember = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Where(p => p.Id == projectId)
                .SelectMany(p => p.ProjectMembers)
                .FirstOrDefaultAsync(pm => pm.UserId == currentUserId);

            if (currentUserProjectMember == null)
            {
                throw new UnauthorizedAccessException($"User is not a member of project {projectId}.");
            }

            var query = _unitOfWork.ExtractionPaperTasks.GetTasksByProcessQueryable(extractionProcessId);

            // Reviewers only see papers assigned to them
            if (currentUserProjectMember.Role == ProjectRole.Member)
            {
                query = query.Where(t => t.Reviewer1Id == currentUserId || t.Reviewer2Id == currentUserId);
            }

            // Calculate Summary on the (potentially filtered) dataset
            var totalIncluded = await query.CountAsync();
            var inProgressCount = await query.CountAsync(t => t.Status == PaperExtractionStatus.InProgress);
            var awaitingConsensusCount = await query.CountAsync(t => t.Status == PaperExtractionStatus.AwaitingConsensus);
            var completedCount = await query.CountAsync(t => t.Status == PaperExtractionStatus.Completed);

            // Apply SearchFilter
            if (!string.IsNullOrWhiteSpace(filter.SearchQuery))
            {
                var searchTerm = filter.SearchQuery.ToLower();
                query = query.Where(t =>
                    t.Paper.Title.ToLower().Contains(searchTerm) ||
                    (t.Paper.Authors != null && t.Paper.Authors.ToLower().Contains(searchTerm)));
            }

            // Apply StatusFilter
            if (!string.IsNullOrWhiteSpace(filter.StatusFilter) && filter.StatusFilter.ToLower() != "all")
            {
                var statusStr = filter.StatusFilter.ToLower();
                var statusEnum = statusStr switch
                {
                    "todo" => PaperExtractionStatus.NotStarted,
                    "in-progress" => PaperExtractionStatus.InProgress,
                    "awaiting-consensus" => PaperExtractionStatus.AwaitingConsensus,
                    "completed" => PaperExtractionStatus.Completed,
                    _ => (PaperExtractionStatus?)null
                };

                if (statusEnum.HasValue)
                {
                    query = query.Where(t => t.Status == statusEnum.Value);
                }
            }

            // Pagination
            var totalCount = await query.CountAsync();
            var items = await query
                .OrderBy(t => t.Paper.Title)
                .Skip((filter.PageNumber - 1) * filter.PageSize)
                .Take(filter.PageSize)
                .Select(t => new ExtractionDashboardTaskDto
                {
                    TaskId = t.Id,
                    PaperId = t.PaperId,
                    Title = t.Paper.Title,
                    Authors = t.Paper.Authors,
                    PublicationYear = t.Paper.PublicationYearInt,
                    Reviewer1Id = t.Reviewer1Id,
                    Reviewer2Id = t.Reviewer2Id,
                    Status = t.Status.ToString(),
                    Reviewer1Status = t.Reviewer1Status.ToString(),
                    Reviewer2Status = t.Reviewer2Status.ToString(),
                    PdfUrl = t.Paper.PdfUrl
                })
                .ToListAsync();

            return new ExtractionDashboardResponseDto
            {
                Summary = new ExtractionDashboardSummaryDto
                {
                    TotalIncluded = totalIncluded,
                    InProgress = inProgressCount,
                    AwaitingConsensus = awaitingConsensusCount,
                    Completed = completedCount
                },
                Tasks = new PaginatedList<ExtractionDashboardTaskDto>(items, totalCount, filter.PageNumber, filter.PageSize)
            };
        }

        public async Task AssignReviewersAsync(Guid extractionProcessId, Guid paperId, AssignReviewersDto dto)
        {
            if (!dto.Reviewer1Id.HasValue || !dto.Reviewer2Id.HasValue)
            {
                throw new ArgumentException("Both Reviewer1 and Reviewer2 are required.");
            }

            if (dto.Reviewer1Id == dto.Reviewer2Id)
            {
                throw new ArgumentException("Reviewer1 and Reviewer2 cannot be the same user.");
            }

            var task = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .FirstOrDefaultAsync(t => t.DataExtractionProcessId == extractionProcessId && t.PaperId == paperId);

            if (task == null)
            {
                throw new InvalidOperationException($"Extraction task for paper {paperId} in process {extractionProcessId} not found.");
            }

            // Validation: Fetch project info
            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId);

            if (extractionProcess?.ReviewProcess == null)
            {
                throw new InvalidOperationException($"DataExtractionProcess {extractionProcessId} or its ReviewProcess not found.");
            }

            var projectId = extractionProcess.ReviewProcess.ProjectId;

            // Leader Authorization Check
            var currentUserIdStr = _currentUserService.GetUserId();
            if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
            {
                throw new UnauthorizedAccessException("Current user ID is invalid.");
            }

            var currentUserProjectMember = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Where(p => p.Id == projectId)
                .SelectMany(p => p.ProjectMembers)
                .FirstOrDefaultAsync(pm => pm.UserId == currentUserId);

            if (currentUserProjectMember == null || currentUserProjectMember.Role != ProjectRole.Leader)
            {
                throw new UnauthorizedAccessException($"User is not authorized. Must be a Leader for project {projectId}.");
            }

            await ValidateReviewerInProjectAsync(projectId, dto.Reviewer1Id);
            await ValidateReviewerInProjectAsync(projectId, dto.Reviewer2Id);

            task.Reviewer1Id = dto.Reviewer1Id;
            task.Reviewer2Id = dto.Reviewer2Id;
            task.Reviewer1Status = ReviewerTaskStatus.InProgress;
            task.Reviewer2Status = ReviewerTaskStatus.InProgress;
            task.Status = PaperExtractionStatus.InProgress;
            task.ModifiedAt = DateTimeOffset.UtcNow;

            await _unitOfWork.SaveChangesAsync();

            // ── Real-time notification ──────────────────────────────────────
            var paperForNotify = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .Include(t => t.Paper)
                .FirstOrDefaultAsync(t => t.DataExtractionProcessId == extractionProcessId && t.PaperId == paperId);

            var paperTitle = paperForNotify?.Paper?.Title ?? paperId.ToString();

            var assignedIds = new List<Guid>();
            if (dto.Reviewer1Id.HasValue) assignedIds.Add(dto.Reviewer1Id.Value);
            if (dto.Reviewer2Id.HasValue) assignedIds.Add(dto.Reviewer2Id.Value);

            await _notificationService.SendToManyAsync(
                assignedIds,
                title: "Data Extraction Task Assigned",
                message: $"You have been assigned to extract data for paper: \"{paperTitle}\".",
                type: NotificationType.Review,
                relatedEntityId: task.Id,
                entityType: NotificationEntityType.PaperAssignment
            );
        }

        private async Task ValidateReviewerInProjectAsync(Guid projectId, Guid? reviewerId)
        {
            if (!reviewerId.HasValue) return;

            var project = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Include(p => p.ProjectMembers)
                .FirstOrDefaultAsync(p => p.Id == projectId);

            var pm = project?.ProjectMembers.FirstOrDefault(pm => pm.UserId == reviewerId.Value);

            if (pm == null)
                throw new ArgumentException($"User {reviewerId} is not a member of project {projectId}.");

            // Fix #8: Reviewers must be Members, not Leaders
            if (pm.Role != ProjectRole.Member)
                throw new ArgumentException($"User {reviewerId} must be a Member (not a Leader) to be assigned as a reviewer.");
        }

        public async Task<DataExtractionProcessResponse?> GetByIdAsync(Guid extractionProcessId)
        {
            var entity = await _unitOfWork.DataExtractionProcesses.FindSingleAsync(
                dp => dp.Id == extractionProcessId);

            if (entity == null)
                return null;

            return new DataExtractionProcessResponse
            {
                Id = entity.Id,
                ReviewProcessId = entity.ReviewProcessId,
                Status = entity.Status,
                StatusText = entity.Status.ToString(),
                StartedAt = entity.StartedAt,
                CompletedAt = entity.CompletedAt,
                Notes = entity.Notes,
                CreatedAt = entity.CreatedAt,
                ModifiedAt = entity.ModifiedAt
            };
        }

        public async Task<DataExtractionProcessResponse> StartAsync(Guid extractionProcessId)
        {
            var entity = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId);

            if (entity == null)
                throw new InvalidOperationException($"Data extraction process {extractionProcessId} not found.");

            if (entity.Status == ExtractionProcessStatus.Completed)
                throw new InvalidOperationException("Data extraction process already completed.");

            // Fix #4: Leader authorization to start extraction
            var currentUserIdStr = _currentUserService.GetUserId();
            if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
                throw new UnauthorizedAccessException("Current user ID is invalid.");

            var projectId = entity.ReviewProcess.ProjectId;
            var currentUserProjectMember = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Where(p => p.Id == projectId)
                .SelectMany(p => p.ProjectMembers)
                .FirstOrDefaultAsync(pm => pm.UserId == currentUserId);

            if (currentUserProjectMember == null || currentUserProjectMember.Role != ProjectRole.Leader)
                throw new UnauthorizedAccessException($"Only Project Leaders can start the data extraction process.");

            if (entity.Status == ExtractionProcessStatus.NotStarted)
            {
                entity.Status = ExtractionProcessStatus.InProgress;
                entity.StartedAt ??= DateTimeOffset.UtcNow;
                entity.ModifiedAt = DateTimeOffset.UtcNow;

                await _unitOfWork.SaveChangesAsync();

                await SyncEligiblePapersAsync(extractionProcessId);
            }

            return new DataExtractionProcessResponse
            {
                Id = entity.Id,
                ReviewProcessId = entity.ReviewProcessId,
                Status = entity.Status,
                StatusText = entity.Status.ToString(),
                StartedAt = entity.StartedAt,
                CompletedAt = entity.CompletedAt,
                Notes = entity.Notes,
                CreatedAt = entity.CreatedAt,
                ModifiedAt = entity.ModifiedAt
            };
        }

        public async Task SubmitExtractionAsync(Guid extractionProcessId, Guid paperId, SubmitExtractionRequestDto request)
        {
            // Authorization & Validation
            var currentUserIdStr = _currentUserService.GetUserId();
            if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
            {
                throw new UnauthorizedAccessException("Current user ID is invalid.");
            }

            var task = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .FirstOrDefaultAsync(t => t.DataExtractionProcessId == extractionProcessId && t.PaperId == paperId);

            if (task == null)
            {
                throw new InvalidOperationException($"Extraction task for paper {paperId} in process {extractionProcessId} not found.");
            }

            if (currentUserId != task.Reviewer1Id && currentUserId != task.Reviewer2Id)
                throw new UnauthorizedAccessException("User is not authorized to submit extraction for this paper.");

            // Fix #12: Block re-submission on a completed task
            if (task.Status == PaperExtractionStatus.Completed)
                throw new InvalidOperationException("Cannot re-submit extraction for a task that is already completed.");

            // Data Upsert Logic
            var existingExtractedDataValues = await _unitOfWork.ExtractedDataValues.FindAllAsync(e => e.PaperId == paperId && e.ReviewerId == currentUserId);

            if (existingExtractedDataValues != null && existingExtractedDataValues.Any())
            {
                foreach (var val in existingExtractedDataValues)
                {
                    await _unitOfWork.ExtractedDataValues.RemoveAsync(val);
                }
            }

            var newExtractedValues = request.Values.Select(v => new ExtractedDataValue
            {
                Id = Guid.NewGuid(),
                PaperId = paperId,
                FieldId = v.FieldId,
                ReviewerId = currentUserId,
                IsNotReported = v.IsNotReported,
                // When NR is flagged, explicitly clear all value fields
                OptionId = v.IsNotReported ? null : v.OptionId,
                StringValue = v.IsNotReported ? null : v.StringValue,
                NumericValue = v.IsNotReported ? null : v.NumericValue,
                BooleanValue = v.IsNotReported ? null : v.BooleanValue,
                MatrixColumnId = v.MatrixColumnId,
                MatrixRowIndex = v.MatrixRowIndex,
                EvidenceCoordinates = v.IsNotReported ? null : v.EvidenceCoordinates,
                IsConsensusFinal = false,
                CreatedAt = DateTimeOffset.UtcNow,
                ModifiedAt = DateTimeOffset.UtcNow
            }).ToList();

            await _unitOfWork.ExtractedDataValues.AddRangeAsync(newExtractedValues);

            // Status Update Logic
            if (currentUserId == task.Reviewer1Id)
            {
                task.Reviewer1Status = ReviewerTaskStatus.Completed;
            }
            else if (currentUserId == task.Reviewer2Id)
            {
                task.Reviewer2Status = ReviewerTaskStatus.Completed;
            }

            // Define Double Extraction vs Single Extraction logic
            // Based on instructions: task matches Double if there's both Reviewer1 and Reviewer2 assigned
            bool isDoubleExtraction = task.Reviewer1Id.HasValue && task.Reviewer2Id.HasValue;

            if (isDoubleExtraction)
            {
                if (task.Reviewer1Status == ReviewerTaskStatus.Completed && task.Reviewer2Status == ReviewerTaskStatus.Completed)
                {
                    task.Status = PaperExtractionStatus.AwaitingConsensus;
                }
                else
                {
                    task.Status = PaperExtractionStatus.InProgress;
                }
            }
            else
            {
                // Single extraction
                task.Status = PaperExtractionStatus.Completed;
            }

            task.ModifiedAt = DateTimeOffset.UtcNow;

            await _unitOfWork.SaveChangesAsync();
        }

        public async Task<ConsensusWorkspaceDto> GetConsensusWorkspaceAsync(Guid extractionProcessId, Guid paperId)
        {
            // Verify extraction task
            var task = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .FirstOrDefaultAsync(t => t.DataExtractionProcessId == extractionProcessId && t.PaperId == paperId);

            if (task == null)
                throw new InvalidOperationException($"Extraction task for paper {paperId} not found.");

            if (task.Status != PaperExtractionStatus.AwaitingConsensus && task.Status != PaperExtractionStatus.Completed)
                throw new InvalidOperationException($"Task is not in a valid state to view consensus. Current status: {task.Status}");

            // Determine extraction mode
            bool isDirectExtraction = task.AdjudicatorId.HasValue && !task.Reviewer1Id.HasValue && !task.Reviewer2Id.HasValue;
            bool isDoubleBlind = task.Reviewer1Id.HasValue && task.Reviewer2Id.HasValue;

            if (!isDirectExtraction && !isDoubleBlind)
                throw new InvalidOperationException("Cannot view consensus: task is neither a valid double-blind nor a direct extraction.");

            // Get protocol ID
            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId);

            if (extractionProcess?.ReviewProcess == null)
                throw new InvalidOperationException("ReviewProcess not found for this extraction process.");

            // var projectId = extractionProcess.ReviewProcess.ProjectId;

            var templateList = await _unitOfWork.ExtractionTemplates.FindAllAsync(t => t.DataExtractionProcessId == extractionProcessId);
            var templateEntity = templateList.FirstOrDefault();

            if (templateEntity == null)
                throw new InvalidOperationException("Extraction template not found for this protocol.");

            var template = await _unitOfWork.ExtractionTemplates.GetByIdWithFieldsAsync(templateEntity.Id);

            if (template == null)
                throw new InvalidOperationException("Extraction template not found.");

            // Safe reviewer IDs: fall back to Guid.Empty for direct extraction (no-op in MapConsensusField)
            var r1Id = task.Reviewer1Id ?? Guid.Empty;
            var r2Id = task.Reviewer2Id ?? Guid.Empty;

            // Fetch extracted answers — for direct extraction only load final consensus records
            IEnumerable<ExtractedDataValue> extractedValues;
            if (isDirectExtraction)
            {
                extractedValues = await _unitOfWork.ExtractedDataValues.FindAllAsync(e =>
                    e.PaperId == paperId && e.IsConsensusFinal == true);
            }
            else
            {
                extractedValues = await _unitOfWork.ExtractedDataValues.FindAllAsync(e =>
                    e.PaperId == paperId &&
                    (e.ReviewerId == r1Id || e.ReviewerId == r2Id || e.IsConsensusFinal == true));
            }

            var valuesList = extractedValues.ToList();

            var comments = await _unitOfWork.ExtractionComments.GetQueryable()
                .Include(c => c.User)
                .Where(c => c.ExtractionPaperTaskId == task.Id)
                .ToListAsync();

            var r1User = task.Reviewer1Id.HasValue ? await _unitOfWork.Users.FindSingleAsync(u => u.Id.Equals(task.Reviewer1Id.Value)) : null;
            var r2User = task.Reviewer2Id.HasValue ? await _unitOfWork.Users.FindSingleAsync(u => u.Id.Equals(task.Reviewer2Id.Value)) : null;

            var dto = new ConsensusWorkspaceDto
            {
                PaperId = paperId,
                TemplateId = template.Id,
                Reviewer1Id = r1Id,
                Reviewer1Username = r1User?.Username ?? (task.Reviewer1Id.HasValue ? "Unknown" : string.Empty),
                Reviewer2Id = r2Id,
                Reviewer2Username = r2User?.Username ?? (task.Reviewer2Id.HasValue ? "Unknown" : string.Empty),
                Sections = template.Sections.OrderBy(s => s.OrderIndex).Select(s => new ConsensusSectionDto
                {
                    SectionId = s.Id,
                    Name = s.Name,
                    Description = s.Description,
                    SectionType = (int)s.SectionType,
                    OrderIndex = s.OrderIndex,
                    MatrixColumns = s.MatrixColumns?.OrderBy(c => c.OrderIndex).Select(c => new ExtractionMatrixColumnDto
                    {
                        ColumnId = c.Id,
                        Name = c.Name,
                        Description = c.Description,
                        OrderIndex = c.OrderIndex
                    }).ToList() ?? new List<ExtractionMatrixColumnDto>(),
                    Fields = s.Fields.Where(f => f.ParentFieldId == null).OrderBy(f => f.OrderIndex)
                        .Select(f => MapConsensusField(f, valuesList, r1Id, r2Id, comments))
                        .ToList()
                }).ToList()
            };

            return dto;
        }

        private ConsensusFieldDto MapConsensusField(ExtractionField field, List<ExtractedDataValue> allValues, Guid r1Id, Guid r2Id, List<ExtractionComment> allComments)
        {
            var fieldDto = new ConsensusFieldDto
            {
                FieldId = field.Id,
                Name = field.Name,
                Instruction = field.Instruction,
                FieldType = (int)field.FieldType,
                IsRequired = field.IsRequired,
                OrderIndex = field.OrderIndex,
                Options = field.Options?.OrderBy(o => o.DisplayOrder).Select(o => new FieldOptionDto
                {
                    OptionId = o.Id,
                    FieldId = o.FieldId,
                    Value = o.Value,
                    DisplayOrder = o.DisplayOrder
                }).ToList() ?? new List<FieldOptionDto>(),
                SubFields = field.SubFields?.OrderBy(sf => sf.OrderIndex)
                    .Select(sf => MapConsensusField(sf, allValues, r1Id, r2Id, allComments))
                    .ToList() ?? new List<ConsensusFieldDto>()
            };

            // Map answers
            // Filter values for this field
            var fieldValues = allValues.Where(v => v.FieldId == field.Id).ToList();

            // Group by matrix coords (ColumnId, RowIndex). For flat forms these will be null.
            var groupedValues = fieldValues
                .GroupBy(v => new { v.MatrixColumnId, v.MatrixRowIndex })
                .ToList();

            var fieldComments = allComments.Where(c => c.FieldId == field.Id).ToList();
            var commentGroups = fieldComments.GroupBy(c => new { c.MatrixColumnId, c.MatrixRowIndex }).ToList();

            var allKeys = groupedValues.Select(g => new { g.Key.MatrixColumnId, g.Key.MatrixRowIndex })
                .Union(commentGroups.Select(g => new { g.Key.MatrixColumnId, g.Key.MatrixRowIndex }))
                .Distinct()
                .ToList();

            foreach (var key in allKeys)
            {
                var r1Records = fieldValues.Where(v => v.MatrixColumnId == key.MatrixColumnId && v.MatrixRowIndex == key.MatrixRowIndex && v.ReviewerId == r1Id && v.IsConsensusFinal != true).ToList();
                var r2Records = fieldValues.Where(v => v.MatrixColumnId == key.MatrixColumnId && v.MatrixRowIndex == key.MatrixRowIndex && v.ReviewerId == r2Id && v.IsConsensusFinal != true).ToList();
                var finalRecords = fieldValues.Where(v => v.MatrixColumnId == key.MatrixColumnId && v.MatrixRowIndex == key.MatrixRowIndex && v.IsConsensusFinal == true).ToList();

                var cellComments = fieldComments.Where(c => c.MatrixColumnId == key.MatrixColumnId && c.MatrixRowIndex == key.MatrixRowIndex)
                    .Select(c => new ExtractionCommentDto
                    {
                        Id = c.Id,
                        FieldId = c.FieldId,
                        ThreadOwnerId = c.ThreadOwnerId,
                        UserId = c.UserId,
                        UserName = c.User?.Username ?? "Unknown",
                        Content = c.Content,
                        CreatedAt = c.CreatedAt
                    })
                    .OrderBy(c => c.CreatedAt)
                    .ToList();

                var r1Answer = BuildAnswerDetail(r1Records, field) ?? new AnswerDetailDto();
                var r1Comments = cellComments.Where(c => c.ThreadOwnerId == r1Id).ToList();
                if (r1Comments.Any()) r1Answer.Comments = r1Comments;
                if (!r1Records.Any() && !r1Comments.Any()) r1Answer = null;

                var r2Answer = BuildAnswerDetail(r2Records, field) ?? new AnswerDetailDto();
                var r2Comments = cellComments.Where(c => c.ThreadOwnerId == r2Id).ToList();
                if (r2Comments.Any()) r2Answer.Comments = r2Comments;
                if (!r2Records.Any() && !r2Comments.Any()) r2Answer = null;

                var finalAnswer = BuildAnswerDetail(finalRecords, field) ?? new AnswerDetailDto();
                var finalComments = cellComments.Where(c => c.ThreadOwnerId != r1Id && c.ThreadOwnerId != r2Id).ToList();
                if (finalComments.Any()) finalAnswer.Comments = finalComments;
                if (!finalRecords.Any() && !finalComments.Any()) finalAnswer = null;

                fieldDto.Answers.Add(new ExtractedAnswerDto
                {
                    MatrixColumnId = key.MatrixColumnId,
                    MatrixRowIndex = key.MatrixRowIndex,
                    Reviewer1Answer = r1Answer,
                    Reviewer2Answer = r2Answer,
                    FinalAnswer = finalAnswer
                });
            }

            // If no answers yet, maybe add an empty placeholder if needed, but UI can handle empty arrays.

            return fieldDto;
        }

        private AnswerDetailDto? BuildAnswerDetail(List<ExtractedDataValue> records, ExtractionField field)
        {
            if (!records.Any()) return null;

            // Handle MultiSelect which has multiple records
            if (field.FieldType == FieldType.MultiSelect)
            {
                // Check if any record is flagged as Not Reported
                var nrRecord = records.FirstOrDefault(r => r.IsNotReported);
                if (nrRecord != null)
                {
                    return new AnswerDetailDto
                    {
                        IsNotReported = true,
                        DisplayValue = "NR"
                    };
                }

                // StringValue might hold the raw options, but OptionId is cleaner
                var optionIds = records.Where(r => r.OptionId.HasValue).Select(r => r.OptionId!.Value).ToList();
                var displayValues = records.Where(r => r.OptionId.HasValue)
                    .Select(r => field.Options?.FirstOrDefault(o => o.Id == r.OptionId!.Value)?.Value)
                    .Where(v => !string.IsNullOrEmpty(v))
                    .ToList();

                return new AnswerDetailDto
                {
                    // For multiselect, we might combine the string values for UI display
                    DisplayValue = string.Join(", ", displayValues),
                    StringValue = string.Join(",", optionIds), // using StringValue temporarily to hold multi-options
                    EvidenceCoordinates = records.FirstOrDefault(r => !string.IsNullOrEmpty(r.EvidenceCoordinates))?.EvidenceCoordinates
                };
            }

            // Single values
            var record = records.First();

            // Short-circuit: Not Reported flag takes priority over all value fields
            if (record.IsNotReported)
            {
                return new AnswerDetailDto
                {
                    IsNotReported = true,
                    DisplayValue = "NR"
                };
            }

            var dto = new AnswerDetailDto
            {
                OptionId = record.OptionId,
                StringValue = record.StringValue,
                NumericValue = record.NumericValue.HasValue ? decimal.Parse(record.NumericValue.Value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture) : null,
                BooleanValue = record.BooleanValue,
                EvidenceCoordinates = record.EvidenceCoordinates
            };

            // Build DisplayValue based on FieldType
            switch (field.FieldType)
            {
                case FieldType.Text:
                    dto.DisplayValue = record.StringValue;
                    break;
                case FieldType.Integer:
                case FieldType.Decimal:
                    dto.DisplayValue = record.NumericValue?.ToString("G29");
                    break;
                case FieldType.Boolean:
                    dto.DisplayValue = record.BooleanValue.HasValue ? (record.BooleanValue.Value ? "Yes" : "No") : null;
                    break;
                case FieldType.SingleSelect:
                    if (record.OptionId.HasValue)
                    {
                        dto.DisplayValue = field.Options?.FirstOrDefault(o => o.Id == record.OptionId.Value)?.Value;
                    }
                    break;
            }

            return dto;
        }

        public async Task SubmitConsensusAsync(Guid extractionProcessId, Guid paperId, SubmitConsensusRequestDto request)
        {
            // Authorization & Validation
            var currentUserIdStr = _currentUserService.GetUserId();
            if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
            {
                throw new UnauthorizedAccessException("Current user ID is invalid.");
            }

            var task = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .FirstOrDefaultAsync(t => t.DataExtractionProcessId == extractionProcessId && t.PaperId == paperId);

            if (task == null)
                throw new InvalidOperationException($"Extraction task for paper {paperId} not found.");

            if (task.Status != PaperExtractionStatus.AwaitingConsensus && task.Status != PaperExtractionStatus.Completed)
                throw new InvalidOperationException($"Task is not in a valid state for consensus: {task.Status}");

            // Check Project Leader
            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId);

            if (extractionProcess?.ReviewProcess == null)
                throw new InvalidOperationException($"DataExtractionProcess or ReviewProcess not found.");

            var projectId = extractionProcess.ReviewProcess.ProjectId;

            var currentUserProjectMember = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Where(p => p.Id == projectId)
                .SelectMany(p => p.ProjectMembers)
                .FirstOrDefaultAsync(pm => pm.UserId == currentUserId);

            if (currentUserProjectMember == null || currentUserProjectMember.Role != ProjectRole.Leader)
            {
                throw new UnauthorizedAccessException($"User is not authorized. Must be a Leader for project {projectId}.");
            }

            // Remove old consensus values
            var existingConsensus = await _unitOfWork.ExtractedDataValues.FindAllAsync(e =>
                e.PaperId == paperId && e.IsConsensusFinal == true);

            if (existingConsensus != null && existingConsensus.Any())
            {
                foreach (var val in existingConsensus)
                {
                    await _unitOfWork.ExtractedDataValues.RemoveAsync(val);
                }
            }

            // Add new consensus values
            var consensusValues = request.Values.Select(v => new ExtractedDataValue
            {
                Id = Guid.NewGuid(),
                PaperId = paperId,
                FieldId = v.FieldId,
                ReviewerId = currentUserId, // the adjudicator
                IsNotReported = v.IsNotReported,
                // When NR is flagged, explicitly clear all value fields
                OptionId = v.IsNotReported ? null : v.OptionId,
                StringValue = v.IsNotReported ? null : v.StringValue,
                NumericValue = v.IsNotReported ? null : v.NumericValue,
                BooleanValue = v.IsNotReported ? null : v.BooleanValue,
                MatrixColumnId = v.MatrixColumnId,
                MatrixRowIndex = v.MatrixRowIndex,
                EvidenceCoordinates = v.IsNotReported ? null : v.EvidenceCoordinates,
                IsConsensusFinal = true,
                CreatedAt = DateTimeOffset.UtcNow,
                ModifiedAt = DateTimeOffset.UtcNow
            }).ToList();

            await _unitOfWork.ExtractedDataValues.AddRangeAsync(consensusValues);

            // Update Task Status
            task.AdjudicatorId = currentUserId;
            task.Status = PaperExtractionStatus.Completed;
            task.ModifiedAt = DateTimeOffset.UtcNow;

            await _unitOfWork.SaveChangesAsync();
        }

        public async Task<ExtractionPreviewDto> GetPivotedExtractionDataAsync(Guid extractionProcessId)
        {
            // 1. Fetch extraction process and protocol
            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId);

            if (extractionProcess?.ReviewProcess == null)
                throw new InvalidOperationException("ReviewProcess not found for this extraction process.");

            // var projectId = extractionProcess.ReviewProcess.ProjectId;

            // 2. Fetch Template
            var templateList = await _unitOfWork.ExtractionTemplates.FindAllAsync(t => t.DataExtractionProcessId == extractionProcessId);
            var templateEntity = templateList.FirstOrDefault();

            if (templateEntity == null)
                throw new InvalidOperationException("Extraction template not found for this protocol.");

            var template = await _unitOfWork.ExtractionTemplates.GetByIdWithFieldsAsync(templateEntity.Id);

            if (template == null)
                throw new InvalidOperationException("Extraction template not found.");

            // 3. Fetch completed tasks
            var completedTasks = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .Include(t => t.Paper)
                .Where(t => t.DataExtractionProcessId == extractionProcessId && t.Status == PaperExtractionStatus.Completed)
                .ToListAsync();

            var completedPaperIds = completedTasks.Select(t => t.PaperId).ToList();

            if (!completedPaperIds.Any())
                throw new InvalidOperationException("No completed papers found for this extraction process to export.");

            // 4. Fetch extracted answers
            var allValues = await _unitOfWork.ExtractedDataValues.FindAllAsync(e => completedPaperIds.Contains(e.PaperId));
            var valuesList = allValues.ToList();

            // 5. Build headers list and column map
            var columns = new List<ExtractionGridColumnMetaDto>();
            var colMap = new Dictionary<(Guid FieldId, Guid? MatrixColumnId), string>();

            Action<ExtractionField, List<ExtractionField>> flatten = null!;
            flatten = (f, list) =>
            {
                list.Add(f);
                if (f.SubFields != null && f.SubFields.Any())
                {
                    foreach (var sf in f.SubFields.OrderBy(x => x.OrderIndex))
                        flatten(sf, list);
                }
            };

            foreach (var section in template.Sections.OrderBy(s => s.OrderIndex))
            {
                var orderedFields = section.Fields?.Where(f => f.ParentFieldId == null).OrderBy(f => f.OrderIndex).ToList() ?? new List<ExtractionField>();
                var sectionFields = new List<ExtractionField>();
                foreach (var f in orderedFields)
                {
                    flatten(f, sectionFields);
                }

                if (section.SectionType == SectionType.FlatForm)
                {
                    foreach (var f in sectionFields)
                    {
                        string headerName = $"{section.Name} - {f.Name}";
                        var colMeta = new ExtractionGridColumnMetaDto
                        {
                            FieldId = f.Id,
                            HeaderName = headerName,
                            SectionName = section.Name,
                            DisplayFieldName = f.Name,
                            FieldType = f.FieldType.ToString(),
                            Options = f.Options?.OrderBy(o => o.DisplayOrder).Select(o => new GridFieldOptionDto { OptionId = o.Id, Value = o.Value }).ToList() ?? new List<GridFieldOptionDto>()
                        };
                        columns.Add(colMeta);
                        colMap[(f.Id, null)] = headerName;
                    }
                }
                else if (section.SectionType == SectionType.MatrixGrid)
                {
                    var matrixColumns = section.MatrixColumns?.OrderBy(c => c.OrderIndex).ToList() ?? new List<ExtractionMatrixColumn>();
                    foreach (var mc in matrixColumns)
                    {
                        foreach (var f in sectionFields)
                        {
                            string headerName = $"{section.Name} - {mc.Name} - {f.Name}";
                            var colMeta = new ExtractionGridColumnMetaDto
                            {
                                FieldId = f.Id,
                                HeaderName = headerName,
                                SectionName = section.Name,
                                DisplayFieldName = $"{mc.Name} - {f.Name}",
                                FieldType = f.FieldType.ToString(),
                                Options = f.Options?.OrderBy(o => o.DisplayOrder).Select(o => new GridFieldOptionDto { OptionId = o.Id, Value = o.Value }).ToList() ?? new List<GridFieldOptionDto>()
                            };
                            columns.Add(colMeta);
                            colMap[(f.Id, mc.Id)] = headerName;
                        }
                    }
                }
            }

            // 6. Build unified fields dictionary
            var allTemplateFields = template.Sections.SelectMany(s => s.Fields).ToList();
            var allSubFields = allTemplateFields.SelectMany(f => f.SubFields ?? new List<ExtractionField>()).ToList();
            var unifiedFieldsDict = allTemplateFields.Concat(allSubFields).ToDictionary(f => f.Id);

            // 7. Build rows
            var rows = new List<Dictionary<string, string>>();

            foreach (var task in completedTasks)
            {
                var paperValues = valuesList.Where(v => v.PaperId == task.PaperId).ToList();

                // Golden Record filtering
                if (paperValues.Any(v => v.IsConsensusFinal))
                {
                    paperValues = paperValues.Where(v => v.IsConsensusFinal).ToList();
                }
                else
                {
                    paperValues = paperValues.Where(v => v.ReviewerId == task.Reviewer1Id).ToList();
                }

                // Generate Citation string
                string firstAuthor = "Unknown";
                if (!string.IsNullOrWhiteSpace(task.Paper.Authors))
                {
                    var authorParts = task.Paper.Authors.Split(',');
                    if (authorParts.Any() && !string.IsNullOrWhiteSpace(authorParts[0]))
                    {
                        firstAuthor = authorParts[0].Trim();
                    }
                }
                string yearStr = task.Paper.PublicationYearInt?.ToString() ?? "Unknown";
                string citationStr = $"{firstAuthor} et al., {yearStr}";

                var row = new Dictionary<string, string>
                {
                    ["Study ID (System)"] = task.PaperId.ToString(),
                    ["Citation"] = citationStr,
                    ["Title"] = task.Paper.Title ?? "",
                    ["Authors"] = task.Paper.Authors ?? "",
                    ["Year"] = task.Paper.PublicationYearInt?.ToString() ?? ""
                };

                // Group by FieldId and MatrixColumnId, ignoring MatrixRowIndex,
                // so that all data for a single paper collapses into one row.
                var rowGroups = paperValues.GroupBy(v => new { v.FieldId, v.MatrixColumnId }).ToList();

                foreach (var group in rowGroups)
                {
                    if (colMap.TryGetValue((group.Key.FieldId, group.Key.MatrixColumnId), out string? headerName))
                    {
                        if (!unifiedFieldsDict.TryGetValue(group.Key.FieldId, out var field))
                            continue;

                        string valueStr = "";

                        // "Not Reported" takes precedence over all field-type logic
                        if (group.Any(v => v.IsNotReported))
                        {
                            valueStr = "NR";
                        }
                        else if (field.FieldType == FieldType.MultiSelect)
                        {
                            var optionIds = group.Where(v => v.OptionId.HasValue).Select(v => v.OptionId!.Value).ToList();
                            var optionValues = field.Options?
                                .Where(o => optionIds.Contains(o.Id))
                                .Select(o => o.Value)
                                .ToList() ?? new List<string>();

                            valueStr = string.Join(", ", optionValues);
                        }
                        else
                        {
                            var record = group.First();
                            switch (field.FieldType)
                            {
                                case FieldType.Text:
                                    valueStr = record.StringValue ?? "";
                                    break;
                                case FieldType.Integer:
                                case FieldType.Decimal:
                                    valueStr = record.NumericValue?.ToString("G29") ?? "";
                                    break;
                                case FieldType.Boolean:
                                    valueStr = record.BooleanValue.HasValue ? (record.BooleanValue.Value ? "Yes" : "No") : "";
                                    break;
                                case FieldType.SingleSelect:
                                    if (record.OptionId.HasValue)
                                    {
                                        valueStr = field.Options?.FirstOrDefault(o => o.Id == record.OptionId.Value)?.Value ?? "";
                                    }
                                    break;
                            }
                        }

                        row[headerName] = valueStr;
                    }
                }

                rows.Add(row);
            }

            return new ExtractionPreviewDto
            {
                Columns = columns,
                Rows = rows
            };
        }

        public async Task<byte[]> ExportExtractedDataAsync(Guid extractionProcessId)
        {
            var data = await GetPivotedExtractionDataAsync(extractionProcessId);

            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Extracted Data");

            // --- Row 1: Group Headers ---
            // A: Study ID, B: Citation, C: Title, D: Authors, E: Year
            var staticHeaders = new[] { "Study ID (System)", "Citation", "Title", "Authors", "Year" };
            for (int i = 0; i < staticHeaders.Length; i++)
            {
                var cell = worksheet.Cell(1, i + 1);
                cell.Value = staticHeaders[i];
                worksheet.Range(1, i + 1, 2, i + 1).Merge().Style
                    .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center)
                    .Alignment.SetVertical(XLAlignmentVerticalValues.Center)
                    .Font.SetBold(true)
                    .Fill.SetBackgroundColor(XLColor.FromHtml("#EFEFEF"));
            }

            // Dynamic Section Headers
            int currentCol = staticHeaders.Length + 1;
            var sections = data.Columns
                .GroupBy(c => c.SectionName)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .ToList();

            foreach (var section in sections)
            {
                int startCol = currentCol;
                int endCol = currentCol + section.Count - 1;

                var cell = worksheet.Cell(1, startCol);
                cell.Value = section.Name;

                if (startCol != endCol)
                {
                    worksheet.Range(1, startCol, 1, endCol).Merge();
                }

                var range = worksheet.Range(1, startCol, 1, endCol);
                range.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center)
                    .Alignment.SetVertical(XLAlignmentVerticalValues.Center)
                    .Font.SetBold(true)
                    .Fill.SetBackgroundColor(XLColor.FromHtml("#D9EAD3")); // Light green for sections

                currentCol = endCol + 1;
            }

            // --- Row 2: Field Headers ---
            currentCol = staticHeaders.Length + 1;
            foreach (var col in data.Columns)
            {
                var cell = worksheet.Cell(2, currentCol);
                cell.Value = col.DisplayFieldName;
                cell.Style.Font.Bold = true;
                cell.Style.Alignment.SetWrapText(true);
                cell.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#F4F4F4"));
                currentCol++;
            }

            // --- Row 3+: Data ---
            for (int r = 0; r < data.Rows.Count; r++)
            {
                var rowData = data.Rows[r];
                int excelRow = r + 3;

                // Static data
                worksheet.Cell(excelRow, 1).Value = rowData.GetValueOrDefault("Study ID (System)");
                worksheet.Cell(excelRow, 2).Value = rowData.GetValueOrDefault("Citation");
                worksheet.Cell(excelRow, 3).Value = rowData.GetValueOrDefault("Title");
                worksheet.Cell(excelRow, 4).Value = rowData.GetValueOrDefault("Authors");
                worksheet.Cell(excelRow, 5).Value = rowData.GetValueOrDefault("Year");

                // Dynamic data
                for (int c = 0; c < data.Columns.Count; c++)
                {
                    var col = data.Columns[c];
                    if (rowData.TryGetValue(col.HeaderName, out var val))
                    {
                        worksheet.Cell(excelRow, staticHeaders.Length + c + 1).Value = val;
                    }
                }
            }

            // Final Polish
            worksheet.Columns().AdjustToContents();
            // Cap width for very long titles/authors
            foreach (var col in worksheet.Columns(3, 4)) 
            {
                if (col.Width > 50) col.Width = 50;
            }

            // Freeze Panes: 2 rows and 2 columns (Study ID and Citation)
            worksheet.SheetView.FreezeRows(2);
            worksheet.SheetView.FreezeColumns(2);

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }

        public async Task<byte[]> ExportExtractedDataCsvAsync(Guid extractionProcessId, CancellationToken cancellationToken = default)
        {
            var data = await GetPivotedExtractionDataAsync(extractionProcessId);

            var sb = new System.Text.StringBuilder();
            var staticHeaders = new List<string> { "Study ID (System)", "Citation", "Title", "Authors", "Year" };
            var dynamicHeaders = data.Columns.Select(c => c.HeaderName).ToList();
            var allHeaders = staticHeaders.Concat(dynamicHeaders).ToList();

            // Write headers
            var headerLine = string.Join(",", allHeaders.Select(EscapeCsvValue));
            sb.AppendLine(headerLine);

            // Write rows
            foreach (var row in data.Rows)
            {
                var rowValues = allHeaders.Select(h => row.TryGetValue(h, out var val) ? EscapeCsvValue(val) : "");
                sb.AppendLine(string.Join(",", rowValues));
            }

            // Include UTF-8 BOM
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            var csvBytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());

            var result = new byte[bom.Length + csvBytes.Length];
            Buffer.BlockCopy(bom, 0, result, 0, bom.Length);
            Buffer.BlockCopy(csvBytes, 0, result, bom.Length, csvBytes.Length);

            return result;
        }

        private string EscapeCsvValue(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            bool requiresQuotes = value.Contains(",") || value.Contains("\"") || value.Contains("\r") || value.Contains("\n");

            if (requiresQuotes)
            {
                value = value.Replace("\"", "\"\"");
                return $"\"{value}\"";
            }

            return value;
        }

        public async Task ReopenExtractionAsync(Guid extractionProcessId, Guid paperId, ReopenExtractionRequestDto request)
        {
            // 1. Authorization: Ensure the current user is the Project Leader
            var currentUserIdStr = _currentUserService.GetUserId();
            if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
            {
                throw new UnauthorizedAccessException("Current user ID is invalid.");
            }

            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId);

            if (extractionProcess?.ReviewProcess == null)
            {
                throw new InvalidOperationException($"DataExtractionProcess {extractionProcessId} or its ReviewProcess not found.");
            }

            var projectId = extractionProcess.ReviewProcess.ProjectId;

            var currentUserProjectMember = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Where(p => p.Id == projectId)
                .SelectMany(p => p.ProjectMembers)
                .FirstOrDefaultAsync(pm => pm.UserId == currentUserId);

            if (currentUserProjectMember == null || currentUserProjectMember.Role != ProjectRole.Leader)
            {
                throw new UnauthorizedAccessException($"User is not authorized. Must be a Leader for project {projectId}.");
            }

            // 2. Validation: Verify the ExtractionPaperTask exists
            var task = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .FirstOrDefaultAsync(t => t.DataExtractionProcessId == extractionProcessId && t.PaperId == paperId);

            if (task == null)
            {
                throw new InvalidOperationException($"Extraction task for paper {paperId} in process {extractionProcessId} not found.");
            }

            if (task.Status != PaperExtractionStatus.AwaitingConsensus && task.Status != PaperExtractionStatus.Completed && task.Status != PaperExtractionStatus.InProgress)
            {
                throw new InvalidOperationException($"Cannot reopen extraction from status '{task.Status}'.");
            }

            if (request.Target == TargetReviewer.Reviewer1 && task.Reviewer1Status != ReviewerTaskStatus.Completed)
            {
                throw new InvalidOperationException($"Cannot reopen extraction for Reviewer 1. Reviewer 1 is not completed.");
            }

            if (request.Target == TargetReviewer.Reviewer2 && task.Reviewer2Status != ReviewerTaskStatus.Completed)
            {
                throw new InvalidOperationException($"Cannot reopen extraction for Reviewer 2. Reviewer 2 is not completed.");
            }

            if (request.Target == TargetReviewer.Both && task.Reviewer1Status != ReviewerTaskStatus.Completed && task.Reviewer2Status != ReviewerTaskStatus.Completed)
            {
                throw new InvalidOperationException($"Cannot reopen extraction for both Reviewers. Both Reviewers are not completed.");
            }

            // 3. State Reversal
            if (request.Target == TargetReviewer.Direct)
            {
                task.AdjudicatorId = null;
                task.Status = PaperExtractionStatus.NotStarted;
                task.Reviewer1Status = ReviewerTaskStatus.NotStarted;
                task.Reviewer2Status = ReviewerTaskStatus.NotStarted;
            }
            else
            {
                if (request.Target == TargetReviewer.Reviewer1 || request.Target == TargetReviewer.Both)
                {
                    task.Reviewer1Status = ReviewerTaskStatus.InProgress;
                }

                if (request.Target == TargetReviewer.Reviewer2 || request.Target == TargetReviewer.Both)
                {
                    task.Reviewer2Status = ReviewerTaskStatus.InProgress;
                }

                task.Status = PaperExtractionStatus.InProgress;
            }

            // 4. Data Cleanup: Remove consensus data AND the targeted reviewer's data
            var allExtractedValues = await _unitOfWork.ExtractedDataValues
                .FindAllAsync(e => e.PaperId == paperId);

            var valuesToDelete = new List<ExtractedDataValue>();

            // Always delete consensus data
            valuesToDelete.AddRange(allExtractedValues.Where(e => e.IsConsensusFinal == true));

            if (request.Target == TargetReviewer.Direct)
            {
                // Clear the adjudicator's own submitted data
                if (task.AdjudicatorId.HasValue)
                    valuesToDelete.AddRange(allExtractedValues.Where(e => e.ReviewerId == task.AdjudicatorId.Value));
            }
            else
            {
                if (request.Target == TargetReviewer.Reviewer1 || request.Target == TargetReviewer.Both)
                {
                    if (task.Reviewer1Id.HasValue)
                        valuesToDelete.AddRange(allExtractedValues.Where(e =>
                            e.ReviewerId == task.Reviewer1Id.Value && e.IsConsensusFinal == false));
                }

                if (request.Target == TargetReviewer.Reviewer2 || request.Target == TargetReviewer.Both)
                {
                    if (task.Reviewer2Id.HasValue)
                        valuesToDelete.AddRange(allExtractedValues.Where(e =>
                            e.ReviewerId == task.Reviewer2Id.Value && e.IsConsensusFinal == false));
                }
            }

            foreach (var val in valuesToDelete.DistinctBy(v => v.Id))
            {
                await _unitOfWork.ExtractedDataValues.RemoveAsync(val);
            }

            // 5. Save
            task.ModifiedAt = DateTimeOffset.UtcNow;
            await _unitOfWork.SaveChangesAsync();

            // ── Real-time notification ──────────────────────────────────────
            var paperForReopen = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .Include(t => t.Paper)
                .FirstOrDefaultAsync(t => t.DataExtractionProcessId == extractionProcessId && t.PaperId == paperId);

            var paperTitleReopen = paperForReopen?.Paper?.Title ?? paperId.ToString();

            var reopenedIds = new List<Guid>();
            if ((request.Target == TargetReviewer.Reviewer1 || request.Target == TargetReviewer.Both) && task.Reviewer1Id.HasValue)
                reopenedIds.Add(task.Reviewer1Id.Value);
            if ((request.Target == TargetReviewer.Reviewer2 || request.Target == TargetReviewer.Both) && task.Reviewer2Id.HasValue)
                reopenedIds.Add(task.Reviewer2Id.Value);

            if (reopenedIds.Any())
            {
                await _notificationService.SendToManyAsync(
                    reopenedIds,
                    title: "Data Extraction Task Reopened",
                    message: $"Your extraction submission for paper \"{paperTitleReopen}\" has been reopened by the leader. Please re-submit your extraction.",
                    type: NotificationType.Review,
                    relatedEntityId: task.Id,
                    entityType: NotificationEntityType.PaperAssignment
                );
            }
        }

        public async Task<List<ExtractedValueDto>> AutoExtractWithAiAsync(Guid extractionProcessId, Guid paperId)
        {
            // 1. Fetch paper & PDF URL
            var extractionTask = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .Include(t => t.Paper)
                .FirstOrDefaultAsync(t => t.DataExtractionProcessId == extractionProcessId && t.PaperId == paperId);

            if (extractionTask == null)
            {
                throw new InvalidOperationException($"Extraction task for paper {paperId} not found.");
            }

            var pdfUrl = extractionTask.Paper.PdfUrl;
            if (string.IsNullOrWhiteSpace(pdfUrl))
            {
                throw new InvalidOperationException("Paper does not have a valid PdfUrl.");
            }

            // 2. Download PDF
            var httpClient = _httpClientFactory.CreateClient();
            var pdfResponse = await httpClient.GetAsync(pdfUrl);
            if (!pdfResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("Failed to download PDF from url.");
            }
            using var networkStream = await pdfResponse.Content.ReadAsStreamAsync();

            // Copy sang MemoryStream để an toàn tuyệt đối
            using var memoryStream = new MemoryStream();
            await networkStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0; // Đặt con trỏ về đầu file

            // 3. Extract text from Grobid (Dùng memoryStream)
            var paperText = await _unitOfWork.PaperFullTexts
                .GetRawXmlByPaperIdAsync(paperId, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(paperText))
            {
                throw new InvalidOperationException("Failed to extract full text from the PDF using Grobid. Check Backend Console Logs for details.");
            }

            var doc = XDocument.Parse(paperText);

            var sb = new System.Text.StringBuilder();

            // 1. Extract Title
            var title = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "title")?.Value;
            if (!string.IsNullOrWhiteSpace(title)) sb.AppendLine($"TITLE: {title}\n");

            // 2. Extract Authors
            var teiHeaderNode = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "teiHeader");

            var authorNodes = teiHeaderNode != null
                ? teiHeaderNode.Descendants().Where(x => x.Name.LocalName == "author")
                : Enumerable.Empty<XElement>();

            var authorsList = new List<string>();
            foreach (var author in authorNodes)
            {
                var names = author.Descendants()
                                .Where(x => x.Name.LocalName == "forename" || x.Name.LocalName == "surname")
                                .Select(x => x.Value);

                if (names.Any())
                {
                    authorsList.Add(string.Join(" ", names));
                }
            }

            if (authorsList.Any())
            {
                sb.AppendLine($"AUTHORS: {string.Join(", ", authorsList)}\n");
            }

            // 3. Extract Publication Date/Year
            var dateNode = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "date" && x.Attribute("type")?.Value == "published");
            var year = dateNode?.Attribute("when")?.Value ?? dateNode?.Value;
            if (!string.IsNullOrWhiteSpace(year)) sb.AppendLine($"PUBLICATION DATE: {year}\n");

            // 4. Extract Abstract
            var abstractText = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "abstract")?.Value;
            if (!string.IsNullOrWhiteSpace(abstractText)) sb.AppendLine($"ABSTRACT:\n{abstractText}\n");

            // 5. Extract Main Body Text (Only from inside the <text> node)
            sb.AppendLine("MAIN TEXT:");
            var textBodyNodes = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "text")?.Descendants()
                                    .Where(x => x.Name.LocalName == "p" || x.Name.LocalName == "head")
                                    .Select(x => x.Value);

            if (textBodyNodes != null && textBodyNodes.Any())
            {
                sb.AppendLine(string.Join("\n\n", textBodyNodes));
            }

            var cleanPaperText = sb.ToString();

            // 4. Fetch Schema
            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId);

            if (extractionProcess?.ReviewProcess == null)
                throw new InvalidOperationException("ReviewProcess not found.");

            var templateList = await _unitOfWork.ExtractionTemplates.FindAllAsync(t => t.DataExtractionProcessId == extractionProcessId);
            var templateEntity = templateList.FirstOrDefault();

            if (templateEntity == null)
                throw new InvalidOperationException("Extraction template not found.");

            var template = await _unitOfWork.ExtractionTemplates.GetByIdWithFieldsAsync(templateEntity.Id);
            if (template == null)
                throw new InvalidOperationException("Extraction template details not found.");

            // Flatten and build schema for LLM
            var schemaObj = new
            {
                TemplateName = template.Name,
                Sections = template.Sections.OrderBy(s => s.OrderIndex).Select(s => new
                {
                    SectionName = s.Name,
                    Description = s.Description,
                    SectionType = s.SectionType.ToString(),
                    MatrixColumns = s.MatrixColumns?.OrderBy(c => c.OrderIndex).Select(c => new { c.Id, c.Name }).ToList(),
                    Fields = GetSimplifiedFields(s.Fields.Where(f => f.ParentFieldId == null).OrderBy(f => f.OrderIndex).ToList())
                })
            };

            var schemaJson = JsonSerializer.Serialize(schemaObj, new JsonSerializerOptions { WriteIndented = true });

            // 5. Call OpenRouter
            string prompt = $@"
You are an expert academic researcher specializing in Systematic Literature Reviews.
I will provide you with a PAPER TEXT and an extraction SCHEMA.
Your task is to extract the correct answers from the PAPER TEXT according to the SCHEMA.

### EXTRACTION RULES
1. For SingleSelect or MultiSelect fields, you MUST map your answer to the exact 'OptionId' (GUID) provided in the schema.
2. For MultiSelect fields, if multiple options are selected, you MUST return a separate object for EACH selected option (each with the same FieldId but different OptionId).
3. For Text, Integer, or Decimal fields, provide the value in 'StringValue', 'NumericValue' as appropriate.
4. If a field is not found in the text, you can omit it or set its values to null.
5. For Matrix Grid sections, you MUST provide both 'FieldId', 'MatrixColumnId' (GUID), and 'MatrixRowIndex' (0-indexed).

### OUTPUT ENFORCEMENT
- Return ONLY a JSON object with a property named ""ExtractedData"" which is an array of objects.
- Each object in the array must follow the structure:
  {{
    ""FieldId"": ""GUID"",
    ""OptionId"": ""GUID or null"",
    ""StringValue"": ""string or null"",
    ""NumericValue"": number or null,
    ""BooleanValue"": boolean or null,
    ""MatrixColumnId"": ""GUID or null"",
    ""MatrixRowIndex"": number or null
  }}
- **FLEXIBLE EXTRACTION**: If a specific numeric/option value is not reported but discussed qualitatively (e.g., ""high"", ""low"", ""not measured""), put that description in 'StringValue'.
- **FALLBACK**: If a study discusses a topic but doesn't give a specific data point, summarize the study's stance in 1 sentence in 'StringValue'.
- Do NOT include any explanations or markdown.

### SCHEMA
{schemaJson}

### PAPER TEXT
{cleanPaperText}
";

            var response = await _openRouterService.GenerateStructuredContentAsync<AutoExtractionAiResponseDto>(prompt);
            return response?.ExtractedData ?? new List<ExtractedValueDto>();
        }

        public async Task<ExtractedValueDto?> AskAiSingleFieldAsync(Guid extractionProcessId, AskAiFieldRequestDto request, CancellationToken cancellationToken = default)
        {
            var extractionTask = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .Include(t => t.Paper)
                .FirstOrDefaultAsync(t => t.DataExtractionProcessId == extractionProcessId && t.PaperId == request.PaperId, cancellationToken);

            if (extractionTask == null)
                throw new InvalidOperationException($"Extraction task for paper {request.PaperId} not found.");

            // string searchQuestion = $"{request.FieldName}. {request.FieldInstruction}".Trim();

            string searchQuestion = request.FieldName;
            if (!string.IsNullOrWhiteSpace(request.FieldInstruction))
            {
                searchQuestion += $". {request.FieldInstruction}";
            }
            searchQuestion += ". Identify related methods, techniques, approaches, or strategies.";

            var relevantChunks = await _ragRetrievalService.GetRelevantChunksAsync(request.PaperId, searchQuestion, topK: 12, cancellationToken);
            if (relevantChunks == null || !relevantChunks.Any())
                return null;

            var coordinatesList = new List<object>();
            var textContextBuilder = new System.Text.StringBuilder();

            foreach (var chunk in relevantChunks)
            {
                textContextBuilder.AppendLine(chunk.TextContent);
                textContextBuilder.AppendLine("---");

                if (!string.IsNullOrWhiteSpace(chunk.CoordinatesJson))
                {
                    try
                    {
                        var coords = JsonSerializer.Deserialize<List<object>>(chunk.CoordinatesJson);
                        if (coords != null)
                        {
                            coordinatesList.AddRange(coords);
                        }
                    }
                    catch { /* ignore invalid json */ }
                }
            }

            string combinedCoordinates = JsonSerializer.Serialize(coordinatesList);
            string combinedContext = textContextBuilder.ToString();

            // 5. Call OpenRouter
            string optionsInstruction = string.IsNullOrWhiteSpace(request.OptionsJson)
                ? ""
                : $"\nValid Options (JSON format: [OptionId, Value]): {request.OptionsJson}\nYou MUST match your extracted answer to ONE of the OptionIds provided above if applicable.\n";

            string prompt = $@"
You are an expert academic researcher. 
Your task is to extract a SINGLE specific field from the provided PAPER CONTEXT.

### FIELD DETAILS
- Field Name: {request.FieldName}
- Field Type: {request.FieldType}
- Instructions: {request.FieldInstruction}
{optionsInstruction}

### OUTPUT ENFORCEMENT
- Return ONLY a JSON object matching the structure:
  {{
    ""FieldId"": ""{request.FieldId}"",
    ""OptionId"": ""GUID or null"",
    ""StringValue"": ""string or null"",
    ""NumericValue"": number or null,
    ""BooleanValue"": boolean or null
  }}
- **FLEXIBLE EXTRACTION**: If the study does not explicitly provide a numeric/boolean value but describes the result (e.g., ""highly satisfied"", ""improved significantly""), put that qualitative description in 'StringValue'. 
- **FALLBACK**: If no direct value is found, provide a 1-sentence summary of what the study says about this field in 'StringValue' instead of leaving everything null.
- Do NOT include any explanations or markdown outside the JSON object.

### PAPER CONTEXT
{combinedContext}
";

            var extractedValue = await _openRouterService.GenerateStructuredContentAsync<ExtractedValueDto>(prompt, ct: cancellationToken);

            if (extractedValue != null)
            {
                extractedValue.FieldId = request.FieldId;
                extractedValue.EvidenceCoordinates = combinedCoordinates;
                extractedValue.MatrixColumnId = request.MatrixColumnId;
                extractedValue.MatrixRowIndex = request.MatrixRowIndex;
            }

            return extractedValue;
        }

        public async Task DirectExtractByLeaderAsync(Guid extractionProcessId, Guid paperId, SubmitExtractionRequestDto payload, CancellationToken cancellationToken)
        {
            // 1. Resolve & validate current user
            var currentUserIdStr = _currentUserService.GetUserId();
            if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
                throw new UnauthorizedAccessException("Current user ID is invalid.");

            // 2. Load the extraction process to get the project context
            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId, cancellationToken);

            if (extractionProcess?.ReviewProcess == null)
                throw new InvalidOperationException($"DataExtractionProcess {extractionProcessId} or its ReviewProcess not found.");

            var projectId = extractionProcess.ReviewProcess.ProjectId;

            // 3. Authorization: current user must be a Project Leader
            var currentUserProjectMember = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Where(p => p.Id == projectId)
                .SelectMany(p => p.ProjectMembers)
                .FirstOrDefaultAsync(pm => pm.UserId == currentUserId, cancellationToken);

            if (currentUserProjectMember == null || currentUserProjectMember.Role != ProjectRole.Leader)
                throw new UnauthorizedAccessException($"User is not authorized. Must be a Leader for project {projectId}.");

            // 4. Find the ExtractionPaperTask for this paper
            var task = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .FirstOrDefaultAsync(t => t.DataExtractionProcessId == extractionProcessId && t.PaperId == paperId, cancellationToken);

            if (task == null)
                throw new InvalidOperationException($"Extraction task for paper {paperId} in process {extractionProcessId} not found.");

            if (task.Status == PaperExtractionStatus.Completed)
                throw new InvalidOperationException($"Extraction task for paper {paperId} is already Completed. Use Reopen to make changes.");

            // 5. Replace-semantics: remove any existing consensus values for this paper
            var existingConsensusFinalValues = await _unitOfWork.ExtractedDataValues.FindAllAsync(
                e => e.PaperId == paperId && e.IsConsensusFinal == true);

            if (existingConsensusFinalValues != null && existingConsensusFinalValues.Any())
            {
                foreach (var val in existingConsensusFinalValues)
                    await _unitOfWork.ExtractedDataValues.RemoveAsync(val);
            }

            // 6. Persist the payload directly as final consensus records (IsConsensusFinal = true)
            var directValues = payload.Values.Select(v => new ExtractedDataValue
            {
                Id = Guid.NewGuid(),
                PaperId = paperId,
                FieldId = v.FieldId,
                ReviewerId = currentUserId,   // the adjudicating leader
                IsNotReported = v.IsNotReported,
                // When NR is flagged, explicitly clear all value fields
                OptionId = v.IsNotReported ? null : v.OptionId,
                StringValue = v.IsNotReported ? null : v.StringValue,
                NumericValue = v.IsNotReported ? null : v.NumericValue,
                BooleanValue = v.IsNotReported ? null : v.BooleanValue,
                MatrixColumnId = v.MatrixColumnId,
                MatrixRowIndex = v.MatrixRowIndex,
                EvidenceCoordinates = v.IsNotReported ? null : v.EvidenceCoordinates,
                IsConsensusFinal = true,       // CRITICAL: marks as final, bypassing reviewers
                CreatedAt = DateTimeOffset.UtcNow,
                ModifiedAt = DateTimeOffset.UtcNow
            }).ToList();

            await _unitOfWork.ExtractedDataValues.AddRangeAsync(directValues);

            // 7. Force task to Completed, bypassing InProgress / AwaitingConsensus
            task.AdjudicatorId = currentUserId;
            task.Reviewer1Status = ReviewerTaskStatus.Completed;
            task.Reviewer2Status = ReviewerTaskStatus.Completed;
            task.Status = PaperExtractionStatus.Completed;
            task.ModifiedAt = DateTimeOffset.UtcNow;

            await _unitOfWork.SaveChangesAsync();
        }

        private object GetSimplifiedFields(IEnumerable<ExtractionField> fields)
        {
            var result = new List<object>();
            foreach (var f in fields)
            {
                result.Add(new
                {
                    f.Id,
                    f.Name,
                    f.Instruction,
                    FieldType = f.FieldType.ToString(),
                    Options = f.Options?.OrderBy(o => o.DisplayOrder).Select(o => new { o.Id, o.Value }).ToList(),
                    SubFields = f.SubFields != null && f.SubFields.Any() ? GetSimplifiedFields(f.SubFields.OrderBy(sf => sf.OrderIndex)) : null
                });
            }
            return result;
        }

        public async Task<ExtractionWorkloadSummaryDto> GetWorkloadSummaryAsync(Guid extractionProcessId, CancellationToken cancellationToken)
        {
            // 1. Resolve current user
            var currentUserIdStr = _currentUserService.GetUserId();
            if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
                throw new UnauthorizedAccessException("Current user ID is invalid.");

            // 2. Resolve the project tied to this extraction process
            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId, cancellationToken);

            if (extractionProcess?.ReviewProcess == null)
                throw new InvalidOperationException($"DataExtractionProcess {extractionProcessId} or its ReviewProcess not found.");

            var projectId = extractionProcess.ReviewProcess.ProjectId;

            // 3. Validate membership and determine role
            var allMembers = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Where(p => p.Id == projectId)
                .SelectMany(p => p.ProjectMembers)
                .Include(pm => pm.User)
                .ToListAsync(cancellationToken);

            var currentMember = allMembers.FirstOrDefault(pm => pm.UserId == currentUserId);
            if (currentMember == null)
                throw new UnauthorizedAccessException($"User is not a member of project {projectId}.");

            var isLeader = currentMember.Role == ProjectRole.Leader;

            // 4. Build userId → display name lookup from project members
            var userNameLookup = allMembers.ToDictionary(
                pm => pm.UserId,
                pm => string.IsNullOrWhiteSpace(pm.User?.FullName) ? pm.User?.Username ?? pm.UserId.ToString() : pm.User.FullName);

            // 5. Fetch all ExtractionPaperTask records for this process
            var allTasks = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .Where(t => t.DataExtractionProcessId == extractionProcessId)
                .ToListAsync(cancellationToken);

            // 6. Compute global stats
            int totalPapers = allTasks.Count;
            int fullyCompleted = allTasks.Count(t => t.Status == PaperExtractionStatus.Completed);
            double overallPct = totalPapers == 0 ? 0 : Math.Round((double)fullyCompleted / totalPapers * 100, 2);

            // 7. Per-reviewer stats using efficient LINQ grouping
            // A reviewer contributes to a task if they are Reviewer1 OR Reviewer2.
            // We emit two "participation records" per task when both slots are filled.
            var participations = allTasks
                .SelectMany(t =>
                {
                    var slots = new List<(Guid ReviewerId, ReviewerTaskStatus Status)>();
                    if (t.Reviewer1Id.HasValue)
                        slots.Add((t.Reviewer1Id.Value, t.Reviewer1Status));
                    if (t.Reviewer2Id.HasValue)
                        slots.Add((t.Reviewer2Id.Value, t.Reviewer2Status));
                    return slots;
                })
                .GroupBy(x => x.ReviewerId)
                .Select(g => new ReviewerWorkloadDto
                {
                    ReviewerId = g.Key,
                    ReviewerName = userNameLookup.TryGetValue(g.Key, out var name) ? name : g.Key.ToString(),
                    TotalAssigned = g.Count(),
                    Completed = g.Count(x => x.Status == ReviewerTaskStatus.Completed),
                    InProgress = g.Count(x => x.Status == ReviewerTaskStatus.InProgress),
                    NotStarted = g.Count(x => x.Status == ReviewerTaskStatus.NotStarted)
                })
                .ToList();

            // 8. Role-based filtering: Members see only their own workload
            List<ReviewerWorkloadDto> reviewerWorkloads;
            if (isLeader)
            {
                reviewerWorkloads = participations;
            }
            else
            {
                var myWorkload = participations.FirstOrDefault(r => r.ReviewerId == currentUserId);
                reviewerWorkloads = myWorkload is not null ? new List<ReviewerWorkloadDto> { myWorkload } : new List<ReviewerWorkloadDto>();
            }

            return new ExtractionWorkloadSummaryDto
            {
                TotalPapers = totalPapers,
                FullyCompletedPapers = fullyCompleted,
                OverallProgressPercentage = overallPct,
                ReviewerWorkloads = reviewerWorkloads
            };
        }
        public async Task CompleteAsync(Guid extractionProcessId, CancellationToken cancellationToken)
        {
            // 1. Fetch Process
            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId, cancellationToken);

            if (extractionProcess?.ReviewProcess == null)
            {
                throw new InvalidOperationException($"DataExtractionProcess {extractionProcessId} or its ReviewProcess not found.");
            }

            var projectId = extractionProcess.ReviewProcess.ProjectId;

            // 2. Authorization
            var currentUserIdStr = _currentUserService.GetUserId();
            if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
            {
                throw new UnauthorizedAccessException("Current user ID is invalid.");
            }

            var currentUserProjectMember = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Where(p => p.Id == projectId)
                .SelectMany(p => p.ProjectMembers)
                .FirstOrDefaultAsync(pm => pm.UserId == currentUserId, cancellationToken);

            if (currentUserProjectMember == null || currentUserProjectMember.Role != ProjectRole.Leader)
            {
                throw new UnauthorizedAccessException($"User is not authorized. Must be a Leader for project {projectId}.");
            }

            // 3. Validation
            var tasks = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .Where(t => t.DataExtractionProcessId == extractionProcessId)
                .ToListAsync(cancellationToken);

            // if (tasks.Any() && tasks.Any(t => t.Status != PaperExtractionStatus.Completed))
            // {
            //     throw new InvalidOperationException("Cannot complete phase. All papers must be completely extracted and resolved.");
            // }

            // 4. Update Status
            extractionProcess.Status = ExtractionProcessStatus.Completed;
            extractionProcess.CompletedAt = DateTimeOffset.UtcNow;
            extractionProcess.ModifiedAt = DateTimeOffset.UtcNow;

            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        public async Task<ExtractionEditableGridDto> GetEditableExtractionGridAsync(Guid extractionProcessId)
        {
            // 1. Fetch extraction process and protocol
            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId);

            if (extractionProcess?.ReviewProcess == null)
                throw new InvalidOperationException("ReviewProcess not found for this extraction process.");

            // var projectId = extractionProcess.ReviewProcess.ProjectId;

            // 2. Fetch Template
            var templateList = await _unitOfWork.ExtractionTemplates.FindAllAsync(t => t.DataExtractionProcessId == extractionProcessId);
            var templateEntity = templateList.FirstOrDefault();

            if (templateEntity == null)
                throw new InvalidOperationException("Extraction template not found for this protocol.");

            var template = await _unitOfWork.ExtractionTemplates.GetByIdWithFieldsAsync(templateEntity.Id);

            if (template == null)
                throw new InvalidOperationException("Extraction template not found.");

            // 3. Fetch completed tasks
            var completedTasks = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .Include(t => t.Paper)
                .Where(t => t.DataExtractionProcessId == extractionProcessId && t.Status == PaperExtractionStatus.Completed)
                .ToListAsync();

            var completedPaperIds = completedTasks.Select(t => t.PaperId).ToList();

            if (!completedPaperIds.Any())
                throw new InvalidOperationException("No completed papers found for this extraction process to export.");

            // 4. Fetch extracted answers (only IsConsensusFinal)
            var allValues = await _unitOfWork.ExtractedDataValues.FindAllAsync(e => completedPaperIds.Contains(e.PaperId) && e.IsConsensusFinal);
            var valuesList = allValues.ToList();

            // 5. Build columns list and column map
            var columns = new List<ExtractionGridColumnMetaDto>();
            var colMap = new Dictionary<(Guid FieldId, Guid? MatrixColumnId), string>();

            Action<ExtractionField, List<ExtractionField>> flatten = null!;
            flatten = (f, list) =>
            {
                list.Add(f);
                if (f.SubFields != null && f.SubFields.Any())
                {
                    foreach (var sf in f.SubFields.OrderBy(x => x.OrderIndex))
                        flatten(sf, list);
                }
            };

            foreach (var section in template.Sections.OrderBy(s => s.OrderIndex))
            {
                var orderedFields = section.Fields?.Where(f => f.ParentFieldId == null).OrderBy(f => f.OrderIndex).ToList() ?? new List<ExtractionField>();
                var sectionFields = new List<ExtractionField>();
                foreach (var f in orderedFields)
                {
                    flatten(f, sectionFields);
                }

                if (section.SectionType == SectionType.FlatForm)
                {
                    foreach (var f in sectionFields)
                    {
                        string headerName = $"{section.Name} - {f.Name}";
                        var colMeta = new ExtractionGridColumnMetaDto
                        {
                            FieldId = f.Id,
                            HeaderName = headerName,
                            SectionName = section.Name,
                            DisplayFieldName = f.Name,
                            FieldType = f.FieldType.ToString(),
                            Options = f.Options?.OrderBy(o => o.DisplayOrder).Select(o => new GridFieldOptionDto { OptionId = o.Id, Value = o.Value }).ToList() ?? new List<GridFieldOptionDto>()
                        };
                        columns.Add(colMeta);
                        colMap[(f.Id, null)] = headerName;
                    }
                }
                else if (section.SectionType == SectionType.MatrixGrid)
                {
                    var matrixColumns = section.MatrixColumns?.OrderBy(c => c.OrderIndex).ToList() ?? new List<ExtractionMatrixColumn>();
                    foreach (var mc in matrixColumns)
                    {
                        foreach (var f in sectionFields)
                        {
                            string headerName = $"{section.Name} - {mc.Name} - {f.Name}";
                            var colMeta = new ExtractionGridColumnMetaDto
                            {
                                FieldId = f.Id,
                                HeaderName = headerName,
                                SectionName = section.Name,
                                DisplayFieldName = $"{mc.Name} - {f.Name}",
                                FieldType = f.FieldType.ToString(),
                                Options = f.Options?.OrderBy(o => o.DisplayOrder).Select(o => new GridFieldOptionDto { OptionId = o.Id, Value = o.Value }).ToList() ?? new List<GridFieldOptionDto>()
                            };
                            columns.Add(colMeta);
                            colMap[(f.Id, mc.Id)] = headerName;
                        }
                    }
                }
            }

            // 6. Build unified fields dictionary
            var allTemplateFields = template.Sections.SelectMany(s => s.Fields).ToList();
            var allSubFields = allTemplateFields.SelectMany(f => f.SubFields ?? new List<ExtractionField>()).ToList();
            var unifiedFieldsDict = allTemplateFields.Concat(allSubFields).DistinctBy(f => f.Id).ToDictionary(f => f.Id);

            // 7. Build rows — strictly 1 row per paper.
            // For matrix fields, all rows across MatrixRowIndex values are collapsed into
            // a single newline-joined string inside the cell. This lets the grid remain flat
            // while preserving full EAV round-trip fidelity via UpdateGridCellAsync.
            var rows = new List<ExtractionGridRowDto>();

            foreach (var task in completedTasks)
            {
                var paperValues = valuesList.Where(v => v.PaperId == task.PaperId).ToList();

                string firstAuthor = "Unknown";
                if (!string.IsNullOrWhiteSpace(task.Paper.Authors))
                {
                    var authorParts = task.Paper.Authors.Split(',');
                    if (authorParts.Any() && !string.IsNullOrWhiteSpace(authorParts[0]))
                        firstAuthor = authorParts[0].Trim();
                }
                string yearStr = task.Paper.PublicationYearInt?.ToString() ?? "Unknown";
                string citationStr = $"{firstAuthor} et al., {yearStr}";

                var rowDto = new ExtractionGridRowDto
                {
                    RowId = task.PaperId.ToString(),
                    PaperTitle = task.Paper.Title ?? "",
                    Citation = citationStr,
                    Cells = new Dictionary<string, ExtractionGridCellDto>()
                };

                foreach (var headerKvp in colMap)
                {
                    var fieldId = headerKvp.Key.FieldId;
                    var matrixColumnId = headerKvp.Key.MatrixColumnId;
                    var headerName = headerKvp.Value;

                    if (!unifiedFieldsDict.TryGetValue(fieldId, out var field))
                        continue;

                    // For a matrix cell, collect ALL records for this (FieldId, MatrixColumnId)
                    // across every MatrixRowIndex — they will be joined with newlines.
                    // For flat-form cells, MatrixColumnId is null so this also works correctly.
                    var cellValues = paperValues
                        .Where(v => v.FieldId == fieldId && v.MatrixColumnId == matrixColumnId)
                        .OrderBy(v => v.MatrixRowIndex ?? 0)
                        .ToList();

                    string valueStr = "";
                    bool isNr = false;

                    if (cellValues.Any())
                    {
                        // "Not Reported" takes precedence over all field-type logic
                        if (cellValues.Any(v => v.IsNotReported))
                        {
                            valueStr = "NR";
                            isNr = true;
                        }
                        else if (field.FieldType == FieldType.MultiSelect)
                        {
                            // MultiSelect: combine all selected options across all rows into one list
                            var optionIds = cellValues.Where(v => v.OptionId.HasValue).Select(v => v.OptionId!.Value).ToList();
                            var optionValues = field.Options?
                                .Where(o => optionIds.Contains(o.Id))
                                .Select(o => o.Value)
                                .ToList() ?? new List<string>();

                            valueStr = string.Join(", ", optionValues);
                        }
                        else if (matrixColumnId.HasValue)
                        {
                            // Matrix non-MultiSelect: one readable token per matrix row, joined by newlines
                            var rowTokens = new List<string>();
                            foreach (var record in cellValues)
                            {
                                string token = field.FieldType switch
                                {
                                    FieldType.Text => record.StringValue ?? "",
                                    FieldType.Integer or FieldType.Decimal => record.NumericValue?.ToString("G29") ?? "",
                                    FieldType.Boolean => record.BooleanValue.HasValue
                                        ? (record.BooleanValue.Value ? "Yes" : "No")
                                        : "",
                                    FieldType.SingleSelect => record.OptionId.HasValue
                                        ? field.Options?.FirstOrDefault(o => o.Id == record.OptionId.Value)?.Value ?? ""
                                        : "",
                                    _ => ""
                                };
                                if (!string.IsNullOrEmpty(token))
                                    rowTokens.Add(token);
                            }
                            valueStr = string.Join("\n", rowTokens);
                        }
                        else
                        {
                            // Flat-form field (no MatrixColumnId): single record
                            var record = cellValues.First();
                            valueStr = field.FieldType switch
                            {
                                FieldType.Text => record.StringValue ?? "",
                                FieldType.Integer or FieldType.Decimal => record.NumericValue?.ToString("G29") ?? "",
                                FieldType.Boolean => record.BooleanValue.HasValue
                                    ? (record.BooleanValue.Value ? "Yes" : "No")
                                    : "",
                                FieldType.SingleSelect => record.OptionId.HasValue
                                    ? field.Options?.FirstOrDefault(o => o.Id == record.OptionId.Value)?.Value ?? ""
                                    : "",
                                _ => ""
                            };
                        }
                    }

                    rowDto.Cells[headerName] = new ExtractionGridCellDto
                    {
                        PaperId = task.PaperId,
                        FieldId = fieldId,
                        MatrixColumnId = matrixColumnId,
                        MatrixRowIndex = null, // flattened — no per-row index exposed to the frontend
                        Value = valueStr,
                        IsNotReported = isNr,
                        FieldType = field.FieldType.ToString()
                    };
                }

                rows.Add(rowDto);
            }

            return new ExtractionEditableGridDto
            {
                Columns = columns,
                Rows = rows
            };
        }

        public async Task UpdateGridCellAsync(Guid extractionProcessId, UpdateGridCellRequestDto request)
        {
            var currentUserIdStr = _currentUserService.GetUserId();
            if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
            {
                throw new UnauthorizedAccessException("Current user ID is invalid.");
            }

            var extractionProcess = await _unitOfWork.DataExtractionProcesses.FindFirstOrDefaultAsync(dp => dp.Id == extractionProcessId);

            if (extractionProcess == null)
            {
                throw new InvalidOperationException($"DataExtractionProcess {extractionProcessId} not found.");
            }

            var reviewProcess = await _unitOfWork.ReviewProcesses.FindFirstOrDefaultAsync(rp => rp.Id == extractionProcess.ReviewProcessId);

            if (reviewProcess == null)
            {
                throw new InvalidOperationException($"ReviewProcess {extractionProcess.ReviewProcessId} not found.");
            }

            var projectId = reviewProcess.ProjectId;

            var allMembers = (await _unitOfWork.SystematicReviewProjects.FindAllAsync(p => p.Id == projectId)).FirstOrDefault()?.ProjectMembers ?? new List<ProjectMember>();

            if (!allMembers.Any())
            {
                // We might need to load members. Let's just do a direct check using SystematicReviewProjects if possible,
                // or just rely on a query we know works. Let's use the explicit check:
            }

            // To be entirely safe and mimic other methods' auth checks:
            var project = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Include(p => p.ProjectMembers)
                .FirstOrDefaultAsync(p => p.Id == projectId);

            var currentUserProjectMember = project?.ProjectMembers.FirstOrDefault(pm => pm.UserId == currentUserId);

            if (currentUserProjectMember == null || currentUserProjectMember.Role != ProjectRole.Leader)
            {
                throw new UnauthorizedAccessException($"User is not authorized. Must be a Leader for project {projectId}.");
            }

            var templateList = await _unitOfWork.ExtractionTemplates.FindAllAsync(t => t.DataExtractionProcessId == extractionProcessId);
            var templateEntity = templateList.FirstOrDefault();

            if (templateEntity == null)
                throw new InvalidOperationException("Extraction template not found for this protocol.");

            var template = await _unitOfWork.ExtractionTemplates.GetByIdWithFieldsAsync(templateEntity.Id);

            if (template == null)
                throw new InvalidOperationException("Extraction template not found.");

            var allTemplateFields = template.Sections.SelectMany(s => s.Fields).ToList();
            var allSubFields = allTemplateFields.SelectMany(f => f.SubFields ?? new List<ExtractionField>()).ToList();
            var fieldEntity = allTemplateFields.Concat(allSubFields).FirstOrDefault(f => f.Id == request.FieldId);

            if (fieldEntity == null)
                throw new InvalidOperationException($"Field {request.FieldId} not found.");

            // Clear ALL existing consensus records for this (PaperId, FieldId, MatrixColumnId).
            // The MatrixRowIndex condition is intentionally omitted: because the frontend sends a
            // single flattened newline-joined value representing all matrix rows, we must replace
            // all rows at once rather than one row at a time.
            var existingRecords = (await _unitOfWork.ExtractedDataValues.FindAllAsync(e =>
                    e.PaperId == request.PaperId &&
                    e.FieldId == request.FieldId &&
                    e.MatrixColumnId == request.MatrixColumnId &&
                    e.IsConsensusFinal == true)).ToList();

            string oldValue = "";
            if (existingRecords.Any())
            {
                if (existingRecords.Any(v => v.IsNotReported))
                {
                    oldValue = "NR";
                }
                else if (fieldEntity.FieldType == FieldType.MultiSelect)
                {
                    var optionIds = existingRecords.Where(v => v.OptionId.HasValue).Select(v => v.OptionId!.Value).ToList();
                    var optionValues = fieldEntity.Options?
                        .Where(o => optionIds.Contains(o.Id))
                        .Select(o => o.Value)
                        .ToList() ?? new List<string>();

                    oldValue = string.Join(", ", optionValues);
                }
                else if (request.MatrixColumnId.HasValue)
                {
                    var rowTokens = new List<string>();
                    foreach (var record in existingRecords.OrderBy(v => v.MatrixRowIndex ?? 0))
                    {
                        string token = fieldEntity.FieldType switch
                        {
                            FieldType.Text => record.StringValue ?? "",
                            FieldType.Integer or FieldType.Decimal => record.NumericValue?.ToString("G29") ?? "",
                            FieldType.Boolean => record.BooleanValue.HasValue
                                ? (record.BooleanValue.Value ? "Yes" : "No")
                                : "",
                            FieldType.SingleSelect => record.OptionId.HasValue
                                ? fieldEntity.Options?.FirstOrDefault(o => o.Id == record.OptionId.Value)?.Value ?? ""
                                : "",
                            _ => ""
                        };
                        if (!string.IsNullOrEmpty(token))
                            rowTokens.Add(token);
                    }
                    oldValue = string.Join("\n", rowTokens);
                }
                else
                {
                    var record = existingRecords.First();
                    oldValue = fieldEntity.FieldType switch
                    {
                        FieldType.Text => record.StringValue ?? "",
                        FieldType.Integer or FieldType.Decimal => record.NumericValue?.ToString("G29") ?? "",
                        FieldType.Boolean => record.BooleanValue.HasValue
                            ? (record.BooleanValue.Value ? "Yes" : "No")
                            : "",
                        FieldType.SingleSelect => record.OptionId.HasValue
                            ? fieldEntity.Options?.FirstOrDefault(o => o.Id == record.OptionId.Value)?.Value ?? ""
                            : "",
                        _ => ""
                    };
                }
            }

            string newValue = request.IsNotReported ? "NR" : (request.NewValue ?? "");

            if (oldValue != newValue)
            {
                var auditLog = new ExtractedDataAuditLog
                {
                    Id = Guid.NewGuid(),
                    ExtractionProcessId = extractionProcessId,
                    PaperId = request.PaperId,
                    FieldId = request.FieldId,
                    MatrixColumnId = request.MatrixColumnId,
                    MatrixRowIndex = request.MatrixRowIndex,
                    UserId = currentUserId,
                    OldValue = oldValue,
                    NewValue = newValue,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ModifiedAt = DateTimeOffset.UtcNow
                };
                await _unitOfWork.ExtractedDataAuditLogs.AddAsync(auditLog);
            }

            if (existingRecords.Any())
            {
                await _unitOfWork.ExtractedDataValues.RemoveMultipleAsync(existingRecords);
            }

            // NR flag: persist a single sentinel record with all value fields null
            if (request.IsNotReported)
            {
                var nrVal = new ExtractedDataValue
                {
                    Id = Guid.NewGuid(),
                    PaperId = request.PaperId,
                    FieldId = request.FieldId,
                    ReviewerId = currentUserId,
                    IsNotReported = true,
                    OptionId = null,
                    StringValue = null,
                    NumericValue = null,
                    BooleanValue = null,
                    MatrixColumnId = request.MatrixColumnId,
                    MatrixRowIndex = null,
                    IsConsensusFinal = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ModifiedAt = DateTimeOffset.UtcNow
                };
                await _unitOfWork.ExtractedDataValues.AddAsync(nrVal);
                await _unitOfWork.SaveChangesAsync();
                return;
            }

            if (string.IsNullOrWhiteSpace(request.NewValue))
            {
                await _unitOfWork.SaveChangesAsync();
                return;
            }

            // Split the incoming value on newlines to reconstruct matrix rows.
            // For flat-form fields (no MatrixColumnId) this always yields exactly 1 item.
            var rowValues = request.MatrixColumnId.HasValue
                ? request.NewValue.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                  .Select(s => s.Trim())
                                  .Where(s => !string.IsNullOrEmpty(s))
                                  .ToList()
                : new List<string> { request.NewValue };

            var newRecords = new List<ExtractedDataValue>();

            for (int rowIdx = 0; rowIdx < rowValues.Count; rowIdx++)
            {
                var rowValue = rowValues[rowIdx];
                int? matrixRowIndex = request.MatrixColumnId.HasValue ? rowIdx : (int?)null;

                if (fieldEntity.FieldType == FieldType.MultiSelect)
                {
                    // MultiSelect options arrive comma-separated within a single row token
                    var optionTokens = rowValue.Split(',').Select(s => s.Trim());
                    var allOptionsForField = fieldEntity.Options ?? new List<FieldOption>();

                    foreach (var optionVal in optionTokens)
                    {
                        var opt = allOptionsForField.FirstOrDefault(o => o.Value.Equals(optionVal, StringComparison.OrdinalIgnoreCase));
                        if (opt != null)
                        {
                            newRecords.Add(new ExtractedDataValue
                            {
                                Id = Guid.NewGuid(),
                                PaperId = request.PaperId,
                                FieldId = request.FieldId,
                                ReviewerId = currentUserId,
                                OptionId = opt.Id,
                                StringValue = optionVal,
                                NumericValue = null,
                                BooleanValue = null,
                                MatrixColumnId = request.MatrixColumnId,
                                MatrixRowIndex = matrixRowIndex,
                                IsConsensusFinal = true,
                                CreatedAt = DateTimeOffset.UtcNow,
                                ModifiedAt = DateTimeOffset.UtcNow
                            });
                        }
                    }
                }
                else
                {
                    var newVal = new ExtractedDataValue
                    {
                        Id = Guid.NewGuid(),
                        PaperId = request.PaperId,
                        FieldId = request.FieldId,
                        ReviewerId = currentUserId,
                        OptionId = null,
                        StringValue = null,
                        NumericValue = null,
                        BooleanValue = null,
                        MatrixColumnId = request.MatrixColumnId,
                        MatrixRowIndex = matrixRowIndex,
                        IsConsensusFinal = true,
                        CreatedAt = DateTimeOffset.UtcNow,
                        ModifiedAt = DateTimeOffset.UtcNow
                    };

                    switch (fieldEntity.FieldType)
                    {
                        case FieldType.Text:
                            newVal.StringValue = rowValue;
                            break;
                        case FieldType.Integer:
                        case FieldType.Decimal:
                            if (decimal.TryParse(rowValue, System.Globalization.NumberStyles.Any,
                                                 System.Globalization.CultureInfo.InvariantCulture, out var nValue))
                            {
                                newVal.NumericValue = nValue;
                            }
                            break;
                        case FieldType.Boolean:
                            if (rowValue.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                                newVal.BooleanValue = true;
                            else if (rowValue.Equals("No", StringComparison.OrdinalIgnoreCase))
                                newVal.BooleanValue = false;
                            else if (bool.TryParse(rowValue, out var bValue))
                                newVal.BooleanValue = bValue;
                            break;
                        case FieldType.SingleSelect:
                            var opt = fieldEntity.Options?.FirstOrDefault(o => o.Value.Equals(rowValue, StringComparison.OrdinalIgnoreCase));
                            if (opt != null)
                            {
                                newVal.OptionId = opt.Id;
                                newVal.StringValue = rowValue;
                            }
                            break;
                    }

                    newRecords.Add(newVal);
                }
            }

            if (newRecords.Any())
            {
                await _unitOfWork.ExtractedDataValues.AddRangeAsync(newRecords);
            }

            await _unitOfWork.SaveChangesAsync();
        }

        private ExtractedDataValue CreateExtractedValue(UpdateGridCellRequestDto req, ExtractionField field, Guid reviewerId, Guid? optionId, string? stringVal, decimal? numVal, bool? boolVal)
        {
            return new ExtractedDataValue
            {
                Id = Guid.NewGuid(),
                PaperId = req.PaperId,
                FieldId = field.Id,
                ReviewerId = reviewerId,
                OptionId = optionId,
                StringValue = stringVal,
                NumericValue = numVal,
                BooleanValue = boolVal,
                MatrixColumnId = req.MatrixColumnId,
                MatrixRowIndex = req.MatrixRowIndex,
                IsConsensusFinal = true,
                CreatedAt = DateTimeOffset.UtcNow,
                ModifiedAt = DateTimeOffset.UtcNow
            };
        }

        public async Task<ExtractionCommentDto> AddCommentAsync(Guid extractionProcessId, Guid paperId, Guid fieldId, AddCommentRequestDto request)
        {
            var task = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .FirstOrDefaultAsync(t => t.DataExtractionProcessId == extractionProcessId && t.PaperId == paperId);

            if (task == null)
            {
                throw new InvalidOperationException($"Extraction task for paper {paperId} in process {extractionProcessId} not found.");
            }

            var currentUserIdStr = _currentUserService.GetUserId();
            if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
            {
                throw new UnauthorizedAccessException("Current user ID is invalid.");
            }

            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId);

            if (extractionProcess?.ReviewProcess == null)
            {
                throw new InvalidOperationException($"DataExtractionProcess {extractionProcessId} or its ReviewProcess not found.");
            }

            var projectId = extractionProcess.ReviewProcess.ProjectId;

            var currentUserProjectMember = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Where(p => p.Id == projectId)
                .SelectMany(p => p.ProjectMembers)
                .FirstOrDefaultAsync(pm => pm.UserId == currentUserId);

            bool isLeader = currentUserProjectMember != null && currentUserProjectMember.Role == ProjectRole.Leader;
            bool isReviewer1 = task.Reviewer1Id == currentUserId;
            bool isReviewer2 = task.Reviewer2Id == currentUserId;

            if (!isLeader && !isReviewer1 && !isReviewer2)
            {
                throw new UnauthorizedAccessException("Only the Leader or assigned Reviewers can comment on this task.");
            }

            if (request.ThreadOwnerId != task.Reviewer1Id && request.ThreadOwnerId != task.Reviewer2Id)
            {
                if (!isLeader || request.ThreadOwnerId != currentUserId)
                {
                    throw new ArgumentException("ThreadOwnerId must be an assigned Reviewer on this task, or the Leader's own ID.");
                }
            }

            if (!isLeader && currentUserId != request.ThreadOwnerId)
            {
                throw new UnauthorizedAccessException("Reviewers can only comment on their own threads.");
            }

            var user = await _unitOfWork.Users.FindSingleOrDefaultAsync(u => u.Id == currentUserId);
            string userName = user?.Username ?? "Unknown";

            var comment = new ExtractionComment
            {
                Id = Guid.NewGuid(),
                ExtractionPaperTaskId = task.Id,
                FieldId = fieldId,
                MatrixColumnId = request.MatrixColumnId,
                MatrixRowIndex = request.MatrixRowIndex,
                ThreadOwnerId = request.ThreadOwnerId,
                UserId = currentUserId,
                Content = request.Content,
                CreatedAt = DateTimeOffset.UtcNow,
                ModifiedAt = DateTimeOffset.UtcNow
            };

            await _unitOfWork.ExtractionComments.AddAsync(comment);
            await _unitOfWork.SaveChangesAsync();

            // Set up notifications
            var paperTitle = (await _unitOfWork.Papers.FindSingleOrDefaultAsync(p => p.Id == paperId))?.Title ?? paperId.ToString();
            var notifyIds = new List<Guid>();

            if (isLeader)
            {
                if (request.ThreadOwnerId != currentUserId)
                {
                    notifyIds.Add(request.ThreadOwnerId);
                }
            }
            else
            {
                // notify leader(s). We must find the leaders of the project
                var leaders = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                    .Where(p => p.Id == projectId)
                    .SelectMany(p => p.ProjectMembers)
                    .Where(pm => pm.Role == ProjectRole.Leader)
                    .Select(pm => pm.UserId)
                    .ToListAsync();

                notifyIds.AddRange(leaders);
            }

            // Remove current user from notification list
            notifyIds = notifyIds.Distinct().Where(id => id != currentUserId).ToList();

            if (notifyIds.Any())
            {
                await _notificationService.SendToManyAsync(
                    notifyIds,
                    title: "New extracted data comment",
                    message: $"{userName} commented on data extraction for paper: \"{paperTitle}\".",
                    type: NotificationType.Review,
                    relatedEntityId: task.Id,
                    entityType: NotificationEntityType.PaperAssignment
                );
            }

            return new ExtractionCommentDto
            {
                Id = comment.Id,
                FieldId = comment.FieldId,
                ThreadOwnerId = comment.ThreadOwnerId,
                UserId = comment.UserId,
                UserName = userName,
                Content = comment.Content,
                CreatedAt = comment.CreatedAt
            };
        }

        public async Task<ReviewerWorkspaceDto> GetReviewerWorkspaceAsync(Guid extractionProcessId, Guid paperId)
        {
            var currentUserIdStr = _currentUserService.GetUserId();
            if (!Guid.TryParse(currentUserIdStr, out var currentUserId))
            {
                throw new UnauthorizedAccessException("Current user ID is invalid.");
            }

            var task = await _unitOfWork.ExtractionPaperTasks.GetQueryable()
                .FirstOrDefaultAsync(t => t.DataExtractionProcessId == extractionProcessId && t.PaperId == paperId);

            if (task == null)
                throw new InvalidOperationException($"Extraction task for paper {paperId} not found.");

            if (task.Reviewer1Id != currentUserId && task.Reviewer2Id != currentUserId)
            {
                throw new UnauthorizedAccessException("Current user is not assigned to review this paper.");
            }

            var extractionProcess = await _unitOfWork.DataExtractionProcesses.GetQueryable()
                .Include(dp => dp.ReviewProcess)
                .FirstOrDefaultAsync(dp => dp.Id == extractionProcessId);

            if (extractionProcess?.ReviewProcess == null)
                throw new InvalidOperationException("ReviewProcess not found for this extraction process.");

            // var projectId = extractionProcess.ReviewProcess.ProjectId;

            var templateList = await _unitOfWork.ExtractionTemplates.FindAllAsync(t => t.DataExtractionProcessId == extractionProcessId);
            var templateEntity = templateList.FirstOrDefault();

            if (templateEntity == null)
                throw new InvalidOperationException("Extraction template not found for this protocol.");

            var template = await _unitOfWork.ExtractionTemplates.GetByIdWithFieldsAsync(templateEntity.Id);

            if (template == null)
                throw new InvalidOperationException("Extraction template not found.");

            var extractedValues = await _unitOfWork.ExtractedDataValues.FindAllAsync(e =>
                e.PaperId == paperId &&
                e.ReviewerId == currentUserId &&
                e.IsConsensusFinal == false);

            var valuesList = extractedValues.ToList();

            var comments = await _unitOfWork.ExtractionComments.GetQueryable()
                .Include(c => c.User)
                .Where(c => c.ExtractionPaperTaskId == task.Id && c.ThreadOwnerId == currentUserId)
                .ToListAsync();

            var dto = new ReviewerWorkspaceDto
            {
                PaperId = paperId,
                TemplateId = template.Id,
                ReviewerId = currentUserId,
                Sections = template.Sections.OrderBy(s => s.OrderIndex).Select(s => new ReviewerSectionDto
                {
                    SectionId = s.Id,
                    Name = s.Name,
                    Description = s.Description,
                    SectionType = (int)s.SectionType,
                    OrderIndex = s.OrderIndex,
                    MatrixColumns = s.MatrixColumns?.OrderBy(c => c.OrderIndex).Select(c => new ExtractionMatrixColumnDto
                    {
                        ColumnId = c.Id,
                        Name = c.Name,
                        Description = c.Description,
                        OrderIndex = c.OrderIndex
                    }).ToList() ?? new List<ExtractionMatrixColumnDto>(),
                    Fields = s.Fields.Where(f => f.ParentFieldId == null).OrderBy(f => f.OrderIndex)
                        .Select(f => MapReviewerField(f, valuesList, comments))
                        .ToList()
                }).ToList()
            };

            return dto;
        }

        private ReviewerFieldDto MapReviewerField(ExtractionField field, List<ExtractedDataValue> allValues, List<ExtractionComment> allComments)
        {
            var fieldDto = new ReviewerFieldDto
            {
                FieldId = field.Id,
                Name = field.Name,
                Instruction = field.Instruction,
                FieldType = (int)field.FieldType,
                IsRequired = field.IsRequired,
                OrderIndex = field.OrderIndex,
                Options = field.Options?.OrderBy(o => o.DisplayOrder).Select(o => new FieldOptionDto
                {
                    OptionId = o.Id,
                    FieldId = o.FieldId,
                    Value = o.Value,
                    DisplayOrder = o.DisplayOrder
                }).ToList() ?? new List<FieldOptionDto>(),
                SubFields = field.SubFields?.OrderBy(sf => sf.OrderIndex)
                    .Select(sf => MapReviewerField(sf, allValues, allComments))
                    .ToList() ?? new List<ReviewerFieldDto>()
            };

            var fieldValues = allValues.Where(v => v.FieldId == field.Id).ToList();
            var groupedValues = fieldValues.GroupBy(v => new { v.MatrixColumnId, v.MatrixRowIndex }).ToList();

            var fieldComments = allComments.Where(c => c.FieldId == field.Id).ToList();
            var commentGroups = fieldComments.GroupBy(c => new { c.MatrixColumnId, c.MatrixRowIndex }).ToList();

            var allKeys = groupedValues.Select(g => new { g.Key.MatrixColumnId, g.Key.MatrixRowIndex })
                .Union(commentGroups.Select(g => new { g.Key.MatrixColumnId, g.Key.MatrixRowIndex }))
                .Distinct()
                .ToList();

            foreach (var key in allKeys)
            {
                var records = fieldValues.Where(v => v.MatrixColumnId == key.MatrixColumnId && v.MatrixRowIndex == key.MatrixRowIndex).ToList();
                var cellComments = fieldComments.Where(c => c.MatrixColumnId == key.MatrixColumnId && c.MatrixRowIndex == key.MatrixRowIndex)
                    .Select(c => new ExtractionCommentDto
                    {
                        Id = c.Id,
                        FieldId = c.FieldId,
                        ThreadOwnerId = c.ThreadOwnerId,
                        UserId = c.UserId,
                        UserName = c.User?.Username ?? "Unknown",
                        Content = c.Content,
                        CreatedAt = c.CreatedAt
                    })
                    .OrderBy(c => c.CreatedAt)
                    .ToList();

                var answer = BuildAnswerDetail(records, field) ?? new AnswerDetailDto();
                if (cellComments.Any()) answer.Comments = cellComments;
                if (!records.Any() && !cellComments.Any()) answer = null;

                fieldDto.Answers.Add(new ReviewerExtractedAnswerDto
                {
                    MatrixColumnId = key.MatrixColumnId,
                    MatrixRowIndex = key.MatrixRowIndex,
                    Answer = answer
                });
            }

            return fieldDto;
        }

        public async Task<List<ExtractedDataAuditLogDto>> GetCellAuditLogsAsync(Guid processId, Guid paperId, Guid fieldId, Guid? matrixColumnId, int? matrixRowIndex)
        {
            var logs = await _unitOfWork.ExtractedDataAuditLogs.GetQueryable()
                .Include(l => l.User)
                .Where(l => l.ExtractionProcessId == processId &&
                            l.PaperId == paperId &&
                            l.FieldId == fieldId &&
                            l.MatrixColumnId == matrixColumnId &&
                            l.MatrixRowIndex == matrixRowIndex)
                .OrderByDescending(l => l.CreatedAt)
                .ToListAsync();

            return logs.Select(l => new ExtractedDataAuditLogDto
            {
                Id = l.Id,
                PaperId = l.PaperId,
                FieldId = l.FieldId,
                UserId = l.UserId,
                UserName = l.User?.Username ?? "Unknown",
                OldValue = l.OldValue,
                NewValue = l.NewValue,
                CreatedAt = l.CreatedAt
            }).ToList();
        }
    }
}
