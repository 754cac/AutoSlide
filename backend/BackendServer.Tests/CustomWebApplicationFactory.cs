using System;
using System.Collections.Generic;
using System.Linq;
using BackendServer.Shared.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BackendServer.Tests
{
    public class CustomWebApplicationFactory : WebApplicationFactory<Program>
    {
        public CustomWebApplicationFactory()
        {
            Environment.SetEnvironmentVariable("UseInMemoryDatabase", "true");
            Environment.SetEnvironmentVariable("SUPABASE_URL", "http://127.0.0.1:54321");
            Environment.SetEnvironmentVariable("SUPABASE_KEY", "test_supabase_key_that_is_long_enough_0123456789");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(AppContext.BaseDirectory);

            builder.ConfigureAppConfiguration((context, config) =>
            {
                var dict = new Dictionary<string, string>
                {
                    ["AppSettings:Token"] = "test_super_long_secret_that_is_at_least_64_characters_long_0123456789",
                };
                config.AddInMemoryCollection(dict);
            });
        }
    }
}
