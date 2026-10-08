using System.Net;
using System.Text;
using ApiaryCopilot.Api.Frontend;
using ApiaryCopilot.Api.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace ApiaryCopilot.UnitTests;

// The built PWA is served from the web root (D-5); the web root is faked, so nothing touches the disk.
public sealed class FrontendExtensionsTests
{
    private const string IndexHtml = "<!doctype html><title>Apiary-Keeper</title>";

    [Fact]
    public async Task UseFrontend_ShouldServeIndexHtml_WhenRootIsRequested()
    {
        await using var app = await StartAsync(("index.html", IndexHtml));

        using var response = await app.GetTestClient().GetAsync(new Uri("/", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(IndexHtml);
    }

    // An unknown path stays 404, not the app shell, so a removed or mistyped endpoint is not masked.
    [Fact]
    public async Task UseFrontend_ShouldReturn404_WhenPathMatchesNoFileOrEndpoint()
    {
        await using var app = await StartAsync(("index.html", IndexHtml));

        using var response = await app.GetTestClient().GetAsync(new Uri("/weatherforecast", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UseFrontend_ShouldServeManifestAsWebManifest_WhenManifestIsRequested()
    {
        await using var app = await StartAsync(("index.html", IndexHtml), ("manifest.webmanifest", "{}"));

        using var response = await app.GetTestClient().GetAsync(new Uri("/manifest.webmanifest", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/manifest+json");
    }

    [Fact]
    public async Task UseFrontend_ShouldReturn404_WhenMissingFileHasExtension()
    {
        await using var app = await StartAsync(("index.html", IndexHtml));

        using var response = await app.GetTestClient().GetAsync(new Uri("/assets/missing.js", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UseFrontend_ShouldLeaveHealthToItsEndpoint_WhenHealthIsRequested()
    {
        await using var app = await StartAsync(("index.html", IndexHtml));

        using var response = await app.GetTestClient().GetAsync(new Uri(HealthExtensions.Path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"version\"");
    }

    [Fact]
    public async Task UseFrontend_ShouldReturn404_WhenNoFrontendIsBuilt()
    {
        await using var app = await StartAsync();

        using var response = await app.GetTestClient().GetAsync(new Uri("/", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<WebApplication> StartAsync(params (string Name, string Content)[] files)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        var app = builder.Build();
        app.Environment.WebRootFileProvider = FakeWebRoot(files);

        app.UseFrontend();
        app.MapHealth();

        await app.StartAsync();
        return app;
    }

    private static IFileProvider FakeWebRoot((string Name, string Content)[] files)
    {
        var provider = A.Fake<IFileProvider>();
        A.CallTo(() => provider.GetFileInfo(A<string>._)).ReturnsLazily((string path) =>
        {
            var match = files.FirstOrDefault(f => "/" + f.Name == path);
            return match.Name is null ? new NotFoundFileInfo(path) : FakeFile(match.Name, match.Content);
        });
        var root = A.Fake<IDirectoryContents>();
        A.CallTo(() => root.Exists).Returns(files.Length > 0);
        A.CallTo(() => provider.GetDirectoryContents(A<string>._)).ReturnsLazily((string path) =>
            path is "" or "/" ? root : NotFoundDirectoryContents.Singleton);
        A.CallTo(() => provider.Watch(A<string>._)).Returns(NullChangeToken.Singleton);
        return provider;
    }

    private static IFileInfo FakeFile(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var file = A.Fake<IFileInfo>();
        A.CallTo(() => file.Exists).Returns(true);
        A.CallTo(() => file.Name).Returns(name);
        A.CallTo(() => file.Length).Returns(bytes.Length);
        A.CallTo(() => file.IsDirectory).Returns(false);
        A.CallTo(() => file.PhysicalPath).Returns(null);
        A.CallTo(() => file.LastModified).Returns(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));
        A.CallTo(() => file.CreateReadStream()).ReturnsLazily(() => new MemoryStream(bytes));
        return file;
    }
}
