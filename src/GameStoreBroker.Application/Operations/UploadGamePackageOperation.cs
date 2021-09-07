﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using GameStoreBroker.Application.Extensions;
using GameStoreBroker.Application.Schema;
using GameStoreBroker.ClientApi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading;
using System.Threading.Tasks;

namespace GameStoreBroker.Application.Operations
{
    internal class UploadGamePackageOperation : Operation
    {
        private readonly IGameStoreBrokerService _storeBrokerService;
        private readonly ILogger<UploadGamePackageOperation> _logger;
        private readonly UploadGamePackageOperationSchema _config;

        public UploadGamePackageOperation(IGameStoreBrokerService storeBrokerService, ILogger<UploadGamePackageOperation> logger, IOptions<UploadGamePackageOperationSchema> config) : base(logger)
        {
            _storeBrokerService = storeBrokerService;
            _logger = logger;
            _config = config.Value;
        }

        protected override async Task ProcessAsync(CancellationToken ct)
        {
            _logger.LogInformation("Starting UploadGamePackage operation.");
            
            var product = await _storeBrokerService.GetProductAsync(_config, ct).ConfigureAwait(false);
            var packageBranch = await _storeBrokerService.GetGamePackageBranch(product, _config, ct).ConfigureAwait(false);
            
            await _storeBrokerService.UploadGamePackageAsync(product, packageBranch, _config.GameAssets, _config.MinutesToWaitForProcessing, ct).ConfigureAwait(false);
        }
    }
}