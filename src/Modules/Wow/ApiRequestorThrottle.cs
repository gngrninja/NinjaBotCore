using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;

namespace NinjaBotCore.Modules.Wow
{
    internal class ApiRequestorThrottle : WclApiRequestor, IApiRequestorThrottle
    {        
        private readonly Semaphore _queue;
        private int _rateLimitRemaining;
        private DateTime _rateLimitResetRemaining;
        private IServiceProvider _services;

        public ApiRequestorThrottle(string apiKey, string baseUrl, HttpClient client) : base(apiKey, baseUrl, client)
        {
            _queue = new Semaphore(1, 1);
            _rateLimitRemaining = 1;
            _rateLimitResetRemaining = DateTime.UtcNow.AddSeconds(1);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            try
            {
                _queue.WaitOne();

                if (_rateLimitRemaining == 0)
                {
                    var startTime = DateTime.UtcNow;
                    //var difference = _rateLimitResetRemaining - startTime;
                    //System.Console.WriteLine($"dif: {difference}");
   
                    await Task.Delay(1000);
                    
                    _rateLimitRemaining = 1;
                    _rateLimitResetRemaining.AddSeconds(1);
                }

                var response = await base.SendAsync(request);
                _rateLimitRemaining -= 1;
                _rateLimitResetRemaining = _rateLimitResetRemaining.AddSeconds(-1);
                //System.Console.WriteLine(_rateLimitRemaining);
                return response;
            }
            finally
            {
                _queue.Release();
            }
        }
    }
}