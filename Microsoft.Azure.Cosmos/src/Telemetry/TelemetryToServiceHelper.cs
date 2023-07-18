﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Telemetry
{
    using System;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Core.Trace;
    using Microsoft.Azure.Cosmos.Handler;
    using Microsoft.Azure.Cosmos.Query.Core.Monads;
    using Microsoft.Azure.Cosmos.Resource.Settings;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Cosmos.Tracing;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Collections;

    internal class TelemetryToServiceHelper : IDisposable
    {
        internal static int DefaultBackgroundRefreshClientConfigTimeIntervalInMS = 10 * 60 * 1000;

        internal ClientTelemetry clientTelemetryInstance = null;

        private readonly AuthorizationTokenProvider cosmosAuthorization;
        private readonly CosmosHttpClient httpClient;
        private readonly Uri serviceEnpoint;
        private readonly ConnectionPolicy connectionPolicy;
        private readonly string clientId;
        private readonly GlobalEndpointManager globalEndpointManager;
        private readonly CancellationTokenSource cancellationTokenSource;

        private Task accountClientConfigTask = null;

        private TelemetryToServiceHelper(
            string clientId,
            ConnectionPolicy connectionPolicy,
            AuthorizationTokenProvider cosmosAuthorization,
            CosmosHttpClient httpClient,
            Uri serviceEndpoint,
            GlobalEndpointManager globalEndpointManager,
            CancellationTokenSource cancellationTokenSource)
        {
            this.clientId = clientId;
            this.cosmosAuthorization = cosmosAuthorization;
            this.httpClient = httpClient;
            this.connectionPolicy = connectionPolicy;
            this.serviceEnpoint = serviceEndpoint;
            this.globalEndpointManager = globalEndpointManager;
            this.cancellationTokenSource = cancellationTokenSource;
        }

        public static TelemetryToServiceHelper StartBackgroundJobForClientConfigAndTelemetry(string clientId,
            ConnectionPolicy connectionPolicy,
            AuthorizationTokenProvider cosmosAuthorization,
            CosmosHttpClient httpClient,
            Uri serviceEndpoint,
            GlobalEndpointManager globalEndpointManager,
            CancellationTokenSource cancellationTokenSource)
        {
            if (connectionPolicy.DisableClientTelemetryToService)
            {
                return null;
            }

            TelemetryToServiceHelper telemetryToServiceHelper = new TelemetryToServiceHelper(
                clientId, connectionPolicy, cosmosAuthorization, httpClient, serviceEndpoint, globalEndpointManager, cancellationTokenSource);
            telemetryToServiceHelper.Initialize();

            return telemetryToServiceHelper;
        }

        private void Initialize()
        {
            this.accountClientConfigTask = Task.Run(() => this.RefreshDatabaseAccountClientConfigInternalAsync());
        }

        private async Task RefreshDatabaseAccountClientConfigInternalAsync()
        {
            while (!this.cancellationTokenSource.IsCancellationRequested)
            {
                Uri serviceEndpointWithPath = new Uri(this.serviceEnpoint + Paths.ClientConfigPathSegment);

                TryCatch<AccountClientConfigProperties> databaseAccountClientConfigs = await this.GetDatabaseAccountClientConfigAsync(this.cosmosAuthorization, this.httpClient, serviceEndpointWithPath);
                if (databaseAccountClientConfigs.Succeeded)
                {
                    this.InitializeClientTelemetry(databaseAccountClientConfigs.Result);
                }
                await Task.Delay(TelemetryToServiceHelper.DefaultBackgroundRefreshClientConfigTimeIntervalInMS);
            }
        }

        private async Task<TryCatch<AccountClientConfigProperties>> GetDatabaseAccountClientConfigAsync(AuthorizationTokenProvider cosmosAuthorization,
            CosmosHttpClient httpClient,
            Uri serviceEndpoint)
        {
            Uri clientConfigEndpoint = new Uri(serviceEndpoint + Paths.ClientConfigPathSegment);

            INameValueCollection headers = new RequestNameValueCollection();
            await cosmosAuthorization.AddAuthorizationHeaderAsync(
                headersCollection: headers,
                clientConfigEndpoint,
                HttpConstants.HttpMethods.Get,
                AuthorizationTokenType.PrimaryMasterKey);

            using (ITrace trace = Trace.GetRootTrace("Account Client Config Read", TraceComponent.Transport, TraceLevel.Info))
            {
                try
                {
                    using (HttpResponseMessage responseMessage = await httpClient.GetAsync(
                        uri: clientConfigEndpoint,
                        additionalHeaders: headers,
                        resourceType: ResourceType.DatabaseAccount,
                        timeoutPolicy: HttpTimeoutPolicyControlPlaneRead.Instance,
                        clientSideRequestStatistics: null,
                        cancellationToken: default))
                    using (DocumentServiceResponse documentServiceResponse = await ClientExtensions.ParseResponseAsync(responseMessage))
                    {
                        return TryCatch<AccountClientConfigProperties>.FromResult(CosmosResource.FromStream<AccountClientConfigProperties>(documentServiceResponse));
                    }
                }
                catch (Exception ex)
                {
                    DefaultTrace.TraceWarning($"Exception while calling client config " + ex.StackTrace);
                    return TryCatch<AccountClientConfigProperties>.FromException(ex);
                }
            }
        }

        /// <summary>
        /// Trigger Client Telemetry job when it is enabled and not already running.
        /// </summary>
        private void InitializeClientTelemetry(AccountClientConfigProperties databaseAccountClientConfigs)
        {
            if (databaseAccountClientConfigs.IsClientTelemetryEnabled())
            {
                if (this.clientTelemetryInstance == null)
                {
                    try
                    {
                        this.clientTelemetryInstance = ClientTelemetry.CreateAndStartBackgroundTelemetry(
                            clientId: this.clientId,
                            httpClient: this.httpClient,
                            userAgent: this.connectionPolicy.UserAgentContainer.BaseUserAgent,
                            connectionMode: this.connectionPolicy.ConnectionMode,
                            authorizationTokenProvider: this.cosmosAuthorization,
                            diagnosticsHelper: DiagnosticsHandlerHelper.Instance,
                            preferredRegions: this.connectionPolicy.PreferredLocations,
                            globalEndpointManager: this.globalEndpointManager,
                            databaseAccountClientConfigs: databaseAccountClientConfigs);

                        DefaultTrace.TraceVerbose("Client Telemetry Enabled.");
                    }
                    catch (Exception ex)
                    {
                        DefaultTrace.TraceWarning($"Error While starting Telemetry Job : {ex.Message}");
                    }
                }
                else
                {
                    DefaultTrace.TraceVerbose("Client Telemetry Job already running.");
                }
            }
            else
            {
                if (this.clientTelemetryInstance != null)
                {
                    DefaultTrace.TraceInformation("Stopping Client Telemetry Job.");

                    this.clientTelemetryInstance.Dispose();

                    this.clientTelemetryInstance = null;
                }
                else
                {
                    DefaultTrace.TraceInformation("Client Telemetry Disabled.");
                }
            }
        }

        public void Dispose()
        {
            this.clientTelemetryInstance?.Dispose();
        }
    }
}
