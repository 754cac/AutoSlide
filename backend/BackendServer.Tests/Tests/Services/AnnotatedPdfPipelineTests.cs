using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BackendServer.Features.Classroom.Services;
using BackendServer.Features.Materials.Services;
using BackendServer.Shared.Services;
using FluentAssertions;
using iText.Kernel.Colors;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PageSize = iText.Kernel.Geom.PageSize;

namespace BackendServer.Tests.Tests.Services;

public sealed class AnnotatedPdfPipelineTests
{
    [Fact]
    public async Task FinalAnnotatedPdf_UsesCanonicalInkArtifacts_AndAppendsSolutions()
    {
        var sessionId = Guid.NewGuid();
        var presentationId = sessionId.ToString("N");
        var uploadsRoot = Path.Combine(AppContext.BaseDirectory, "uploads");
        var basePdfPath = Path.Combine(uploadsRoot, presentationId + ".pdf");
        var inkArtifactDir = Path.Combine(uploadsRoot, sessionId.ToString("D"), "ink-artifacts");
        var solutionArtifactDir = Path.Combine(uploadsRoot, sessionId.ToString("D"), "solutions");

        try
        {
            Directory.CreateDirectory(uploadsRoot);
            Directory.CreateDirectory(inkArtifactDir);
            Directory.CreateDirectory(solutionArtifactDir);

            CreateSinglePagePdf(basePdfPath);
            await File.WriteAllBytesAsync(Path.Combine(inkArtifactDir, "slide_001.png"), CreateTinyPng());

            var storage = new FakeStorageService();
            var solutionPageService = new SolutionPageService(storage);
            var pdfService = new PdfService();

            var solutionPage = await solutionPageService.CreateSolutionPageAsync(
                sessionId,
                new CreateSolutionPageServiceRequest
                {
                    Kind = "blank",
                    OrderIndex = 1
                });

            await solutionPageService.UpdateSolutionArtifactAsync(
                sessionId,
                solutionPage.SolutionPageId,
                CreateTinyPng(),
                hasInk: true);

            var annotatedBytes = await pdfService.GetAnnotatedPdfBytesAsync(presentationId);
            annotatedBytes.Should().NotBeNull();
            annotatedBytes!.Length.Should().BeGreaterThan(0);

            var finalBytes = await solutionPageService.AppendSolutionsToDeckPdfAsync(sessionId, annotatedBytes, skipEmptyPages: true);
            finalBytes.Should().NotBeNull();
            finalBytes.Length.Should().BeGreaterThan(annotatedBytes.Length);

            using var reader = new PdfReader(new MemoryStream(finalBytes));
            using var pdf = new PdfDocument(reader);

            pdf.GetNumberOfPages().Should().Be(2);

            var firstPageResources = pdf.GetPage(1).GetResources().GetResource(PdfName.XObject) as PdfDictionary;
            firstPageResources.Should().NotBeNull();
            firstPageResources!.KeySet().Should().NotBeEmpty();
            firstPageResources.KeySet().Any(key =>
            {
                var xObject = firstPageResources.GetAsStream(key);
                return xObject != null && PdfName.Image.Equals(xObject.GetAsName(PdfName.Subtype));
            }).Should().BeTrue();
        }
        finally
        {
            TryDeleteDirectory(Path.Combine(uploadsRoot, sessionId.ToString("D")));
            TryDeleteFile(basePdfPath);
        }
    }

    [Fact]
    public async Task AnnotatedAndInkOnlyArtifacts_RemainSeparate_ByContentAndStoragePath()
    {
        var sessionId = Guid.NewGuid();
        var presentationId = sessionId.ToString("N");
        var uploadsRoot = Path.Combine(AppContext.BaseDirectory, "uploads");
        var basePdfPath = Path.Combine(uploadsRoot, presentationId + ".pdf");
        var inkArtifactDir = Path.Combine(uploadsRoot, sessionId.ToString("D"), "ink-artifacts");

        try
        {
            Directory.CreateDirectory(uploadsRoot);
            Directory.CreateDirectory(inkArtifactDir);

            CreateSinglePagePdf(basePdfPath);
            await File.WriteAllBytesAsync(Path.Combine(inkArtifactDir, "slide_001.png"), CreateTinyPng());

            var storage = new FakeStorageService();
            var solutionPageService = new SolutionPageService(storage);
            var pdfService = new PdfService();
            var inkOnlyExporter = new InkArtifactExportService(
                storage,
                solutionPageService,
                NullLogger<InkArtifactExportService>.Instance);

            var annotatedBytes = await pdfService.GetAnnotatedPdfBytesAsync(presentationId);
            annotatedBytes.Should().NotBeNull();
            annotatedBytes!.Length.Should().BeGreaterThan(0);

            var inkOnlyResult = await inkOnlyExporter.GenerateAndUploadAsync(sessionId);
            inkOnlyResult.Generated.Should().BeTrue();
            inkOnlyResult.LocalPath.Should().NotBeNull();
            inkOnlyResult.StoragePath.Should().Be($"{sessionId:D}/exports/ink_only_artifact.pdf");
            inkOnlyResult.StoragePath.Should().NotBe($"{sessionId:D}/exports/annotated.pdf");

            var inkOnlyBytes = await File.ReadAllBytesAsync(inkOnlyResult.LocalPath!);
            inkOnlyBytes.Should().NotBeNull();
            inkOnlyBytes.Length.Should().BeGreaterThan(0);

            inkOnlyBytes.Should().NotEqual(annotatedBytes);
        }
        finally
        {
            TryDeleteDirectory(Path.Combine(uploadsRoot, sessionId.ToString("D")));
            TryDeleteFile(basePdfPath);
        }
    }

    private static void CreateSinglePagePdf(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        var page = pdf.AddNewPage(new PageSize(200, 200));
        var canvas = new PdfCanvas(page);
        canvas.SetFillColor(new DeviceRgb(232, 236, 255));
        canvas.Rectangle(0, 0, 200, 200);
        canvas.Fill();
    }

    private static byte[] CreateTinyPng()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ncKkAAAAASUVORK5CYII=");
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed class FakeStorageService : IStorageService
    {
        public Task<string> UploadAsync(Stream stream, string storagePath, string contentType)
        {
            return Task.FromResult(storagePath);
        }

        public Task<string> GetSignedUrlAsync(string storagePath, int expirySeconds = 300)
        {
            return Task.FromResult($"https://example.test/{Uri.EscapeDataString(storagePath)}");
        }

        public Task DeleteAsync(string storagePath)
        {
            return Task.CompletedTask;
        }

        public Task<string> GetSignedDownloadUrlAsync(string bucket, string path, string downloadFileName, int expirySeconds = 3600)
        {
            return Task.FromResult($"https://example.test/{bucket}/{Uri.EscapeDataString(path)}?download={Uri.EscapeDataString(downloadFileName)}");
        }

        public Task<string?> GetSignedDownloadUrlIfExistsAsync(string bucket, string path, string downloadFileName, int expirySeconds = 3600)
        {
            return Task.FromResult<string?>(
                $"https://example.test/{bucket}/{Uri.EscapeDataString(path)}?download={Uri.EscapeDataString(downloadFileName)}");
        }

        public Task<string> UploadToBucketAsync(Stream stream, string bucket, string storagePath, string contentType)
        {
            return Task.FromResult(storagePath);
        }
    }
}
