using System.Net;
using System.Text.Json;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.UnitTests.Api;

// The generated OpenAPI document, from the real host in memory (served in Development only).
public sealed class OpenApiDocumentTests
{
    [Theory] // regression risk: each yard operation is published in the document under its name
    [InlineData("/api/yards", "post", "CreateYard")]
    [InlineData("/api/yards", "get", "ListYards")]
    [InlineData("/api/yards/{id}", "get", "GetYard")]
    [InlineData("/api/yards/{id}", "put", "UpdateYard")]
    public async Task OpenApiDocument_ShouldDescribeTheOperation_WhenTheYardEndpointsAreMapped(string path, string method, string operationId)
    {
        using var document = await GetDocumentAsync();

        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

        operation.GetProperty("operationId").GetString().Should().Be(operationId);
        operation.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        operation.GetProperty("responses").TryGetProperty("503", out _).Should().BeTrue();
    }

    [Theory] // regression risk: each colony operation is published in the document under its name
    [InlineData("/api/colonies", "post", "RegisterColony")]
    [InlineData("/api/colonies/swarm-captures", "post", "CaptureSwarm")]
    [InlineData("/api/colonies", "get", "ListColonies")]
    [InlineData("/api/colonies/{id}", "get", "GetColony")]
    [InlineData("/api/colonies/{id}/events", "get", "ListColonyEvents")]
    [InlineData("/api/colonies/{id}/queens", "get", "ListQueens")]
    [InlineData("/api/colonies/{id}/splits", "post", "SplitColony")]
    [InlineData("/api/colonies/{id}/merges", "post", "MergeColony")]
    [InlineData("/api/colonies/{id}/requeens", "post", "RequeenColony")]
    [InlineData("/api/colonies/{id}/moves", "post", "MoveColony")]
    public async Task OpenApiDocument_ShouldDescribeTheOperation_WhenTheColonyEndpointsAreMapped(string path, string method, string operationId)
    {
        using var document = await GetDocumentAsync();

        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

        operation.GetProperty("operationId").GetString().Should().Be(operationId);
        operation.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        operation.GetProperty("responses").TryGetProperty("503", out _).Should().BeTrue();
    }

    [Fact] // regression risk: the invitation is published under its name, with its conflict and unavailable answers
    public async Task OpenApiDocument_ShouldDescribeTheInvitation_WhenTheInvitationEndpointIsMapped()
    {
        using var document = await GetDocumentAsync();

        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/invitations").GetProperty("post");

        operation.GetProperty("operationId").GetString().Should().Be("InviteUser");
        operation.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        foreach (var status in new[] { "201", "400", "403", "409", "503" })
        {
            operation.GetProperty("responses").TryGetProperty(status, out _).Should().BeTrue(status);
        }
    }

    [Fact] // invariant (FR-6, D-2): the published contract offers no way to change or delete a colony event
    public async Task OpenApiDocument_ShouldPublishOnlyReadsAndAppendsForColonies_WhenTheColonyEndpointsAreMapped()
    {
        using var document = await GetDocumentAsync();

        var methods = document.RootElement.GetProperty("paths").EnumerateObject()
            .Where(path => path.Name.StartsWith("/api/colonies", StringComparison.Ordinal)
                && !path.Name.Contains("/inspections", StringComparison.Ordinal)) // a colony's inspections have their own test below
            .SelectMany(path => path.Value.EnumerateObject().Select(operation => operation.Name))
            .ToList();

        methods.Should().HaveCount(10).And.OnlyContain(m => m == "get" || m == "post");
    }

    [Theory] // regression risk: each inspection operation is published in the document under its name
    [InlineData("/api/colonies/{id}/inspections", "post", "RecordInspection")]
    [InlineData("/api/colonies/{id}/inspections", "get", "ListInspections")]
    [InlineData("/api/inspections/{id}", "get", "GetInspection")]
    public async Task OpenApiDocument_ShouldDescribeTheOperation_WhenTheInspectionEndpointsAreMapped(string path, string method, string operationId)
    {
        using var document = await GetDocumentAsync();

        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

        operation.GetProperty("operationId").GetString().Should().Be(operationId);
        operation.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        operation.GetProperty("responses").TryGetProperty("503", out _).Should().BeTrue();
    }

    [Fact] // invariant: the published description of a recorded inspection names every field of the catalogue
    public async Task OpenApiDocument_ShouldNameEveryInspectionField_WhenTheRecordOperationIsPublished()
    {
        using var document = await GetDocumentAsync();

        var description = document.RootElement.GetProperty("paths").GetProperty("/api/colonies/{id}/inspections")
            .GetProperty("post").GetProperty("description").GetString();

        InspectionFields.All.Should().AllSatisfy(field => description.Should().Contain(field.Name));
    }

    [Fact] // invariant: the published contract offers an inspection no way to be changed or deleted
    public async Task OpenApiDocument_ShouldPublishOnlyReadsAndAppendsForInspections_WhenTheInspectionEndpointsAreMapped()
    {
        using var document = await GetDocumentAsync();

        var methods = document.RootElement.GetProperty("paths").EnumerateObject()
            .Where(path => path.Name.Contains("/inspections", StringComparison.Ordinal)
                && !path.Name.Contains("/media", StringComparison.Ordinal)) // an inspection's media has its own test below
            .SelectMany(path => path.Value.EnumerateObject().Select(operation => operation.Name))
            .ToList();

        methods.Should().HaveCount(3).And.OnlyContain(m => m == "get" || m == "post");
    }

    [Theory] // regression risk: each media operation is published in the document under its name
    [InlineData("/api/inspections/{id}/media/uploads", "post", "RequestMediaUpload")]
    [InlineData("/api/inspections/{id}/media", "post", "AttachMedia")]
    [InlineData("/api/inspections/{id}/media", "get", "ListMedia")]
    [InlineData("/api/media/{id}", "get", "GetMedia")]
    public async Task OpenApiDocument_ShouldDescribeTheOperation_WhenTheMediaEndpointsAreMapped(string path, string method, string operationId)
    {
        using var document = await GetDocumentAsync();

        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

        operation.GetProperty("operationId").GetString().Should().Be(operationId);
        operation.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        operation.GetProperty("responses").TryGetProperty("503", out _).Should().BeTrue();
    }

    [Fact] // invariant: the published contract offers attached media no way to be changed or removed
    public async Task OpenApiDocument_ShouldPublishOnlyReadsAndAppendsForMedia_WhenTheMediaEndpointsAreMapped()
    {
        using var document = await GetDocumentAsync();

        var methods = document.RootElement.GetProperty("paths").EnumerateObject()
            .Where(path => path.Name.Contains("/media", StringComparison.Ordinal))
            .SelectMany(path => path.Value.EnumerateObject().Select(operation => operation.Name))
            .ToList();

        methods.Should().HaveCount(4).And.OnlyContain(m => m == "get" || m == "post");
    }

    [Fact] // regression risk: the request schema carries the landowner (FR-2), so the caller sees the whole contract
    public async Task OpenApiDocument_ShouldDescribeTheLandowner_WhenTheRequestSchemaIsPublished()
    {
        using var document = await GetDocumentAsync();

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        schemas.GetProperty("LandownerRequest").GetProperty("properties").TryGetProperty("terms", out _).Should().BeTrue();
    }

    [Fact] // regression risk: the colony result publishes its optional warnings, described, so a caller knows to look for them
    public async Task OpenApiDocument_ShouldDescribeTheWarnings_WhenTheColonyResultSchemaIsPublished()
    {
        using var document = await GetDocumentAsync();

        var properties = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ColonyResult").GetProperty("properties");

        properties.GetProperty("warnings").GetProperty("description").GetString().Should().Contain("Warning:");
    }

    [Fact] // regression risk: the published API document names the product by its external name, not the internal codename
    public async Task OpenApiDocument_ShouldNameTheProduct_WhenTheDocumentIsPublished()
    {
        using var document = await GetDocumentAsync();

        var info = document.RootElement.GetProperty("info");

        info.GetProperty("title").GetString().Should().Be("Apiary-Keeper API");
        info.GetProperty("description").GetString().Should().Be(
            "Apiary-Keeper is an offline-first web app for owner-operator Texas apiarists that tracks yards, hives, inspections, treatments, harvests, and sales, and includes a Telegram family chat, a customer storefront, and AI agents planned for a later phase.");
    }

    private static async Task<JsonDocument> GetDocumentAsync()
    {
        using var factory = new ApiFactory(environment: "Development");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
