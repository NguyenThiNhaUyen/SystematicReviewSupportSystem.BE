using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Shared.Builder;
using Shared.Exceptions;
using Shared.Models;
using SRSS.IAM.Services.AuthService;
using SRSS.IAM.Services.DTOs.Auth;
using SRSS.IAM.Services.DTOs.User;
using SRSS.IAM.Services.JWTService;
using SRSS.IAM.Services.RefreshTokenService;
using System.Data.Common;
using System.Security.Claims;

namespace SRSS.IAM.API.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : BaseController
    {
        private const string RefreshTokenCookieName = "SRSS_IAM_refreshToken";

        private readonly IAuthService _authService;
        private readonly IJwtService _jwtService;
        private readonly IRefreshTokenService _refreshTokenService;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            IAuthService authService,
            IJwtService jwtService,
            IRefreshTokenService refreshTokenService,
            IWebHostEnvironment env,
            ILogger<AuthController> logger)
        {
            _authService = authService;
            _jwtService = jwtService;
            _refreshTokenService = refreshTokenService;
            _env = env;
            _logger = logger;
        }

        [HttpPost("register")]
        public async Task<ActionResult<ApiResponse>> Register([FromBody] RegisterRequest request)
        {
            await _authService.RegisterAsync(request);
            return Created("Dang ky thanh cong");
        }

        [HttpPost("login")]
        public async Task<ActionResult<ApiResponse<LoginResponse>>> Login([FromBody] LoginRequest request)
        {
            var result = await _authService.LoginAsync(request);
            await IssueRefreshTokenAsync(result.UserId);
            return Ok(result, "Dang nhap thanh cong");
        }

        [HttpPost("google/login")]
        public async Task<ActionResult<ApiResponse<LoginResponse>>> GoogleLogin([FromBody] GoogleLoginRequest request)
        {
            var result = await _authService.GoogleLoginAsync(request);
            await IssueRefreshTokenAsync(result.UserId);
            return Ok(result, "Dang nhap bang Google thanh cong");
        }

        [HttpPost("google/oauth-url")]
        public async Task<ActionResult<ApiResponse<GoogleOAuthUrlResponse>>> GenerateGoogleOAuthUrl([FromBody] GoogleOAuthUrlRequest request)
        {
            var result = await _authService.GenerateGoogleOAuthUrlAsync(request);
            return Ok(result, "Tao Google OAuth URL thanh cong");
        }

        [HttpPost("refresh")]
        public async Task<ActionResult<ApiResponse<LoginResponse>>> RefreshToken()
        {
            if (!Request.Cookies.TryGetValue(RefreshTokenCookieName, out var refreshToken) || string.IsNullOrWhiteSpace(refreshToken))
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ResponseBuilder.Unauthorized("Refresh token is missing"));
            }

            try
            {
                var validationResult = await _refreshTokenService.ValidateAsync(refreshToken);
                if (validationResult == null)
                {
                    await ClearRefreshCookieAsync(refreshToken);
                    return StatusCode(StatusCodes.Status401Unauthorized, ResponseBuilder.Unauthorized("Refresh token is invalid or expired"));
                }

                var result = await _authService.RefreshAsync(validationResult.UserId);
                await IssueRefreshTokenAsync(result.UserId);
                return Ok(result, "Lam moi token thanh cong");
            }
            catch (BaseDomainException)
            {
                throw;
            }
            catch (DbException ex)
            {
                _logger.LogError(ex, "Database operation failed during token refresh.");
                throw new ServiceUnavailableException("Khong the ket noi co so du lieu. Vui long thu lai sau.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Refresh token operation failed.");
                throw new ServiceUnavailableException("Khong the ket noi co so du lieu. Vui long thu lai sau.");
            }
        }

        [HttpPost("logout")]
        [Authorize]
        public async Task<ActionResult<ApiResponse>> Logout()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized("Khong tim thay nguoi dung");
            }

            var accessToken = Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized("Khong tim thay access token");
            }

            var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(accessToken);
            var expUnix = jwtToken.Payload.Exp;
            if (expUnix == null)
            {
                return BadRequest("Access token khong hop le");
            }

            var expiry = DateTimeOffset.FromUnixTimeSeconds((long)expUnix);
            var ttl = expiry - DateTimeOffset.UtcNow;
            if (ttl <= TimeSpan.Zero)
            {
                return BadRequest("Access token da het han");
            }

            await _authService.LogoutAsync(userId, accessToken, ttl);
            return Ok("Dang xuat thanh cong");
        }

        [HttpGet("profile")]
        [Authorize]
        public async Task<ActionResult<ApiResponse<UserProfileResponse>>> GetUserProfile()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                throw new UnauthorizedException("Unauthorized");
            }

            var result = await _authService.GetUserProfileAsync(userId);
            return Ok(result, "Lay thong tin nguoi dung thanh cong");
        }

        [HttpGet("me")]
        [Authorize]
        public async Task<ActionResult<ApiResponse<AuthMeResponse>>> GetAuthMe()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                throw new UnauthorizedException("Unauthorized");
            }

            var result = await _authService.GetAuthMeAsync(userId);
            return Ok(result, "Check user login status successfully.");
        }

        private async Task IssueRefreshTokenAsync(Guid userId)
        {
            RefreshTokenIssueResult issued;
            try
            {
                issued = await _refreshTokenService.IssueAsync(userId);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Database update failed while issuing refresh token.");
                throw new ServiceUnavailableException("Khong the ket noi co so du lieu. Vui long thu lai sau.");
            }
            catch (DbException ex)
            {
                _logger.LogError(ex, "Database operation failed while issuing refresh token.");
                throw new ServiceUnavailableException("Khong the ket noi co so du lieu. Vui long thu lai sau.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Refresh token issue operation failed.");
                throw new ServiceUnavailableException("Khong the ket noi co so du lieu. Vui long thu lai sau.");
            }

            Response.Cookies.Append(
                RefreshTokenCookieName,
                issued.RefreshToken,
                BuildCookieOptions(issued.ExpiresAt));
        }

        private CookieOptions BuildCookieOptions(DateTimeOffset expiresUtc)
        {
            var options = new CookieOptions
            {
                HttpOnly = true,
                Expires = expiresUtc
            };

            if (_env.IsDevelopment())
            {
                options.SameSite = SameSiteMode.Lax;
                options.Secure = false;
            }
            else
            {
                options.SameSite = SameSiteMode.None;
                options.Secure = true;
            }

            return options;
        }

        private async Task ClearRefreshCookieAsync(string refreshToken)
        {
            await _refreshTokenService.RevokeAsync(refreshToken);
            Response.Cookies.Delete(RefreshTokenCookieName, BuildCookieOptions(DateTimeOffset.UtcNow.AddYears(-1)));
        }
    }
}
