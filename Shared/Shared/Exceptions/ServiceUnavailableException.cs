using System.Net;

namespace Shared.Exceptions
{
    public class ServiceUnavailableException : BaseDomainException
    {
        public ServiceUnavailableException(string message = "Dich vu tam thoi khong kha dung. Vui long thu lai sau.")
            : base(message, HttpStatusCode.ServiceUnavailable, "SERVICE_UNAVAILABLE") { }
    }
}
