using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using BackendServer.Features.Auth.DTOs;
using FluentAssertions;
using Xunit;
using System.Threading.Tasks;

namespace BackendServer.Tests;

public class AuthIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Register_Login_GetMe_Workflow_Succeeds()
    {
        var client = _factory.CreateClient();

        var register = new RegisterDto
        {
            Email = $"testuser+{System.Guid.NewGuid():N}@example.com",
            Password = "Passw0rd!",
            FullName = "Test User"
        };

        var regResp = await client.PostAsJsonAsync("/api/auth/register", register);
        if (!regResp.IsSuccessStatusCode)
        {
            var text = await regResp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Register failed: {regResp.StatusCode}: {text}");
        }

        var auth = await regResp.Content.ReadFromJsonAsync<AuthResponseDto>();
        auth.Should().NotBeNull();
        auth!.Token.Should().NotBeNullOrWhiteSpace();

        var login = new LoginDto { Email = register.Email, Password = register.Password };
        var loginResp = await client.PostAsJsonAsync("/api/auth/login", login);
        if (!loginResp.IsSuccessStatusCode)
        {
            var text = await loginResp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Login failed: {loginResp.StatusCode}: {text}");
        }

        var auth2 = await loginResp.Content.ReadFromJsonAsync<AuthResponseDto>();
        auth2.Should().NotBeNull();
        auth2!.Token.Should().NotBeNullOrWhiteSpace();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth2.Token);
        var meResp = await client.GetAsync("/api/auth/me");
        meResp.EnsureSuccessStatusCode();

        var meText = await meResp.Content.ReadAsStringAsync();
        meText.Should().Contain(register.Email);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        var register = new RegisterDto
        {
            Email = $"badpass+{System.Guid.NewGuid():N}@example.com",
            Password = "RightPass1!",
            FullName = "Bad Pass"
        };

        var regResp = await client.PostAsJsonAsync("/api/auth/register", register);
        regResp.EnsureSuccessStatusCode();

        var badLogin = new LoginDto { Email = register.Email, Password = "WrongPass" };
        var resp = await client.PostAsJsonAsync("/api/auth/login", badLogin);

        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var content = await resp.Content.ReadAsStringAsync();
        content.Should().Contain("Wrong password");
    }

    [Fact]
    public async Task ChangePassword_Workflow_Succeeds_AndKeepsCurrentSessionActive()
    {
        var client = _factory.CreateClient();
        var (email, currentPassword, token) = await CreateAuthenticatedUserAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var newPassword = "NewPass2!";
        var changeResp = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new ChangePasswordRequest
            {
                CurrentPassword = currentPassword,
                NewPassword = newPassword,
                ConfirmNewPassword = newPassword
            });

        changeResp.EnsureSuccessStatusCode();

        var changeResult = await changeResp.Content.ReadFromJsonAsync<ChangePasswordResponse>();
        changeResult.Should().NotBeNull();
        changeResult!.SessionRemainsActive.Should().BeTrue();
        changeResult.Message.Should().Contain("session stays active");

        var meResp = await client.GetAsync("/api/auth/me");
        meResp.EnsureSuccessStatusCode();

        var oldLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = email, Password = currentPassword });
        oldLogin.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var oldLoginText = await oldLogin.Content.ReadAsStringAsync();
        oldLoginText.Should().Contain("Wrong password");

        var newLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = email, Password = newPassword });
        newLogin.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var (email, currentPassword, token) = await CreateAuthenticatedUserAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new ChangePasswordRequest
            {
                CurrentPassword = "WrongPass1!",
                NewPassword = "NewPass2!",
                ConfirmNewPassword = "NewPass2!"
            });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        ReadErrorCode(await response.Content.ReadAsStringAsync()).Should().Be("current_password_incorrect");

        var loginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = email, Password = currentPassword });
        loginResp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ChangePassword_MismatchedConfirmation_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var (_, currentPassword, token) = await CreateAuthenticatedUserAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new ChangePasswordRequest
            {
                CurrentPassword = currentPassword,
                NewPassword = "NewPass2!",
                ConfirmNewPassword = "Different2!"
            });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        ReadErrorCode(await response.Content.ReadAsStringAsync()).Should().Be("password_mismatch");
    }

    [Fact]
    public async Task ChangePassword_WeakPassword_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var (_, currentPassword, token) = await CreateAuthenticatedUserAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new ChangePasswordRequest
            {
                CurrentPassword = currentPassword,
                NewPassword = "short",
                ConfirmNewPassword = "short"
            });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        ReadErrorCode(await response.Content.ReadAsStringAsync()).Should().Be("password_too_weak");
    }

    [Fact]
    public async Task ChangePassword_WithoutJwt_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new ChangePasswordRequest
            {
                CurrentPassword = "OldPass1!",
                NewPassword = "NewPass2!",
                ConfirmNewPassword = "NewPass2!"
            });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    private static async Task<(string Email, string Password, string Token)> CreateAuthenticatedUserAsync(HttpClient client)
    {
        var email = $"change-password+{System.Guid.NewGuid():N}@example.com";
        const string password = "OldPass1!";

        var register = new RegisterDto
        {
            Email = email,
            Password = password,
            FullName = "Password Change User"
        };

        var regResp = await client.PostAsJsonAsync("/api/auth/register", register);
        if (!regResp.IsSuccessStatusCode)
        {
            var text = await regResp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Register failed: {regResp.StatusCode}: {text}");
        }

        var loginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = email, Password = password });
        if (!loginResp.IsSuccessStatusCode)
        {
            var text = await loginResp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Login failed: {loginResp.StatusCode}: {text}");
        }

        var auth = await loginResp.Content.ReadFromJsonAsync<AuthResponseDto>();
        auth.Should().NotBeNull();
        auth!.Token.Should().NotBeNullOrWhiteSpace();

        return (email, password, auth.Token);
    }

    private static string ReadErrorCode(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);

        if (document.RootElement.TryGetProperty("code", out var lowerCode))
        {
            return lowerCode.GetString() ?? string.Empty;
        }

        if (document.RootElement.TryGetProperty("Code", out var upperCode))
        {
            return upperCode.GetString() ?? string.Empty;
        }

        return string.Empty;
    }
}
