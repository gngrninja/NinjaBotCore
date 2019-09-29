using System;
using Discord.Net;
using Discord;
using Discord.Commands;
using System.Threading.Tasks;
using NinjaBotCore.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace NinjaBotCore
{
    class Program
    {
        public static void Main(string[] args)
        {
            try
            {
                new NinjaBot().StartAsync().GetAwaiter().GetResult();       
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }
}
