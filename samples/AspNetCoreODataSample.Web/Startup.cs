// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using AspNetCoreODataSample.Web.Models;
using Microsoft.AspNet.OData.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AspNetCoreODataSample.Web
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddDbContext<MovieContext>(opt => opt.UseInMemoryDatabase("MovieList"));
            services.AddOData();
            services.AddMvc();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            // OData does sync IO which is not enabled by default.
            app.Use(next => context =>
            {
                var syncIOFeature = context.Features.Get<IHttpBodyControlFeature>();
                if (syncIOFeature != null)
                {
                    syncIOFeature.AllowSynchronousIO = true;
                }

                return next(context);
            });

            var model = EdmModelBuilder.GetEdmModel();

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapODataServiceRoute("odata1", "efcore", model);

                endpoints.MapODataServiceRoute("odata2", "inmem", model);

                endpoints.MapODataServiceRoute("odata3", "composite", EdmModelBuilder.GetCompositeModel());
            });
        }
    }
}
