﻿// Copyright (c) Brock Allen & Dominick Baier. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using IdentityModel.AspNetCore.OAuth2Introspection;
using IdentityModel.Client;
using IdentityServer4.AccessTokenValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Microsoft.AspNetCore.Builder
{
    /// <summary>
    /// Options for IdentityServer authentication
    /// </summary>
    public class IdentityServerAuthenticationOptions : AuthenticationSchemeOptions
    {
        static readonly Func<HttpRequest, string> InternalTokenRetriever = request => request.HttpContext.Items[IdentityServerAuthenticationDefaults.TokenItemsKey] as string;
        
        /// <summary>
        /// Base-address of the token issuer
        /// </summary>
        public string Authority { get; set; }

        /// <summary>
        /// Specifies whether HTTPS is required for the discovery endpoint
        /// </summary>
        public bool RequireHttpsMetadata { get; set; } = true;

        /// <summary>
        /// Specifies which token types are supported (JWT, reference or both)
        /// </summary>
        public SupportedTokens SupportedTokens { get; set; } = SupportedTokens.Both;

        /// <summary>
        /// Callback to retrieve token from incoming request
        /// </summary>
        public Func<HttpRequest, string> TokenRetriever { get; set; } = TokenRetrieval.FromAuthorizationHeader();

        /// <summary>
        /// Name of the API resource used for authentication against introspection endpoint
        /// </summary>
        public string ApiName { get; set; }

        /// <summary>
        /// Secret used for authentication against introspection endpoint
        /// </summary>
        public string ApiSecret { get; set; }

        /// <summary>
        /// Enable if this API is being secured by IdentityServer3, and if you need to support both JWTs and reference tokens.
        /// If you enable this, you should add scope validation for incoming JWTs.
        /// </summary>
        public bool LegacyAudienceValidation { get; set; } = false;

        /// <summary>
        /// Claim type for name
        /// </summary>
        public string NameClaimType { get; set; } = "name";

        /// <summary>
        /// Claim type for role
        /// </summary>
        public string RoleClaimType { get; set; } = "role";

        /// <summary>
        /// Specifies inbound claim type map for JWT tokens (mainly used to disable the annoying default behavior of the MS JWT handler)
        /// </summary>
        public Dictionary<string, string> InboundJwtClaimTypeMap { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Specifies whether caching is enabled for introspection responses (requires a distributed cache implementation)
        /// </summary>
        public bool EnableCaching { get; set; } = false;

        /// <summary>
        /// Specifies ttl for introspection response caches
        /// </summary>
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// specifies whether the token should be saved in the authentication properties
        /// </summary>
        public bool SaveToken { get; set; } = true;

        /// <summary>
        /// back-channel handler for JWT middleware
        /// </summary>
        public HttpMessageHandler JwtBackChannelHandler { get; set; }

        /// <summary>
        /// back-channel handler for introspection endpoint
        /// </summary>
        public HttpMessageHandler IntrospectionBackChannelHandler { get; set; }

        /// <summary>
        /// back-channel handler for introspection discovery endpoint
        /// </summary>
        public HttpMessageHandler IntrospectionDiscoveryHandler { get; set; }

        /// <summary>
        /// timeout for back-channel operations
        /// </summary>
        public TimeSpan BackChannelTimeouts { get; set; } = TimeSpan.FromSeconds(60);

        // todo
        /// <summary>
        /// events for JWT middleware
        /// </summary>
        public JwtBearerEvents JwtBearerEvents { get; set; } = new JwtBearerEvents();

        /// <summary>
        /// Specifies how often the cached copy of the discovery document should be refreshed.
        /// If not set, it defaults to the default value of Microsoft's underlying configuration manager (which right now is 24h).
        /// If you need more fine grained control, provide your own configuration manager on the JWT options.
        /// </summary>
        public TimeSpan? DiscoveryDocumentRefreshInterval { get; set; }

        /// <summary>
        /// Gets a value indicating whether JWTs are supported.
        /// </summary>
        public bool SupportsJwt => SupportedTokens == SupportedTokens.Jwt || SupportedTokens == SupportedTokens.Both;

        /// <summary>
        /// Gets a value indicating whether reference tokens are supported.
        /// </summary>
        public bool SupportsIntrospection => SupportedTokens == SupportedTokens.Reference || SupportedTokens == SupportedTokens.Both;

        internal void ConfigureJwtBearer(JwtBearerOptions jwtOptions)
        {
            jwtOptions.Authority = Authority;
            jwtOptions.RequireHttpsMetadata = RequireHttpsMetadata;
            jwtOptions.BackchannelTimeout = BackChannelTimeouts;
            jwtOptions.RefreshOnIssuerKeyNotFound = true;
            jwtOptions.SaveToken = SaveToken;
            jwtOptions.Events = new JwtBearerEvents
            {
                OnMessageReceived = e =>
                {
                    e.Token = InternalTokenRetriever(e.Request);
                    return JwtBearerEvents.MessageReceived(e);
                },

                OnTokenValidated = e => JwtBearerEvents.TokenValidated(e),
                OnAuthenticationFailed = e => JwtBearerEvents.AuthenticationFailed(e),
                OnChallenge = e => JwtBearerEvents.Challenge(e)
            };

            if (DiscoveryDocumentRefreshInterval.HasValue)
            {
                var parsedUrl = DiscoveryClient.ParseUrl(Authority);

                var httpClient = new HttpClient(JwtBackChannelHandler ?? new HttpClientHandler())
                {
                    Timeout = BackChannelTimeouts,
                    MaxResponseContentBufferSize = 1024 * 1024 * 10 // 10 MB
                };

                var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    parsedUrl.discoveryEndpoint,
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever(httpClient) { RequireHttps = RequireHttpsMetadata })
                {
                    AutomaticRefreshInterval = DiscoveryDocumentRefreshInterval.Value
                };

                jwtOptions.ConfigurationManager = manager;
            }

            if (JwtBackChannelHandler != null)
            {
                jwtOptions.BackchannelHttpHandler = JwtBackChannelHandler;
            }

            // if API name is set, do a strict audience check for
            if (!string.IsNullOrWhiteSpace(ApiName) && !LegacyAudienceValidation)
            {
                jwtOptions.Audience = ApiName;
            }
            else
            {
                // no audience validation, rely on scope checks only
                jwtOptions.TokenValidationParameters.ValidateAudience = false;
            }

            jwtOptions.TokenValidationParameters.NameClaimType = NameClaimType;
            jwtOptions.TokenValidationParameters.RoleClaimType = RoleClaimType;

            if (InboundJwtClaimTypeMap != null)
            {
                var handler = new JwtSecurityTokenHandler
                {
                    InboundClaimTypeMap = InboundJwtClaimTypeMap
                };

                jwtOptions.SecurityTokenValidators.Clear();
                jwtOptions.SecurityTokenValidators.Add(handler);
            }
        }

        internal void ConfigureIntrospection(OAuth2IntrospectionOptions introspectionOptions)
        {
            if (String.IsNullOrWhiteSpace(ApiSecret))
            {
                return;
            }

            if (String.IsNullOrWhiteSpace(ApiName))
            {
                throw new ArgumentException("ApiName must be configured if ApiSecret is set.");
            }

            introspectionOptions.Authority = Authority;
            introspectionOptions.ClientId = ApiName;
            introspectionOptions.ClientSecret = ApiSecret;
            introspectionOptions.NameClaimType = NameClaimType;
            introspectionOptions.RoleClaimType = RoleClaimType;
            introspectionOptions.TokenRetriever = InternalTokenRetriever;
            introspectionOptions.SaveToken = SaveToken;

            introspectionOptions.EnableCaching = EnableCaching;
            introspectionOptions.CacheDuration = CacheDuration;

            introspectionOptions.DiscoveryTimeout = BackChannelTimeouts;
            introspectionOptions.IntrospectionTimeout = BackChannelTimeouts;

            introspectionOptions.DiscoveryPolicy.RequireHttps = RequireHttpsMetadata;

            if (IntrospectionBackChannelHandler != null)
            {
                introspectionOptions.IntrospectionHttpHandler = IntrospectionBackChannelHandler;
            }

            if (IntrospectionDiscoveryHandler != null)
            {
                introspectionOptions.DiscoveryHttpHandler = IntrospectionDiscoveryHandler;
            }
        }
    }
}