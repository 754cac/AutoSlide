using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace PowerPointSharing
{
    public class PresentationApiClient
    {
        private readonly HttpClient _httpClient;

        public PresentationApiClient(string backendBaseUrl)
        {
            _httpClient = new HttpClient { BaseAddress = new Uri(backendBaseUrl) };
        }

        public void SetToken(string token)
        {
            _httpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        public async Task<string?> LoginAsync(string email, string password)
        {
            var response = await _httpClient.PostAsJsonAsync("api/auth/login", new { email, password });
            if (!response.IsSuccessStatusCode) return null;

            var loginResult = await response.Content.ReadFromJsonAsync<AuthenticationResponse>();
            return loginResult?.Token;
        }

        public async Task<List<CourseInfo>> GetCoursesAsync()
        {
            return await _httpClient.GetFromJsonAsync<List<CourseInfo>>("api/courses")
                ?? new List<CourseInfo>();
        }

        public async Task<SessionCreationResult?> StartSessionAsync(string presentationTitle, int slideCount, Guid? courseId)
        {
            var requestPayload = new { PresentationTitle = presentationTitle, SlideCount = slideCount, CourseId = courseId };
            var response = await _httpClient.PostAsJsonAsync("api/sessions/create", requestPayload);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SessionCreationResult>();
        }
        public class AuthenticationResponse 
        { 
            public string Token { get; set; } = string.Empty;
        }

        public class CourseInfo 
        { 
            public Guid Id { get; set; } 
            public string Name { get; set; } = string.Empty;
            public string Code { get; set; } = string.Empty;
        }

        public class SessionCreationResult 
        { 
            public string SessionId { get; set; } = string.Empty;
            public string PresenterToken { get; set; } = string.Empty;
        }
    }
}
