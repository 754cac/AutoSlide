using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace PowerPointSharing
{
    public class ContractCompatibilityTests
    {
        [Fact]
        public async Task UploadMultipartFields_Are_Unchanged()
        {
            var handler = new RecordingHttpHandler();
            var client = new HttpClient(handler);
            var gateway = new PresentationApiGateway(client);

            string pptPath = Path.GetTempFileName() + ".pptx";
            string pdfPath = Path.GetTempFileName() + ".pdf";
            await File.WriteAllBytesAsync(pptPath, Encoding.UTF8.GetBytes("ppt"));
            await File.WriteAllBytesAsync(pdfPath, Encoding.UTF8.GetBytes("pdf"));

            try
            {
                var response = await gateway.UploadPresentationAndRegisterAsync(new UploadPresentationRequest
                {
                    BackendBaseUrl = "http://localhost:5012",
                    PresentationPath = pptPath,
                    PresentationName = "deck",
                    TotalSlides = 7,
                    CourseId = "course-1",
                    FlatPdfPath = pdfPath,
                    SlideAnimationMap = new Dictionary<int, List<int>>
                    {
                        { 1, new List<int> { 1, 2 } }
                    },
                    FrameDescriptors = new List<DeckFrameDescriptor>
                    {
                        new DeckFrameDescriptor
                        {
                            FrameIndex = 1,
                            ExportFrameIndex = 1,
                            OriginalSlideIndex = 1,
                            ClickIndex = 0,
                            IsBoundary = false,
                            BoundaryAfterSlideIndex = null
                        }
                    },
                    AuthToken = "jwt-token"
                });

                response.Should().NotBeNull();
                response.PresentationId.Should().Be("pres-1");

                handler.Method.Should().Be(HttpMethod.Post);
                handler.Url.Should().NotBeNull();
                handler.Url.AbsolutePath.Should().Be("/api/upload");
                handler.Url.Query.Should().Contain("name=deck");
                handler.Url.Query.Should().Contain("totalSlides=7");
                handler.Url.Query.Should().Contain("frameMap=");
                handler.Url.Query.Should().Contain("courseId=course-1");

                handler.Authorization.Should().Be("Bearer jwt-token");

                handler.PartNames.Should().Contain("file");
                handler.PartNames.Should().Contain("flatPdf");
                handler.PartNames.Should().Contain("frameDescriptors");

                handler.FrameDescriptorsBody.Should().Contain("\"FrameIndex\":1");
                handler.FrameDescriptorsBody.Should().Contain("\"ExportFrameIndex\":1");
                handler.FrameDescriptorsBody.Should().Contain("\"OriginalSlideIndex\":1");
                handler.FrameDescriptorsBody.Should().Contain("\"ClickIndex\":0");
                handler.FrameDescriptorsBody.Should().Contain("\"IsBoundary\":false");
            }
            finally
            {
                if (File.Exists(pptPath)) File.Delete(pptPath);
                if (File.Exists(pdfPath)) File.Delete(pdfPath);
            }
        }

        [Fact]
        public void ViewerUrl_Format_Is_Unchanged()
        {
            var url = ViewerUrlBuilder.Build(
                "http://frontend.local/",
                "presentation-123",
                "presentation-123",
                "http://backend/viewer");

            url.Should().Be("http://frontend.local/viewer/presentation-123?sessionId=presentation-123");
        }

        [Fact]
        public void UnlockSlide_And_AdvanceSlide_Payloads_Are_Unchanged()
        {
            PresentationApiGateway.BuildUnlockSlideJson("pres-7", 42)
                .Should().Be("{\"presentationId\":\"pres-7\", \"slide\":42}");

            PresentationApiGateway.BuildAdvanceSlideJson(99)
                .Should().Be("{\"newIndex\":99}");
        }

        [Fact]
        public void SignalR_GroupId_Usage_Remains_ActiveSignalRGroupId()
        {
            var sessionManagerSource = File.ReadAllText(FindSourcePath("PowerPointSharing", "Services", "SessionManager.cs"));
            var viewerBuilderSource = File.ReadAllText(FindSourcePath("PowerPointSharing", "Services", "Runtime", "ViewerUrlBuilder.cs"));

            sessionManagerSource.Should().Contain("ConnectAsync(_activeSignalRGroupId)");
            sessionManagerSource.Should().Contain("_activeSignalRGroupId");

            viewerBuilderSource.Should().Contain("?sessionId=");
            viewerBuilderSource.Should().Contain("activeSignalRGroupId");
        }

        [Fact]
        public void FrameNumbering_For_DeckIndex_Is_Unchanged_For_Same_Deck_Metadata()
        {
            var deckIndex = BuildSampleDeckIndex();

            deckIndex.ResolveExportFrameForSlideClick(1, 0).Should().Be(1);
            deckIndex.ResolveExportFrameForSlideClick(1, 1).Should().Be(2);
            deckIndex.ResolveExportFrameForSlideClick(2, 0).Should().Be(3);
            deckIndex.ResolveExportFrameForSlideClick(2, 1).Should().Be(4);
        }

        private static DeckIndex BuildSampleDeckIndex()
        {
            var builder = new DeckIndexBuilder();
            return builder.Build(
                new[]
                {
                    new DeckFrameDescriptor { FrameIndex = 1, ExportFrameIndex = 1, OriginalSlideIndex = 1, ClickIndex = 0, IsBoundary = false },
                    new DeckFrameDescriptor { FrameIndex = 2, ExportFrameIndex = 2, OriginalSlideIndex = 1, ClickIndex = 1, IsBoundary = false },
                    new DeckFrameDescriptor { FrameIndex = 3, ExportFrameIndex = null, OriginalSlideIndex = 1, ClickIndex = null, IsBoundary = true, BoundaryAfterSlideIndex = 1 },
                    new DeckFrameDescriptor { FrameIndex = 4, ExportFrameIndex = 3, OriginalSlideIndex = 2, ClickIndex = 0, IsBoundary = false },
                    new DeckFrameDescriptor { FrameIndex = 5, ExportFrameIndex = 4, OriginalSlideIndex = 2, ClickIndex = 1, IsBoundary = false }
                },
                new Dictionary<int, List<int>>
                {
                    { 1, new List<int> { 1, 2 } },
                    { 2, new List<int> { 4, 5 } }
                });
        }

        private static string FindSourcePath(params string[] relativePathSegments)
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; depth < 10 && current != null; depth++, current = current.Parent)
            {
                var candidate = Path.Combine(new[] { current.FullName }.Concat(relativePathSegments).ToArray());
                if (File.Exists(candidate))
                    return candidate;
            }

            throw new FileNotFoundException("Could not locate source file from test output directory.");
        }

        private sealed class RecordingHttpHandler : HttpMessageHandler
        {
            public HttpMethod Method { get; private set; }
            public Uri Url { get; private set; }
            public string Authorization { get; private set; }
            public string RequestBody { get; private set; }
            public string FrameDescriptorsBody { get; private set; }
            public List<string> PartNames { get; } = new List<string>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Method = request.Method;
                Url = request.RequestUri;
                Authorization = request.Headers.Authorization != null
                    ? request.Headers.Authorization.Scheme + " " + request.Headers.Authorization.Parameter
                    : null;
                RequestBody = request.Content != null
                    ? await request.Content.ReadAsStringAsync().ConfigureAwait(false)
                    : null;

                if (request.Content is MultipartContent multipart)
                {
                    foreach (var part in multipart)
                    {
                        var partName = part.Headers.ContentDisposition?.Name?.Trim('"');
                        if (!string.IsNullOrEmpty(partName))
                            PartNames.Add(partName);

                        if (string.Equals(partName, "frameDescriptors", StringComparison.OrdinalIgnoreCase))
                            FrameDescriptorsBody = await part.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"presentationId\":\"pres-1\",\"presenterToken\":\"pt\",\"sessionId\":\"pres-1\",\"viewerUrl\":\"http://viewer\"}",
                        Encoding.UTF8,
                        "application/json")
                };
            }
        }
    }
}
