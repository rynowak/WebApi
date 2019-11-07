// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNet.OData.Common;
using Microsoft.AspNet.OData.Extensions;
using Microsoft.AspNet.OData.Interfaces;
using Microsoft.AspNet.OData.Routing;
using Microsoft.AspNet.OData.Routing.Conventions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData;

namespace Microsoft.AspNetCore.Mvc.Routing
{
    internal class ODataDynamicRouteValueTransformer : DynamicRouteValueTransformer
    {
        // "%2F"
        private static readonly string _escapedSlash = Uri.EscapeDataString("/");

        public override ValueTask<RouteValueDictionary> TransformAsync(HttpContext httpContext, RouteValueDictionary values)
        {
            // HACK: we pass the "Route Name" as the key of the single route value.
            if (values.Count != 1)
            {
                return default;
            }

            try
            {
                var single = values.Single();
                var routeName = single.Key;
                var oDataPathValue = single.Value;

                values.Remove(routeName);
                values.Add(ODataRouteConstants.ODataPath, oDataPathValue);

                HttpRequest request = httpContext.Request;
                ODataPath path = null;

                // We need to call Uri.GetLeftPart(), which returns an encoded Url.
                // The ODL parser does not like raw values.
                Uri requestUri = new Uri(request.GetEncodedUrl());
                string requestLeftPart = requestUri.GetLeftPart(UriPartial.Path);
                string queryString = request.QueryString.HasValue ? request.QueryString.ToString() : null;

                path = GetODataPath(oDataPathValue as string, requestLeftPart, queryString, () => request.CreateRequestContainer(routeName));

                if (path == null)
                {
                    // The request doesn't match this route.
                    return default;
                }

                // HACK: This code (and the existing code) has the assumption that either:
                //  1. Routes are processed in sequential order OR
                //  2. Routes are disjoint (only one will match a given request)
                //
                // The fact of Endpoint Routing is that multiple matches *could* happen for a request - but OData uses
                // a single-use feature to communicate that.
                //

                // Set all the properties we need for routing, querying, formatting.
                IODataFeature odataFeature = httpContext.ODataFeature();
                odataFeature.Path = path;
                odataFeature.RouteName = routeName;

                IEnumerable<IODataRoutingConvention> routingConventions = request.GetRoutingConventions();
                if (routingConventions != null)
                {
                    var routeContext = new RouteContext(httpContext);
                    foreach (var kvp in values)
                    {
                        routeContext.RouteData.Values.TryAdd(kvp.Key, kvp.Value);
                    }

                    foreach (IODataRoutingConvention convention in routingConventions)
                    {
                        var actionDescriptors = convention.SelectAction(routeContext)?.ToArray();
                        if (actionDescriptors != null && actionDescriptors.Length > 0)
                        {
                            // All actions have the same name but may differ by number of parameters.
                            routeContext.RouteData.Values[ODataRouteConstants.Controller] = actionDescriptors[0].ControllerName;
                            routeContext.RouteData.Values[ODataRouteConstants.Action] = actionDescriptors[0].ActionName;

                            return new ValueTask<RouteValueDictionary>(routeContext.RouteData.Values);
                        }
                    }
                }

                return default;
            }
            finally
            {
                // Eagerly delete the request container to prevent leaks. We need it to run this code,
                // but there are lots of reasons why we might not ever execute an OData action.
                //
                // This can be recreated on demand.
                httpContext.Request.DeleteRequestContainer(true);
            }
        }

        /// <summary>
        /// Get the OData path from the url and query string.
        /// </summary>
        /// <param name="oDataPathString">The ODataPath from the route values.</param>
        /// <param name="uriPathString">The Uri from start to end of path, i.e. the left portion.</param>
        /// <param name="queryString">The Uri from the query string to the end, i.e. the right portion.</param>
        /// <param name="requestContainerFactory">The request container factory.</param>
        /// <returns>The OData path.</returns>
        private static ODataPath GetODataPath(string oDataPathString, string uriPathString, string queryString, Func<IServiceProvider> requestContainerFactory)
        {
            ODataPath path = null;

            try
            {
                // Service root is the current RequestUri, less the query string and the ODataPath (always the
                // last portion of the absolute path).  ODL expects an escaped service root and other service
                // root calculations are calculated using AbsoluteUri (also escaped).  But routing exclusively
                // uses unescaped strings, determined using
                //    address.GetComponents(UriComponents.Path, UriFormat.Unescaped)
                //
                // For example if the AbsoluteUri is
                // <http://localhost/odata/FunctionCall(p0='Chinese%E8%A5%BF%E9%9B%85%E5%9B%BEChars')>, the
                // oDataPathString will contain "FunctionCall(p0='Chinese西雅图Chars')".
                //
                // Due to this decoding and the possibility of unnecessarily-escaped characters, there's no
                // reliable way to determine the original string from which oDataPathString was derived.
                // Therefore a straightforward string comparison won't always work.  See RemoveODataPath() for
                // details of chosen approach.
                string serviceRoot = uriPathString;

                if (!String.IsNullOrEmpty(oDataPathString))
                {
                    serviceRoot = RemoveODataPath(serviceRoot, oDataPathString);
                }

                // As mentioned above, we also need escaped ODataPath.
                // The requestLeftPart and request.QueryString are both escaped.
                // The ODataPath for service documents is empty.
                string oDataPathAndQuery = uriPathString.Substring(serviceRoot.Length);

                if (!String.IsNullOrEmpty(queryString))
                {
                    // Ensure path handler receives the query string as well as the path.
                    oDataPathAndQuery += queryString;
                }

                // Leave an escaped '/' out of the service route because DefaultODataPathHandler will add a
                // literal '/' to the end of this string if not already present. That would double the slash
                // in response links and potentially lead to later 404s.
                if (serviceRoot.EndsWith(_escapedSlash, StringComparison.OrdinalIgnoreCase))
                {
                    serviceRoot = serviceRoot.Substring(0, serviceRoot.Length - _escapedSlash.Length);
                }

                IServiceProvider requestContainer = requestContainerFactory();
                IODataPathHandler pathHandler = requestContainer.GetRequiredService<IODataPathHandler>();
                path = pathHandler.Parse(serviceRoot, oDataPathAndQuery, requestContainer);
            }
            catch (ODataException)
            {
                path = null;
            }

            return path;
        }

        // Find the substring of the given URI string before the given ODataPath.  Tests rely on the following:
        // 1. ODataPath comes at the end of the processed Path
        // 2. Virtual path root, if any, comes at the beginning of the Path and a '/' separates it from the rest
        // 3. OData prefix, if any, comes between the virtual path root and the ODataPath and '/' characters separate
        //    it from the rest
        // 4. Even in the case of Unicode character corrections, the only differences between the escaped Path and the
        //    unescaped string used for routing are %-escape sequences which may be present in the Path
        //
        // Therefore, look for the '/' character at which to lop off the ODataPath.  Can't just unescape the given
        // uriString because subsequent comparisons would only help to check whether a match is _possible_, not where
        // to do the lopping.
        private static string RemoveODataPath(string uriString, string oDataPathString)
        {
            // Potential index of oDataPathString within uriString.
            int endIndex = uriString.Length - oDataPathString.Length - 1;
            if (endIndex <= 0)
            {
                // Bizarre: oDataPathString is longer than uriString.  Likely the values collection passed to Match()
                // is corrupt.
                throw Error.InvalidOperation(SRResources.RequestUriTooShortForODataPath, uriString, oDataPathString);
            }

            string startString = uriString.Substring(0, endIndex + 1);  // Potential return value.
            string endString = uriString.Substring(endIndex + 1);       // Potential oDataPathString match.
            if (String.Equals(endString, oDataPathString, StringComparison.Ordinal))
            {
                // Simple case, no escaping in the ODataPathString portion of the Path.  In this case, don't do extra
                // work to look for trailing '/' in startString.
                return startString;
            }

            while (true)
            {
                // Escaped '/' is a derivative case but certainly possible.
                int slashIndex = startString.LastIndexOf('/', endIndex - 1);
                int escapedSlashIndex =
                    startString.LastIndexOf(_escapedSlash, endIndex - 1, StringComparison.OrdinalIgnoreCase);
                if (slashIndex > escapedSlashIndex)
                {
                    endIndex = slashIndex;
                }
                else if (escapedSlashIndex >= 0)
                {
                    // Include the escaped '/' (three characters) in the potential return value.
                    endIndex = escapedSlashIndex + 2;
                }
                else
                {
                    // Failure, unable to find the expected '/' or escaped '/' separator.
                    throw Error.InvalidOperation(SRResources.ODataPathNotFound, uriString, oDataPathString);
                }

                startString = uriString.Substring(0, endIndex + 1);
                endString = uriString.Substring(endIndex + 1);

                // Compare unescaped strings to avoid both arbitrary escaping and use of lowercase 'a' through 'f' in
                // %-escape sequences.
                endString = Uri.UnescapeDataString(endString);
                if (String.Equals(endString, oDataPathString, StringComparison.Ordinal))
                {
                    return startString;
                }

                if (endIndex == 0)
                {
                    // Failure, could not match oDataPathString after an initial '/' or escaped '/'.
                    throw Error.InvalidOperation(SRResources.ODataPathNotFound, uriString, oDataPathString);
                }
            }
        }
    }
}
