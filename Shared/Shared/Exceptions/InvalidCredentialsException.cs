using System.Net;

namespace Shared.Exceptions
{
    public class InvalidCredentialsException : BaseDomainException
    {
        public InvalidCredentialsException(string message = "Thông tin đăng nhập không chính xác")
            : base(message, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS") { }
    }
}
