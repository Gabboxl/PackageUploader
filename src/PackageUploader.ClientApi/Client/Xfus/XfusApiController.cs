﻿using PackageUploader.ClientApi.Client.Xfus.Config;
using PackageUploader.ClientApi.Client.Xfus.Exceptions;
using PackageUploader.ClientApi.Client.Xfus.Models.Internal;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace PackageUploader.ClientApi.Client.Xfus;

internal class XfusApiController
{
    private static readonly JsonSerializerOptions DefaultJsonSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private ILogger<XfusUploader> _logger;
    private UploadConfig _uploadConfig;

    public XfusApiController(ILogger<XfusUploader> logger, UploadConfig uploadConfig)
    {
        _logger = logger;
        _uploadConfig = uploadConfig;
    }

    internal async Task UploadBlocksAsync(HttpClient httpClient, Block[] blockToBeUploaded, int maxBlockSize, FileInfo uploadFile, Guid assetId, XfusBlockProgressReporter blockProgressReporter, CancellationToken ct)
    {
        var bufferPool = new BufferPool(maxBlockSize);
        var actionBlockOptions = new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = _uploadConfig.MaxParallelism };
        var uploadBlock = new ActionBlock<Block>(async block =>
        {
            byte[] buffer;
            try
            {
                buffer = bufferPool.GetBuffer(); // Take or create buffer from the pool
                await using var stream = File.OpenRead(uploadFile.FullName);
                stream.Seek(block.Offset, SeekOrigin.Begin);
                var bytesRead = await stream.ReadAsync(buffer, 0, (int)block.Size, ct).ConfigureAwait(false);

                _logger.LogTrace($"Uploading block {block.Id} with payload: {new ByteSize(bytesRead)}.");

                // In certain scenarios like delta uploads, or the last chunk in an upload,
                // the actual chunk size could be less than the largest chunk size.
                // We need to make sure buffer size matches chunk size otherwise we will get an error
                // when trying to send http request.
                Array.Resize(ref buffer, bytesRead);

                await UploadBlockFromPayloadAsync(httpClient, block.Size, assetId, block.Id, buffer, ct).ConfigureAwait(false);

                blockProgressReporter.BlocksLeftToUpload--;
                blockProgressReporter.BytesUploaded += bytesRead;
                _logger.LogTrace($"Uploaded block {block.Id}. Total uploaded: {new ByteSize(blockProgressReporter.BytesUploaded)} / {new ByteSize(blockProgressReporter.TotalBlockBytes)}.");
                blockProgressReporter.ReportProgress();
            }
            // Swallow exceptions so other chunk upload can proceed without ActionBlock terminating
            // from a midway-failed chunk upload. We'll re-upload failed chunks later on so this is ok.
            catch (Exception e)
            {
                _logger.LogTrace($"Block {block.Id} failed, will retry. {e}");
                return;
            }

            bufferPool.RecycleBuffer(buffer);
        },
            actionBlockOptions);

        foreach (var block in blockToBeUploaded)
        {
            await uploadBlock.SendAsync(block, ct).ConfigureAwait(false);
        }

        uploadBlock.Complete();
        await uploadBlock.Completion.ConfigureAwait(false);
    }

    internal static async Task UploadBlockFromPayloadAsync(HttpMessageInvoker httpClient, long contentLength, Guid assetId, long blockId, byte[] payload, CancellationToken ct)
    {
        using var req = CreateStreamRequest(HttpMethod.Put, $"{assetId}/blocks/{blockId}/source/payload", payload, contentLength);

        var response = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new XfusServerException(response.StatusCode, response.ReasonPhrase);
        }
    }

    internal async Task<UploadProgress> InitializeAssetAsync(HttpClient httpClient, Guid assetId, FileInfo uploadFile, bool deltaUpload, CancellationToken ct)
    {
        var properties = new UploadProperties
        {
            FileProperties = new FileProperties
            {
                Name = uploadFile.Name,
                Size = uploadFile.Length,
            }
        };

        using var req = CreateJsonRequest(HttpMethod.Post, $"{assetId}/initialize", deltaUpload, properties);
        using var cts = new CancellationTokenSource(_uploadConfig.HttpTimeoutMs);

        var response = await httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new XfusServerException(response.StatusCode, response.ReasonPhrase);
        }

        _logger.LogInformation("XFUS AssetId: {assetId}", assetId);
        var uploadProgress = await response.Content.ReadFromJsonAsync<UploadProgress>(DefaultJsonSerializerOptions, ct).ConfigureAwait(false);
        return uploadProgress;
    }

    internal async Task<UploadProgress> ContinueAssetAsync(HttpMessageInvoker httpClient, Guid assetId, bool deltaUpload, CancellationToken ct)
    {
        using var req = CreateJsonRequest(HttpMethod.Post, $"{assetId}/continue", deltaUpload, string.Empty);

        var response = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new XfusServerException(response.StatusCode, response.ReasonPhrase);
        }

        var uploadProgress = await response.Content.ReadFromJsonAsync<UploadProgress>(DefaultJsonSerializerOptions, ct).ConfigureAwait(false);
        return uploadProgress;
    }

    internal static HttpRequestMessage CreateJsonRequest<T>(HttpMethod method, string url, bool deltaUpload, T content)
    {
        var request = new HttpRequestMessage(method, url);
        request.Content = new StringContent(JsonSerializer.Serialize(content, DefaultJsonSerializerOptions));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);

        if (deltaUpload)
        {
            request.Headers.Add("X-MS-EnableDeltaUploads", "True");
        }

        return request;
    }

    internal static HttpRequestMessage CreateStreamRequest(HttpMethod method, string url, byte[] content, long contentLength)
    {
        var request = new HttpRequestMessage(method, url);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentLength = contentLength;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Octet);
        return request;
    }
}
