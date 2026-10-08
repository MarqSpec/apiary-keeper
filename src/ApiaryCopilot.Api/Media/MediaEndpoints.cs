using System.ComponentModel;
using ApiaryCopilot.Api.Accounts;
using ApiaryCopilot.Api.Paging;
using ApiaryCopilot.Api.Problems;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.Api.Media;

/// <summary>
/// Photos and short videos of an inspection (UC-1; FR-8; D-13): ask for a URL to upload one straight to the media
/// bucket, attach it once uploaded, and list or read what is attached, each with a fresh download URL. The bytes never
/// cross the API. Media is attached once; no endpoint changes or removes it.
/// </summary>
public static partial class MediaEndpoints
{
    /// <summary>The route an inspection's media lives under, after the inspection's id.</summary>
    public const string InspectionPath = "/api/inspections/{id:guid}/media";

    /// <summary>The route one media item is read at.</summary>
    public const string Path = "/api/media";

    private const string LogCategory = "ApiaryCopilot.Api.Media";

    /// <summary>
    /// Registers the media contracts' JSON and the bucket, with <see cref="MediaOptions"/> bound from
    /// <c>Media__*</c> and validated at startup.
    /// </summary>
    public static IServiceCollection AddMedia(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, MediaJsonContext.Default));
        services.AddMediaStorage().Bind(configuration.GetSection(MediaOptions.SectionName));
        return services;
    }

    /// <summary>
    /// Maps the media endpoints: an upload request, an attach and two reads, nothing that updates or deletes. Each is
    /// for a signed-in owner or helper acting in their own account and fails closed (<see cref="AccountMemberAuthorization"/>),
    /// and answers 503 while no media bucket is configured.
    /// </summary>
    public static RouteGroupBuilder MapMedia(this IEndpointRouteBuilder endpoints)
    {
        var media = endpoints.MapGroup(string.Empty)
            .WithTags("Media")
            .RequireAccountMember()
            .AddEndpointFilter<RequireMediaStorageFilter>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        media.MapPost($"{InspectionPath}/uploads", RequestUploadAsync)
            .WithName("RequestMediaUpload")
            .WithSummary("Get a URL to upload a photo or video")
            .WithDescription(
                "Returns a short-lived presigned URL to upload one photo or video of one of the account's inspections straight to the media bucket, and the objectKey to attach it by. " +
                "Send the file with the method and headers returned, its body exactly sizeBytes long, before expiresAt: the bucket refuses any other content type or length, and any upload after that. " +
                "Then attach it with the objectKey. The URL is a credential; keep it only until the upload is done.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        media.MapPost(InspectionPath, AttachAsync)
            .WithName("AttachMedia")
            .WithSummary("Attach an uploaded photo or video")
            .WithDescription(
                "Attaches an uploaded file to the inspection it was uploaded for, stamped with the server's time. The objectKey must be one the upload request returned for this inspection, " +
                "and the file must be in the bucket. A file is attached once. Returns the media, with a download URL.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        media.MapGet(InspectionPath, ListAsync)
            .WithName("ListMedia")
            .WithSummary("List an inspection's photos and videos")
            .WithDescription("Lists an inspection's media in the order attached, one page at a time, each with a fresh short-lived download URL. Pass the returned nextCursor to get the next page.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        media.MapGet($"{Path}/{{id:guid}}", GetAsync)
            .WithName("GetMedia")
            .WithSummary("Read a photo or video")
            .WithDescription("Returns one media item with a fresh short-lived download URL. Media the account does not have is not found.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return media;
    }

    private static async Task<Results<Ok<MediaUploadResult>, ValidationProblem, ProblemHttpResult>> RequestUploadAsync(
        [Description("The id of the inspection the file belongs to.")] Guid id,
        MediaUploadRequest request,
        IInspectionMediaStore store,
        IMediaStorage storage,
        ICurrentAccount currentAccount,
        IOptions<MediaOptions> options,
        CancellationToken cancellationToken)
    {
        var parsed = MediaRequestParser.Parse(request, options.Value);
        if (parsed.Value is not { } upload)
        {
            return ProblemResults.Validation(parsed.Errors.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal));
        }

        if (id == Guid.Empty || !await store.InspectionExistsAsync(new InspectionId(id), cancellationToken))
        {
            return NoSuchInspection();
        }

        var key = MediaObjectKey.New(currentAccount.AccountId!.Value, new InspectionId(id));
        var presigned = storage.PresignUpload(key, upload.ContentType, upload.SizeBytes);
        return TypedResults.Ok(new MediaUploadResult(
            key.Value,
            presigned.Url,
            HttpMethods.Put,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = upload.ContentType },
            presigned.ExpiresAt));
    }

    private static async Task<Results<Created<MediaResult>, ValidationProblem, ProblemHttpResult>> AttachAsync(
        [Description("The id of the inspection the file was uploaded for.")] Guid id,
        AttachMediaRequest request,
        IInspectionMediaStore store,
        IMediaStorage storage,
        ICurrentAccount currentAccount,
        IOptions<MediaOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NoSuchInspection();
        }

        // A key that is not this account's and this inspection's is refused before the bucket or the database is read.
        var inspectionId = new InspectionId(id);
        var key = MediaObjectKey.Parse(request.ObjectKey);
        if (key is null || !key.BelongsTo(currentAccount.AccountId!.Value, inspectionId))
        {
            return ObjectKeyProblem("objectKey must be one the upload request returned for this inspection.");
        }

        if (!await store.InspectionExistsAsync(inspectionId, cancellationToken))
        {
            return NoSuchInspection();
        }

        StoredObject? stored;
        try
        {
            stored = await storage.FindAsync(key, cancellationToken);
        }
        catch (MediaStorageUnavailableException e)
        {
            // The type only: the inner failure's message may carry the bucket's URL or the key.
            BucketUnavailable(loggers.CreateLogger(LogCategory), e.InnerException?.GetType().Name ?? e.GetType().Name);
            return ProblemResults.Unavailable("the media bucket did not answer; retry later.");
        }

        if (stored is null)
        {
            return ObjectKeyProblem("nothing has been uploaded with that objectKey yet; upload the file first.");
        }

        // The bucket enforces the signed type and length; this refuses anything that reached it some other way.
        if (options.Value.MaxBytesFor(stored.ContentType) is not { } max || stored.SizeBytes < 1 || stored.SizeBytes > max)
        {
            return ObjectKeyProblem("the uploaded file is not an allowed image or video within its size limit.");
        }

        var result = await store.AttachAsync(inspectionId, key, stored, cancellationToken);
        return result.Outcome switch
        {
            MediaAttachOutcome.Attached => TypedResults.Created($"{Path}/{result.Media!.MediaId}", ToResult(result.Media, storage)),
            MediaAttachOutcome.NoSuchInspection => NoSuchInspection(),
            MediaAttachOutcome.KeyNotForThisInspection => ObjectKeyProblem("objectKey must be one the upload request returned for this inspection."),
            MediaAttachOutcome.AlreadyAttached => ObjectKeyProblem("that file is already attached."),
            _ => throw new InvalidOperationException($"Unknown attach outcome {result.Outcome}."),
        };
    }

    private static async Task<Results<Ok<MediaPage>, ValidationProblem, ProblemHttpResult>> ListAsync(
        [Description("The inspection's id.")] Guid id,
        IInspectionMediaStore store,
        IMediaStorage storage,
        CancellationToken cancellationToken,
        [Description("How many media items to return: 1 to 100, default 50.")] int? limit = null,
        [Description("The nextCursor of the previous page; omit for the first page.")] string? cursor = null)
    {
        if (!PageRequest.TryRead(limit, cursor, out var page, out var errors))
        {
            return ProblemResults.Validation(errors);
        }

        var media = id == Guid.Empty ? null : await store.ListAsync(new InspectionId(id), page.Skip, page.Fetch, cancellationToken);
        return media is null
            ? NoSuchInspection()
            : TypedResults.Ok(new MediaPage([.. media.Take(page.Take).Select(m => ToResult(m, storage))], page.NextCursor(media.Count)));
    }

    private static async Task<Results<Ok<MediaResult>, ProblemHttpResult>> GetAsync(
        [Description("The media's id.")] Guid id, IInspectionMediaStore store, IMediaStorage storage, CancellationToken cancellationToken)
    {
        var media = id == Guid.Empty ? null : await store.FindAsync(new MediaId(id), cancellationToken);
        return media is null
            ? ProblemResults.NotFound("no media with that id.")
            : TypedResults.Ok(ToResult(media, storage));
    }

    private static ProblemHttpResult NoSuchInspection() => ProblemResults.NotFound("no inspection with that id.");

    private static ValidationProblem ObjectKeyProblem(string message) =>
        ProblemResults.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["objectKey"] = [$"Validation: {message}"] });

    private static MediaResult ToResult(InspectionMedia media, IMediaStorage storage)
    {
        var download = storage.PresignDownload(media.ObjectKey);
        return new MediaResult(
            media.MediaId.Value,
            media.InspectionId.Value,
            media.ContentType,
            media.SizeBytes,
            media.AttachedAt,
            download.Url,
            download.ExpiresAt);
    }

    [LoggerMessage(EventId = 1501, Level = LogLevel.Warning, Message = "The media bucket did not answer a read ({FailureType}).")]
    private static partial void BucketUnavailable(ILogger logger, string failureType);
}
