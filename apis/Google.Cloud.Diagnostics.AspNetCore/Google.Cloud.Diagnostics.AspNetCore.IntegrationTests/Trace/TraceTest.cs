﻿// Copyright 2017 Google Inc. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Cloud.Diagnostics.Common;
using Google.Cloud.Diagnostics.Common.IntegrationTests;
using Google.Cloud.Diagnostics.Common.Tests;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Google.Cloud.Diagnostics.AspNetCore.IntegrationTests
{
    public class TraceTest
    {
        private readonly TraceEntryPolling _polling = new TraceEntryPolling();

        [Fact]
        public async Task Trace()
        {
            string testId = Utils.GetTestId();
            var startTime = Timestamp.FromDateTime(DateTime.UtcNow);

            var builder = new WebHostBuilder().UseStartup<TraceTestNoBufferHighQpsApplication>();
            using (var server = new TestServer(builder))
            {
                var client = server.CreateClient();
                await client.GetAsync($"/Trace/Trace/{testId}");

                var spanName = TraceController.GetMessage(nameof(TraceController.Trace), testId);
                var trace = _polling.GetTrace(spanName, startTime);

                Assert.NotNull(trace);
                Assert.Equal(2, trace.Spans.Count);
                var span = trace.Spans.First(s => s.Name.StartsWith("/Trace/Trace/"));
                Assert.NotEmpty(span.Labels);
                Assert.Equal(span.Labels[TraceLabels.HttpMethod], "GET");
                Assert.Equal(span.Labels[TraceLabels.HttpStatusCode], "200");
            }
        }

        [Fact]
        public async Task Trace_Label()
        {
            string testId = Utils.GetTestId();
            var startTime = Timestamp.FromDateTime(DateTime.UtcNow);

            var builder = new WebHostBuilder().UseStartup<TraceTestNoBufferHighQpsApplication>();
            using (var server = new TestServer(builder))
            {
                var client = server.CreateClient();
                await client.GetAsync($"/Trace/TraceLabels/{testId}");

                var spanName = TraceController.GetMessage(nameof(TraceController.TraceLabels), testId);
                var trace = _polling.GetTrace(spanName, startTime);

                Assert.NotNull(trace);
                Assert.Equal(2, trace.Spans.Count);
                var span = trace.Spans.First(s => s.Name.StartsWith("Trace"));
                Assert.Single(span.Labels);
                Assert.Equal(span.Labels[TraceController.Label], TraceController.LabelValue);
            }   
        }

        [Fact]
        public async Task Trace_StackTrace()
        {
            string testId = Utils.GetTestId();
            var startTime = Timestamp.FromDateTime(DateTime.UtcNow);

            var builder = new WebHostBuilder().UseStartup<TraceTestNoBufferHighQpsApplication>();
            using (var server = new TestServer(builder))
            {
                var client = server.CreateClient();
                await client.GetAsync($"/Trace/TraceStackTrace/{testId}");

                var spanName = TraceController.GetMessage(nameof(TraceController.TraceStackTrace), testId);
                var trace = _polling.GetTrace(spanName, startTime);

                Assert.NotNull(trace);
                Assert.Equal(2, trace.Spans.Count);
                var span = trace.Spans.First(s => s.Name.StartsWith("Trace"));
                Assert.Single(span.Labels);
                Assert.Contains(nameof(TraceController), span.Labels[TraceLabels.StackTrace]);
                Assert.Contains(nameof(TraceController.CreateStackTrace), span.Labels[TraceLabels.StackTrace]);
            }   
        }

        [Fact]
        public async Task Trace_Header()
        {
            string traceId = TraceIdFactory.Create().NextId();
            ulong spanId = SpanIdFactory.Create().NextId();

            string testId = Utils.GetTestId();           
            var startTime = Timestamp.FromDateTime(DateTime.UtcNow);
            var builder = new WebHostBuilder().UseStartup<TraceTestNoBufferLowQpsApplication>();
            using (var server = new TestServer(builder))
            {
                var client = server.CreateClient();
                
                var header = TraceHeaderContext.Create(traceId, spanId, shouldTrace: true);
                client.DefaultRequestHeaders.Add(TraceHeaderContext.TraceHeader, header.ToString());
                await client.GetAsync($"/Trace/Trace/{testId}");

                var spanName = TraceController.GetMessage(nameof(TraceController.Trace), testId);
                var trace = _polling.GetTrace(spanName, startTime);

                Assert.NotNull(trace);
                Assert.Equal(traceId, trace.TraceId);
                Assert.Equal(2, trace.Spans.Count);
                var span = trace.Spans.First(s => s.Name.StartsWith("/Trace"));
                Assert.Equal(spanId, span.ParentSpanId);
            }
        }

        [Fact]
        public async Task Trace_QPS()
        {
            string testId = Utils.GetTestId();
            var startTime = Timestamp.FromDateTime(DateTime.UtcNow);

            var builder = new WebHostBuilder().UseStartup<TraceTestNoBufferApplication>();
            using (var server = new TestServer(builder))
            {
                var client = server.CreateClient();

                // The first request is not traced, sleep long enough 
                // for the rate limiter to allow a request, then the second
                // request is traced.
                await client.GetAsync($"/Trace/TraceLabels/{testId}");
                Thread.Sleep(TimeSpan.FromSeconds(2));
                await client.GetAsync($"/Trace/Trace/{testId}");

                var spanNameTrace = TraceController.GetMessage(nameof(TraceController.Trace), testId);
                var spanNameLabels = TraceController.GetMessage(nameof(TraceController.TraceLabels), testId);
                Assert.Null(_polling.GetTrace(spanNameLabels, startTime, expectTrace: false));
                Assert.NotNull(_polling.GetTrace(spanNameTrace, startTime));
            }
        }

        [Fact]
        public async Task Trace_Buffer()
        {
            string testId = Utils.GetTestId();
            var startTime = Timestamp.FromDateTime(DateTime.UtcNow);

            var spanName = TraceController.GetMessage(nameof(TraceController.Trace), testId);

            var builder = new WebHostBuilder().UseStartup<TraceTestBufferHighQpsApplication>();
            using (var server = new TestServer(builder))
            {
                var client = server.CreateClient();

                // Make a trace with a small span that will not cause the buffer to flush.
                await client.GetAsync($"/Trace/Trace/{testId}");
                Assert.Null(_polling.GetTrace(spanName, startTime, expectTrace: false));

                // Make a large trace that will flush the buffer.
                await client.GetAsync($"/Trace/TraceStackTrace/{testId}");

                var spanNameStack = TraceController.GetMessage(nameof(TraceController.TraceStackTrace), testId);
                Assert.NotNull(_polling.GetTrace(spanName, startTime));
                Assert.NotNull(_polling.GetTrace(spanNameStack, startTime));
            }
        }

        [Fact]
        public async Task Trace_Exception()
        {
            string testId = Utils.GetTestId();
            var startTime = Timestamp.FromDateTime(DateTime.UtcNow);

            var builder = new WebHostBuilder().UseStartup<TraceTestNoBufferHighQpsApplication>();
            using (var server = new TestServer(builder))
            {
                var client = server.CreateClient();
                try
                {
                    await client.GetAsync($"/Trace/ThrowException/{testId}");
                }
                catch
                {
                    // This will throw as the task faults.
                }

                var spanName = TraceController.GetMessage(nameof(TraceController.ThrowException), testId);
                var trace = _polling.GetTrace(spanName, startTime);

                Assert.NotNull(trace);
                var span = trace.Spans.First(s => s.Name.StartsWith("/Trace/ThrowException"));
                Assert.NotEmpty(span.Labels);
                Assert.Contains(nameof(TraceController), span.Labels[Common.TraceLabels.StackTrace]);
                Assert.Contains(nameof(TraceController.ThrowException), span.Labels[Common.TraceLabels.StackTrace]);
            }
        }
    }

    /// <summary>
    /// A web application to test <see cref="CloudTraceMiddleware"/> and associated classes.
    /// This app uses a size buffer (1,000 bytes) and will sample 1000000 QPS.
    /// </summary>
    public class TraceTestBufferHighQpsApplication : AbstractTraceTestApplication
    {
        public override double GetSampleRate() => 1000000;
        public override BufferOptions GetBufferOptions() =>
            BufferOptions.SizedBuffer(1000);
    }

    /// <summary>
    /// A web application to test <see cref="CloudTraceMiddleware"/> and associated classes.
    /// This app does not use a buffer and will sample 1 QPS.
    /// </summary>
    public class TraceTestNoBufferApplication : AbstractTraceTestApplication
    {
        public override double GetSampleRate() => 1;
        public override BufferOptions GetBufferOptions() => BufferOptions.NoBuffer();
    }

    /// <summary>
    /// A web application to test <see cref="CloudTraceMiddleware"/> and associated classes.
    /// This app does not use a buffer and will sample 1,000,000 QPS.
    /// This will allow all calls to be traced and push them to the Trace API immediately.
    /// </summary>
    public class TraceTestNoBufferHighQpsApplication : AbstractTraceTestApplication
    {
        public override double GetSampleRate() => 1000000;
        public override BufferOptions GetBufferOptions() => BufferOptions.NoBuffer();
    }

    /// <summary>
    /// A web application to test <see cref="CloudTraceMiddleware"/> and associated classes.
    /// This app does not use a buffer and will sample 0.0000001 QPS.
    /// This will allow no calls to be traced (unless they have a trace header) and
    /// push them to the Trace API immediately.
    /// </summary>
    public class TraceTestNoBufferLowQpsApplication : AbstractTraceTestApplication
    {
        public override double GetSampleRate() => 0.0000001;
        public override BufferOptions GetBufferOptions() => BufferOptions.NoBuffer();
    }

    /// <summary>
    /// A base web application to test <see cref="CloudTraceMiddleware"/> and associated classes.
    /// </summary>
    public abstract class AbstractTraceTestApplication
    {
        private readonly string _projectId;

        public AbstractTraceTestApplication()
        {
            _projectId = Utils.GetProjectIdFromEnvironment();
        }

        public abstract double GetSampleRate();

        public abstract BufferOptions GetBufferOptions();

        public void ConfigureServices(IServiceCollection services)
        {
            var traceConfig = TraceConfiguration.Create(GetSampleRate(), GetBufferOptions());
            services.AddGoogleTrace(_projectId, traceConfig);
            services.AddMvc();
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseGoogleTrace();
            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Trace}/{action=Index}/{id}");
            });
        }
    }

    /// <summary>
    /// A controller for the <see cref="AbstractTraceTestApplication"/> used trace calls.
    /// </summary>
    public class TraceController : Controller
    {
        public const string Label = "SomeLabel";
        public const string LabelValue = "SomeValue";

        public TraceController() { }

        /// <summary>Sleeps for 10ms before returning.</summary>
        public string Index(string id)
        {
            Thread.Sleep(10);
            return nameof(Index);
        }

        /// <summary>Traces a 10ms sleep.</summary>
        public string Trace(string id, [FromServices] IManagedTracer tracer)
        {
            string message = GetMessage(nameof(Trace), id);
            tracer.StartSpan(message);
            Thread.Sleep(10);
            tracer.EndSpan();
            return message;
        }

        /// <summary>Traces a 10ms sleep and adds an annotation.</summary>
        public string TraceLabels(string id, [FromServices] IManagedTracer tracer)
        {
            string message = GetMessage(nameof(TraceLabels), id);
            using (tracer.StartSpan(message))
            {
                Thread.Sleep(10);
                tracer.AnnotateSpan(new Dictionary<string, string> { { Label, LabelValue } });
                return message;
            }
        }

        /// <summary>Traces a 10ms sleep and adds a stacktrace.</summary>
        public string TraceStackTrace(string id, [FromServices] IManagedTracer tracer)
        {
            string message = GetMessage(nameof(TraceStackTrace), id);
            tracer.StartSpan(message);
            Thread.Sleep(10);
            tracer.SetStackTrace(CreateStackTrace());
            tracer.EndSpan();
            return message;
        }

        /// <summary>Traces a 10ms sleep and throws an exception.</summary>
        public string ThrowException(string id, [FromServices] IManagedTracer tracer)
        {
            // Add a span with the test id to allow for the trace to be found.
            string message = GetMessage(nameof(ThrowException), id);
            using (tracer.StartSpan(message))
            {
                Thread.Sleep(10);
                throw new DivideByZeroException();
            }
        }

        /// <summary>Creates a stack trace.</summary>
        internal StackTrace CreateStackTrace()
        {
            try
            {
                throw new Exception("opps...");
            }
            catch (Exception e)
            {
                return new StackTrace(e, true);
            }
        }

        public static string GetMessage(string message, string id) => $"{message} - {id}";
    }
}
