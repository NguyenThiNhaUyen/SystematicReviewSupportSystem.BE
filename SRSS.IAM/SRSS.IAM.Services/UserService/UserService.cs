using Microsoft.EntityFrameworkCore;
using SRSS.IAM.Repositories.UnitOfWork;
using SRSS.IAM.Services.DTOs.Common;
using SRSS.IAM.Services.DTOs.User;
using SRSS.IAM.Services.Mappers;
using SRSS.IAM.Repositories.Entities;
using Microsoft.AspNetCore.Identity;
using Shared.Exceptions;

namespace SRSS.IAM.Services.UserService
{
    public class UserService : IUserService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IPasswordHasher<User> _passwordHasher;

        public UserService(IUnitOfWork unitOfWork, IPasswordHasher<User> passwordHasher)
        {
            _unitOfWork = unitOfWork;
            _passwordHasher = passwordHasher;
        }

        public async Task<UserResponse> GetUserByIdAsync(Guid userId)
        {
            var user = await _unitOfWork.Users.FindSingleAsync(u => u.Id == userId);
            if (user == null)
            {
                throw new NotFoundException($"User with ID {userId} not found.");
            }

            return user.ToUserResponse();
        }

        public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request)
        {
            var user = await _unitOfWork.Users.FindSingleAsync(u => u.Id == userId);
            if (user == null)
            {
                throw new NotFoundException($"User with ID {userId} not found.");
            }

            // Verify old password
            var verifyResult = _passwordHasher.VerifyHashedPassword(user, user.Password ?? string.Empty, request.OldPassword);
            if (verifyResult == PasswordVerificationResult.Failed)
            {
                throw new InvalidCredentialsException("Invalid credentials.");
            }

            // Hash and update new password
            user.Password = _passwordHasher.HashPassword(user, request.NewPassword);

            await _unitOfWork.Users.UpdateAsync(user);
            await _unitOfWork.SaveChangesAsync();
        }

        public async Task<IEnumerable<UserSearchResponse>> SearchUsersAsync(Guid projectId, string keyword, int limit = 15)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return Enumerable.Empty<UserSearchResponse>();
            }

            var trimmedKeyword = keyword.Trim();
            if (trimmedKeyword.Length < 2)
            {
                throw new ArgumentException("Search keyword must be at least 2 characters long.");
            }

            var users = await _unitOfWork.Users.SearchUsersAsync(projectId, trimmedKeyword, limit);

            return users.Select(u => new UserSearchResponse
            {
                Id = u.Id,
                Email = u.Email,
                Username = u.Username,
                FullName = u.FullName,
                ProjectRole = u.ProjectRole,
                IsAlreadyMember = u.IsAlreadyMember
            });
        }

        public async Task<PaginatedResponse<UserResponse>> GetUsersAsync(UserListRequest request)
        {
            var (users, totalCount) = await _unitOfWork.Users.GetPaginatedUsersAsync(
                request.Search,
                request.IsActive,
                request.PageNumber,
                request.PageSize);

            return new PaginatedResponse<UserResponse>
            {
                Items = users.Select(u => u.ToUserResponse()).ToList(),
                TotalCount = totalCount,
                PageNumber = request.PageNumber,
                PageSize = request.PageSize
            };
        }

        public async Task<UserResponse> UpdateUserProfileAsync(Guid userId, UpdateUserProfileRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Email))
                throw new ArgumentException("Email is required.");
            if (string.IsNullOrWhiteSpace(request.Username))
                throw new ArgumentException("Username is required.");
            if (string.IsNullOrWhiteSpace(request.FullName))
                throw new ArgumentException("FullName is required.");

            var user = await _unitOfWork.Users.FindSingleAsync(u => u.Id == userId);
            if (user == null)
                throw new InvalidOperationException($"User with ID {userId} not found.");

            // Check if email is already taken by another user
            if (!string.Equals(user.Email, request.Email, StringComparison.OrdinalIgnoreCase))
            {
                var emailExists = await _unitOfWork.Users.AnyAsync(u => u.Email.ToLower() == request.Email.ToLower());
                if (emailExists)
                    throw new InvalidOperationException($"Email '{request.Email}' is already taken.");
                user.Email = request.Email;
            }

            // Check if username is already taken by another user
            if (!string.Equals(user.Username, request.Username, StringComparison.OrdinalIgnoreCase))
            {
                var usernameExists = await _unitOfWork.Users.AnyAsync(u => u.Username.ToLower() == request.Username.ToLower());
                if (usernameExists)
                    throw new InvalidOperationException($"Username '{request.Username}' is already taken.");
                user.Username = request.Username;
            }

            user.FullName = request.FullName;

            await _unitOfWork.Users.UpdateAsync(user);
            await _unitOfWork.SaveChangesAsync();

            return user.ToUserResponse();
        }

        public async Task<UserResponse> ToggleUserStatusAsync(Guid userId)
        {
            var user = await _unitOfWork.Users.FindSingleAsync(u => u.Id == userId);
            if (user == null)
                throw new InvalidOperationException($"User with ID {userId} not found.");

            user.IsActive = !user.IsActive;

            await _unitOfWork.Users.UpdateAsync(user);
            await _unitOfWork.SaveChangesAsync();

            return user.ToUserResponse();
        }

        public async Task<PaginatedResponse<UserProgressOverviewResponse>> GetUserProgressOverviewAsync(UserProgressRequest request)
        {
            // 1. Get queryable for project members (excluding Leader)
            var query = _unitOfWork.SystematicReviewProjects.GetProjectMembersQueryable(request.ProjectId)
                .Include(m => m.User)
                .Where(m => m.Role != ProjectRole.Leader)
                .AsNoTracking();

            // 2. Search by FullName, Username, Email
            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                var search = request.Search.Trim().ToLower();
                query = query.Where(m =>
                    m.User.FullName.ToLower().Contains(search) ||
                    m.User.Username.ToLower().Contains(search) ||
                    m.User.Email.ToLower().Contains(search));
            }

            // 3. Paginate
            var totalCount = await query.CountAsync();
            var members = await query
                .OrderBy(m => m.User.FullName)
                .Skip((request.PageNumber - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToListAsync();

            if (!members.Any())
            {
                return new PaginatedResponse<UserProgressOverviewResponse>
                {
                    Items = new List<UserProgressOverviewResponse>(),
                    TotalCount = totalCount,
                    PageNumber = request.PageNumber,
                    PageSize = request.PageSize
                };
            }

            var memberIds = members.Select(m => m.Id).ToList();
            var userIds = members.Select(m => m.UserId).ToList();

            // 4. Batch Fetch Assignments, Decisions, Submissions
            // To be efficient, we fetch all relevant data for the current page of users

            // Get all assignments for these members
            var assignments = await _unitOfWork.PaperAssignments.FindAllAsync(
                pa => memberIds.Contains(pa.ProjectMemberId),
                isTracking: false);

            // Get all decisions for these users
            var decisions = await _unitOfWork.ScreeningDecisions.FindAllAsync(
                sd => userIds.Contains(sd.ReviewerId),
                isTracking: false);

            // Get all checklist submissions for these users
            var submissions = await _unitOfWork.StudySelectionChecklistSubmissions.FindAllAsync(
                cs => userIds.Contains(cs.ReviewerId),
                isTracking: false);

            var resultItems = new List<UserProgressOverviewResponse>();

            foreach (var member in members)
            {
                var memberAssignments = assignments.Where(a => a.ProjectMemberId == member.Id).ToList();
                var workload = memberAssignments.Count();

                var completedCount = 0;
                var hasAnyWork = false;
                var allCompleted = workload > 0; // if workload is 0, logically not completed

                foreach (var assignment in memberAssignments)
                {
                    var hasDecision = decisions.Any(d =>
                        d.ReviewerId == member.UserId &&
                        d.PaperId == assignment.PaperId &&
                        d.StudySelectionProcessId == assignment.StudySelectionProcessId &&
                        d.Phase == assignment.Phase);

                    var hasSubmission = submissions.Any(s =>
                        s.ReviewerId == member.UserId &&
                        s.PaperId == assignment.PaperId &&
                        s.StudySelectionProcessId == assignment.StudySelectionProcessId &&
                        s.Phase == assignment.Phase);

                    if (hasDecision)
                    {
                        completedCount++;
                    }

                    if (hasDecision || hasSubmission)
                    {
                        hasAnyWork = true;
                    }

                    if (!hasDecision)
                    {
                        allCompleted = false;
                    }
                }

                var progress = workload > 0 ? (double)completedCount / workload * 100 : 0;

                ReviewerStatus status;
                if (workload == 0)
                {
                    status = ReviewerStatus.NotStarted;
                }
                else if (allCompleted)
                {
                    status = ReviewerStatus.Completed;
                }
                else if (hasAnyWork)
                {
                    status = ReviewerStatus.InProgress;
                }
                else
                {
                    status = ReviewerStatus.NotStarted;
                }

                resultItems.Add(new UserProgressOverviewResponse
                {
                    UserId = member.UserId,
                    FullName = member.User.FullName ?? string.Empty,
                    Username = member.User.Username ?? string.Empty,
                    Email = member.User.Email ?? string.Empty,
                    Workload = workload,
                    Completed = completedCount,
                    Progress = Math.Round(progress, 2),
                    Status = status,
                    LastSynchronizedAt = DateTimeOffset.UtcNow
                });
            }

            return new PaginatedResponse<UserProgressOverviewResponse>
            {
                Items = resultItems,
                TotalCount = totalCount,
                PageNumber = request.PageNumber,
                PageSize = request.PageSize
            };
        }
    }
}
