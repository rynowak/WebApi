using System;
using System.Collections.Generic;
using Microsoft.AspNet.OData;
using Microsoft.AspNet.OData.Adapters;
using Microsoft.AspNet.OData.Common;
using Microsoft.AspNet.OData.Extensions;
using Microsoft.AspNet.OData.Formatter;
using Microsoft.AspNet.OData.Interfaces;
using Microsoft.AspNet.OData.Query;
using Microsoft.AspNet.OData.Routing;
using Microsoft.AspNet.OData.Routing.Conventions;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData;
using Microsoft.OData.Edm;

namespace Microsoft.AspNetCore.Builder
{
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public static class EndpointRouteBuilderExtensions
    {
        public static void MapODataServiceRoute(this IEndpointRouteBuilder endpoints, string routeName,string routePrefix, IEdmModel model)
        {
            endpoints.MapODataServiceRoute(routeName, routePrefix, containerBuilder =>
                containerBuilder.AddService(Microsoft.OData.ServiceLifetime.Singleton, sp => model)
                       .AddService<IEnumerable<IODataRoutingConvention>>(Microsoft.OData.ServiceLifetime.Singleton, sp =>
                           ODataRoutingConventions.CreateDefaultWithAttributeRouting(routeName, endpoints)));
        }

        public static void MapODataServiceRoute(this IEndpointRouteBuilder endpoints, string routeName, string prefix, Action<IContainerBuilder> configureAction)
        {
            endpoints.MapControllers(); // Called for side-effect

            endpoints.MapDynamicControllerRoute<ODataDynamicRouteValueTransformer>($"{prefix}/{{**{routeName}}}");

            // Add a "link generation route" so we can configure the route name.
            endpoints.Map(
                $"{prefix}/{{**{routeName}}}",
                context => throw new InvalidOperationException("This endpoint should not be executed."))
                .Add(b =>
                {
                    b.Metadata.Add(new SuppressMatchingMetadata());
                    b.Metadata.Add(new RouteNameMetadata(routeName));
                    b.Metadata.Add(new EndpointNameMetadata(routeName));
                });

            // Build and configure the root container.
            IPerRouteContainer perRouteContainer = endpoints.ServiceProvider.GetRequiredService<IPerRouteContainer>();
            if (perRouteContainer == null)
            {
                throw Error.InvalidOperation(SRResources.MissingODataServices, nameof(IPerRouteContainer));
            }

            // Create an service provider for this route. Add the default services to the custom configuration actions.
            Action<IContainerBuilder> builderAction = ConfigureDefaultServices(endpoints, configureAction);
            IServiceProvider serviceProvider = perRouteContainer.CreateODataRootContainer(routeName, builderAction);

            // Make sure the MetadataController is registered with the ApplicationPartManager.
            ApplicationPartManager applicationPartManager = endpoints.ServiceProvider.GetRequiredService<ApplicationPartManager>();
            applicationPartManager.ApplicationParts.Add(new AssemblyPart(typeof(MetadataController).Assembly));

            // Resolve the path handler and set URI resolver to it.
            IODataPathHandler pathHandler = serviceProvider.GetRequiredService<IODataPathHandler>();

            // If settings is not on local, use the global configuration settings.
            ODataOptions options = endpoints.ServiceProvider.GetRequiredService<ODataOptions>();
            if (pathHandler != null && pathHandler.UrlKeyDelimiter == null)
            {
                pathHandler.UrlKeyDelimiter = options.UrlKeyDelimiter;
            }

            // Resolve some required services and create the route constraint.
            ODataPathRouteConstraint routeConstraint = new ODataPathRouteConstraint(routeName);

            // Get constraint resolver.
            IInlineConstraintResolver inlineConstraintResolver = endpoints
                .ServiceProvider
                .GetRequiredService<IInlineConstraintResolver>();

            // HACK: batching?
        }

        internal static Action<IContainerBuilder> ConfigureDefaultServices(IEndpointRouteBuilder endpoints, Action<IContainerBuilder> configureAction)
        {
            return (builder =>
            {
                // Add platform-specific services here. Add Configuration first as other services may rely on it.
                // For assembly resolution, add the and internal (IWebApiAssembliesResolver) where IWebApiAssembliesResolver
                // is transient and instantiated from ApplicationPartManager by DI.
                builder.AddService<IWebApiAssembliesResolver, WebApiAssembliesResolver>(OData.ServiceLifetime.Transient);
                builder.AddService<IODataPathTemplateHandler, DefaultODataPathHandler>(OData.ServiceLifetime.Singleton);
                builder.AddService<IETagHandler, DefaultODataETagHandler>(OData.ServiceLifetime.Singleton);

                // Access the default query settings and options from the global container.
                builder.AddService(OData.ServiceLifetime.Singleton, sp => GetDefaultQuerySettings(endpoints));
                builder.AddService(OData.ServiceLifetime.Singleton, sp => GetDefaultODataOptions(endpoints));

                // Add the default webApi services.
                builder.AddDefaultWebApiServices();

                // Add custom actions.
                configureAction?.Invoke(builder);
            });
        }

        private static DefaultQuerySettings GetDefaultQuerySettings(this IEndpointRouteBuilder builder)
        {
            if (builder == null)
            {
                throw Error.ArgumentNull("builder");
            }

            DefaultQuerySettings querySettings = builder.ServiceProvider.GetRequiredService<DefaultQuerySettings>();
            if (querySettings == null)
            {
                throw Error.InvalidOperation(SRResources.MissingODataServices, nameof(DefaultQuerySettings));
            }

            return querySettings;
        }

        private static ODataOptions GetDefaultODataOptions(this IEndpointRouteBuilder builder)
        {
            if (builder == null)
            {
                throw Error.ArgumentNull("builder");
            }

            ODataOptions options = builder.ServiceProvider.GetRequiredService<ODataOptions>();
            if (options == null)
            {
                throw Error.InvalidOperation(SRResources.MissingODataServices, nameof(ODataOptions));
            }

            return options;
        }
    }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
}
