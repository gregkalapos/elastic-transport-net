// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Elastic.Transport.Products.Elasticsearch;

/// <summary>
/// An implementation of <see cref="ProductRegistration"/> that fills in the bespoke implementations
/// for Elasticsearch so that <see cref="RequestPipeline"/> knows how to ping and sniff if we setup
/// <see cref="HttpTransport{TConnectionSettings}"/> to talk to Elasticsearch
/// </summary>
public class ElasticsearchProductRegistration : ProductRegistration
{
	private readonly HeadersList _headers;
	private readonly MetaHeaderProvider _metaHeaderProvider;

	/// <summary>
	/// Create a new instance of the Elasticsearch product registration.
	/// </summary>
	public ElasticsearchProductRegistration() => _headers = new HeadersList("warning");

	/// <summary>
	/// 
	/// </summary>
	/// <param name="markerType"></param>
	public ElasticsearchProductRegistration(Type markerType) : this() => _metaHeaderProvider = new DefaultMetaHeaderProvider(markerType, "es");

	/// <summary> A static instance of <see cref="ElasticsearchProductRegistration"/> to promote reuse </summary>
	public static ProductRegistration Default { get; } = new ElasticsearchProductRegistration();

	/// <inheritdoc cref="ProductRegistration.Name"/>
	public override string Name { get; } = "elasticsearch-net";

	/// <inheritdoc cref="ProductRegistration.SupportsPing"/>
	public override bool SupportsPing { get; } = true;

	/// <inheritdoc cref="ProductRegistration.SupportsSniff"/>
	public override bool SupportsSniff { get; } = true;

	/// <inheritdoc cref="ProductRegistration.ResponseHeadersToParse"/>
	public override HeadersList ResponseHeadersToParse => _headers;

	/// <inheritdoc cref="ProductRegistration.MetaHeaderProvider"/>
	public override MetaHeaderProvider MetaHeaderProvider => _metaHeaderProvider;

	/// <inheritdoc cref="ProductRegistration.ResponseBuilder"/>
	public override ResponseBuilder ResponseBuilder => new ElasticsearchResponseBuilder();

	/// <summary> Exposes the path used for sniffing in Elasticsearch </summary>
	public const string SniffPath = "_nodes/http,settings";

	/// <summary>
	/// Implements an ordering that prefers master eligible nodes when attempting to sniff the
	/// <see cref="NodePool.Nodes"/>
	/// </summary>
	public override int SniffOrder(Node node) =>
		node.HasFeature(ElasticsearchNodeFeatures.MasterEligible) ? node.Uri.Port : int.MaxValue;

	/// <summary>
	/// If we know that a node is a master eligible node that hold no data it is excluded from regular
	/// API calls. They are considered for ping and sniff requests.
	/// </summary>
	public override bool NodePredicate(Node node) =>
		// skip master only nodes (holds no data and is master eligible)
		!(node.HasFeature(ElasticsearchNodeFeatures.MasterEligible) &&
		  !node.HasFeature(ElasticsearchNodeFeatures.HoldsData));

	/// <inheritdoc cref="ProductRegistration.HttpStatusCodeClassifier"/>
	public override bool HttpStatusCodeClassifier(HttpMethod method, int statusCode) =>
		statusCode >= 200 && statusCode < 300;

	/// <inheritdoc cref="ProductRegistration.TryGetServerErrorReason{TResponse}"/>>
	public override bool TryGetServerErrorReason<TResponse>(TResponse response, out string reason)
	{
		reason = null;
		if (response is StringResponse s && s.TryGetElasticsearchServerError(out var e)) reason = e.Error?.ToString();
		else if (response is BytesResponse b && b.TryGetElasticsearchServerError(out e)) reason = e.Error?.ToString();
		else if (response.TryGetElasticsearchServerError(out e)) reason = e.Error?.ToString();
		return e != null;
	}

	/// <inheritdoc cref="ProductRegistration.CreateSniffRequestData"/>
	public override RequestData CreateSniffRequestData(Node node, IRequestConfiguration requestConfiguration,
		ITransportConfiguration settings,
		MemoryStreamFactory memoryStreamFactory
	)
	{
		var requestParameters = new DefaultRequestParameters
		{
			QueryString = {{"timeout", requestConfiguration.PingTimeout}, {"flat_settings", true},}
		};
		return new RequestData(HttpMethod.GET, SniffPath, null, settings, requestParameters, memoryStreamFactory)
		{
			Node = node
		};
	}

	/// <inheritdoc cref="ProductRegistration.SniffAsync"/>
	public override async Task<Tuple<TransportResponse, IReadOnlyCollection<Node>>> SniffAsync(TransportClient transportClient,
		bool forceSsl, RequestData requestData, CancellationToken cancellationToken)
	{
		var response = await transportClient.RequestAsync<SniffResponse>(requestData, cancellationToken)
			.ConfigureAwait(false);
		var nodes = response.ToNodes(forceSsl);
		return Tuple.Create<TransportResponse, IReadOnlyCollection<Node>>(response,
			new ReadOnlyCollection<Node>(nodes.ToArray()));
	}

	/// <inheritdoc cref="ProductRegistration.Sniff"/>
	public override Tuple<TransportResponse, IReadOnlyCollection<Node>> Sniff(TransportClient transportClient, bool forceSsl,
		RequestData requestData)
	{
		var response = transportClient.Request<SniffResponse>(requestData);
		var nodes = response.ToNodes(forceSsl);
		return Tuple.Create<TransportResponse, IReadOnlyCollection<Node>>(response,
			new ReadOnlyCollection<Node>(nodes.ToArray()));
	}

	/// <inheritdoc cref="ProductRegistration.CreatePingRequestData"/>
	public override RequestData CreatePingRequestData(Node node, RequestConfiguration requestConfiguration,
		ITransportConfiguration global,
		MemoryStreamFactory memoryStreamFactory
	)
	{
		var requestParameters = new DefaultRequestParameters
		{
			RequestConfiguration = requestConfiguration
		};

		var data = new RequestData(HttpMethod.HEAD, string.Empty, null, global, requestParameters,
			memoryStreamFactory) {Node = node};
		return data;
	}

	/// <inheritdoc cref="ProductRegistration.PingAsync"/>
	public override async Task<TransportResponse> PingAsync(TransportClient transportClient, RequestData pingData,
		CancellationToken cancellationToken)
	{
		var response = await transportClient.RequestAsync<VoidResponse>(pingData, cancellationToken).ConfigureAwait(false);
		return response;
	}

	/// <inheritdoc cref="ProductRegistration.Ping"/>
	public override TransportResponse Ping(TransportClient connection, RequestData pingData)
	{
		var response = connection.Request<VoidResponse>(pingData);
		return response;
	}
}
