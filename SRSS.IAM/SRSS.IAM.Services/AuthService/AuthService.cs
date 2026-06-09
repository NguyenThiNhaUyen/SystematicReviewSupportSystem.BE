using Google.Apis.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Cache;
using Shared.Exceptions;
using SRSS.IAM.Repositories.Entities;
using SRSS.IAM.Repositories.UnitOfWork;
using SRSS.IAM.Services.Configurations;
using SRSS.IAM.Services.DTOs.Auth;
using SRSS.IAM.Services.DTOs.User;
using SRSS.IAM.Services.JWTService;
using SRSS.IAM.Services.Mappers;
using SRSS.IAM.Services.RefreshTokenService;
using System.Data.Common;

namespace SRSS.IAM.Services.AuthService
{
    public class AuthService : IAuthService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IPasswordHasher<User> _passwordHasher;
        private readonly IJwtService _jwtService;
        private readonly IRefreshTokenService _refreshTokenService;
        private readonly JwtSettings _jwtSettings;
        private readonly IRedisCacheService _redisService;
        private readonly GoogleAuthSettings _googleAuthSettings;
        private readonly ILogger<AuthService> _logger;

        public AuthService(
            IUnitOfWork unitOfWork,
            IPasswordHasher<User> passwordHasher,
            IJwtService jwtService,
            IRefreshTokenService refreshTokenService,
            IOptions<JwtSettings> jwtSettings,
            IRedisCacheService redisService,
            IOptions<GoogleAuthSettings> googleAuthSettings,
            ILogger<AuthService> logger)
        {
            _unitOfWork = unitOfWork;
            _passwordHasher = passwordHasher;
            _jwtService = jwtService;
            _refreshTokenService = refreshTokenService;
            _jwtSettings = jwtSettings.Value;
            _redisService = redisService;
            _googleAuthSettings = googleAuthSettings.Value;
            _logger = logger;
        }

        public async Task<LoginResponse> LoginAsync(LoginRequest request)
        {
            try
            {
                var keyLoginNormalized = request.EffectiveKeyLogin.Trim().ToLower();

                var existingUser = await _unitOfWork.Users.FindSingleAsync(u =>
                    u.Username.ToLower() == keyLoginNormalized
                    || u.Email.ToLower() == keyLoginNormalized);

                if (existingUser == null)
                {
                    throw new InvalidCredentialsException("Thong tin dang nhap khong chinh xac");
                }

                if (!existingUser.IsActive)
                {
                    throw new ForbiddenException("Tai khoan da bi vo hieu hoa");
                }

                var verifyResult = _passwordHasher.VerifyHashedPassword(existingUser, existingUser.Password ?? string.Empty, request.Password);
                if (verifyResult == PasswordVerificationResult.Failed)
                {
                    throw new InvalidCredentialsException("Thong tin dang nhap khong chinh xac");
                }

                var accessToken = _jwtService.GenerateAccessToken(existingUser);
                await _unitOfWork.Users.UpdateAsync(existingUser);
                await _unitOfWork.SaveChangesAsync();

                return CreateLoginResponse(existingUser, accessToken);
            }
            catch (BaseDomainException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Database update failed during login.");
                throw new ServiceUnavailableException("Khong the ket noi co so du lieu. Vui long thu lai sau.");
            }
            catch (DbException ex)
            {
                _logger.LogError(ex, "Database query failed during login.");
                throw new ServiceUnavailableException("Khong the ket noi co so du lieu. Vui long thu lai sau.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Database operation failed during login.");
                throw new ServiceUnavailableException("Khong the ket noi co so du lieu. Vui long thu lai sau.");
            }
        }

        public async Task RegisterAsync(RegisterRequest request)
        {
            var usernameNormalized = request.Username.Trim().ToLower();
            var emailNormalized = request.Email.Trim().ToLower();

            var existingUser = await _unitOfWork.Users.FindSingleAsync(u =>
                u.Username.ToLower() == usernameNormalized
                || u.Email.ToLower() == emailNormalized);

            if (existingUser != null)
            {
                throw new BadRequestException("Email/ten dang nhap nay da duoc dang ki");
            }

            var newUser = new User
            {
                Username = usernameNormalized,
                FullName = request.FullName,
                Email = emailNormalized,
                Role = request.Role,
                Password = _passwordHasher.HashPassword(null, request.Password),
                IsActive = true,
            };

            await _unitOfWork.Users.AddAsync(newUser);
            await _unitOfWork.SaveChangesAsync();
        }

        public async Task<LoginResponse> GoogleLoginAsync(GoogleLoginRequest request)
        {
            try
            {
                var payload = await GoogleJsonWebSignature.ValidateAsync(request.IdToken, new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = new[] { _googleAuthSettings.ClientId }
                });

                var user = await _unitOfWork.Users.FindSingleAsync(u =>
                    u.Email.ToLower() == payload.Email.ToLower());

                if (user == null)
                {
                    user = new User
                    {
                        FullName = payload.Name,
                        Email = payload.Email,
                        Username = payload.Email.Split('@')[0],
                        Role = Role.Client,
                        IsActive = true,
                        Password = null
                    };

                    await _unitOfWork.Users.AddAsync(user);
                    if (await _unitOfWork.SaveChangesAsync() == 0)
                    {
                        throw new Exception("Failed to create Google OAuth user");
                    }
                }
                else if (!user.IsActive)
                {
                    throw new ForbiddenException("Tai khoan da bi vo hieu hoa");
                }

                var accessToken = _jwtService.GenerateAccessToken(user);
                return CreateLoginResponse(user, accessToken);
            }
            catch (InvalidJwtException)
            {
                throw new UnauthorizedException("Invalid Google ID token");
            }
        }

        public async Task<GoogleOAuthUrlResponse> GenerateGoogleOAuthUrlAsync(GoogleOAuthUrlRequest request)
        {
            var state = Guid.NewGuid().ToString();
            var scope = Uri.EscapeDataString(_googleAuthSettings.Scope);
            var redirectUri = Uri.EscapeDataString(request.RedirectUrl);
            var clientId = Uri.EscapeDataString(_googleAuthSettings.ClientId);

            var url = $"{_googleAuthSettings.AuthorizationEndpoint}" +
                      $"?client_id={clientId}" +
                      $"&redirect_uri={redirectUri}" +
                      $"&response_type=code" +
                      $"&scope={scope}" +
                      $"&access_type=offline" +
                      $"&prompt=consent" +
                      $"&state={state}";

            return await Task.FromResult(new GoogleOAuthUrlResponse
            {
                Url = url
            });
        }

        public async Task<LoginResponse> RefreshAsync(Guid userId)
        {
            try
            {
                var user = await _unitOfWork.Users.FindSingleAsync(u => u.Id == userId)
                    ?? throw new UnauthorizedException("Refresh token khong hop le hoac da het han");

                if (!user.IsActive)
                {
                    throw new ForbiddenException("Tai khoan da bi vo hieu hoa");
                }

                var newAccessToken = _jwtService.GenerateAccessToken(user);
                return CreateLoginResponse(user, newAccessToken);
            }
            catch (BaseDomainException)
            {
                throw;
            }
            catch (DbException ex)
            {
                _logger.LogError(ex, "Database query failed during token refresh.");
                throw new ServiceUnavailableException("Khong the ket noi co so du lieu. Vui long thu lai sau.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Database operation failed during token refresh.");
                throw new ServiceUnavailableException("Khong the ket noi co so du lieu. Vui long thu lai sau.");
            }
        }

        public async Task LogoutAsync(string userId, string accessToken, TimeSpan accessTokenTtl)
        {
            var user = await _unitOfWork.Users.FindSingleAsync(u => u.Id == Guid.Parse(userId));
            if (user != null)
            {
                user.RefreshToken = null;
                user.IsRefreshTokenRevoked = true;
                await _unitOfWork.Users.UpdateAsync(user);
                await _unitOfWork.SaveChangesAsync();
            }

            await _redisService.SetAsync($"iam:blacklist:{accessToken}", "revoked", accessTokenTtl);
        }

        public async Task<UserProfileResponse> GetUserProfileAsync(string userId)
        {
            var userGuid = Guid.Parse(userId);
            var user = await _unitOfWork.Users.FindSingleAsync(u => u.Id == userGuid);
            if (user == null)
            {
                throw new NotFoundException("Nguoi dung khong ton tai");
            }

            return user.ToUserProfileResponse();
        }

        public async Task<AuthMeResponse> GetAuthMeAsync(string userId)
        {
            var userGuid = Guid.Parse(userId);
            var user = await _unitOfWork.Users.FindSingleAsync(u => u.Id == userGuid);
            if (user == null)
            {
                throw new NotFoundException("Nguoi dung khong ton tai");
            }

            return user.ToAuthMeResponse();
        }

        private LoginResponse CreateLoginResponse(User user, string accessToken)
        {
            return new LoginResponse
            {
                UserId = user.Id,
                Username = user.FullName,
                Email = user.Email,
                Role = user.Role.ToString(),
                AccessToken = accessToken,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
            };
        }
    }
}
