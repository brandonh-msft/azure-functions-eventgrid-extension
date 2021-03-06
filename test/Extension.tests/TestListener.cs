﻿using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Azure.WebJobs.Extensions.EventGrid.Tests
{
    public class TestListener
    {
        // multiple events are processed concurrently
        static private ConcurrentDictionary<string, string> _log = new ConcurrentDictionary<string, string>();

        public TestListener()
        {
            _log.Clear();
        }

        // Unsubscribe gives a 202.
        [Fact]
        public async Task TestUnsubscribe()
        {
            var ext = new EventGridExtensionConfigProvider(NullLoggerFactory.Instance);
            var host = TestHelpers.NewHost<MyProg1>(ext);
            await host.StartAsync(); // add listener

            var request = CreateUnsubscribeRequest("TestEventGrid");
            var response = await ext.ConvertAsync(request, CancellationToken.None);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        // Test that an event payload with multiple events causes multiple dispatches,
        // and that each instance has correct binding data.
        // This is the fundamental difference between a regular HTTP trigger and a EventGrid trigger.
        [Fact]
        public async Task TestDispatch()
        {
            var ext = new EventGridExtensionConfigProvider(NullLoggerFactory.Instance);
            var host = TestHelpers.NewHost<MyProg1>(ext);
            await host.StartAsync(); // add listener

            var request = CreateDispatchRequest("TestEventGrid",
                JObject.Parse(@"{'subject':'one','data':{'prop':'alpha'}}"),
                JObject.Parse(@"{'subject':'two','data':{'prop':'beta'}}"));
            var response = await ext.ConvertAsync(request, CancellationToken.None);

            // Verify that the user function was dispatched twice, NOT necessarily in order
            // Also verifies each instance gets its own proper binding data (from FakePayload.Prop)
            _log.TryGetValue("one", out string alpha);
            _log.TryGetValue("two", out string beta);
            Assert.Equal("alpha", alpha);
            Assert.Equal("beta", beta);
            // TODO - Verify that we return from webhook before the dispatch is finished
            // https://github.com/Azure/azure-functions-eventgrid-extension/issues/10
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        [Fact]
        public async Task TestCloudEvent()
        {
            // individual elements
            var ext = new EventGridExtensionConfigProvider(NullLoggerFactory.Instance);
            var host = TestHelpers.NewHost<MyProg1>(ext);
            await host.StartAsync(); // add listener

            var request = CreateSingleRequest("TestEventGrid",
                JObject.Parse(@"{'subject':'one','data':{'prop':'alpha'}}"));
            var response = await ext.ConvertAsync(request, CancellationToken.None);

            // verifies each instance gets its own proper binding data (from FakePayload.Prop)
            _log.TryGetValue("one", out string alpha);
            Assert.Equal("alpha", alpha);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        [Fact]
        public async Task WrongFunctionNameTest()
        {
            var ext = new EventGridExtensionConfigProvider(NullLoggerFactory.Instance);
            var host = TestHelpers.NewHost<MyProg2>(ext);
            await host.StartAsync(); // add listener

            JObject dummyPayload = JObject.Parse("{}");
            var request = CreateDispatchRequest("RandomFunctionName", dummyPayload);
            var response = await ext.ConvertAsync(request, CancellationToken.None);

            string responseContent = await response.Content.ReadAsStringAsync();
            Assert.Equal("cannot find function: 'RandomFunctionName'", responseContent);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task ExecutionFailureTest()
        {
            var ext = new EventGridExtensionConfigProvider(NullLoggerFactory.Instance);
            var host = TestHelpers.NewHost<MyProg2>(ext);
            await host.StartAsync(); // add listener

            JObject dummyPayload = JObject.Parse("{}");
            var request = CreateDispatchRequest("EventGridThrowsException", dummyPayload);
            var response = await ext.ConvertAsync(request, CancellationToken.None);

            string responseContent = await response.Content.ReadAsStringAsync();
            Assert.Equal("Exception while executing function: EventGridThrowsException", responseContent);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        static HttpRequestMessage CreateUnsubscribeRequest(string funcName)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/?functionName=" + funcName);
            request.Headers.Add("aeg-event-type", "Unsubscribe");
            return request;
        }

        static HttpRequestMessage CreateSingleRequest(string funcName, JObject item)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/?functionName=" + funcName);
            request.Headers.Add("aeg-event-type", "Notification");
            request.Content = new StringContent(item.ToString(), Encoding.UTF8, "application/json");
            return request;
        }

        static HttpRequestMessage CreateDispatchRequest(string funcName, params JObject[] items)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/?functionName=" + funcName);
            request.Headers.Add("aeg-event-type", "Notification");
            var payloadArray = new JArray();
            foreach (var item in items)
            {
                payloadArray.Add(item);
            }
            request.Content = new StringContent(payloadArray.ToString(), Encoding.UTF8, "application/json");
            return request;
        }

        public class FakePayload
        {
            public string Prop { get; set; }
        }

        public class MyProg1
        {
            [FunctionName("TestEventGrid")]
            public void Run(
                [EventGridTrigger] JObject value,
                [BindingData("{data.prop}")] string prop)
            {
                // if the key already exists, we should error
                if (!_log.TryAdd((string)value["subject"], prop))
                {
                    throw new InvalidOperationException($"duplicate subject '{(string)value["subject"]}'");
                }
            }
        }

        public class MyProg2
        {
            [FunctionName("EventGridThrowsException")]
            public void Run([EventGridTrigger] JObject value)
            {
                throw new InvalidOperationException($"failed with {value.ToString()}");
            }
        }
    }
}
