using System;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Http.Json;
using BackendServer.Features.Auth.DTOs;
using BackendServer.Features.Courses.DTOs;
using BackendServer.Shared.Data;
using BackendServer.Shared.Data.Entities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace BackendServer.Tests;

public class CoursesIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CoursesIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(string Token, string Email)> RegisterAndLogin(HttpClient client, UserRole role)
    {
        var email = $"{role.ToString().ToLower()}_{System.Guid.NewGuid():N}@example.com";
        var register = new RegisterDto
        {
            Email = email,
            Password = "Passw0rd!",
            FullName = $"{role} User",
            Role = role
        };

        var regResp = await client.PostAsJsonAsync("/api/auth/register", register);
        regResp.EnsureSuccessStatusCode();

        var login = new LoginDto { Email = email, Password = "Passw0rd!" };
        var loginResp = await client.PostAsJsonAsync("/api/auth/login", login);
        loginResp.EnsureSuccessStatusCode();

        var auth = await loginResp.Content.ReadFromJsonAsync<AuthResponseDto>();
        return (auth!.Token, email);
    }

    [Fact]
    public async Task Teacher_Can_Create_And_Retrieve_Course()
    {
        var client = _factory.CreateClient();
        var (token, _) = await RegisterAndLogin(client, UserRole.Teacher);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createDto = new CreateCourseDto { Code = "CS101", Name = "Intro to CS" };
        var createResp = await client.PostAsJsonAsync("/api/courses", createDto);
        createResp.EnsureSuccessStatusCode();

        var createdCourse = await createResp.Content.ReadFromJsonAsync<Course>();
        createdCourse.Should().NotBeNull();
        createdCourse!.Code.Should().Be("CS101");

        var getResp = await client.GetAsync("/api/courses");
        getResp.EnsureSuccessStatusCode();
        var courses = await getResp.Content.ReadFromJsonAsync<List<object>>();
        courses.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Student_Cannot_Create_Course_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        var (token, _) = await RegisterAndLogin(client, UserRole.Student);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createDto = new CreateCourseDto { Code = "CS102", Name = "Hacking 101" };
        var resp = await client.PostAsJsonAsync("/api/courses", createDto);

        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Student_Cannot_Access_Teacher_Courses_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        var (token, _) = await RegisterAndLogin(client, UserRole.Student);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync("/api/courses");
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Teacher_Can_Enroll_Students_Bulk()
    {
        var client = _factory.CreateClient();
        var (token, _) = await RegisterAndLogin(client, UserRole.Teacher);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createDto = new CreateCourseDto { Code = "CS201", Name = "Advanced CS" };
        var createResp = await client.PostAsJsonAsync("/api/courses", createDto);
        var course = await createResp.Content.ReadFromJsonAsync<Course>();

        var enrollDto = new EnrollDto 
        { 
            Emails = new List<string> { "student1@test.com", "student2@test.com" } 
        };
        var enrollResp = await client.PostAsJsonAsync($"/api/courses/{course!.Id}/enroll", enrollDto);
        enrollResp.EnsureSuccessStatusCode();

        var rosterResp = await client.GetAsync($"/api/courses/{course.Id}/roster");
        rosterResp.EnsureSuccessStatusCode();

        var rosterJson = await rosterResp.Content.ReadAsStringAsync();
        rosterJson.Should().Contain("student1@test.com");
        rosterJson.Should().Contain("student2@test.com");
    }

    [Fact]
    public async Task Enroll_Duplicate_Emails_Handled_Gracefully()
    {
        var client = _factory.CreateClient();
        var (token, _) = await RegisterAndLogin(client, UserRole.Teacher);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createDto = new CreateCourseDto { Code = "CS301", Name = "Dupes 101" };
        var createResp = await client.PostAsJsonAsync("/api/courses", createDto);
        var course = await createResp.Content.ReadFromJsonAsync<Course>();

        var enrollDto = new EnrollDto 
        { 
            Emails = new List<string> { "dupe@test.com", "dupe@test.com", "unique@test.com" } 
        };
        var enrollResp = await client.PostAsJsonAsync($"/api/courses/{course!.Id}/enroll", enrollDto);
        enrollResp.EnsureSuccessStatusCode();

        var rosterResp = await client.GetAsync($"/api/courses/{course.Id}/roster");
        var rosterJson = await rosterResp.Content.ReadAsStringAsync();

        var roster = await rosterResp.Content.ReadFromJsonAsync<List<RosterItemDto>>();
        roster.Should().HaveCount(2);
        roster.Should().ContainSingle(x => x.Email == "dupe@test.com");
    }

    [Fact]
    public async Task Enroll_Invalid_Emails_Ignored()
    {
        var client = _factory.CreateClient();
        var (token, _) = await RegisterAndLogin(client, UserRole.Teacher);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createDto = new CreateCourseDto { Code = "CS401", Name = "Invalid Emails 101" };
        var createResp = await client.PostAsJsonAsync("/api/courses", createDto);
        var course = await createResp.Content.ReadFromJsonAsync<Course>();

        var enrollDto = new EnrollDto 
        { 
            Emails = new List<string> { "valid@test.com", "not-an-email", "missing-at.com", "@missing-user.com" } 
        };
        var enrollResp = await client.PostAsJsonAsync($"/api/courses/{course!.Id}/enroll", enrollDto);
        enrollResp.EnsureSuccessStatusCode();

        var rosterResp = await client.GetAsync($"/api/courses/{course.Id}/roster");
        var roster = await rosterResp.Content.ReadFromJsonAsync<List<RosterItemDto>>();
        
        roster.Should().HaveCount(1);
        roster.Should().ContainSingle(x => x.Email == "valid@test.com");
    }

    [Fact]
    public async Task Student_Materials_List_Respects_Visibility_And_Enrollment()
    {
        var teacherClient = _factory.CreateClient();
        var studentClient = _factory.CreateClient();
        var outsiderClient = _factory.CreateClient();

        var (teacherToken, _) = await RegisterAndLogin(teacherClient, UserRole.Teacher);
        var (studentToken, studentEmail) = await RegisterAndLogin(studentClient, UserRole.Student);
        var (outsiderToken, _) = await RegisterAndLogin(outsiderClient, UserRole.Student);

        teacherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", teacherToken);
        studentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", studentToken);
        outsiderClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderToken);

        var createResp = await teacherClient.PostAsJsonAsync(
            "/api/courses",
            new CreateCourseDto { Code = $"CS{System.Guid.NewGuid():N}"[..8], Name = "Materials Visibility" });
        createResp.EnsureSuccessStatusCode();

        var course = await createResp.Content.ReadFromJsonAsync<Course>();
        course.Should().NotBeNull();

        var enrollResp = await teacherClient.PostAsJsonAsync(
            $"/api/courses/{course!.Id}/enroll",
            new EnrollDto { Emails = new List<string> { studentEmail } });
        enrollResp.EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Materials.AddRange(
                new Material
                {
                    CourseId = course.Id,
                    Title = "Visible material",
                    OriginalFileName = "visible.pdf",
                    StoragePath = "materials/visible.pdf",
                    Week = 1,
                    IsVisible = true,
                    UploadedAt = DateTime.UtcNow
                },
                new Material
                {
                    CourseId = course.Id,
                    Title = "Released material",
                    OriginalFileName = "released.pdf",
                    StoragePath = "materials/released.pdf",
                    Week = 1,
                    IsVisible = false,
                    ReleaseAt = DateTime.UtcNow.AddMinutes(-5),
                    UploadedAt = DateTime.UtcNow
                },
                new Material
                {
                    CourseId = course.Id,
                    Title = "Future hidden material",
                    OriginalFileName = "future.pdf",
                    StoragePath = "materials/future.pdf",
                    Week = 1,
                    IsVisible = false,
                    ReleaseAt = DateTime.UtcNow.AddHours(1),
                    UploadedAt = DateTime.UtcNow
                });

            await db.SaveChangesAsync();
        }

        var studentMaterialsResp = await studentClient.GetAsync($"/api/courses/{course.Id}/materials");
        studentMaterialsResp.EnsureSuccessStatusCode();

        var materials = await studentMaterialsResp.Content.ReadFromJsonAsync<List<MaterialListItemDto>>();
        materials.Should().NotBeNull();
        materials!.Should().ContainSingle(material => material.Title == "Visible material");
        materials.Should().ContainSingle(material => material.Title == "Released material");
        materials.Should().NotContain(material => material.Title == "Future hidden material");

        var outsiderMaterialsResp = await outsiderClient.GetAsync($"/api/courses/{course.Id}/materials");
        outsiderMaterialsResp.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    private class RosterItemDto
    {
        public string Email { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
    }

    private class MaterialListItemDto
    {
        public string Title { get; set; } = string.Empty;
    }
}
