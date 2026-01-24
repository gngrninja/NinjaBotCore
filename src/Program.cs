using System;
using Discord.Net;
using Discord;
using Discord.Commands;
using System.Threading.Tasks;
using NinjaBotCore.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using Serilog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace NinjaBotCore
{
    class Program
    {
        public static IHostBuilder CreateHostBuilder(string[] args) 
        { 
            return Host.CreateDefaultBuilder(); 
        }
        
        public static async Task Main(string[] args)
        {
            try
            {
                await new NinjaBot().StartAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
