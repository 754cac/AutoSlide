using BackendServer.Features.Auth.Services;
using Xunit;

namespace BackendServer.Tests
{
    public class PasswordServiceTests
    {
        [Fact]
        public void HashAndVerify_CorrectPassword_ReturnsTrue()
        {
            var svc = new PasswordService();
            var password = "P@ssw0rd!";

            var hash = svc.HashPassword(password);

            Assert.True(svc.VerifyPassword(password, hash));
        }

        [Fact]
        public void Verify_WrongPassword_ReturnsFalse()
        {
            var svc = new PasswordService();
            var password = "P@ssw0rd!";
            var wrong = "not_the_password";

            var hash = svc.HashPassword(password);

            Assert.False(svc.VerifyPassword(wrong, hash));
        }
    }
}
