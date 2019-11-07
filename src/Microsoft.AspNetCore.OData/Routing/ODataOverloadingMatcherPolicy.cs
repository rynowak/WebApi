using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNet.OData.Common;
using Microsoft.AspNet.OData.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;

namespace Microsoft.AspNet.OData.Routing
{
    internal class ODataOverloadingMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
    {
        public override int Order => int.MaxValue - 100;

        // This always has to run. We have to see check dynamically if it applies based on the OData feature.
        public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) => true;

        public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
        {
            if (httpContext.ODataFeature().Path == null)
            {
                // Not an OData request.
                return Task.CompletedTask;
            }

            var actions = new List<ActionDescriptor>();
            RouteValueDictionary values = null;

            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates.IsValidCandidate(i) && 
                    candidates[i].Endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is ControllerActionDescriptor action)
                {
                    actions.Add(action);

                    // HACK: old-style routing assumed that you could inspect "THE" route values for a request. In endpoint
                    // routing there's no guarantee that there's a single set of route values.
                    //
                    // However this is probably a safe assumption for OData.
                    values ??= candidates[i].Values;
                }
            }

            // No valid candidate
            if (actions.Count == 0)
            {
                return Task.CompletedTask;
            }

            var match = SelectBestCandidate(httpContext, values, actions);

            // Mark as invalid everything that *wasn't* the match.
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates.IsValidCandidate(i) &&
                    candidates[i].Endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is ControllerActionDescriptor action &&
                    !object.ReferenceEquals(action, match))
                {
                    candidates.SetValidity(i, false);
                }
            }

            return Task.CompletedTask;
        }

        private bool TryMatch(IList<ParameterDescriptor> parameters, IList<string> availableKeys, ODataOptionalParameter optionalWrapper)
        {
            // use the parameter name to match.
            foreach (var p in parameters)
            {
                string parameterName = p.Name.ToLowerInvariant();
                if (availableKeys.Contains(parameterName))
                {
                    continue;
                }

                ControllerParameterDescriptor cP = p as ControllerParameterDescriptor;
                if (cP != null && optionalWrapper != null)
                {
                    if (cP.ParameterInfo.IsOptional && optionalWrapper.OptionalParameters.Any(o => o.Name.ToLowerInvariant() == parameterName))
                    {
                        continue;
                    }
                }

                return false;
            }

            return true;
        }

        public ActionDescriptor SelectBestCandidate(HttpContext httpContext, RouteValueDictionary values, IReadOnlyList<ActionDescriptor> candidates)
        {
            ODataPath odataPath = httpContext.ODataFeature().Path;
            if (odataPath != null && values.ContainsKey(ODataRouteConstants.Action))
            {
                // Get the available parameter names from the route data. Ignore case of key names.
                IList<string> availableKeys = values.Keys.Select(k => k.ToLowerInvariant()).AsList();

                // Filter out types we know how to bind out of the parameter lists. These values
                // do not show up in RouteData() but will bind properly later.
                var considerCandidates = candidates
                    .Select(c => new ActionIdAndParameters(c.Id, c.Parameters.Count, c.Parameters
                        .Where(p =>
                        {
                            return p.ParameterType != typeof(ODataPath) &&
                                !ODataQueryParameterBindingAttribute.ODataQueryParameterBinding.IsODataQueryOptions(p.ParameterType);
                        })));

                // retrieve the optional parameters
                values.TryGetValue(ODataRouteConstants.OptionalParameters, out object wrapper);
                ODataOptionalParameter optionalWrapper = wrapper as ODataOptionalParameter;

                // Find the action with the all matched parameters from available keys including
                // matches with no parameters. Ordered first by the total number of matched
                // parameters followed by the total number of parameters.  Ignore case of
                // parameter names. The first one is the best match.
                //
                // Assume key,relatedKey exist in RouteData. 1st one wins:
                // Method(ODataPath,ODataQueryOptions) vs Method(ODataPath).
                // Method(key,ODataQueryOptions) vs Method(key).
                // Method(key,ODataQueryOptions) vs Method(key).
                // Method(key,relatedKey) vs Method(key).
                // Method(key,relatedKey,ODataPath) vs Method(key,relatedKey).
                var matchedCandidates = considerCandidates
                    .Where(c => !c.FilteredParameters.Any() || TryMatch(c.FilteredParameters, availableKeys, optionalWrapper))
                    .OrderByDescending(c => c.FilteredParameters.Count)
                    .ThenByDescending(c => c.TotalParameterCount);

                // Return either the best matched candidate or the first
                // candidate if none matched.
                return (matchedCandidates.Any())
                    ? candidates.Where(c => c.Id == matchedCandidates.FirstOrDefault().Id).FirstOrDefault()
                    : candidates.FirstOrDefault();
            }

            throw null;
        }

        private class ActionIdAndParameters
        {
            public ActionIdAndParameters(string id, int parameterCount, IEnumerable<ParameterDescriptor> filteredParameters)
            {
                Id = id;
                TotalParameterCount = parameterCount;
                FilteredParameters = filteredParameters.ToList();
            }

            public string Id { get; set; }

            public int TotalParameterCount { get; set; }

            public IList<ParameterDescriptor> FilteredParameters { get; private set; }
        }
    }
}
