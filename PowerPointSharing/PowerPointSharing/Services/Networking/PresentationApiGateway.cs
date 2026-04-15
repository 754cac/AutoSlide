using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace PowerPointSharing
{
    internal sealed class UploadPresentationRequest
    {
        public string BackendBaseUrl { get; set; } = string.Empty;
        public string PresentationPath { get; set; } = string.Empty;
        public string PresentationName { get; set; } = string.Empty;
        public int TotalSlides { get; set; }
        public string? CourseId { get; set; }
        public string? FlatPdfPath { get; set; }
        public Dictionary<int, List<int>>? SlideAnimationMap { get; set; }
        public List<DeckFrameDescriptor>? FrameDescriptors { get; set; }
        public string? AuthToken { get; set; }
    }

    internal sealed class UploadPresentationResponse
    {
        public string PresentationId { get; set; } = string.Empty;
        public string PresenterToken { get; set; } = string.Empty;
        public string ViewerUrl { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
    }

    internal sealed class CreateSolutionPageResponse
    {
        public bool Success { get; set; }
        public string SolutionPageId { get; set; } = string.Empty;
    }

    internal sealed class AnnotatedPptxUploadResponse
    {
        public bool Success { get; set; }
        public string StoragePath { get; set; } = string.Empty;
    }

    internal sealed class PresentationApiGateway
    {
        private readonly HttpClient _httpClient;

        public PresentationApiGateway(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<UploadPresentationResponse?> UploadPresentationAndRegisterAsync(UploadPresentationRequest? request)
        {
            if (request == null)
                return null;

            if (string.IsNullOrEmpty(request.PresentationPath) || !File.Exists(request.PresentationPath))
                return null;

            var url = request.BackendBaseUrl.TrimEnd('/') + "/api/upload?name="
                + Uri.EscapeDataString(request.PresentationName ?? "presentation")
                + "&totalSlides=" + request.TotalSlides;

            if (request.SlideAnimationMap != null && request.SlideAnimationMap.Count > 0)
            {
                var frameMapJson = System.Text.Json.JsonSerializer.Serialize(request.SlideAnimationMap);
                url += "&frameMap=" + Uri.EscapeDataString(frameMapJson);
            }

            if (!string.IsNullOrEmpty(request.CourseId))
                url += "&courseId=" + Uri.EscapeDataString(request.CourseId);

            using var content = new MultipartFormDataContent();

            if (request.FrameDescriptors != null && request.FrameDescriptors.Count > 0)
            {
                var frameDescriptorsJson = System.Text.Json.JsonSerializer.Serialize(request.FrameDescriptors);
                content.Add(new StringContent(frameDescriptorsJson, Encoding.UTF8, "application/json"), "frameDescriptors");
            }

            var pptStream = new FileStream(request.PresentationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var pptContent = new StreamContent(pptStream);
            var extension = Path.GetExtension(request.PresentationPath)?.ToLowerInvariant();
            var pptMime = extension == ".ppt"
                ? "application/vnd.ms-powerpoint"
                : "application/vnd.openxmlformats-officedocument.presentationml.presentation";
            pptContent.Headers.ContentType = new MediaTypeHeaderValue(pptMime);
            content.Add(pptContent, "file", Path.GetFileName(request.PresentationPath));

            if (!string.IsNullOrEmpty(request.FlatPdfPath) && File.Exists(request.FlatPdfPath))
            {
                var pdfStream = new FileStream(request.FlatPdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                var pdfContent = new StreamContent(pdfStream);
                pdfContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                content.Add(pdfContent, "flatPdf", Path.GetFileName(request.FlatPdfPath));
            }

            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };

            if (!string.IsNullOrEmpty(request.AuthToken))
                requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.AuthToken);

            var response = await _httpClient.SendAsync(requestMessage).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            var presentationId = ExtractJson(body, "presentationId");
            var presenterToken = ExtractJson(body, "presenterToken");
            var sessionId = ExtractJson(body, "sessionId");
            var viewer = ExtractJson(body, "viewerUrl");
            var pdfAbsolute = ExtractJson(body, "pdfAbsolute");

            if (!string.IsNullOrEmpty(pdfAbsolute))
            {
                var pdfAbsoluteValue = pdfAbsolute!;
                if (string.IsNullOrEmpty(viewer))
                {
                    viewer = pdfAbsoluteValue;
                }
                else
                {
                    var viewerValue = viewer!;
                    if (!viewerValue.Contains("pdf="))
                    {
                        var separator = viewerValue.Contains("?") ? "&" : "?";
                        viewer = viewerValue + separator + "pdf=" + Uri.EscapeDataString(pdfAbsoluteValue);
                    }
                }
            }

            if (string.IsNullOrEmpty(presentationId) || string.IsNullOrEmpty(presenterToken) || string.IsNullOrEmpty(sessionId))
                return null;

            return new UploadPresentationResponse
            {
                PresentationId = presentationId ?? string.Empty,
                PresenterToken = presenterToken ?? string.Empty,
                ViewerUrl = viewer ?? string.Empty,
                SessionId = sessionId ?? string.Empty
            };
        }

        public async Task StartSessionAsync(string backendBaseUrl, string presentationId, string? presenterToken)
        {
            var url = backendBaseUrl.TrimEnd('/') + "/api/" + presentationId + "/start";
            var json = "{\"state\":\"running\"}";

            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                if (!string.IsNullOrEmpty(presenterToken)) wc.Headers["X-Presenter-Token"] = presenterToken;
                await wc.UploadStringTaskAsync(url, json).ConfigureAwait(false);
            }
        }

        public async Task UnlockSlideAsync(string backendBaseUrl, string presentationId, string? presenterToken, int slide)
        {
            var url = backendBaseUrl.TrimEnd('/') + "/api/sessions/unlock-slide";
            var json = BuildUnlockSlideJson(presentationId, slide);
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                if (!string.IsNullOrEmpty(presenterToken)) wc.Headers["X-Presenter-Token"] = presenterToken;
                await wc.UploadStringTaskAsync(url, json).ConfigureAwait(false);
            }
        }

        public async Task AdvanceSlideAsync(string backendBaseUrl, string presentationId, string? presenterToken, int frameIndex)
        {
            var url = backendBaseUrl.TrimEnd('/') + "/api/sessions/" + presentationId + "/advance-slide";
            var json = BuildAdvanceSlideJson(frameIndex);
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                if (!string.IsNullOrEmpty(presenterToken)) wc.Headers["X-Presenter-Token"] = presenterToken;
                await wc.UploadStringTaskAsync(url, json).ConfigureAwait(false);
            }
        }

        public async Task EndSessionAsync(
            string backendBaseUrl,
            string presentationId,
            string? presenterToken,
            string? jwtToken)
        {
            var url = backendBaseUrl.TrimEnd('/') + "/api/sessions/end";
            var json = BuildEndSessionJson(presentationId, presenterToken);

            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                if (!string.IsNullOrEmpty(presenterToken)) wc.Headers["X-Presenter-Token"] = presenterToken;
                if (!string.IsNullOrEmpty(presentationId)) wc.Headers["X-Session-Id"] = presentationId;
                if (!string.IsNullOrEmpty(jwtToken)) wc.Headers["Authorization"] = "Bearer " + jwtToken;

                try
                {
                    await wc.UploadStringTaskAsync(url, json).ConfigureAwait(false);
                }
                catch (WebException webException)
                {
                    var response = webException.Response as HttpWebResponse;
                    var statusCode = response != null ? (int)response.StatusCode : 0;
                    if (statusCode == 401 && !string.IsNullOrEmpty(presenterToken))
                    {
                        using (var fallback = new WebClient())
                        {
                            fallback.Encoding = Encoding.UTF8;
                            fallback.Headers[HttpRequestHeader.ContentType] = "application/json";
                            fallback.Headers["X-Presenter-Token"] = presenterToken;
                            var fallbackUrl = url + "?presenterToken=" + Uri.EscapeDataString(presenterToken);
                            await fallback.UploadStringTaskAsync(fallbackUrl, json).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        public async Task UploadInkAsync(string backendBaseUrl, string presentationId, string? presenterToken, int frameIndex, byte[] inkPng)
        {
            var frameUrl = backendBaseUrl.TrimEnd('/') + "/api/sessions/" + presentationId + "/frames/" + frameIndex + "/ink";
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "image/png";
                if (!string.IsNullOrEmpty(presenterToken)) wc.Headers["X-Presenter-Token"] = presenterToken;
                try
                {
                    await wc.UploadDataTaskAsync(frameUrl, "POST", inkPng).ConfigureAwait(false);
                }
                catch (WebException webException)
                {
                    var response = webException.Response as HttpWebResponse;
                    var statusCode = response != null ? (int)response.StatusCode : 0;
                    if (statusCode == 404)
                    {
                        var fallbackUrl = backendBaseUrl.TrimEnd('/') + "/api/sessions/" + presentationId + "/slides/" + frameIndex + "/ink";
                        await wc.UploadDataTaskAsync(fallbackUrl, "POST", inkPng).ConfigureAwait(false);
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }

        public async Task UploadInkArtifactAsync(string backendBaseUrl, string sessionId, string? presenterToken, int slideIndex, byte[] inkPng)
        {
            var url = backendBaseUrl.TrimEnd('/') + "/api/sessions/" + sessionId + "/exports/ink-artifacts/slides/" + slideIndex;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "image/png";
                if (!string.IsNullOrEmpty(presenterToken)) wc.Headers["X-Presenter-Token"] = presenterToken;
                await wc.UploadDataTaskAsync(url, "POST", inkPng).ConfigureAwait(false);
            }
        }

        public async Task<CreateSolutionPageResponse> CreateSolutionPageAsync(
            string backendBaseUrl,
            string sessionId,
            string? presenterToken,
            CreateSolutionPageRequest request)
        {
            if (request == null)
                return new CreateSolutionPageResponse { Success = false };

            var normalizedSessionId = NormalizeSessionId(sessionId);
            var url = backendBaseUrl.TrimEnd('/') + "/api/sessions/" + normalizedSessionId + "/solutions";
            var payload = BuildCreateSolutionPageJson(request);

            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                if (!string.IsNullOrEmpty(presenterToken))
                    wc.Headers["X-Presenter-Token"] = presenterToken;

                var responseBody = await wc.UploadStringTaskAsync(url, payload).ConfigureAwait(false);
                var solutionPageId = ExtractJson(responseBody, "solutionPageId");
                return new CreateSolutionPageResponse
                {
                    Success = !string.IsNullOrEmpty(solutionPageId),
                    SolutionPageId = solutionPageId ?? string.Empty
                };
            }
        }

        public async Task<CreateSolutionPageResponse> UploadSolutionArtifactAsync(
            string backendBaseUrl,
            string sessionId,
            string? presenterToken,
            string solutionPageId,
            byte[] imagePng,
            bool hasInk,
            string kind,
            int? sourceSlideIndex,
            int? orderIndex)
        {
            var normalizedSessionId = NormalizeSessionId(sessionId);
            var normalizedKind = string.Equals(kind, "currentSlide", StringComparison.OrdinalIgnoreCase)
                ? "currentSlide"
                : "blank";
            var hasInkValue = hasInk ? "true" : "false";
            var queryParts = new List<string>
            {
                "hasInk=" + hasInkValue,
                "kind=" + Uri.EscapeDataString(normalizedKind)
            };

            if (sourceSlideIndex.HasValue)
                queryParts.Add("sourceSlideIndex=" + sourceSlideIndex.Value);

            if (orderIndex.HasValue && orderIndex.Value > 0)
                queryParts.Add("orderIndex=" + orderIndex.Value);

            var url = backendBaseUrl.TrimEnd('/') + "/api/sessions/" + normalizedSessionId + "/solutions/" + Uri.EscapeDataString(solutionPageId)
                + "?" + string.Join("&", queryParts);

            using var request = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new ByteArrayContent(imagePng ?? Array.Empty<byte>())
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            if (!string.IsNullOrEmpty(presenterToken))
                request.Headers.TryAddWithoutValidation("X-Presenter-Token", presenterToken);

            var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Solution upload failed with {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
            }

            var returnedId = ExtractJson(body, "solutionPageId");
            return new CreateSolutionPageResponse
            {
                Success = true,
                SolutionPageId = string.IsNullOrWhiteSpace(returnedId) ? solutionPageId : (returnedId ?? solutionPageId)
            };
        }

        public async Task<AnnotatedPptxUploadResponse> UploadAnnotatedPptxAsync(
            string backendBaseUrl,
            string sessionId,
            string? presenterToken,
            byte[] pptxBytes)
        {
            var normalizedSessionId = NormalizeSessionId(sessionId);
            var url = backendBaseUrl.TrimEnd('/') + "/api/sessions/" + normalizedSessionId + "/exports/annotated-pptx";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(pptxBytes ?? Array.Empty<byte>())
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.presentationml.presentation");
            if (!string.IsNullOrEmpty(presenterToken))
                request.Headers.TryAddWithoutValidation("X-Presenter-Token", presenterToken);

            var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Annotated PPTX upload failed with {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
            }

            return new AnnotatedPptxUploadResponse
            {
                Success = string.Equals(ExtractJson(body, "success"), "true", StringComparison.OrdinalIgnoreCase) || body.Contains("\"success\":true"),
                StoragePath = ExtractJson(body, "storagePath") ?? string.Empty
            };
        }

        private static string NormalizeSessionId(string sessionId)
        {
            if (Guid.TryParse(sessionId, out var parsed))
                return parsed.ToString("D");

            return sessionId;
        }

        public async Task DeleteInkArtifactAsync(string backendBaseUrl, string sessionId, string? presenterToken, int slideIndex)
        {
            var url = backendBaseUrl.TrimEnd('/') + "/api/sessions/" + sessionId + "/exports/ink-artifacts/slides/" + slideIndex;
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            if (!string.IsNullOrEmpty(presenterToken))
                request.Headers.TryAddWithoutValidation("X-Presenter-Token", presenterToken);

            var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NotFound)
                response.EnsureSuccessStatusCode();
        }

        public async Task SendTranscriptAsync(string backendBaseUrl, string presentationId, string? presenterToken, string text)
        {
            var url = backendBaseUrl.TrimEnd('/') + "/api/transcript";
            var json = BuildTranscriptJson(presentationId, text);

            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                if (!string.IsNullOrEmpty(presenterToken)) wc.Headers["X-Presenter-Token"] = presenterToken;
                await wc.UploadStringTaskAsync(url, json).ConfigureAwait(false);
            }
        }

        private static string? ExtractJson(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            try
            {
                string pattern = "\\\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"";
                var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
                if (match.Success) return match.Groups[1].Value;
            }
            catch
            {
                return null;
            }

            return null;
        }

        internal static string BuildUnlockSlideJson(string presentationId, int slide)
        {
            return "{\"presentationId\":\"" + presentationId + "\", \"slide\":" + slide + "}";
        }

        internal static string BuildAdvanceSlideJson(int frameIndex)
        {
            return "{\"newIndex\":" + frameIndex + "}";
        }

        internal static string BuildEndSessionJson(string presentationId, string? presenterToken)
        {
            return "{\"presentationId\":\"" + presentationId + "\", \"presenterToken\":\"" + (presenterToken ?? string.Empty) + "\"}";
        }

        internal static string BuildTranscriptJson(string presentationId, string text)
        {
            var safeText = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "{\"presentationId\":\"" + presentationId + "\", \"text\":\"" + safeText + "\"}";
        }

        internal static string BuildCreateSolutionPageJson(CreateSolutionPageRequest request)
        {
            var kind = request.Kind == "currentSlide" ? "currentSlide" : "blank";
            var sourceSlideSegment = request.SourceSlideIndex.HasValue
                ? "\"sourceSlideIndex\":" + request.SourceSlideIndex.Value + ","
                : string.Empty;

            return "{" +
                "\"kind\":\"" + kind + "\"," +
                sourceSlideSegment +
                "\"orderIndex\":" + request.OrderIndex +
                "}";
        }
    }
}
