// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi.Models;

namespace Microsoft.AspNetCore.OpenApi;

/// <summary>
/// Defines a set of methods for generating OpenAPI definitions for endpoints.
/// </summary>
internal sealed class OpenApiGenerator
{
    private readonly IHostEnvironment? _environment;
    private readonly IServiceProviderIsService? _serviceProviderIsService;

    internal static readonly ParameterBindingMethodCache ParameterBindingMethodCache = new();

    /// <summary>
    /// Creates an <see cref="OpenApiGenerator" /> instance given an <see cref="IHostEnvironment" />
    /// and an <see cref="IServiceProviderIsService" /> instance.
    /// </summary>
    /// <param name="environment">The host environment.</param>
    /// <param name="serviceProviderIsService">The service to determine if the type is available from the <see cref="IServiceProvider"/>.</param>
    internal OpenApiGenerator(
        IHostEnvironment? environment,
        IServiceProviderIsService? serviceProviderIsService)
    {
        _environment = environment;
        _serviceProviderIsService = serviceProviderIsService;
    }

    /// <summary>
    /// Generates an <see cref="OpenApiPathItem"/> for a given <see cref="Endpoint" />.
    /// </summary>
    /// <param name="methodInfo">The <see cref="MethodInfo"/> associated with the route handler of the endpoint.</param>
    /// <param name="metadata">The endpoint <see cref="EndpointMetadataCollection"/>.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <returns>An <see cref="OpenApiPathItem"/> annotation derived from the given inputs.</returns>
    internal OpenApiOperation? GetOpenApiOperation(
        MethodInfo methodInfo,
        EndpointMetadataCollection metadata,
        RoutePattern pattern)
    {
        if (metadata.GetMetadata<IHttpMethodMetadata>() is { } httpMethodMetadata &&
            httpMethodMetadata.HttpMethods.SingleOrDefault() is { } method &&
            metadata.GetMetadata<IExcludeFromDescriptionMetadata>() is null or { ExcludeFromDescription: false })
        {
            return GetOperation(method, methodInfo, metadata, pattern);
        }

        return null;
    }

    private OpenApiOperation GetOperation(string httpMethod, MethodInfo methodInfo, EndpointMetadataCollection metadata, RoutePattern pattern)
    {
        var disableInferredBody = ShouldDisableInferredBody(httpMethod);
        return new OpenApiOperation
        {
            OperationId = metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
            Summary = metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary,
            Description = metadata.GetMetadata<IEndpointDescriptionMetadata>()?.Description,
            Tags = GetOperationTags(methodInfo, metadata),
            Parameters = GetOpenApiParameters(methodInfo, metadata, pattern, disableInferredBody),
            RequestBody = GetOpenApiRequestBody(methodInfo, metadata, pattern),
            Responses = GetOpenApiResponses(methodInfo, metadata)
        };

        static bool ShouldDisableInferredBody(string method)
        {
            // GET, DELETE, HEAD, CONNECT, TRACE, and OPTIONS normally do not contain bodies
            return method.Equals(HttpMethods.Get, StringComparison.Ordinal) ||
                   method.Equals(HttpMethods.Delete, StringComparison.Ordinal) ||
                   method.Equals(HttpMethods.Head, StringComparison.Ordinal) ||
                   method.Equals(HttpMethods.Options, StringComparison.Ordinal) ||
                   method.Equals(HttpMethods.Trace, StringComparison.Ordinal) ||
                   method.Equals(HttpMethods.Connect, StringComparison.Ordinal);
        }
    }

    private static OpenApiResponses GetOpenApiResponses(MethodInfo method, EndpointMetadataCollection metadata)
    {
        var responses = new OpenApiResponses();
        var responseType = method.ReturnType;
        if (AwaitableInfo.IsTypeAwaitable(responseType, out var awaitableInfo))
        {
            responseType = awaitableInfo.ResultType;
        }

        if (typeof(IResult).IsAssignableFrom(responseType))
        {
            responseType = typeof(void);
        }

        var errorMetadata = metadata.GetMetadata<ProducesErrorResponseTypeAttribute>();
        var defaultErrorType = errorMetadata?.Type;

        var responseProviderMetadata = metadata.GetOrderedMetadata<IApiResponseMetadataProvider>();
        var producesResponseMetadata = metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();

        var eligibileAnnotations = new Dictionary<int, (Type?, MediaTypeCollection)>();

        foreach (var responseMetadata in producesResponseMetadata)
        {
            var statusCode = responseMetadata.StatusCode;

            var discoveredTypeAnnotation = responseMetadata.Type;
            var discoveredContentTypeAnnotation = new MediaTypeCollection();

            if (discoveredTypeAnnotation == typeof(void))
            {
                if (responseType != null && (statusCode == StatusCodes.Status200OK || statusCode == StatusCodes.Status201Created))
                {
                    discoveredTypeAnnotation = responseType;
                }
            }

            foreach (var contentType in responseMetadata.ContentTypes)
            {
                discoveredContentTypeAnnotation.Add(contentType);
            }

            discoveredTypeAnnotation = discoveredTypeAnnotation == null || discoveredTypeAnnotation == typeof(void)
                ? responseType
                : discoveredTypeAnnotation;

            if (discoveredTypeAnnotation is not null)
            {
                GenerateDefaultContent(discoveredContentTypeAnnotation, discoveredTypeAnnotation);
                eligibileAnnotations.Add(statusCode, (discoveredTypeAnnotation, discoveredContentTypeAnnotation));
            }
        }

        foreach (var providerMetadata in responseProviderMetadata)
        {
            var statusCode = providerMetadata.StatusCode;

            var discoveredTypeAnnotation = providerMetadata.Type;
            var discoveredContentTypeAnnotation = new MediaTypeCollection();

            if (discoveredTypeAnnotation == typeof(void))
            {
                if (responseType != null && (statusCode == StatusCodes.Status200OK || statusCode == StatusCodes.Status201Created))
                {
                    // ProducesResponseTypeAttribute's constructor defaults to setting "Type" to void when no value is specified.
                    // In this event, use the action's return type for 200 or 201 status codes. This lets you decorate an action with a
                    // [ProducesResponseType(201)] instead of [ProducesResponseType(typeof(Person), 201] when typeof(Person) can be inferred
                    // from the return type.
                    discoveredTypeAnnotation = responseType;
                }
                else if (statusCode >= 400 && statusCode < 500)
                {
                    // Determine whether or not the type was provided by the user. If so, favor it over the default
                    // error type for 4xx client errors if no response type is specified.
                    discoveredTypeAnnotation = defaultErrorType is not null ? defaultErrorType : discoveredTypeAnnotation;
                }
                else if (providerMetadata is IApiDefaultResponseMetadataProvider)
                {
                    discoveredTypeAnnotation = defaultErrorType;
                }
            }

            providerMetadata.SetContentTypes(discoveredContentTypeAnnotation);

            discoveredTypeAnnotation = discoveredTypeAnnotation == null || discoveredTypeAnnotation == typeof(void)
                ? responseType
                : discoveredTypeAnnotation;

            GenerateDefaultContent(discoveredContentTypeAnnotation, discoveredTypeAnnotation);
            eligibileAnnotations.Add(statusCode, (discoveredTypeAnnotation, discoveredContentTypeAnnotation));
        }

        if (eligibileAnnotations.Count == 0)
        {
            GenerateDefaultResponses(eligibileAnnotations, responseType);
        }

        foreach (var annotation in eligibileAnnotations)
        {
            var statusCode = annotation.Key.ToString(CultureInfo.InvariantCulture);
            var (type, contentTypes) = annotation.Value;
            var responseContent = new Dictionary<string, OpenApiMediaType>();

            foreach (var contentType in contentTypes)
            {
                responseContent[contentType] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema { Type = SchemaGenerator.GetOpenApiSchemaType(type) }
                };
            }

            responses[statusCode] = new OpenApiResponse { Content = responseContent };
        }

        return responses;
    }

    private static void GenerateDefaultContent(MediaTypeCollection discoveredContentTypeAnnotation, Type? discoveredTypeAnnotation)
    {
        if (discoveredContentTypeAnnotation.Count == 0)
        {
            if (discoveredTypeAnnotation == typeof(void) || discoveredTypeAnnotation == null)
            {
                return;
            }
            if (discoveredTypeAnnotation == typeof(string))
            {
                discoveredContentTypeAnnotation.Add("text/plain");
            }
            else
            {
                discoveredContentTypeAnnotation.Add("application/json");
            }
        }
    }

    private static void GenerateDefaultResponses(Dictionary<int, (Type?, MediaTypeCollection)> eligibleAnnotations, Type responseType)
    {
        if (responseType == typeof(void))
        {
            eligibleAnnotations.Add(StatusCodes.Status200OK, (responseType, new MediaTypeCollection()));
        }
        else if (responseType == typeof(string))
        {
            eligibleAnnotations.Add(StatusCodes.Status200OK, (responseType, new MediaTypeCollection() { "text/plain" }));
        }
        else
        {
            eligibleAnnotations.Add(StatusCodes.Status200OK, (responseType, new MediaTypeCollection() { "application/json" }));
        }
    }

    private OpenApiRequestBody? GetOpenApiRequestBody(MethodInfo methodInfo, EndpointMetadataCollection metadata, RoutePattern pattern)
    {
        var hasFormOrBodyParameter = false;
        ParameterInfo? requestBodyParameter = null;

        foreach (var parameter in methodInfo.GetParameters())
        {
            var (bodyOrFormParameter, _) = GetOpenApiParameterLocation(parameter, pattern, false);
            hasFormOrBodyParameter |= bodyOrFormParameter;
            if (hasFormOrBodyParameter)
            {
                requestBodyParameter = parameter;
                break;
            }
        }

        var acceptsMetadata = metadata.GetMetadata<IAcceptsMetadata>();
        var requestBodyContent = new Dictionary<string, OpenApiMediaType>();
        var isRequired = false;

        if (acceptsMetadata is not null)
        {
            foreach (var contentType in acceptsMetadata.ContentTypes)
            {
                requestBodyContent[contentType] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = SchemaGenerator.GetOpenApiSchemaType(acceptsMetadata.RequestType ?? requestBodyParameter?.ParameterType)
                    }
                };
            }
            isRequired = !acceptsMetadata.IsOptional;
        }

        if (!hasFormOrBodyParameter)
        {
            return new OpenApiRequestBody()
            {
                Required = isRequired,
                Content = requestBodyContent
            };
        }

        if (requestBodyParameter is not null)
        {
            if (requestBodyContent.Count == 0)
            {
                var isFormType = requestBodyParameter.ParameterType == typeof(IFormFile) || requestBodyParameter.ParameterType == typeof(IFormFileCollection);
                var hasFormAttribute = requestBodyParameter.GetCustomAttributes().OfType<IFromFormMetadata>().FirstOrDefault() != null;
                if (isFormType || hasFormAttribute)
                {
                    requestBodyContent["multipart/form-data"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = SchemaGenerator.GetOpenApiSchemaType(requestBodyParameter.ParameterType)
                        }
                    };
                }
                else
                {
                    requestBodyContent["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = SchemaGenerator.GetOpenApiSchemaType(requestBodyParameter.ParameterType)
                        }
                    };
                }
            }

            var nullabilityContext = new NullabilityInfoContext();
            var nullability = nullabilityContext.Create(requestBodyParameter);
            var allowEmpty = requestBodyParameter.GetCustomAttributes().OfType<IFromBodyMetadata>().SingleOrDefault()?.AllowEmpty ?? false;
            var isOptional = requestBodyParameter.HasDefaultValue
                || nullability.ReadState != NullabilityState.NotNull
                || allowEmpty;

            return new OpenApiRequestBody
            {
                Required = !isOptional,
                Content = requestBodyContent
            };
        }

        return null;
    }

    private List<OpenApiTag> GetOperationTags(MethodInfo methodInfo, EndpointMetadataCollection metadata)
    {
        var tags = metadata.GetMetadata<ITagsMetadata>();
        string controllerName;

        if (methodInfo.DeclaringType is not null && !TypeHelper.IsCompilerGeneratedType(methodInfo.DeclaringType))
        {
            controllerName = methodInfo.DeclaringType.Name;
        }
        else
        {
            // If the declaring type is null or compiler-generated (e.g. lambdas),
            // group the methods under the application name.
            controllerName = _environment?.ApplicationName ?? string.Empty;
        }

        return tags is not null
            ? tags.Tags.Select(tag => new OpenApiTag() { Name = tag }).ToList()
            : new List<OpenApiTag>() { new OpenApiTag() { Name = controllerName } };
    }

    private List<OpenApiParameter> GetOpenApiParameters(MethodInfo methodInfo, EndpointMetadataCollection metadata, RoutePattern pattern, bool disableInferredBody)
    {
        var parameters = methodInfo.GetParameters();
        var openApiParameters = new List<OpenApiParameter>();

        foreach (var parameter in parameters)
        {
            var (isBodyOrFormParameter, parameterLocation) = GetOpenApiParameterLocation(parameter, pattern, disableInferredBody);

            // If the parameter isn't something that would be populated in RequestBody
            // or doesn't have a valid ParameterLocation, then it must be a service
            // parameter that we can ignore.
            if (!isBodyOrFormParameter && parameterLocation is null)
            {
                continue;
            }

            var nullabilityContext = new NullabilityInfoContext();
            var nullability = nullabilityContext.Create(parameter);
            var isOptional = parameter.HasDefaultValue || nullability.ReadState != NullabilityState.NotNull;
            var openApiParameter = new OpenApiParameter()
            {
                Name = parameter.Name,
                In = parameterLocation,
                Content = GetOpenApiParameterContent(metadata),
                Schema = new OpenApiSchema { Type = SchemaGenerator.GetOpenApiSchemaType(parameter.ParameterType) },
                Required = !isOptional

            };
            openApiParameters.Add(openApiParameter);
        }

        return openApiParameters;
    }

    private static Dictionary<string, OpenApiMediaType> GetOpenApiParameterContent(EndpointMetadataCollection metadata)
    {
        var openApiParameterContent = new Dictionary<string, OpenApiMediaType>();
        var acceptsMetadata = metadata.GetMetadata<IAcceptsMetadata>();
        if (acceptsMetadata is not null)
        {
            foreach (var contentType in acceptsMetadata.ContentTypes)
            {
                openApiParameterContent.Add(contentType, new OpenApiMediaType());
            }
        }

        return openApiParameterContent;
    }

    private (bool isBodyOrForm, ParameterLocation? locatedIn) GetOpenApiParameterLocation(ParameterInfo parameter, RoutePattern pattern, bool disableInferredBody)
    {
        var attributes = parameter.GetCustomAttributes();

        if (attributes.OfType<IFromRouteMetadata>().FirstOrDefault() is { } routeAttribute)
        {
            return (false, ParameterLocation.Path);
        }
        else if (attributes.OfType<IFromQueryMetadata>().FirstOrDefault() is { } queryAttribute)
        {
            return (false, ParameterLocation.Query);
        }
        else if (attributes.OfType<IFromHeaderMetadata>().FirstOrDefault() is { } headerAttribute)
        {
            return (false, ParameterLocation.Header);
        }
        else if (attributes.OfType<IFromBodyMetadata>().FirstOrDefault() is { } fromBodyAttribute)
        {
            return (true, null);
        }
        else if (attributes.OfType<IFromFormMetadata>().FirstOrDefault() is { } fromFormAttribute)
        {
            return (true, null);
        }
        else if (parameter.CustomAttributes.Any(a => typeof(IFromServiceMetadata).IsAssignableFrom(a.AttributeType)) ||
                parameter.ParameterType == typeof(HttpContext) ||
                parameter.ParameterType == typeof(HttpRequest) ||
                parameter.ParameterType == typeof(HttpResponse) ||
                parameter.ParameterType == typeof(ClaimsPrincipal) ||
                parameter.ParameterType == typeof(CancellationToken) ||
                ParameterBindingMethodCache.HasBindAsyncMethod(parameter) ||
                _serviceProviderIsService?.IsService(parameter.ParameterType) == true)
        {
            return (false, null);
        }
        else if (parameter.ParameterType == typeof(string) || ParameterBindingMethodCache.HasTryParseMethod(parameter.ParameterType))
        {
            // complex types will display as strings since they use custom parsing via TryParse on a string
            var displayType = !parameter.ParameterType.IsPrimitive && Nullable.GetUnderlyingType(parameter.ParameterType)?.IsPrimitive != true
                ? typeof(string) : parameter.ParameterType;
            // Path vs query cannot be determined by RequestDelegateFactory at startup currently because of the layering, but can be done here.
            if (parameter.Name is { } name && pattern.GetParameter(name) is not null)
            {
                return (false, ParameterLocation.Path);
            }
            else
            {
                return (false, ParameterLocation.Query);
            }
        }
        else if (parameter.ParameterType == typeof(IFormFile) || parameter.ParameterType == typeof(IFormFileCollection))
        {
            return (true, null);
        }
        else if (disableInferredBody && (
                 (parameter.ParameterType.IsArray && ParameterBindingMethodCache.HasTryParseMethod(parameter.ParameterType.GetElementType()!)) ||
                 parameter.ParameterType == typeof(string[]) ||
                 parameter.ParameterType == typeof(StringValues)))
        {
            return (false, ParameterLocation.Query);
        }
        else
        {
            return (true, null);
        }
    }
}
