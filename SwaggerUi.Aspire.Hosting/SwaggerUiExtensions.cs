﻿using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

public static class SwaggerUiExtensions
{
    /// <summary>
    /// Maps the swagger ui endpoint to the application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="documentNames">The list of open api documents. Defaults to "v1" if null.</param>
    /// <param name="path">The path to the open api document.</param>
    /// <param name="endpointName">The endpoint name</param>
    public static IResourceBuilder<ProjectResource> WithSwaggerUi(this IResourceBuilder<ProjectResource> builder,
        string[]? documentNames = null, string path = "swagger/v1/swagger.json", string endpointName = "http")
    {
        if (!builder.ApplicationBuilder.Resources.OfType<SwaggerUiResource>().Any())
        {
            // Add the swagger ui code hook and resource
            builder.ApplicationBuilder.Services.TryAddLifecycleHook<SwaggerUiHook>();
            builder.ApplicationBuilder.AddResource(new SwaggerUiResource("swagger-ui"))
                .WithInitialState(new CustomResourceSnapshot
                {
                    ResourceType = "swagger-ui",
                    Properties = [],
                    State = "Starting"
                })
                .ExcludeFromManifest();
        }

        return builder.WithAnnotation(new SwaggerUiAnnotation(documentNames ?? ["v1"], path, builder.GetEndpoint(endpointName)));
    }

    class SwaggerUiHook(ResourceNotificationService notificationService,
               ResourceLoggerService resourceLoggerService) : IDistributedApplicationLifecycleHook
    {
        public async Task AfterEndpointsAllocatedAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
        {
            var openApiResource = appModel.Resources.OfType<SwaggerUiResource>().SingleOrDefault();

            if (openApiResource is null)
            {
                return;
            }

            // We host a single webserver that will manage the swagger ui endpoints for all resources
            var builder = WebApplication.CreateSlimBuilder();

            builder.Services.AddHttpForwarder();
            builder.Logging.ClearProviders();

            builder.Logging.AddProvider(new ResourceLoggerProvider(resourceLoggerService.GetLogger(openApiResource.Name)));

            var app = builder.Build();

            // openapi/resourcename/documentname.json
            app.MapSwaggerUi();

            var swaggerUiPaths = new List<string>();
            var resourceToEndpoint = new Dictionary<string, (string, string)>();
            var hostToResourceUrl = new Dictionary<int, string>();

            foreach (var r in appModel.Resources)
            {
                if (!r.TryGetLastAnnotation<SwaggerUiAnnotation>(out var annotation))
                {
                    continue;
                }

                // We store the url and path for each resource so we can hit the open api endpoint
                resourceToEndpoint[r.Name] = (annotation.EndpointReference.Url, annotation.Path);

                // We store the URL for the resource on the host so we can map it back to the actual address once they are allocated
                hostToResourceUrl[app.Urls.Count] = annotation.EndpointReference.Url;

                // We add a new URL for each resource that has a swagger ui annotation
                // This is because swagger ui takes over the entire url space
                app.Urls.Add("http://127.0.0.1:0");

                // To avoid cors issues, we expose URLs that send requests to the apphost and then forward them to the actual resource
                foreach (var documentName in annotation.DocumentNames)
                {
                    swaggerUiPaths.Add($"swagger/{r.Name}/{documentName}");
                }
            }

            var client = new HttpMessageInvoker(new SocketsHttpHandler());

            // Swagger UI will make requests to the apphost so we can avoid doing any CORS configuration.
            app.Map("/openapi/{resourceName}/{documentName}.json",
                async (string resourceName, string documentName, IHttpForwarder forwarder, HttpContext context) =>
            {
                var (endpoint, path) = resourceToEndpoint[resourceName];

                await forwarder.SendAsync(context, endpoint, client, (c, r) =>
                {
                    r.RequestUri = new($"{endpoint}/{path}");
                    return ValueTask.CompletedTask;
                });
            });

            app.Map("{*path}", async (HttpContext context, IHttpForwarder forwarder, string? path) =>
            {
                var endpoint = hostToResourceUrl[context.Connection.LocalPort];

                await forwarder.SendAsync(context, endpoint, client, (c, r) =>
                {
                    r.RequestUri = path is null ? new(endpoint) : new($"{endpoint}/{path}");
                    return ValueTask.CompletedTask;
                });
            });

            await app.StartAsync(cancellationToken);

            var addresses = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses;

            // Map our index back to the actual address
            var index = 0;
            foreach (var rawAddress in addresses)
            {
                var address = BindingAddress.Parse(rawAddress);

                // We map the bound port to the resource URL. This lets us forward requests to the correct resource
                hostToResourceUrl[address.Port] = hostToResourceUrl[index++];
            }

            await notificationService.PublishUpdateAsync(openApiResource, s => s with
            {
                State = "Running",
                Urls = [.. addresses.Zip(swaggerUiPaths, (a, p) => new UrlSnapshot(a, $"{a}/{p}", IsInternal: false))]
            });
        }
    }

    private class ResourceLoggerProvider(ILogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return new ResourceLogger(logger);
        }

        public void Dispose()
        {
        }

        private class ResourceLogger(ILogger logger) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return logger.BeginScope(state);
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logger.IsEnabled(logLevel);
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                logger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}