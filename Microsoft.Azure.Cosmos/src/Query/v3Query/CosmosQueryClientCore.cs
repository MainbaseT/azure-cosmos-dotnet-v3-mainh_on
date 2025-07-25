﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using Microsoft.Azure.Cosmos.Common;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.Json;
    using Microsoft.Azure.Cosmos.Query.Core;
    using Microsoft.Azure.Cosmos.Query.Core.Metrics;
    using Microsoft.Azure.Cosmos.Query.Core.Monads;
    using Microsoft.Azure.Cosmos.Query.Core.Pipeline.Pagination;
    using Microsoft.Azure.Cosmos.Query.Core.QueryClient;
    using Microsoft.Azure.Cosmos.Query.Core.QueryPlan;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Cosmos.Tracing;
    using Microsoft.Azure.Cosmos.Tracing.TraceData;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;

    using Newtonsoft.Json;

    using static Microsoft.Azure.Documents.RuntimeConstants;

    internal class CosmosQueryClientCore : CosmosQueryClient
    {
        private const string QueryExecutionInfoHeader = "x-ms-cosmos-query-execution-info";

        private readonly CosmosClientContext clientContext;
        private readonly ContainerInternal cosmosContainerCore;
        private readonly DocumentClient documentClient;
        private readonly SemaphoreSlim semaphore;

        public CosmosQueryClientCore(
            CosmosClientContext clientContext,
            ContainerInternal cosmosContainerCore)
        {
            this.clientContext = clientContext ?? throw new ArgumentException(nameof(clientContext));
            this.cosmosContainerCore = cosmosContainerCore;
            this.documentClient = this.clientContext.DocumentClient;
            this.semaphore = new SemaphoreSlim(1, 1);
        }

        public override Action<IQueryable> OnExecuteScalarQueryCallback => this.documentClient.OnExecuteScalarQueryCallback;

        public override async Task<ContainerQueryProperties> GetCachedContainerQueryPropertiesAsync(
            string containerLink,
            PartitionKey? partitionKey,
            ITrace trace,
            CancellationToken cancellationToken)
        {
            ContainerProperties containerProperties = await this.clientContext.GetCachedContainerPropertiesAsync(
                containerLink,
                trace,
                cancellationToken);

            List<Range<string>> effectivePartitionKeyRange = null;
            if (partitionKey != null)
            {
                // Dis-ambiguate the NonePK if used 
                PartitionKeyInternal partitionKeyInternal = partitionKey.Value.IsNone ? containerProperties.GetNoneValue() : partitionKey.Value.InternalKey;
                effectivePartitionKeyRange = new List<Range<string>>
                {
                    PartitionKeyInternal.GetEffectivePartitionKeyRange(
                        containerProperties.PartitionKey,
                        new Range<PartitionKeyInternal>(
                            min: partitionKeyInternal,
                            max: partitionKeyInternal,
                            isMinInclusive: true,
                            isMaxInclusive: true))
                };
            }

            return new ContainerQueryProperties(
                containerProperties.ResourceId,
                effectivePartitionKeyRange,
                containerProperties.PartitionKey,
                containerProperties.VectorEmbeddingPolicy,
                containerProperties.GeospatialConfig.GeospatialType);
        }

        public override async Task<TryCatch<PartitionedQueryExecutionInfo>> TryGetPartitionedQueryExecutionInfoAsync(
            SqlQuerySpec sqlQuerySpec,
            ResourceType resourceType,
            PartitionKeyDefinition partitionKeyDefinition,
            VectorEmbeddingPolicy vectorEmbeddingPolicy,
            bool requireFormattableOrderByQuery,
            bool isContinuationExpected,
            bool allowNonValueAggregateQuery,
            bool hasLogicalPartitionKey,
            bool allowDCount,
            bool useSystemPrefix,
            bool isHybridSearchQueryPlanOptimizationDisabled,
            Cosmos.GeospatialType geospatialType,
            CancellationToken cancellationToken)
        {
            string queryString = null;
            if (sqlQuerySpec != null)
            {
                using (Stream stream = this.clientContext.SerializerCore.ToStreamSqlQuerySpec(sqlQuerySpec, resourceType))
                {
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        queryString = reader.ReadToEnd();
                    }
                }
            }
            
            return (await this.documentClient.QueryPartitionProvider).TryGetPartitionedQueryExecutionInfo(
                querySpecJsonString: queryString,
                partitionKeyDefinition: partitionKeyDefinition,
                vectorEmbeddingPolicy: vectorEmbeddingPolicy,
                requireFormattableOrderByQuery: requireFormattableOrderByQuery,
                isContinuationExpected: isContinuationExpected,
                allowNonValueAggregateQuery: allowNonValueAggregateQuery,
                hasLogicalPartitionKey: hasLogicalPartitionKey,
                allowDCount: allowDCount,
                useSystemPrefix: useSystemPrefix,
                hybridSearchSkipOrderByRewrite: !isHybridSearchQueryPlanOptimizationDisabled,
                geospatialType: geospatialType);
        }

        public override async Task<TryCatch<QueryPage>> ExecuteItemQueryAsync(
            string resourceUri,
            ResourceType resourceType,
            OperationType operationType,
            FeedRange feedRange,
            QueryRequestOptions requestOptions,
            AdditionalRequestHeaders additionalRequestHeaders,
            SqlQuerySpec sqlQuerySpec,
            string continuationToken,
            int pageSize,
            ITrace trace,
            CancellationToken cancellationToken)
        {
            requestOptions.MaxItemCount = pageSize;

            ResponseMessage message = await this.clientContext.ProcessResourceOperationStreamAsync(
                resourceUri: resourceUri,
                resourceType: resourceType,
                operationType: operationType,
                requestOptions: requestOptions,
                feedRange: feedRange,
                cosmosContainerCore: this.cosmosContainerCore,
                streamPayload: this.clientContext.SerializerCore.ToStreamSqlQuerySpec(sqlQuerySpec, resourceType),
                requestEnricher: (cosmosRequestMessage) =>
                {
                    cosmosRequestMessage.Headers.Add(
                        HttpConstants.HttpHeaders.IsContinuationExpected,
                        additionalRequestHeaders.IsContinuationExpected.ToString());
                    QueryRequestOptions.FillContinuationToken(
                        cosmosRequestMessage,
                        continuationToken);
                    cosmosRequestMessage.Headers.Add(HttpConstants.HttpHeaders.ContentType, MediaTypes.QueryJson);
                    cosmosRequestMessage.Headers.Add(HttpConstants.HttpHeaders.IsQuery, bool.TrueString);
                    cosmosRequestMessage.Headers.Add(WFConstants.BackendHeaders.CorrelatedActivityId, additionalRequestHeaders.CorrelatedActivityId.ToString());
                    cosmosRequestMessage.Headers.Add(HttpConstants.HttpHeaders.OptimisticDirectExecute, additionalRequestHeaders.OptimisticDirectExecute.ToString());
                },
                trace: trace,
                cancellationToken: cancellationToken);

            return CosmosQueryClientCore.GetCosmosElementResponse(
                resourceType,
                message,
                trace);
        }

        public override async Task<PartitionedQueryExecutionInfo> ExecuteQueryPlanRequestAsync(
            string resourceUri,
            ResourceType resourceType,
            OperationType operationType,
            SqlQuerySpec sqlQuerySpec,
            PartitionKey? partitionKey,
            string supportedQueryFeatures,
            Guid clientQueryCorrelationId,
            ITrace trace,
            CancellationToken cancellationToken)
        {
            PartitionedQueryExecutionInfo partitionedQueryExecutionInfo;
            using (ResponseMessage message = await this.clientContext.ProcessResourceOperationStreamAsync(
                resourceUri: resourceUri,
                resourceType: resourceType,
                operationType: operationType,
                requestOptions: null,
                feedRange: partitionKey.HasValue ? new FeedRangePartitionKey(partitionKey.Value) : null,
                cosmosContainerCore: this.cosmosContainerCore,
                streamPayload: this.clientContext.SerializerCore.ToStreamSqlQuerySpec(sqlQuerySpec, resourceType),
                requestEnricher: (requestMessage) =>
                {
                    requestMessage.Headers.Add(HttpConstants.HttpHeaders.ContentType, RuntimeConstants.MediaTypes.QueryJson);
                    requestMessage.Headers.Add(HttpConstants.HttpHeaders.IsQueryPlanRequest, bool.TrueString);
                    requestMessage.Headers.Add(HttpConstants.HttpHeaders.SupportedQueryFeatures, supportedQueryFeatures);
                    requestMessage.Headers.Add(HttpConstants.HttpHeaders.QueryVersion, new Version(major: 1, minor: 0).ToString());
                    requestMessage.Headers.Add(WFConstants.BackendHeaders.CorrelatedActivityId, clientQueryCorrelationId.ToString());
                    requestMessage.UseGatewayMode = true;
                },
                trace: trace,
                cancellationToken: cancellationToken))
            {
                // Syntax exception are argument exceptions and thrown to the user.
                message.EnsureSuccessStatusCode();
                partitionedQueryExecutionInfo = this.clientContext.SerializerCore.FromStream<PartitionedQueryExecutionInfo>(message.Content);
            }

            return partitionedQueryExecutionInfo;
        }

        public override async Task<bool> GetClientDisableOptimisticDirectExecutionAsync()
        {
            QueryPartitionProvider provider = await this.clientContext.DocumentClient.QueryPartitionProvider;
            return provider.ClientDisableOptimisticDirectExecution;
        }

        public override async Task<List<PartitionKeyRange>> GetTargetPartitionKeyRangeByFeedRangeAsync(
            string resourceLink,
            string collectionResourceId,
            PartitionKeyDefinition partitionKeyDefinition,
            FeedRangeInternal feedRangeInternal,
            bool forceRefresh,
            ITrace trace)
        {
            using (ITrace childTrace = trace.StartChild("Get Overlapping Feed Ranges", TraceComponent.Routing, Tracing.TraceLevel.Info))
            {
                IRoutingMapProvider routingMapProvider = await this.GetRoutingMapProviderAsync();
                List<Range<string>> ranges = await feedRangeInternal.GetEffectiveRangesAsync(routingMapProvider, collectionResourceId, partitionKeyDefinition, trace);

                return await this.GetTargetPartitionKeyRangesAsync(
                    resourceLink,
                    collectionResourceId,
                    ranges,
                    forceRefresh,
                    childTrace);
            }
        }

        public override async Task<List<PartitionKeyRange>> GetTargetPartitionKeyRangesAsync(
            string resourceLink,
            string collectionResourceId,
            IReadOnlyList<Range<string>> providedRanges,
            bool forceRefresh,
            ITrace trace)
        {
            if (string.IsNullOrEmpty(collectionResourceId))
            {
                throw new ArgumentNullException(nameof(collectionResourceId));
            }

            if (providedRanges == null ||
                !providedRanges.Any() ||
                providedRanges.Any(x => x == null))
            {
                throw new ArgumentNullException(nameof(providedRanges));
            }

            using (ITrace getPKRangesTrace = trace.StartChild("Get Partition Key Ranges", TraceComponent.Routing, Tracing.TraceLevel.Info))
            {
                IRoutingMapProvider routingMapProvider = await this.GetRoutingMapProviderAsync();

                List<PartitionKeyRange> ranges = await routingMapProvider.TryGetOverlappingRangesAsync(collectionResourceId, providedRanges, getPKRangesTrace);
                if (ranges == null && PathsHelper.IsNameBased(resourceLink))
                {
                    // Refresh the cache and don't try to re-resolve collection as it is not clear what already
                    // happened based on previously resolved collection rid.
                    // Return NotFoundException this time. Next query will succeed.
                    // This can only happen if collection is deleted/created with same name and client was not restarted
                    // in between.
                    CollectionCache collectionCache = await this.documentClient.GetCollectionCacheAsync(getPKRangesTrace);
                    collectionCache.Refresh(resourceLink);
                }

                if (ranges == null)
                {
                    throw new NotFoundException($"{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}: GetTargetPartitionKeyRanges(collectionResourceId:{collectionResourceId}, providedRanges: {string.Join(",", providedRanges)} failed due to stale cache");
                }

                return ranges;
            }
        }

        public override bool BypassQueryParsing()
        {
            return QueryPlanRetriever.BypassQueryParsing();
        }

        public override void ClearSessionTokenCache(string collectionFullName)
        {
            ISessionContainer sessionContainer = this.clientContext.DocumentClient.sessionContainer;
            sessionContainer.ClearTokenByCollectionFullname(collectionFullName);
        }

        private static TryCatch<QueryPage> GetCosmosElementResponse(
            ResourceType resourceType,
            ResponseMessage cosmosResponseMessage,
            ITrace trace)
        {
            using (ITrace getCosmosElementResponse = trace.StartChild(TraceDatumKeys.GetCosmosElementResponse, TraceComponent.Json, Tracing.TraceLevel.Info))
            {
                using (cosmosResponseMessage)
                {
                    if (cosmosResponseMessage.Headers.QueryMetricsText != null)
                    {
                        QueryMetricsTraceDatum datum = new QueryMetricsTraceDatum(
                            new Lazy<QueryMetrics>(() => new QueryMetrics(
                                cosmosResponseMessage.Headers.QueryMetricsText, 
                                IndexUtilizationInfo.Empty, 
                                ClientSideMetrics.Empty)));
                        trace.AddDatum(TraceDatumKeys.QueryMetrics, datum);
                    }

                    if (!cosmosResponseMessage.IsSuccessStatusCode)
                    {
                        CosmosException exception = cosmosResponseMessage.CosmosException ?? new CosmosException(
                            cosmosResponseMessage.ErrorMessage,
                            cosmosResponseMessage.StatusCode,
                            (int)cosmosResponseMessage.Headers.SubStatusCode,
                            cosmosResponseMessage.Headers.ActivityId,
                            cosmosResponseMessage.Headers.RequestCharge);
                        return TryCatch<QueryPage>.FromException(exception);
                    }

                    return CreateQueryPage(
                        cosmosResponseMessage.Headers,
                        cosmosResponseMessage.Content,
                        resourceType);
                }
            }
        }

        internal static TryCatch<QueryPage> CreateQueryPage(
            Headers headers,
            Stream content,
            ResourceType resourceType)
        {
            if (!(content is MemoryStream memoryStream))
            {
                memoryStream = new MemoryStream();
                content.CopyTo(memoryStream);
            }

            CosmosQueryClientCore.ParseRestStream(
                memoryStream,
                resourceType,
                out CosmosArray documents,
                out CosmosObject distributionPlan,
                out bool? streaming);

            DistributionPlanSpec distributionPlanSpec = null;

            // ISSUE-TODO-adityasa-2024/1/31 - Uncomment this when distributionPlanSpec is hooked with rest of the code so that it can be tested.
            // if (distributionPlan != null)
            // {
            //     bool backendPlan = distributionPlan.TryGetValue("backendDistributionPlan", out CosmosElement backendDistributionPlan);
            //     bool clientPlan = distributionPlan.TryGetValue("clientDistributionPlan", out CosmosElement clientDistributionPlan);

            //     Debug.Assert(clientPlan == backendPlan, "Response Body Contract was violated. Out of the backend and client plans, only one  is present in the distribution plan.");

            //     if (backendPlan && clientPlan)
            //     {
            //         distributionPlanSpec = new DistributionPlanSpec(backendDistributionPlan.ToString(), clientDistributionPlan.ToString());
            //     }
            // }

            QueryState queryState = (headers.ContinuationToken != null) ? new QueryState(CosmosString.Create(headers.ContinuationToken)) : default;
            Dictionary<string, string> additionalHeaders = new Dictionary<string, string>();
            foreach (string key in headers)
            {
                if (!QueryPage.BannedHeaders.Contains(key))
                {
                    additionalHeaders[key] = headers[key];
                }
            }

            Lazy<CosmosQueryExecutionInfo> cosmosQueryExecutionInfo = default;
            if (headers.TryGetValue(QueryExecutionInfoHeader, out string queryExecutionInfoString))
            {
                cosmosQueryExecutionInfo = 
                    new Lazy<CosmosQueryExecutionInfo>(
                        () => JsonConvert.DeserializeObject<CosmosQueryExecutionInfo>(queryExecutionInfoString));
            }

            QueryPage response = new QueryPage(
                documents,
                headers.RequestCharge,
                headers.ActivityId,
                cosmosQueryExecutionInfo,
                distributionPlanSpec,
                disallowContinuationTokenMessage: null,
                additionalHeaders,
                queryState,
                streaming);

            return TryCatch<QueryPage>.FromResult(response);
        }

        private void PopulatePartitionKeyRangeInfo(
            RequestMessage request,
            PartitionKeyRangeIdentity partitionKeyRangeIdentity)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.ResourceType.IsPartitioned())
            {
                // If the request already has the logical partition key,
                // then we shouldn't add the physical partition key range id.

                bool hasPartitionKey = request.Headers.PartitionKey != null;
                if (!hasPartitionKey)
                {
                    request
                        .ToDocumentServiceRequest()
                        .RouteTo(partitionKeyRangeIdentity);
                }
            }
        }

        public override async Task ForceRefreshCollectionCacheAsync(string collectionLink, CancellationToken cancellationToken)
        {
            this.ClearSessionTokenCache(collectionLink);

            CollectionCache collectionCache = await this.documentClient.GetCollectionCacheAsync(NoOpTrace.Singleton);
            using (Documents.DocumentServiceRequest request = Documents.DocumentServiceRequest.Create(
               Documents.OperationType.Query,
               Documents.ResourceType.Collection,
               collectionLink,
               Documents.AuthorizationTokenType.Invalid)) //this request doesn't actually go to server
            {
                request.ForceNameCacheRefresh = true;
                await collectionCache.ResolveCollectionAsync(request, cancellationToken, NoOpTrace.Singleton);
            }
        }

        public override async Task<IReadOnlyList<PartitionKeyRange>> TryGetOverlappingRangesAsync(
            string collectionResourceId,
            Range<string> range,
            bool forceRefresh = false)
        {
            PartitionKeyRangeCache partitionKeyRangeCache = await this.GetRoutingMapProviderAsync();
            return await partitionKeyRangeCache.TryGetOverlappingRangesAsync( 
                collectionResourceId, 
                range,
                NoOpTrace.Singleton,
                forceRefresh);
        }

        private Task<PartitionKeyRangeCache> GetRoutingMapProviderAsync()
        {
            return this.documentClient.GetPartitionKeyRangeCacheAsync(NoOpTrace.Singleton);
        }

        /// <summary>
        /// Converts a list of CosmosElements into a memory stream.
        /// </summary>
        /// <param name="stream">The memory stream response for the query REST response Azure Cosmos</param>
        /// <param name="resourceType">The resource type</param>
        /// <param name="documents">An array of CosmosElements parsed from the response body</param>
        /// <param name="distributionPlan">An object containing the distribution plan for the client</param>
        /// <param name="streaming">An optional return value indicating if the backend response is streaming</param>
        public static void ParseRestStream(
            Stream stream,
            ResourceType resourceType,
            out CosmosArray documents,
            out CosmosObject distributionPlan,
            out bool? streaming)
        {
            if (!(stream is MemoryStream memoryStream))
            {
                memoryStream = new MemoryStream();
                stream.CopyTo(memoryStream);
            }

            if (!memoryStream.CanRead)
            {
                throw new InvalidDataException("Stream can not be read");
            }

            // Parse out the document from the REST response like this:
            // {
            //    "_rid": "qHVdAImeKAQ=",
            //    "Documents": [{
            //        "id": "03230",
            //        "_rid": "qHVdAImeKAQBAAAAAAAAAA==",
            //        "_self": "dbs\/qHVdAA==\/colls\/qHVdAImeKAQ=\/docs\/qHVdAImeKAQBAAAAAAAAAA==\/",
            //        "_etag": "\"410000b0-0000-0000-0000-597916b00000\"",
            //        "_attachments": "attachments\/",
            //        "_ts": 1501107886
            //    }],
            //    "_count": 1,
            //    "_distributionPlan": {
            //         "backendDistributionPlan": {
            //              "query": "\nSELECT Count(r.a) AS count_a\nFROM r",
            //              "obfuscatedQuery": "{\"query\":\"SELECT Count(r.a) AS p1\\nFROM r\",\"parameters\":[]}",
            //              "shape": "{\"Select\":{\"Type\":\"List\",\"AggCount\":1},\"From\":{\"Expr\":\"Aliased\"}}",
            //              "signature":-4885972563975185329,
            //              "shapeSignature":-6171928203673877984,
            //              "queryIL": {...},
            //              "noSpatial": true,
            //              "language": "QueryIL"
            //          },
            //          "coordinatorDistributionPlan": {
            //              "clientQL": {
            //                  "Kind": "Input",
            //                  "Name": "root"
            //              }
            //          }
            //      },
            //      "_streaming": true
            // }
            // You want to create a CosmosElement for each document in "Documents".

            ReadOnlyMemory<byte> content = memoryStream.TryGetBuffer(out ArraySegment<byte> buffer) ? buffer : (ReadOnlyMemory<byte>)memoryStream.ToArray();
            IJsonNavigator jsonNavigator = JsonNavigator.Create(content);

            string resourceName = resourceType switch
            {
                ResourceType.Collection => "DocumentCollections",
                _ => resourceType.ToResourceTypeString() + "s",
            };

            if (!jsonNavigator.TryGetObjectProperty(
                jsonNavigator.GetRootNode(),
                resourceName,
                out ObjectProperty objectProperty))
            {
                throw new InvalidOperationException($"Response Body Contract was violated. QueryResponse did not have property: {resourceName}");
            }

            if (!(CosmosElement.Dispatch(
                jsonNavigator,
                objectProperty.ValueNode) is CosmosArray cosmosArray))
            {
                throw new InvalidOperationException($"QueryResponse did not have an array of : {resourceName}");
            }

            documents = cosmosArray;

            if (resourceType == ResourceType.Document && jsonNavigator.TryGetObjectProperty(jsonNavigator.GetRootNode(), "_distributionPlan", out ObjectProperty distributionPlanObjectProperty))
            {
                switch (CosmosElement.Dispatch(jsonNavigator, distributionPlanObjectProperty.ValueNode))
                {
                    case CosmosString binaryDistributionPlan:
                        byte[] binaryJson = Convert.FromBase64String(binaryDistributionPlan.Value);
                        IJsonNavigator binaryJsonNavigator = JsonNavigator.Create(binaryJson);
                        IJsonNavigatorNode binaryJsonNavigatorNode = binaryJsonNavigator.GetRootNode();
                        distributionPlan = CosmosObject.Create(binaryJsonNavigator, binaryJsonNavigatorNode);
                        break;
                    case CosmosObject textDistributionPlan:
                        distributionPlan = textDistributionPlan;
                        break;
                    default:
                        throw new InvalidOperationException($"Response Body Contract was violated. QueryResponse did not have property: {resourceName}");
                }
            }
            else
            {
                distributionPlan = null;
            }

            if (resourceType == ResourceType.Document && jsonNavigator.TryGetObjectProperty(jsonNavigator.GetRootNode(), "_streaming", out ObjectProperty streamingProperty))
            {
                JsonNodeType jsonNodeType = jsonNavigator.GetNodeType(streamingProperty.ValueNode);
                streaming = jsonNodeType switch
                {
                    JsonNodeType.False => false,
                    JsonNodeType.True => true,
                    _ => throw new InvalidOperationException($"Response Body Contract was violated. QueryResponse had _streaming property as a non boolean: {jsonNodeType}"),
                };
            }
            else
            {
                streaming = null;
            }
        }
    }
}